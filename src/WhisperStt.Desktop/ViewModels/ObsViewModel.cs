using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using WhisperStt.Core.Obs;
using WhisperStt.Desktop.Mvvm;
using WhisperStt.Desktop.Services;

namespace WhisperStt.Desktop.ViewModels;

// One choice in a list, with the words shown for it.
public sealed record Choice<T>(T Value, string Label);

// A Google Fonts family found by the search.
public sealed class FontResult(GoogleFont font, bool installed) : ObservableObject
{
    private bool _installed = installed, _busy;

    public string Family { get; } = font.Family;
    public string Category { get; } = font.Category.Replace('_', ' ').ToLowerInvariant();

    public bool IsInstalled
    {
        get => _installed;
        set
        {
            if (SetProperty(ref _installed, value))
                OnPropertyChanged(nameof(ActionText));
        }
    }

    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (SetProperty(ref _busy, value))
                OnPropertyChanged(nameof(ActionText));
        }
    }

    public string ActionText => IsBusy ? "Installing…" : IsInstalled ? "Use" : "Install";
}

// The OBS overlay: while the canvas is on, every sentence also appears on a transparent page that OBS
// shows as a Browser Source, styled as the user chooses here.
public sealed class ObsViewModel : ObservableObject, IDisposable
{
    private const int MaxResults = 30;

    private readonly Action _changed;
    private readonly Action<Action> _post;
    private readonly OverlayStyle _style;
    private readonly IReadOnlyList<string> _systemFonts;
    private OverlayServer? _server;
    private bool _isOpen, _isOn;
    private string _status = "", _fontQuery = "", _searchStatus = "";
    private CancellationTokenSource? _search;

    public ObsViewModel(AppState state, Action changed, Action<Action> post)
    {
        _changed = changed;
        _post = post;
        _style = state.Obs?.Clone() ?? new OverlayStyle();
        _isOn = state.ObsOn;
        _systemFonts = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        RefreshFamilies();

        OpenCommand = new RelayCommand(() => IsOpen = true);
        CopyUrlCommand = new RelayCommand(() => ClipboardHelper.TrySetText(Url));
        FontActionCommand = new RelayCommand<FontResult>(result => _ = FontActionAsync(result));
        InstallTypedCommand = new RelayCommand(() => _ = InstallTypedAsync(), () => FontQuery.Trim().Length > 0);
        ResetCommand = new RelayCommand(Reset);
        UpdateServer();
    }

