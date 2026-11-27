using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Media;
using WhisperStt.Core.Obs;
using WhisperStt.Desktop.Mvvm;
using WhisperStt.Desktop.Services;

namespace WhisperStt.Desktop.ViewModels;

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

// The OBS overlay: any number of canvases, each a page OBS shows as a Browser Source. While a canvas is on,
// every sentence also appears on it, styled as the user chose. One local server serves them all.
public sealed class ObsViewModel : ObservableObject, IDisposable
{
    private const int MaxResults = 30;

    private readonly Action _changed;
    private readonly IReadOnlyList<string> _systemFonts;
    private OverlayServer? _server;
    private int _port;
    private string _portText;
    private bool _isOpen, _confirmDelete;
    private CanvasViewModel? _editing;
    private string _status = "", _fontQuery = "", _searchStatus = "";
    private CancellationTokenSource? _search;

    public ObsViewModel(AppState state, Action changed)
    {
        _changed = changed;
        _port = state.ObsPort is > 1023 and < 65536 ? state.ObsPort : OverlayServer.DefaultPort;
        _portText = _port.ToString();
        _systemFonts = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        // Settings from before there were several canvases become the first canvas.
        var saved = state.ObsCanvases.Count > 0 ? state.ObsCanvases
            : state.Obs is { } old ? [old] : [];
        if (state.ObsCanvases.Count == 0 && state.Obs is not null)
            saved[0].IsOn = state.ObsOn;
        foreach (var style in saved)
        {
            var copy = style.Clone();
            copy.Upgrade();
            Canvases.Add(Wrap(copy));
        }
        RefreshFamilies();

        OpenCommand = new RelayCommand(() => IsOpen = true);
        AddCommand = new RelayCommand(Add);
        EditCommand = new RelayCommand<CanvasViewModel>(canvas => Editing = canvas);
        BackCommand = new RelayCommand(() => Editing = null);
        DeleteCommand = new RelayCommand(Delete, () => Editing is not null);
        CopyUrlCommand = new RelayCommand(() => ClipboardHelper.TrySetText(Editing?.Url ?? ""));
        ApplyPortCommand = new RelayCommand(ApplyPort);
        ChooseImageCommand = new RelayCommand(ChooseImage, () => Editing is not null);
        RemoveImageCommand = new RelayCommand(() => { if (Editing is { } c) { OverlayImages.Remove(c.Id); c.SetImage(""); } });
        ResetCommand = new RelayCommand(Reset, () => Editing is not null);
        FontActionCommand = new RelayCommand<FontResult>(result => _ = FontActionAsync(result));
        InstallTypedCommand = new RelayCommand(() => _ = InstallTypedAsync(), () => FontQuery.Trim().Length > 0);
        AddFontFileCommand = new RelayCommand(AddFontFile);
        ChooseSoundCommand = new RelayCommand<SoundViewModel>(ChooseSound);
        PlaySoundCommand = new RelayCommand<SoundViewModel>(s => PlayRequested?.Invoke(s.Slot));
        PlayDemoCommand = new RelayCommand(() => DemoRequested?.Invoke());
        SoundViewModel.FileWanted += ChooseSound;
        UpdateServer();
    }

    public ICommand OpenCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand ApplyPortCommand { get; }
    public ICommand ChooseImageCommand { get; }
    public ICommand RemoveImageCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand FontActionCommand { get; }
    public ICommand InstallTypedCommand { get; }
    public ICommand AddFontFileCommand { get; }
    public ICommand ChooseSoundCommand { get; }
    public ICommand PlaySoundCommand { get; }
    public ICommand PlayDemoCommand { get; }

    // The preview plays these (it holds the page that makes the sounds and animations).
    public event Action<string>? PlayRequested;
    public event Action? DemoRequested;

    public IReadOnlyList<Choice<string>> SoundKinds => SoundViewModel.Kinds;

    public IReadOnlyList<Choice<RevealUnit>> RevealUnits { get; } =
        [new(RevealUnit.Character, "Character by character"), new(RevealUnit.Word, "Word by word")];

    public IReadOnlyList<Choice<RevealEffect>> RevealEffects { get; } =
        [new(RevealEffect.Appear, "Appear"), new(RevealEffect.Fade, "Fade in"), new(RevealEffect.Pop, "Pop"), new(RevealEffect.Rise, "Rise")];

    public IReadOnlyList<Choice<ClearAnimation>> ClearAnimations { get; } =
    [
        new(ClearAnimation.Fade, "Fade out"), new(ClearAnimation.SlideUp, "Slide up"), new(ClearAnimation.SlideDown, "Slide down"),
        new(ClearAnimation.Shrink, "Shrink"), new(ClearAnimation.None, "None (instant)")
    ];

