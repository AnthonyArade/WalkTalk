using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhisperStt.Desktop.Motion;
using WhisperStt.Desktop.Services;
using WhisperStt.Desktop.ViewModels;

namespace WhisperStt.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Keep the newest sentence in view.
        _viewModel.Transcript.CollectionChanged += (_, _) => ScrollTranscriptToEnd();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PendingText))
                ScrollTranscriptToEnd();
            // Opening the hints dialog puts the cursor straight in its input box.
            if (e.PropertyName == nameof(MainViewModel.IsHintsOpen) && _viewModel.IsHintsOpen)
                Dispatcher.BeginInvoke(() => NewHintBox.Focus(), DispatcherPriority.Input);
        };
        _viewModel.TargetAdded += OnTargetAdded;
        // The replacements dialog opens with the cursor in the search box; picking a suggested word
        // moves it on to the box for what to write instead.
        _viewModel.Replacements.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReplacementsViewModel.IsOpen) && _viewModel.Replacements.IsOpen)
                Dispatcher.BeginInvoke(() => ReplacementSearchBox.Focus(), DispatcherPriority.Input);
        };
        _viewModel.Replacements.Picked += () => Dispatcher.BeginInvoke(() => ReplacementCustomBox.Focus(), DispatcherPriority.Input);
        // The canvas editor's preview shows that canvas's overlay page itself, loaded when a canvas is opened.
        // While it is on screen, a picture of it is taken for the canvas's tile: once the page has loaded, and
        // again shortly after each change (a hidden preview can't be photographed, so not on leaving).
        _snapshotTimer.Tick += async (_, _) =>
        {
            _snapshotTimer.Stop();
            if (_previewed is { } canvas && ObsPreview.IsVisible)
                await SnapshotCanvasAsync(canvas);
        };
        // The editor's Play buttons: the preview page plays a sound, or a demo of the animation.
        _viewModel.Obs.PlayRequested += slot => _ = ObsPreview.CoreWebView2?.ExecuteScriptAsync(
            $"window.walktalkPlay && walktalkPlay({System.Text.Json.JsonSerializer.Serialize(slot)})");
        _viewModel.Obs.DemoRequested += () => _ = ObsPreview.CoreWebView2?.ExecuteScriptAsync("window.walktalkDemo && walktalkDemo()");
        _viewModel.Obs.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName != nameof(ObsViewModel.Editing))
                return;
            if (_previewed is not null)
                _previewed.PropertyChanged -= OnPreviewedCanvasChanged;
            _snapshotTimer.Stop();
            _previewed = _viewModel.Obs.Editing;
            if (_previewed is null)
                return;
            _previewed.PropertyChanged += OnPreviewedCanvasChanged;
            await ShowObsPreviewAsync();
        };
    }

    // The canvas the preview is showing, and the wait before photographing it after a change.
    private CanvasViewModel? _previewed;
    private readonly DispatcherTimer _snapshotTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    private void OnPreviewedCanvasChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CanvasViewModel.Thumbnail) or nameof(CanvasViewModel.HasThumbnail))
            return;
        _snapshotTimer.Stop();
        _snapshotTimer.Start();
    }

    // Saves a picture of the canvas exactly as the preview draws it (the real page: fonts, outline, shadow,
    // background) and puts it on the canvas's tile.
    private async Task SnapshotCanvasAsync(CanvasViewModel canvas)
    {
        var web = ObsPreview.CoreWebView2;
        if (web is null || !_viewModel.Obs.Canvases.Contains(canvas))
            return; // no preview yet, or the canvas was just deleted
        try
        {
            // Where the canvas sits in the preview (it is scaled to fit and centered).
            var json = await web.ExecuteScriptAsync(
                "JSON.stringify((r => ({ x: r.left, y: r.top, w: r.width, h: r.height }))(document.getElementById('canvas').getBoundingClientRect()))");
            using var rect = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "{}");
            double Get(string name) => rect.RootElement.TryGetProperty(name, out var v) ? v.GetDouble() : 0;
            if (Get("w") < 2 || Get("h") < 2)
                return;
            // A copy of what the preview already shows, cut to the canvas. (The browser's own screenshot of an area
            // re-lays out the page to take it, which made the preview jump for a moment.)
            using var shot = new System.IO.MemoryStream();
            await web.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, shot);
            shot.Position = 0;
            var full = System.Windows.Media.Imaging.BitmapFrame.Create(shot, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var scale = ObsPreview.ActualWidth > 0 ? full.PixelWidth / ObsPreview.ActualWidth : 1; // page px to image px
            int x = (int)Math.Max(0, Math.Floor(Get("x") * scale)), y = (int)Math.Max(0, Math.Floor(Get("y") * scale));
            int w = (int)Math.Min(full.PixelWidth - x, Math.Ceiling(Get("w") * scale)), h = (int)Math.Min(full.PixelHeight - y, Math.Ceiling(Get("h") * scale));
            if (w < 2 || h < 2)
                return;
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                new System.Windows.Media.Imaging.CroppedBitmap(full, new Int32Rect(x, y, w, h))));
            using var png = new System.IO.MemoryStream();
            encoder.Save(png);
            WhisperStt.Core.Obs.OverlayImages.SaveThumbnail(canvas.Id, png.ToArray());
            canvas.LoadThumbnail();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or System.Text.Json.JsonException or FormatException or System.IO.IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            // The tile keeps its previous picture.
        }
    }

    private async Task ShowObsPreviewAsync()
    {
        var obs = _viewModel.Obs;
        if (obs.Editing is not { } canvas)
            return;
        if (!obs.IsRunning)
        {
            ObsPreviewMessage.Text = obs.Status;
            ObsPreviewMessage.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            if (ObsPreview.CoreWebView2 is null)
            {
                // Its browser data goes with WalkTalk's other data, not next to the app.
                // Sounds may play without a click first, as they do in OBS.
                var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, ObsViewModel.PreviewDataFolder, options);
                await ObsPreview.EnsureCoreWebView2Async(environment);
                ObsPreview.CoreWebView2!.Settings.AreDefaultContextMenusEnabled = false;
                ObsPreview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                // A file dropped on the preview would open in it; templates are dropped on the rest of the dialog.
                ObsPreview.AllowExternalDrop = false;
                // A freshly loaded preview (fonts included, given a moment) is photographed for the tile.
                ObsPreview.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    _snapshotTimer.Stop();
                    _snapshotTimer.Start();
                };
            }
            ObsPreview.CoreWebView2!.Navigate(canvas.PreviewUrl);
            ObsPreviewMessage.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            ObsPreviewMessage.Text = "The preview needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11. The overlay itself still works in OBS.";
            ObsPreviewMessage.Visibility = Visibility.Visible;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowMaterial();
        ThemeManager.Changed += _ => ApplyWindowMaterial();
    }

    // Dark: translucent Mica behind the toolbar and sidebar. Light: solid sky blue, title bar included.
    private void ApplyWindowMaterial()
    {
        if (ThemeManager.Current == AppTheme.Dark && WindowMaterial.TryApplyMica(this))
        {
            Background = Brushes.Transparent;
            return;
        }

        SetResourceReference(BackgroundProperty, ThemeManager.Current == AppTheme.Light ? "ChromeBrush" : "WindowBrush");
        if (ThemeManager.Current == AppTheme.Light && TryFindResource("ChromeBrush") is SolidColorBrush chrome
            && TryFindResource("ChromeTextBrush") is SolidColorBrush text)
            WindowMaterial.ApplySolidTitleBar(this, chrome.Color, Color.FromRgb(text.Color.R, text.Color.G, text.Color.B));
    }

    private void ScrollTranscriptToEnd() =>
        Dispatcher.BeginInvoke(TranscriptScroll.ScrollToEnd, DispatcherPriority.Background);

    // Bring the app back and select the new field's name, so it can be renamed right away.
    private void OnTargetAdded(TargetViewModel target)
    {
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            if (TargetList.ItemContainerGenerator.ContainerFromItem(target) is ContentPresenter presenter &&
                presenter.ContentTemplate.FindName("NameBox", presenter) is TextBox nameBox)
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    // -- Curses templates (.cursestmp) dropped on the OBS dialog: a hint while dragging, an import on drop

    private static List<string> DroppedTemplates(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.Where(ObsViewModel.CanImport).ToList() : [];

    private void ObsDialog_DragOver(object sender, DragEventArgs e)
    {
        var any = DroppedTemplates(e).Count > 0;
        e.Effects = any ? DragDropEffects.Copy : DragDropEffects.None;
        ShowDropHint(any);
        e.Handled = true;
    }

    // Leaving a child element raises DragLeave too: hide the hint only when the pointer left the dialog itself.
    private void ObsDialog_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement dialog)
        {
            var at = e.GetPosition(dialog);
            if (at.X > 0 && at.Y > 0 && at.X < dialog.ActualWidth && at.Y < dialog.ActualHeight)
                return;
        }
        ShowDropHint(false);
    }

    // The preview is a separate browser window drawn over everything in the dialog: hidden while the hint shows.
    private void ShowDropHint(bool show)
    {
        ObsDropHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ObsPreview.Visibility = show ? Visibility.Hidden : Visibility.Visible;
    }

    private void ObsDialog_Drop(object sender, DragEventArgs e)
    {
        ShowDropHint(false);
        var files = DroppedTemplates(e);
        if (files.Count > 0)
            _viewModel.Obs.ImportTemplates(files);
        e.Handled = true;
    }

    // A click on the dimmed area around a dialog closes it; clicks inside the dialog don't.
    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
            _viewModel.CloseDialogsCommand.Execute(null);
    }

    // Reviewing a sentence puts the cursor at the end of its text, ready to fix it.
    private void ReviewBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }, DispatcherPriority.Input);
    }

    // Enter saves the new name, Escape restores the old one.
    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || e.Key is not (Key.Enter or Key.Escape))
            return;
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        if (e.Key == Key.Enter)
            binding?.UpdateSource();
        else
            binding?.UpdateTarget();
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(box), null);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownDone)
            return;

        // Let the microphone and model shut down cleanly before the window goes away.
        e.Cancel = true;
        await _viewModel.ShutdownAsync();
        _shutdownDone = true;
        // The shutdown may finish synchronously, and Close() can't be called while still inside Closing.
        _ = Dispatcher.BeginInvoke(Close);
    }
}

// Visible when the bound value's name equals the converter parameter, e.g. a step of a wizard.
// With Invert, visible when it differs (e.g. a text that isn't empty).
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString() ?? "", parameter?.ToString() ?? "", StringComparison.Ordinal) != Invert
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// For a row of radio buttons bound to one enum: checked when the value's name is the parameter, and
// checking a button sets the value to it.
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

// A color typed as "#RRGGBB" or "#AARRGGBB", for a swatch next to the box; transparent while it isn't one.
public sealed class ColorTextToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        WhisperStt.Core.Obs.OverlayStyle.TryParse(value as string, out var a, out var r, out var g, out var b)
            ? new SolidColorBrush(Color.FromArgb(a, r, g, b))
            : Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