    // Where the preview's browser keeps its data. Settable so tests can keep it apart from the user's.
    public static string PreviewDataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "webview");

    public ICommand OpenCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand FontActionCommand { get; }
    public ICommand InstallTypedCommand { get; }
    public ICommand ResetCommand { get; }

    public IReadOnlyList<Choice<TextCase>> Cases { get; } =
    [
        new(TextCase.AsSaid, "As said"), new(TextCase.Uppercase, "UPPERCASE"),
        new(TextCase.Lowercase, "lowercase"), new(TextCase.Capitalize, "Capitalize Each Word")
    ];

    public IReadOnlyList<Choice<HorizontalAlign>> HorizontalAligns { get; } =
        [new(HorizontalAlign.Left, "Left"), new(HorizontalAlign.Center, "Center"), new(HorizontalAlign.Right, "Right")];

    public IReadOnlyList<Choice<VerticalAlign>> VerticalAligns { get; } =
        [new(VerticalAlign.Top, "Top"), new(VerticalAlign.Middle, "Middle"), new(VerticalAlign.Bottom, "Bottom")];

    // The fonts to pick from: installed Google Fonts first, then the fonts installed on this PC.
    public ObservableCollection<string> Families { get; } = [];
    public ObservableCollection<FontResult> FontResults { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
                return;
            _isOpen = value;
            UpdateServer(); // the preview needs the page even while the canvas is off, so start it before saying so
            OnPropertyChanged();
        }
    }

    // The canvas switch: whether sentences go to OBS.
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value))
                return;
            OnPropertyChanged(nameof(ToggleText));
            OnPropertyChanged(nameof(Summary));
            UpdateServer();
            _changed();
        }
    }

    public string ToggleText => IsOn ? "Canvas on: sentences appear in OBS" : "Canvas off";
    public string Summary => IsOn ? "On" : "Off";
    public string Url => _server?.Url ?? $"http://127.0.0.1:{OverlayServer.DefaultPort}/";
    public string PreviewUrl => Url + "?preview=1";
    public bool IsRunning => _server is not null;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // -- the style

    public int CanvasWidth { get => _style.Width; set => Set(_style.Width, Math.Clamp(value, 50, 7680), v => _style.Width = v); }
    public int CanvasHeight { get => _style.Height; set => Set(_style.Height, Math.Clamp(value, 20, 4320), v => _style.Height = v); }

    public string FontFamily
    {
        get => _style.FontFamily;
        set => Set(_style.FontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim(), v => _style.FontFamily = v);
    }

    public double FontSize { get => _style.FontSize; set => Set(_style.FontSize, Math.Round(value), v => _style.FontSize = v, nameof(FontSizeText)); }
    public string FontSizeText => $"{FontSize:0} px";
    public bool Bold { get => _style.Bold; set => Set(_style.Bold, value, v => _style.Bold = v); }
    public string TextColor { get => _style.Color; set => SetColor(_style.Color, value, v => _style.Color = v); }
    public TextCase Case { get => _style.Case; set => Set(_style.Case, value, v => _style.Case = v); }
    public HorizontalAlign HorizontalAlign { get => _style.HorizontalAlign; set => Set(_style.HorizontalAlign, value, v => _style.HorizontalAlign = v); }
    public VerticalAlign VerticalAlign { get => _style.VerticalAlign; set => Set(_style.VerticalAlign, value, v => _style.VerticalAlign = v); }
    public double LineHeight { get => _style.LineHeight; set => Set(_style.LineHeight, Math.Round(value, 2), v => _style.LineHeight = v, nameof(LineHeightText)); }
    public string LineHeightText => $"{LineHeight:0.00}×";

    public bool Shadow { get => _style.Shadow; set => Set(_style.Shadow, value, v => _style.Shadow = v); }
    public double ShadowX { get => _style.ShadowX; set => Set(_style.ShadowX, Math.Round(value), v => _style.ShadowX = v, nameof(ShadowXText)); }
    public string ShadowXText => $"{ShadowX:0} px";
    public double ShadowY { get => _style.ShadowY; set => Set(_style.ShadowY, Math.Round(value), v => _style.ShadowY = v, nameof(ShadowYText)); }
    public string ShadowYText => $"{ShadowY:0} px";
    public double ShadowBlur { get => _style.ShadowBlur; set => Set(_style.ShadowBlur, Math.Round(value), v => _style.ShadowBlur = v, nameof(ShadowBlurText)); }
    public string ShadowBlurText => $"{ShadowBlur:0} px";
    public string ShadowColor { get => _style.ShadowColor; set => SetColor(_style.ShadowColor, value, v => _style.ShadowColor = v); }

    public double OutlineWidth { get => _style.OutlineWidth; set => Set(_style.OutlineWidth, Math.Round(value), v => _style.OutlineWidth = v, nameof(OutlineWidthText)); }
    public string OutlineWidthText => OutlineWidth == 0 ? "None" : $"{OutlineWidth:0} px";
    public string OutlineColor { get => _style.OutlineColor; set => SetColor(_style.OutlineColor, value, v => _style.OutlineColor = v); }

    public double Lines { get => _style.Lines; set => Set(_style.Lines, (int)Math.Round(value), v => _style.Lines = v, nameof(LinesText)); }
    public string LinesText => _style.Lines == 1 ? "1 sentence" : $"{_style.Lines} sentences";
    public double HideAfter { get => _style.HideAfterSeconds; set => Set(_style.HideAfterSeconds, Math.Round(value), v => _style.HideAfterSeconds = v, nameof(HideAfterText)); }
    public string HideAfterText => HideAfter == 0 ? "Never" : $"{HideAfter:0} s";

    private void Set<T>(T current, T value, Action<T> apply, string? also = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        apply(value);
        OnPropertyChanged(name);
        if (also is not null)
            OnPropertyChanged(also);
        _server?.SetStyle(_style);
        _changed();
    }

    // A color typed as #RRGGBB or #AARRGGBB; anything else is ignored until it becomes a color.
    private void SetColor(string current, string value, Action<string> apply, [CallerMemberName] string? name = null)
    {
        var text = value.Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;
        if (OverlayStyle.TryParse(text, out _, out _, out _, out _))
            Set(current, text.ToUpperInvariant(), apply, null, name);
    }

    private void Reset()
    {
        var defaults = new OverlayStyle();
        foreach (var property in typeof(OverlayStyle).GetProperties())
            property.SetValue(_style, property.GetValue(defaults));
        OnPropertyChanged(string.Empty); // every property
        _server?.SetStyle(_style);
        _changed();
    }

    public void WriteTo(AppState state)
    {
        state.Obs = _style.Clone();
        state.ObsOn = IsOn;
    }

    // -- the page server

    private void UpdateServer()
    {
        var wanted = IsOn || IsOpen;
        if (wanted && _server is null)
        {
            try
            {
                _server = new OverlayServer();
                _server.SetStyle(_style);
                Status = IsOn ? "Waiting for sentences." : "";
            }
            catch (HttpListenerException ex)
            {
                Status = $"Couldn't start the overlay on port {OverlayServer.DefaultPort}: {ex.Message}. Is another app using it?";
            }
        }
        else if (!wanted && _server is not null)
        {
            _server.Dispose();
            _server = null;
        }
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(PreviewUrl));
    }

    // A sentence that was just said, for the screen.
    public void Show(string text)
    {
        if (!IsOn || _server is null)
            return;
        _server.Show(text);
        Status = $"Last sentence sent at {DateTime.Now:HH:mm:ss}.";
    }

    public void Clear()
    {
        if (IsOn)
            _server?.Clear();
    }

    // -- fonts

    private void RefreshFamilies()
    {
        var google = GoogleFonts.Installed();
        var all = google.Concat(_systemFonts.Where(f => !google.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();
        if (all.SequenceEqual(Families))
            return;
        Families.Clear();
        foreach (var family in all)
            Families.Add(family);
        OnPropertyChanged(nameof(FontFamily)); // keep the selection shown after the list changed
    }

    public string FontQuery
    {
        get => _fontQuery;
        set
        {
            if (SetProperty(ref _fontQuery, value))
                _ = SearchAsync(value);
        }
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    private async Task SearchAsync(string query)
    {
        _search?.Cancel();
        var cancellation = _search = new CancellationTokenSource();
        FontResults.Clear();
        if (query.Trim().Length == 0)
        {
            SearchStatus = "";
            return;
        }
        try
        {
            await Task.Delay(250, cancellation.Token); // wait for the typing to settle
            SearchStatus = "Searching Google Fonts…";
            var all = await GoogleFonts.ListAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;
            var found = GoogleFonts.Search(all, query, MaxResults);
            foreach (var font in found)
                FontResults.Add(new FontResult(font, GoogleFonts.IsInstalled(font.Family)));
            SearchStatus = found.Count == 0 ? $"No Google Fonts family contains \"{query.Trim()}\"." : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            SearchStatus = "Couldn't get the Google Fonts list. Type the exact family name and press Install it.";
        }
    }

    // Installs a family the first time, then uses it.
    private async Task FontActionAsync(FontResult result)
    {
        if (result.IsBusy)
            return;
        if (!result.IsInstalled && !await InstallAsync(result.Family, busy => result.IsBusy = busy))
            return;
        result.IsInstalled = true;
        FontFamily = result.Family;
    }

    // For when the list can't be reached: installs the family typed in the search box.
    private async Task InstallTypedAsync()
    {
        var family = FontQuery.Trim();
        if (await InstallAsync(family, _ => { }))
            FontFamily = family;
    }

    private async Task<bool> InstallAsync(string family, Action<bool> busy)
    {
        busy(true);
        SearchStatus = $"Installing {family}…";
        try
        {
            await Task.Run(() => GoogleFonts.InstallAsync(family));
            RefreshFamilies();
            _server?.FontsChanged();
            SearchStatus = $"{family} is installed.";
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            SearchStatus = $"Couldn't install {family}: {ex.Message}";
            return false;
        }
        finally
        {
            busy(false);
        }
    }

    public void Dispose()
    {
        _search?.Cancel();
        _server?.Dispose();
        _server = null;
    }
}