    // Where the preview's browser keeps its data. Settable so tests can keep it apart from the user's.
    public static string PreviewDataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "webview");

    public ObservableCollection<CanvasViewModel> Canvases { get; } = [];
    // The fonts to pick from: installed Google Fonts first, then the fonts installed on this PC.
    public ObservableCollection<string> Families { get; } = [];
    public ObservableCollection<FontResult> FontResults { get; } = [];

    public IReadOnlyList<Choice<TextCase>> Cases { get; } =
    [
        new(TextCase.AsSaid, "As said"), new(TextCase.Uppercase, "UPPERCASE"),
        new(TextCase.Lowercase, "lowercase"), new(TextCase.Capitalize, "Capitalize Each Word")
    ];

    public IReadOnlyList<Choice<int>> Weights { get; } =
    [
        new(100, "100 · Thin"), new(200, "200 · Extra light"), new(300, "300 · Light"), new(400, "400 · Regular"),
        new(500, "500 · Medium"), new(600, "600 · Semibold"), new(700, "700 · Bold"), new(800, "800 · Extra bold"), new(900, "900 · Black")
    ];

    public IReadOnlyList<Choice<BackgroundType>> BackgroundTypes { get; } =
        [new(BackgroundType.None, "None (transparent)"), new(BackgroundType.Color, "Color"), new(BackgroundType.Gradient, "Gradient"), new(BackgroundType.Image, "Picture")];

    public IReadOnlyList<Choice<ImageFit>> ImageFits { get; } =
        [new(ImageFit.Cover, "Fill (crop)"), new(ImageFit.Contain, "Fit (whole picture)"), new(ImageFit.Stretch, "Stretch"), new(ImageFit.Tile, "Tile")];

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (_isOpen == value)
                return;
            _isOpen = value;
            if (!value)
                Editing = null;
            UpdateServer(); // the preview needs the pages even while every canvas is off, so start it first
            OnPropertyChanged();
        }
    }

    // The canvas being edited, or null on the list of canvases.
    public CanvasViewModel? Editing
    {
        get => _editing;
        set
        {
            if (!SetProperty(ref _editing, value))
                return;
            ConfirmDelete = false;
            OnPropertyChanged(nameof(IsEditing));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsEditing => Editing is not null;

    // Whether any canvas is on (the dot on the toolbar button).
    public bool AnyOn => Canvases.Any(c => c.IsOn);

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsRunning => _server is not null;

    // -- canvases

    private CanvasViewModel Wrap(OverlayStyle style)
    {
        var canvas = new CanvasViewModel(style, OnCanvasChanged);
        canvas.SetUrl(UrlOf(style.Id));
        return canvas;
    }

    private string UrlOf(string id) => $"http://127.0.0.1:{_port}/c/{id}";

    private void OnCanvasChanged(CanvasViewModel canvas, bool imageChanged)
    {
        _server?.SetCanvas(canvas.Style, imageChanged);
        OnPropertyChanged(nameof(AnyOn));
        UpdateServer();
        _changed();
    }

    private void Add()
    {
        var name = $"Text template {Canvases.Count + 1}";
        for (int n = Canvases.Count + 2; Canvases.Any(c => c.Name == name); n++)
            name = $"Text template {n}";
        var canvas = Wrap(new OverlayStyle { Name = name });
        Canvases.Add(canvas);
        _server?.SetCanvas(canvas.Style);
        OnPropertyChanged(nameof(AnyOn));
        UpdateServer();
        _changed();
        Editing = canvas;
    }

    // First press asks, the second deletes.
    public bool ConfirmDelete
    {
        get => _confirmDelete;
        private set
        {
            if (SetProperty(ref _confirmDelete, value))
                OnPropertyChanged(nameof(DeleteText));
        }
    }

    public string DeleteText => ConfirmDelete ? "Click again to delete" : "Delete text template";

    private void Delete()
    {
        if (Editing is not { } canvas)
            return;
        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }
        Canvases.Remove(canvas);
        _server?.RemoveCanvas(canvas.Id);
        OverlayImages.Remove(canvas.Id);
        OverlayImages.RemoveSounds(canvas.Id);
        OverlayImages.RemoveThumbnail(canvas.Id);
        Editing = null;
        OnPropertyChanged(nameof(AnyOn));
        UpdateServer();
        _changed();
    }

    // Back to the default look; the canvas keeps its name, size, switch and address.
    private void Reset()
    {
        if (Editing is not { } canvas)
            return;
        var defaults = new OverlayStyle();
        var keep = new[] { nameof(OverlayStyle.Id), nameof(OverlayStyle.Name), nameof(OverlayStyle.IsOn), nameof(OverlayStyle.Width), nameof(OverlayStyle.Height) };
        foreach (var property in typeof(OverlayStyle).GetProperties().Where(p => p.CanWrite && !keep.Contains(p.Name)))
            property.SetValue(canvas.Style, property.GetValue(defaults));
        OverlayImages.Remove(canvas.Id);
        OverlayImages.RemoveSounds(canvas.Id);
        canvas.Refresh();
        OnCanvasChanged(canvas, true);
    }

    private void ChooseSound(SoundViewModel sound)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"{sound.Title} sound",
            Filter = "Sounds|*.mp3;*.wav;*.ogg;*.m4a;*.aac;*.flac;*.webm"
        };
        if (dialog.ShowDialog() != true)
        {
            if (sound.FileText == "No file chosen")
                sound.Kind = ""; // nothing chosen: back to no sound
            return;
        }
        SetSoundFile(sound, dialog.FileName);
    }

    // Separate from the dialog so it can be tested.
    public bool SetSoundFile(SoundViewModel sound, string path)
    {
        try
        {
            sound.SetFile(OverlayImages.ImportSound(sound.Canvas.Id, sound.Slot, path));
            sound.Kind = "file";
            Status = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Couldn't use that sound: {ex.Message}";
            return false;
        }
    }

    private void ChooseImage()
    {
        if (Editing is not { } canvas)
            return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Background picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            canvas.SetImage(OverlayImages.Import(canvas.Id, dialog.FileName));
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Couldn't use that picture: {ex.Message}";
        }
    }

    public void WriteTo(AppState state)
    {
        state.ObsCanvases = Canvases.Select(c => c.Style.Clone()).ToList();
        state.ObsPort = _port;
        state.Obs = null;
        state.ObsOn = false;
    }

    // -- the port and the server

    public string PortText
    {
        get => _portText;
        set => SetProperty(ref _portText, value);
    }

    private void ApplyPort()
    {
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1024 or > 65535)
        {
            Status = "The port must be a number from 1024 to 65535.";
            return;
        }
        if (port == _port)
            return;
        _port = port;
        PortText = port.ToString();
        _server?.Dispose();
        _server = null;
        foreach (var canvas in Canvases)
            canvas.SetUrl(UrlOf(canvas.Id));
        UpdateServer();
        Status = IsRunning ? $"Now on port {port}. Update the address in OBS." : Status;
        _changed();
    }

    private void UpdateServer()
    {
        var wanted = AnyOn || IsOpen;
        if (wanted && _server is null)
        {
            try
            {
                _server = new OverlayServer(_port);
                foreach (var canvas in Canvases)
                    _server.SetCanvas(canvas.Style);
                Status = "";
            }
            catch (HttpListenerException ex)
            {
                Status = $"Couldn't start the overlay on port {_port}: {ex.Message.TrimEnd('.')}. Another app may be using it; choose another port.";
            }
        }
        else if (!wanted && _server is not null)
        {
            _server.Dispose();
            _server = null;
        }
        OnPropertyChanged(nameof(IsRunning));
    }

    // A sentence that was just said, for every canvas that is on.
    public void Show(string text)
    {
        if (!AnyOn || _server is null)
            return;
        _server.Show(text);
        Status = $"Last sentence sent at {DateTime.Now:HH:mm:ss}.";
    }

    public void Clear() => _server?.Clear();

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
        Editing?.Refresh(); // keep the selection shown after the list changed
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
            SearchStatus = "Couldn't get the Google Fonts list. Type the exact family name and press Enter to install it.";
        }
    }

    // Installs a family the first time, then uses it on the canvas being edited.
    private async Task FontActionAsync(FontResult result)
    {
        if (result.IsBusy)
            return;
        if (!result.IsInstalled && !await InstallAsync(result.Family, busy => result.IsBusy = busy))
            return;
        result.IsInstalled = true;
        if (Editing is { } canvas)
            canvas.FontFamily = result.Family;
    }

    // A font from a file the user has (TTF, OTF, WOFF, WOFF2), kept for the overlay and used on this canvas.
    private void AddFontFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add a font",
            Filter = "Fonts|*.ttf;*.otf;*.woff;*.woff2"
        };
        if (dialog.ShowDialog() != true)
            return;
        AddFontFile(dialog.FileName);
    }

    // Separate from the dialog so it can be tested. Returns the family name, or null when it failed.
    public string? AddFontFile(string path)
    {
        var family = FamilyNameOf(path);
        try
        {
            GoogleFonts.InstallFile(family, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SearchStatus = $"Couldn't add that font: {ex.Message}";
            return null;
        }
        RefreshFamilies();
        _server?.FontsChanged();
        if (Editing is { } canvas)
            canvas.FontFamily = family;
        SearchStatus = $"{family} is added.";
        return family;
    }

    // The family name stored in a TTF or OTF file; for web fonts (or an unreadable file), the file's name.
    private static string FamilyNameOf(string path)
    {
        var fallback = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Trim();
        if (Path.GetExtension(path).ToLowerInvariant() is not (".ttf" or ".otf"))
            return fallback;
        try
        {
            var face = new GlyphTypeface(new Uri(path));
            var names = face.Win32FamilyNames.Count > 0 ? face.Win32FamilyNames : face.FamilyNames;
            return names.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-US"), out var english) ? english
                : names.Values.FirstOrDefault() is { Length: > 0 } any ? any
                : fallback;
        }
        catch (Exception ex) when (ex is IOException or FileFormatException or UriFormatException or NotSupportedException)
        {
            return fallback;
        }
    }

    // For when the list can't be reached: installs the family typed in the search box.
    private async Task InstallTypedAsync()
    {
        var family = FontQuery.Trim();
        if (await InstallAsync(family, _ => { }) && Editing is { } canvas)
            canvas.FontFamily = family;
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
        SoundViewModel.FileWanted -= ChooseSound;
        _server?.Dispose();
        _server = null;
    }
}
