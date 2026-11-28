using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WhisperStt.Core;
using WhisperStt.Core.Training;
using WhisperStt.Desktop;
using WhisperStt.Desktop.Services;
using WhisperStt.Desktop.ViewModels;

// Drives the real window, off-screen, on a sandbox copy of the recordings and the voice model, and saves
// screenshots. It never reads or writes the user's own settings, sign-in, recordings or voice model.
//
// Usage: UiChecks <sandbox folder> <folder with a copy of the recordings> <screenshot folder> [mode]
//   <sandbox folder>\models must hold a copy of ggml-voice.bin and ggml-voice.json for every mode but "train".
//   (no mode)  the sentence share, Review in the transcript, the list of corrections
//   learn      the same, then teaches the corrections to the voice model (about 2 min)
//   dialogs    opens every dialog, closes them with a click outside, and opens the microphone for a moment
//   train      a full training (about 10 min)
//   worc       says the hardest sentence again, reviews it and teaches it (needs a "…orcestershire…" recording)
//   import     imports a Curses template (args[4]: the .cursestmp file), adds an avatar and plays it in the preview
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Never work on the user's own settings, sign-in, recordings or voice model.
        var sandbox = args[0];
        var voice = Path.Combine(sandbox, "voice", "en");
        if (Directory.Exists(voice))
            Directory.Delete(voice, recursive: true);
        Directory.CreateDirectory(voice);
        foreach (var file in Directory.GetFiles(args[1]))
            File.Copy(file, Path.Combine(voice, Path.GetFileName(file)));
        StateStore.FilePath = Path.Combine(sandbox, "desktop-state.json");
        TwitchTokenStore.FilePath = Path.Combine(sandbox, "twitch-sign-in.bin");
        VoiceDataset.Root = Path.Combine(sandbox, "voice");
        PersonalModel.Directory = Path.Combine(sandbox, "models");
        WhisperStt.Core.Obs.GoogleFonts.Directory = Path.Combine(sandbox, "fonts");
        WhisperStt.Core.Obs.OverlayImages.Directory = Path.Combine(sandbox, "overlay-images");
        ObsViewModel.PreviewDataFolder = Path.Combine(sandbox, "webview");
        VrChatChatbox.Endpoint = new IPEndPoint(IPAddress.Loopback, 9123);
        TwitchApi.AuthBase = "http://127.0.0.1:9/oauth2";
        TwitchApi.ApiBase = "http://127.0.0.1:9/helix";
        File.Delete(StateStore.FilePath);
        // The OBS check runs on its own port, so a WalkTalk already serving the overlay on 7480 is left alone.
        if (args.Length > 3 && args[3] is "obs" or "picker")
            File.WriteAllText(StateStore.FilePath, "{ \"ObsPort\": 7491 }");
        if (args.Length > 3 && args[3] == "import")
            File.WriteAllText(StateStore.FilePath, "{ \"ObsPort\": 7493 }");
        File.Delete(PersonalModel.CandidatePath);
        // The stream check: a stand-in for Twitch's API that says live or offline, a made-up sign-in, and an
        // OBS text template switched on (on its own port).
        var streamCheck = args.Length > 3 && args[3] == "stream";
        var streamLive = false;
        var streamAsks = 0;
        var streamBroken = false;
        if (streamCheck)
        {
            File.WriteAllText(StateStore.FilePath, "{ \"ObsPort\": 7492, \"ObsCanvases\": [ { \"Id\": \"s1\", \"IsOn\": true } ] }");
            TwitchTokenStore.Save(new TwitchSession("access", "refresh", DateTime.UtcNow.AddHours(4), "4242", "tester"));
            TwitchApi.ApiBase = "http://127.0.0.1:9131/helix";
            var fake = new HttpListener();
            fake.Prefixes.Add("http://127.0.0.1:9131/helix/");
            fake.Start();
            _ = Task.Run(async () =>
            {
                while (fake.IsListening)
                {
                    var context = await fake.GetContextAsync();
                    streamAsks++;
                    var ok = context.Request.Url!.AbsolutePath == "/helix/streams" && context.Request.QueryString["user_id"] == "4242" &&
                             context.Request.Headers["Authorization"] == "Bearer access";
                    var body = System.Text.Encoding.UTF8.GetBytes(!ok ? "{\"message\":\"wrong request\"}"
                        : streamLive ? "{\"data\":[{\"id\":\"1\",\"user_id\":\"4242\",\"type\":\"live\"}]}" : "{\"data\":[]}");
                    if (streamBroken) { ok = false; body = System.Text.Encoding.UTF8.GetBytes("{\"message\":\"server error\"}"); }
                    context.Response.StatusCode = streamBroken ? 500 : ok ? 200 : 400;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }
        Directory.CreateDirectory(args[2]);
        var learn = args.Length > 3 && args[3] == "learn";
        Console.WriteLine($"voice model in the sandbox: {PersonalModel.Exists}, info: {PersonalModel.Info is not null}, files known: {PersonalModel.Info?.Files?.Count}");

        var app = new App();
        app.InitializeComponent();
        ThemeManager.Apply(AppTheme.Dark);
        var window = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        var vm = (MainViewModel)window.DataContext;
        var vt = vm.VoiceTraining;
        window.Show();

        window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                string Shot(string name) { Capture(window, Path.Combine(args[2], name)); return name; }
                if (streamCheck)
                {
                    var tw = vm.Twitch;
                    Console.WriteLine($"twitch: {tw.State}, obs on {tw.ObsOn}, option {tw.StopOnStreamEnd}, watches {tw.WatchesStream}");
                    tw.StopOnStreamEnd = true;
                    Console.WriteLine($"option on -> watches {tw.WatchesStream} | {tw.StreamEndHint}");
                    await Task.Delay(500);
                    Shot("s1-twitch-on.png");

                    // As if listening: the stream check runs on its own (the microphone and model aren't needed here).
                    var listening = typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsListening))!;
                    var check = typeof(MainViewModel).GetMethod("CheckStreamAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    listening.SetValue(vm, true);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, "listening");
                    foreach (var (live, label) in new[] { (false, "not live yet"), (true, "live"), (false, "offline 1"), (true, "back live"), (false, "offline 1"), (false, "offline 2") })
                    {
                        streamLive = live;
                        await (Task)check.Invoke(vm, null)!;
                        Console.WriteLine($"stream {label,-12} -> status \"{vm.StatusText}\" | card \"{tw.StreamStatus}\"");
                    }
                    Console.WriteLine($"Twitch was asked {streamAsks} times");
                    listening.SetValue(vm, true);
                    streamBroken = true;
                    await (Task)check.Invoke(vm, null)!;
                    Console.WriteLine($"Twitch answers an error -> card \"{tw.StreamStatus}\"");
                    streamBroken = false;
                    await Task.Delay(300);
                    Shot("s1b-twitch-status.png");

                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.StatusText))!.SetValue(vm, "listening");
                    var asked = streamAsks;
                    vm.Obs.Canvases[0].IsOn = false;
                    Console.WriteLine($"OBS off -> obs on {tw.ObsOn}, watches {tw.WatchesStream}, switch shows {tw.StreamSwitch}, choice kept {tw.StopOnStreamEnd} | {tw.StreamEndHint}");
                    streamLive = false;
                    await (Task)check.Invoke(vm, null)!;
                    Console.WriteLine($"check with OBS off: Twitch asked {streamAsks - asked} more times, status \"{vm.StatusText}\"");
                    await Task.Delay(400);
                    Shot("s2-twitch-obs-off.png");
                    vm.Obs.Canvases[0].IsOn = true;
                    Console.WriteLine($"OBS back on -> switch shows {tw.StreamSwitch}, watches {tw.WatchesStream}");
                    listening.SetValue(vm, false);
                    window.Closed += (_, _) => app.Shutdown();
                    window.Close();
                    return;
                }
                if (args.Length > 3 && args[3] == "picker")
                {
                    // ---- the color picker: the swatch shows the color; the picker follows the color; dragging works
                    var field = new WhisperStt.Desktop.Controls.ColorField { Width = 260, Color = "#CC9146FF" };
                    var host = new Window
                    {
                        Content = new Border { Padding = new Thickness(16), Child = field, Background = (Brush)app.FindResource("DialogBrush") },
                        SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual
                    };
                    host.Show();
                    await Task.Delay(400);
                    var swatch = (System.Windows.Controls.Primitives.ToggleButton)field.FindName("SwatchButton");
                    swatch.IsChecked = true;
                    await Task.Delay(500);
                    var popup = (System.Windows.Controls.Primitives.Popup)field.FindName("Picker");
                    void Render(Visual visual, double w, double h, string name)
                    {
                        var dpi = VisualTreeHelper.GetDpi(visual);
                        var bmp = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                        bmp.Render(visual);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using var file = File.Create(Path.Combine(args[2], name));
                        enc.Save(file);
                    }
                    var swatchColor = ((SolidColorBrush)field.SwatchBrush).Color;
                    Console.WriteLine($"popup open {popup.IsOpen} | swatch brush {swatchColor} for {field.Color}");
                    var child = (FrameworkElement)popup.Child;
                    Render(host.Content as Visual ?? host, ((FrameworkElement)host.Content).ActualWidth, ((FrameworkElement)host.Content).ActualHeight, "p1-field.png");
                    Render(child, child.ActualWidth, child.ActualHeight, "p2-picker.png");
                    field.Color = "#FF0000";
                    await Task.Delay(200);
                    Console.WriteLine($"set red -> boxes {((TextBox)field.FindName("RedBox")).Text},{((TextBox)field.FindName("GreenBox")).Text},{((TextBox)field.FindName("BlueBox")).Text},{((TextBox)field.FindName("AlphaBox")).Text}");
                    ((TextBox)field.FindName("AlphaBox")).Text = "0.5";
                    await Task.Delay(200);
                    Console.WriteLine($"opacity box 0.5 -> {field.Color}");
                    field.Color = "#FFE45C";
                    await Task.Delay(300);
                    Render(child, child.ActualWidth, child.ActualHeight, "p3-picker-yellow.png");
                    Render(host.Content as Visual ?? host, ((FrameworkElement)host.Content).ActualWidth, ((FrameworkElement)host.Content).ActualHeight, "p4-field-yellow.png");

                    // ---- a font from a file
                    var obsVm = vm.Obs;
                    obsVm.OpenCommand.Execute(null);
                    obsVm.AddCommand.Execute(null);
                    var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "comic.ttf");
                    var family = obsVm.AddFontFile(source);
                    Console.WriteLine($"font file {Path.GetFileName(source)} -> family \"{family}\", canvas font \"{obsVm.Editing?.FontFamily}\", first in list: {obsVm.Families.FirstOrDefault()} | {obsVm.SearchStatus}");
                    using var http = new System.Net.Http.HttpClient();
                    var css = await http.GetStringAsync($"http://127.0.0.1:{obsVm.PortText}/fonts.css");
                    var cssLine = css.Split('\n').FirstOrDefault(l => l.Contains(family ?? "?")) ?? "(missing)";
                    Console.WriteLine($"fonts.css: {cssLine.Trim()}");
                    var url = System.Text.RegularExpressions.Regex.Match(cssLine, @"url\(([^)]+)\)").Groups[1].Value;
                    var served = await http.GetAsync($"http://127.0.0.1:{obsVm.PortText}{url}");
                    Console.WriteLine($"font file served: {served.StatusCode} {served.Content.Headers.ContentType} {served.Content.Headers.ContentLength} bytes");
                    host.Close();
                    obsVm.IsOpen = false;
                    window.Closed += (_, _) => app.Shutdown();
                    window.Close();
                    return;
                }
                if (args.Length > 4 && args[3] == "import")
                {
                    // ---- a Curses template imported as a text template, then an avatar added to it and played
                    var obs = vm.Obs;
                    obs.OpenCommand.Execute(null);
                    await Task.Delay(700);
                    Shot("i1-list.png");
                    var drop = (FrameworkElement)window.FindName("ObsDropHint");
                    drop.Visibility = Visibility.Visible;
                    await Task.Delay(200);
                    Shot("i1b-drop-hint.png");
                    drop.Visibility = Visibility.Collapsed;
                    Console.WriteLine($"can import: .cursestmp {ObsViewModel.CanImport(args[4])}, .png {ObsViewModel.CanImport("x.png")}");
                    Console.WriteLine($"not a template: {obs.ImportTemplates([Path.Combine(sandbox, "beep.cursestmp")])} added | {obs.Status}");
                    File.WriteAllBytes(Path.Combine(sandbox, "junk.cursestmp"), [1, 2, 3, 4, 5, 200, 7]);
                    Console.WriteLine($"junk file: {obs.ImportTemplates([Path.Combine(sandbox, "junk.cursestmp")])} added | {obs.Status}");

                    var added = obs.ImportTemplates([args[4]]);
                    var t = obs.Editing!;
                    var s = t.Style;
                    Console.WriteLine($"imported {added}: \"{t.Name}\" {s.Width}x{s.Height} | status: {obs.Status}");
                    Console.WriteLine($"  text: font \"{s.FontFamily}\" (in list {obs.Families.Contains(s.FontFamily)}) {s.FontSize}px w{s.FontWeight} {s.Color} align {s.HorizontalAlign}/{s.VerticalAlign} auto {s.AutoWidth}/{s.AutoHeight} pad {s.PaddingX}");
                    Console.WriteLine($"  box: {s.Background} {s.ImageFit} slice {s.SliceSize}x{s.SliceScale} picture \"{s.BackgroundImage}\" border {s.Border}");
                    Console.WriteLine($"  animation: {s.Animate} {s.CharacterInterval}/{s.WordPause}/{s.SentenceInterval} ms, clear after {s.HideAfterSeconds}s in {s.ClearDuration}ms");
                    Console.WriteLine($"  typing sound: {s.TypingSound.Kind} \"{s.TypingSound.File}\" vol {s.TypingSound.Volume}");
                    var preview = (Microsoft.Web.WebView2.Wpf.WebView2)window.FindName("ObsPreview");
                    for (int i = 0; i < 40 && preview.CoreWebView2 is null; i++)
                        await Task.Delay(250);
                    await Task.Delay(2000);
                    Shot("i2-imported.png");

                    // an avatar: two faces drawn here, mouth closed and open
                    string Face(string name, bool open)
                    {
                        var path = Path.Combine(sandbox, name);
                        var bmp = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
                        var dv = new DrawingVisual();
                        using (var dc = dv.RenderOpen())
                        {
                            dc.DrawEllipse(Brushes.Gold, new Pen(Brushes.Black, 3), new Point(48, 48), 44, 44);
                            dc.DrawEllipse(Brushes.Black, null, new Point(33, 38), 5, 7);
                            dc.DrawEllipse(Brushes.Black, null, new Point(63, 38), 5, 7);
                            if (open) dc.DrawEllipse(Brushes.DarkRed, new Pen(Brushes.Black, 2), new Point(48, 68), 14, 11);
                            else dc.DrawLine(new Pen(Brushes.Black, 3), new Point(34, 68), new Point(62, 68));
                        }
                        bmp.Render(dv);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using (var file = File.Create(path)) enc.Save(file);
                        return path;
                    }
                    Console.WriteLine($"avatar before: on {t.Avatar}");
                    Console.WriteLine($"base picture set {obs.SetAvatarImage(t, "base", args.Length > 6 ? args[5] : Face("closed.png", false))} -> on {t.Avatar} \"{s.AvatarImage}\" ({t.AvatarImageText})");
                    Console.WriteLine($"active picture set {obs.SetAvatarImage(t, "active", args.Length > 6 ? args[6] : Face("open.png", true))} -> \"{s.AvatarActiveImage}\" ({t.AvatarActiveImageText})");
                    Console.WriteLine($"not a picture: {obs.SetAvatarImage(t, "base", Path.Combine(sandbox, "junk.cursestmp"))} | {obs.Status}");
                    t.AvatarX = 400; t.AvatarY = 20; t.AvatarWidth = 90; t.AvatarHeight = 90;
                    t.AvatarOpacity = 0.8; t.AvatarActiveOpacity = 1; t.AvatarClearDelay = 3; t.AvatarClearDuration = 300; t.AvatarTransition = 120;
                    t.AvatarWidth = 0; // clamped
                    Console.WriteLine($"avatar: {t.AvatarX},{t.AvatarY} {t.AvatarWidth}x{t.AvatarHeight} opacity {t.AvatarOpacityText}/{t.AvatarActiveOpacityText} clear {t.AvatarClearDelayText} in {t.AvatarClearDurationText} {t.AvatarTransitionText}");
                    t.AvatarWidth = 90;
                    using var http = new System.Net.Http.HttpClient();
                    var port = obs.PortText;
                    foreach (var url in new[] { $"/avatar/{t.Id}/base", $"/avatar/{t.Id}/active", $"/avatar/{t.Id}/other", $"/bg/{t.Id}", $"/sound/{t.Id}/typing" })
                    {
                        var r = await http.GetAsync($"http://127.0.0.1:{port}{url}");
                        Console.WriteLine($"  {url}: {(int)r.StatusCode} {r.Content.Headers.ContentType} {r.Content.Headers.ContentLength}");
                    }

                    // open the Avatar section so it shows in the screenshot
                    Expander? Section(string header)
                    {
                        var stack = new Stack<DependencyObject>([window]);
                        while (stack.Count > 0)
                        {
                            var node = stack.Pop();
                            if (node is Expander ex && Equals(ex.Header, header)) return ex;
                            for (int c = 0; c < VisualTreeHelper.GetChildrenCount(node); c++) stack.Push(VisualTreeHelper.GetChild(node, c));
                        }
                        return null;
                    }
                    var avatarSection = Section("Avatar");
                    if (avatarSection is not null) { avatarSection.IsExpanded = true; await Task.Delay(300); avatarSection.BringIntoView(); }
                    await Task.Delay(1500);
                    async Task<string> Opacities() => await preview.CoreWebView2.ExecuteScriptAsync(
                        "JSON.stringify([getComputedStyle(avatarBase).opacity, getComputedStyle(avatarActive).opacity, getComputedStyle(avatar).display, getComputedStyle(box).borderImageSource.slice(0, 30), box.offsetWidth + 'x' + box.offsetHeight])");
                    Console.WriteLine($"page before talking (base, active, display, frame, box): {await Opacities()}");
                    Shot("i3-avatar.png");
                    obs.PlayDemoCommand.Execute(null);
                    await Task.Delay(500);
                    // with a turn every 120 ms, sampling every 40 ms should see both pictures several times
                    var turns = new List<string>();
                    for (int i = 0; i < 25; i++)
                    {
                        var o = await preview.CoreWebView2.ExecuteScriptAsync(
                            "getComputedStyle(avatarActive).opacity === '1' ? 'A' : getComputedStyle(avatarBase).opacity === '0.8' ? 'b' : '?'");
                        turns.Add(o.Trim('"'));
                        await Task.Delay(40);
                    }
                    Console.WriteLine($"page while writing (A = active, b = base, every 40 ms): {string.Concat(turns)}");
                    Shot("i4-talking.png");
                    for (int i = 0; i < 80; i++)
                    {
                        await Task.Delay(250);
                        var o = await Opacities();
                        if (o.StartsWith("\"[\\\"0.8")) { Console.WriteLine($"page after writing (+{(i + 1) * 250} ms): {o}"); break; }
                    }
                    Shot("i5-done.png");

                    // ---- glitch probe: frames of the real window (the preview included), while idle, while a setting
                    // changes quickly, and when the tile picture is taken; then the clear race, run in the page itself.
                    var probe = Path.Combine(args[2], "probe");
                    Directory.CreateDirectory(probe);
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    var previewBox = preview.TransformToAncestor(window).TransformBounds(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight));
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var region = new Int32Rect((int)(previewBox.X * dpi.DpiScaleX), (int)(previewBox.Y * dpi.DpiScaleY),
                        (int)(previewBox.Width * dpi.DpiScaleX), (int)(previewBox.Height * dpi.DpiScaleY));
                    var frames = new List<(string Label, long Ms, string Hash)>();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    int frameNo = 0;
                    void Frame(string label)
                    {
                        var shot = Native.PrintWindow(hwnd);
                        if (shot is null) return;
                        var crop = new CroppedBitmap(shot, new Int32Rect(region.X, region.Y,
                            Math.Min(region.Width, shot.PixelWidth - region.X), Math.Min(region.Height, shot.PixelHeight - region.Y)));
                        var pixels = new byte[crop.PixelWidth * crop.PixelHeight * 4];
                        crop.CopyPixels(pixels, crop.PixelWidth * 4, 0);
                        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(pixels))[..8];
                        frames.Add((label, clock.ElapsedMilliseconds, hash));
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(crop));
                        using var file = File.Create(Path.Combine(probe, $"{frameNo++:000}-{label}.png"));
                        enc.Save(file);
                    }
                    // scripts that take a while: started, then asked for their answer until it is there
                    async Task<string> RunAsync(string body)
                    {
                        await preview.CoreWebView2.ExecuteScriptAsync("window.__probe = undefined; (async () => { " + body + " })().then(r => window.__probe = String(r), e => window.__probe = 'error ' + e)");
                        for (int i = 0; i < 400; i++)
                        {
                            await Task.Delay(50);
                            var got = await preview.CoreWebView2.ExecuteScriptAsync("window.__probe === undefined ? null : window.__probe");
                            if (got != "null") return got;
                        }
                        return "(no answer)";
                    }
                    Console.WriteLine("after the demo (sentences, needed height, box height): " + await RunAsync("while (working) await new Promise(r => setTimeout(r, 100)); return box.children.length + ' ' + box.scrollHeight + ' ' + box.clientHeight;"));
                    await Task.Delay(1500); // the tile picture after the demo
                    if (args.Length > 6)
                    {
                        // as in the recording: 800 wide, the avatar inside the frame on the left, then the editor opened afresh
                        t.CanvasWidth = 800; t.AvatarX = 20; t.AvatarY = 20; t.AvatarWidth = 110; t.AvatarHeight = 110;
                        obs.BackCommand.Execute(null); await Task.Delay(500); obs.Editing = t;
                        for (int i = 0; i < 50; i++) { Frame("enter"); await Task.Delay(60); }
                    }
                    for (int i = 0; i < 10; i++) { Frame("idle"); await Task.Delay(100); }
                    for (int i = 0; i < 16; i++) { t.FontSize = 25 + i; Frame("drag"); await Task.Delay(40); }
                    // the tile picture is taken 0.9 s after the last change: watch that moment closely
                    for (int i = 0; i < 30; i++) { Frame("after"); await Task.Delay(50); }
                    t.FontSize = 25;
                    string Summary(string label)
                    {
                        var run = frames.Where(f => f.Label == label).ToList();
                        return $"{label}: {run.Count} frames, {run.Select(f => f.Hash).Distinct().Count()} different: " +
                               string.Join(" ", run.Select(f => f.Hash[..3]));
                    }
                    Console.WriteLine($"probe frames ({region.Width}x{region.Height}), first idle frame blank: {frames.FirstOrDefault().Hash}");
                    foreach (var label in new[] { "enter", "idle", "drag", "after" }) Console.WriteLine("  " + Summary(label));

                    // the race: a sentence that arrives while the box fades out must stay on screen
                    var race = await RunAsync(@"
                        const wait = ms => new Promise(r => setTimeout(r, ms));
                        receive('First sentence.');
                        while (working) await wait(50);
                        clearBox(true);
                        await wait(60);
                        receive('Second sentence.');
                        await wait((style.clearDuration || 0) + 400);
                        while (working) await wait(50);
                        return 'class=[' + box.className + '] text=[' + box.textContent + ']';");
                    Console.WriteLine($"race (sentence during the fade): {race}");
                    // clear delay: hidden 3 s after the last sentence, back with the next
                    var avatarClear = await RunAsync(@"
                        const wait = ms => new Promise(r => setTimeout(r, ms));
                        const o = () => getComputedStyle(avatar).opacity;
                        const shown = o();
                        await wait(3600);
                        const later = o();
                        receive('Back again.');
                        await wait(100);
                        return 'after the sentence ' + shown + ', 3.6 s later ' + later + ', with the next ' + o();");
                    Console.WriteLine($"avatar clear: {avatarClear}");
                    // no overlap: the text's left edge against the avatar's right edge (the avatar is on the right here)
                    var overlap = await preview.CoreWebView2.ExecuteScriptAsync(
                        "(() => { const a = avatar.getBoundingClientRect(), r = document.createRange(); r.selectNodeContents(box); const t = r.getBoundingClientRect();" +
                        " return 'text right ' + Math.round(t.right) + ' avatar left ' + Math.round(a.left) + ' box padding ' + getComputedStyle(box).padding; })()");
                    Console.WriteLine($"overlap (avatar right): {overlap}");
                    t.AvatarX = 20; t.AvatarY = 40; await Task.Delay(400);
                    overlap = await preview.CoreWebView2.ExecuteScriptAsync(
                        "(() => { const a = avatar.getBoundingClientRect(), r = document.createRange(); r.selectNodeContents(box); const t = r.getBoundingClientRect();" +
                        " return 'text left ' + Math.round(t.left) + ' avatar right ' + Math.round(a.right) + ' box padding ' + getComputedStyle(box).padding; })()");
                    Console.WriteLine($"overlap (avatar left, inside the frame): {overlap}");
                    t.AutoWidth = true; await Task.Delay(400);
                    overlap = await preview.CoreWebView2.ExecuteScriptAsync(
                        "(() => { const a = avatar.getBoundingClientRect(), b = box.getBoundingClientRect();" +
                        " return 'box left ' + Math.round(b.left) + ' avatar right ' + Math.round(a.right) + ' canvas padding ' + getComputedStyle(document.getElementById(\"canvas\")).padding; })()");
                    Console.WriteLine($"overlap (auto width: the box moves aside): {overlap}");
                    await preview.CoreWebView2.ExecuteScriptAsync("window.walktalkDemo()");
                    await Task.Delay(1500);
                    Frame("left-avatar");
                    t.AutoWidth = false;
                    await Task.Delay(400);
                    Frame("left-avatar-full");

                    // saved with the settings, and the files go away with the text template
                    obs.BackCommand.Execute(null);
                    await Task.Delay(1500);
                    Shot("i6-list.png");
                    var savedImport = StateStore.Load().ObsCanvases.FirstOrDefault(c => c.Id == t.Id);
                    Console.WriteLine($"saved: avatar {savedImport?.Avatar} \"{savedImport?.AvatarImage}\" \"{savedImport?.AvatarActiveImage}\" {savedImport?.AvatarX},{savedImport?.AvatarY} fit {savedImport?.ImageFit}");
                    var files = Directory.GetFiles(WhisperStt.Core.Obs.OverlayImages.Directory, t.Id + "*").Select(Path.GetFileName).ToList();
                    Console.WriteLine($"files: {string.Join(", ", files)}");
                    obs.ImportTemplates([args[4]]);
                    Console.WriteLine($"imported again: \"{obs.Editing?.Name}\", templates {obs.Canvases.Count}");
                    obs.Editing = t;
                    obs.DeleteCommand.Execute(null);
                    obs.DeleteCommand.Execute(null);
                    Console.WriteLine($"after delete: files left {Directory.GetFiles(WhisperStt.Core.Obs.OverlayImages.Directory, t.Id + "*").Length}, templates {obs.Canvases.Count}");
                    obs.IsOpen = false;
                    window.Closed += (_, _) => app.Shutdown();
                    window.Close();
                    return;
                }
                if (args.Length > 3 && args[3] == "obs")
                {
                    // ---- the OBS overlay: two canvases, styled differently; fonts, picture, port; what each page gets
                    var obs = vm.Obs;
                    obs.OpenCommand.Execute(null);
                    await Task.Delay(600);
                    Console.WriteLine($"dialog open {obs.IsOpen}, server running {obs.IsRunning}, canvases {obs.Canvases.Count} | {obs.Status}");
                    obs.AddCommand.Execute(null);
                    var canvas1 = obs.Editing!;
                    canvas1.CanvasWidth = 1280; canvas1.CanvasHeight = 300;
                    canvas1.FontSize = 64; canvas1.TextColor = "#FFE45C"; canvas1.TextColor = "not a color";
                    canvas1.Case = WhisperStt.Core.Obs.TextCase.Uppercase;
                    canvas1.FontWeight = 900;
                    canvas1.ShadowX = 4; canvas1.ShadowY = 4; canvas1.ShadowBlur = 8; canvas1.ShadowColor = "#AA000000";
                    canvas1.Outline = true; canvas1.OutlineWidth = 4; canvas1.OutlineColor = "#1A1A40";
                    canvas1.Lines = 2; canvas1.HideAfter = 0;
                    canvas1.AutoWidth = true; canvas1.AutoHeight = true; canvas1.PaddingX = 28; canvas1.PaddingY = 10;
                    canvas1.Background = WhisperStt.Core.Obs.BackgroundType.Gradient;
                    canvas1.BackgroundColor = "#CC9146FF"; canvas1.BackgroundColor2 = "#CC00F5D4"; canvas1.GradientAngle = 90;
                    canvas1.Border = true; canvas1.BorderWidth = 3; canvas1.BorderColor = "#FFFFFF"; canvas1.BorderRadius = 24;
                    canvas1.Animate = true; canvas1.RevealUnit = WhisperStt.Core.Obs.RevealUnit.Character; canvas1.RevealEffect = WhisperStt.Core.Obs.RevealEffect.Pop;
                    canvas1.CharacterInterval = 60; canvas1.WordPause = 120; canvas1.SentenceInterval = 800;
                    canvas1.ReplacePrevious = true; canvas1.ClearAnimation = WhisperStt.Core.Obs.ClearAnimation.SlideUp; canvas1.ClearDuration = 500;
                    canvas1.Sounds[0].Kind = "typewriter"; canvas1.Sounds[0].Volume = 0.4;
                    canvas1.Sounds[1].Kind = "pop";
                    canvas1.Sounds[3].Kind = "whoosh-out";
                    // a sound file of our own for "show": a short beep written as a WAV
                    var wavPath = Path.Combine(sandbox, "beep.wav");
                    {
                        const int rate = 22050; var samples = rate / 5;
                        using var wav = new BinaryWriter(File.Create(wavPath));
                        wav.Write("RIFF"u8); wav.Write(36 + samples * 2); wav.Write("WAVEfmt "u8); wav.Write(16); wav.Write((short)1); wav.Write((short)1);
                        wav.Write(rate); wav.Write(rate * 2); wav.Write((short)2); wav.Write((short)16); wav.Write("data"u8); wav.Write(samples * 2);
                        for (int i = 0; i < samples; i++) wav.Write((short)(Math.Sin(2 * Math.PI * 880 * i / rate) * 8000 * (1 - (double)i / samples)));
                    }
                    Console.WriteLine($"sound file set: {obs.SetSoundFile(canvas1.Sounds[2], wavPath)} -> {canvas1.Sounds[2].Kind} {canvas1.Style.ShowSound.File} ({canvas1.Sounds[2].FileText})");
                    Console.WriteLine($"canvas 1 \"{canvas1.Name}\" {canvas1.Url} | color after a bad value {canvas1.TextColor}");

                    obs.FontQuery = "bang";
                    for (int i = 0; i < 60 && obs.FontResults.Count == 0 && !obs.SearchStatus.StartsWith("Couldn't") && !obs.SearchStatus.StartsWith("No "); i++)
                        await Task.Delay(500);
                    var bangers = obs.FontResults.FirstOrDefault(r => r.Family == "Bangers");
                    if (bangers is not null)
                    {
                        obs.FontActionCommand.Execute(bangers);
                        for (int i = 0; i < 120 && !bangers.IsInstalled && !obs.SearchStatus.StartsWith("Couldn't"); i++)
                            await Task.Delay(500);
                    }
                    obs.FontQuery = "Inter";
                    for (int i = 0; i < 20 && !obs.FontResults.Any(r => r.Family == "Inter"); i++)
                        await Task.Delay(300);
                    var inter = obs.FontResults.FirstOrDefault(r => r.Family == "Inter");
                    Console.WriteLine($"fonts: Bangers installed {bangers?.IsInstalled}, canvas 1 font \"{canvas1.FontFamily}\"");
                    var preview = (Microsoft.Web.WebView2.Wpf.WebView2)window.FindName("ObsPreview");
                    for (int i = 0; i < 40 && preview.CoreWebView2 is null; i++)
                        await Task.Delay(250);
                    Console.WriteLine($"preview: browser ready {preview.CoreWebView2 is not null}, showing {preview.Source}");
                    await Task.Delay(1500);
                    Shot("o1-editor.png");

                    // alignment: icon buttons instead of lists
                    System.Windows.Controls.RadioButton? AlignButton(string name)
                    {
                        var stack = new Stack<DependencyObject>([window]);
                        while (stack.Count > 0)
                        {
                            var node = stack.Pop();
                            if (node is System.Windows.Controls.RadioButton rb && System.Windows.Automation.AutomationProperties.GetName(rb) == name)
                                return rb;
                            for (int c = 0; c < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); c++)
                                stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(node, c));
                        }
                        return null;
                    }
                    var alignRight = AlignButton("Align right");
                    var alignTop = AlignButton("Align top");
                    Console.WriteLine($"align: before {canvas1.HorizontalAlign}/{canvas1.VerticalAlign}, center checked {AlignButton("Align center")?.IsChecked}, bottom checked {AlignButton("Align bottom")?.IsChecked}");
                    alignRight?.BringIntoView();
                    if (alignRight is not null) alignRight.IsChecked = true;
                    if (alignTop is not null) alignTop.IsChecked = true;
                    await Task.Delay(400);
                    Console.WriteLine($"align: after clicks {canvas1.HorizontalAlign}/{canvas1.VerticalAlign}, center checked {AlignButton("Align center")?.IsChecked}");
                    Shot("o1b-align.png");
                    canvas1.HorizontalAlign = WhisperStt.Core.Obs.HorizontalAlign.Center;
                    Console.WriteLine($"align: set from code -> center checked {AlignButton("Align center")?.IsChecked}, right checked {alignRight?.IsChecked}, top checked {alignTop?.IsChecked}");
                    canvas1.VerticalAlign = WhisperStt.Core.Obs.VerticalAlign.Bottom;
                    var caseBefore = canvas1.Case;
                    var lower = AlignButton("Lowercase");
                    Console.WriteLine($"case: before {caseBefore}, uppercase checked {AlignButton("Uppercase")?.IsChecked}");
                    if (lower is not null) lower.IsChecked = true;
                    await Task.Delay(300);
                    Console.WriteLine($"case: after click {canvas1.Case}, uppercase checked {AlignButton("Uppercase")?.IsChecked}");
                    Shot("o1c-case.png");
                    canvas1.Case = caseBefore;

                    // second canvas: a plain one with a picture background, from the + tile
                    obs.BackCommand.Execute(null);
                    obs.AddCommand.Execute(null);
                    var canvas2 = obs.Editing!;
                    canvas2.Name = "Chat box";
                    canvas2.CanvasWidth = 600; canvas2.CanvasHeight = 600;
                    canvas2.HorizontalAlign = WhisperStt.Core.Obs.HorizontalAlign.Left;
                    canvas2.VerticalAlign = WhisperStt.Core.Obs.VerticalAlign.Top;
                    canvas2.Lines = 4;
                    if (inter is not null)
                    {
                        obs.FontActionCommand.Execute(inter);
                        for (int i = 0; i < 120 && !inter.IsInstalled && !obs.SearchStatus.StartsWith("Couldn't"); i++)
                            await Task.Delay(500);
                    }
                    canvas2.FontWeight = 300;
                    var picture = Path.Combine(sandbox, "picture.png");
                    var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
                    var drawing = new DrawingVisual();
                    using (var dc = drawing.RenderOpen())
                        dc.DrawRectangle(new LinearGradientBrush(Colors.DarkSlateBlue, Colors.DarkOrange, 45), null, new Rect(0, 0, 64, 64));
                    bitmap.Render(drawing);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(picture))
                        png.Save(file);
                    canvas2.Background = WhisperStt.Core.Obs.BackgroundType.Image;
                    canvas2.SetImage(WhisperStt.Core.Obs.OverlayImages.Import(canvas2.Id, picture));
                    Console.WriteLine($"canvas 2 \"{canvas2.Name}\" font \"{canvas2.FontFamily}\" weight {canvas2.FontWeight}, picture \"{canvas2.Style.BackgroundImage}\" ({canvas2.ImageText})");
                    await Task.Delay(800);
                    await Task.Delay(1500);
                    Shot("o2-editor-picture.png");
                    obs.BackCommand.Execute(null);
                    for (int i = 0; i < 40 && !(canvas1.HasThumbnail && canvas2.HasThumbnail); i++) await Task.Delay(250);
                    Console.WriteLine($"tile pictures: canvas 1 {canvas1.HasThumbnail}, canvas 2 {canvas2.HasThumbnail}");
                    Shot("o3-canvases.png");

                    // what each OBS page receives
                    using var http = new System.Net.Http.HttpClient();
                    async Task<List<string>> Listen(string id, Func<Task> act)
                    {
                        using var stream = await http.GetStreamAsync($"http://127.0.0.1:{obs.PortText}/events?c={id}");
                        using var reader = new StreamReader(stream);
                        var got = new List<string>();
                        var reading = Task.Run(async () =>
                        {
                            string? line;
                            while ((line = await reader.ReadLineAsync()) is not null)
                                if (line.StartsWith("event:") || line.StartsWith("data:"))
                                    lock (got) got.Add(line);
                        });
                        await Task.Delay(300);
                        await act();
                        await Task.Delay(500);
                        lock (got) return [.. got];
                    }
                    var onTranscribed = typeof(MainViewModel).GetMethod("OnTranscribed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    void SayIt(string text) => onTranscribed.Invoke(vm, [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), text, 0.9f)]);
                    var page = await http.GetStringAsync(canvas1.Url);
                    Console.WriteLine($"page for canvas 1: {page.Length} chars | picture served: {(await http.GetAsync($"http://127.0.0.1:{obs.PortText}/bg/{canvas2.Id}")).StatusCode}");
                    canvas2.IsOn = false;
                    var toFirst = await Listen(canvas1.Id, () => { SayIt("Hello chat, welcome to the stream!"); return Task.CompletedTask; });
                    var toSecond = await Listen(canvas2.Id, () => { SayIt("Canvas two is off, so nothing here."); return Task.CompletedTask; });
                    Console.WriteLine("canvas 1 got:");
                    foreach (var e in toFirst) Console.WriteLine($"   {(e.Length > 150 ? e[..150] + "…" : e)}");
                    Console.WriteLine($"canvas 2 (off) got lines: {toSecond.Count(e => e == "event: line")}");
                    canvas2.IsOn = true;
                    SayIt("Today we try two canvases.");
                    SayIt("The second one shows four sentences.");

                    // port change
                    obs.PortText = "7495";
                    obs.ApplyPortCommand.Execute(null);
                    await Task.Delay(300);
                    Console.WriteLine($"after the port change: {canvas1.Url} | running {obs.IsRunning} | {obs.Status}");
                    var moved = await http.GetAsync(canvas1.Url);
                    Console.WriteLine($"page on the new port: {moved.StatusCode}");
                    SayIt("Hello chat, welcome to the stream!");
                    SayIt("Today we try two canvases.");

                    // deleting asks twice
                    obs.AddCommand.Execute(null);
                    obs.DeleteCommand.Execute(null);
                    var asked = obs.DeleteText;
                    obs.DeleteCommand.Execute(null);
                    Console.WriteLine($"delete: canvas1 press \"{asked}\", canvases now {obs.Canvases.Count}, editing {obs.IsEditing}");
                    var savedState = StateStore.Load();
                    Console.WriteLine($"saved: {savedState.ObsCanvases.Count} canvases on port {savedState.ObsPort}: {string.Join(", ", savedState.ObsCanvases.Select(c => $"{c.Name} {c.Width}x{c.Height} on={c.IsOn}"))}");
                    Console.WriteLine($"KEEP-OPEN: 45 s at {canvas1.Url} and {canvas2.Url}");
                    await Task.Delay(45_000);
                    obs.IsOpen = false;
                    foreach (var c in obs.Canvases) c.IsOn = false;
                    await Task.Delay(300);
                    Console.WriteLine($"after switching everything off and closing: server running {obs.IsRunning}");
                    window.Closed += (_, _) => app.Shutdown();
                    window.Close();
                    return;
                }
                if (args.Length > 3 && args[3] == "dialogs")
                {
                    // ---- every dialog opens with the shared frame and closes on a click outside it
                    var dialogs = new (string Name, Action Open, Func<bool> IsOpen, string? Overlay)[]
                    {
                        ("hints", () => vm.OpenHintsCommand.Execute(null), () => vm.IsHintsOpen, "HintsOverlay"),
                        ("replacements", () => vm.Replacements.OpenCommand.Execute(null), () => vm.Replacements.IsOpen, "ReplacementsOverlay"),
                        ("read-aloud", () => vm.ReadAloud.OpenCommand.Execute(null), () => vm.ReadAloud.IsOpen, "ReadAloudOverlay"),
                        ("voice-training", () => vt.OpenCommand.Execute(null), () => vt.IsOpen, null),
                    };
                    foreach (var (name, open, isOpen, overlayName) in dialogs)
                    {
                        open();
                        await Task.Delay(700); // let the opening animation finish
                        Shot($"d-{name}.png");
                        var opened = isOpen();
                        if (overlayName is not null && window.FindName(overlayName) is UIElement overlay)
                            overlay.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                        else
                            vm.CloseDialogsCommand.Execute(null);
                        await Task.Delay(200);
                        Console.WriteLine($"{name,-15} opened {opened}, closed by {(overlayName is null ? "Esc" : "a click outside")}: {!isOpen()}");
                    }
                    Console.WriteLine($"test button text: \"{MainViewModel.TestText}\" | Twitch card: {vm.Twitch.Detail}");

                    // ---- the microphone path shared by dictation and voice training (nothing is kept)
                    if (vm.Microphones.Count > 0)
                    {
                        var recorder = new MicRecorder();
                        float loudest = 0;
                        recorder.LevelChanged += level => loudest = Math.Max(loudest, level);
                        recorder.Start(0);
                        await Task.Delay(700);
                        var bytes = recorder.Take().Length;
                        await recorder.StopAsync();
                        await recorder.StopAsync();
                        Console.WriteLine($"voice-training recorder: {bytes} bytes in 0.7 s (expected about {16000 * 2 * 7 / 10}), level meter moved: {loudest > 0}, stopped twice without error");

                        await using var tiny = await WhisperEngine.LoadAsync("tiny", "en");
                        var transcriber = new LiveTranscriber(tiny);
                        var levels = 0;
                        Exception? failed = null;
                        transcriber.LevelChanged += _ => Interlocked.Increment(ref levels);
                        transcriber.MicrophoneFailed += ex => failed = ex;
                        transcriber.StartMicrophone(0);
                        await Task.Delay(700);
                        await transcriber.StopAsync();
                        await transcriber.DisposeAsync();
                        Console.WriteLine($"dictation microphone: {levels} level updates in 0.7 s (expected about 14), failure reported: {failed?.Message ?? "none"}, stopped and disposed without error");
                    }
                    window.Closed += (_, _) => app.Shutdown();
                    window.Close();
                    return;
                }
                if (args.Length > 3 && args[3] == "train")
                {
                    // ---- a full training: two passes, the second on every recording
                    vm.SelectedModel = "turbo";
                    vt.OpenCommand.Execute(null);
                    await Task.Delay(3000);
                    Console.WriteLine($"intro: {vt.RecordedText}");
                    Console.WriteLine($"       button \"{vt.TrainText}\" enabled {vt.TrainCommand.CanExecute(null)} | {vt.TrainHint}");
                    Console.WriteLine($"       options: {string.Join("  ", vt.ShareOptions.Select(o => $"{o.Label}={o.TimeText}"))}");
                    Shot("t1-intro.png");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    vt.TrainCommand.Execute(null);
                    await Task.Delay(1500);
                    var last = "";
                    var shots = 0;
                    while (vt.Step == VoiceTrainingStep.Working && clock.Elapsed < TimeSpan.FromMinutes(25))
                    {
                        await Task.Delay(2000);
                        var now = vt.ProgressText.Split(':')[0];
                        if (now != last)
                            Console.WriteLine($"   [{clock.Elapsed:mm\\:ss}] {vt.ProgressText} ({vt.Progress:P0})");
                        if (now != last && vt.ProgressText.StartsWith("Training on your voice") && shots++ < 2)
                            Shot($"t2-working-{shots}.png");
                        last = now;
                    }
                    await Task.Delay(500);
                    Console.WriteLine($"finished after {clock.Elapsed:mm\\:ss}: step {vt.Step} | {vt.IntroMessage}");
                    Console.WriteLine($"detail while working: {vt.ProgressDetail}");
                    Console.WriteLine($"title : {vt.ResultTitle}");
                    Console.WriteLine($"detail: {vt.ResultDetail}");
                    Console.WriteLine($"tiles : {vt.BeforeLabel} {vt.BeforeText} -> {vt.AfterLabel} {vt.AfterText} | can use {vt.CanUse}");
                    foreach (var example in vt.Examples)
                        Console.WriteLine($"   \"{example.Sentence}\"\n      before: {example.Before}\n      after : {example.After}");
                    Shot("t3-result.png");
                    if (vt.CanUse)
                    {
                        vt.UseCommand.Execute(null);
                        await Task.Delay(500);
                        var info = PersonalModel.Info;
                        Console.WriteLine($"accepted: sentences {info?.Sentences}, test sentences {info?.TestSentences}, files {info?.Files?.Count}, {info?.AccuracyBefore:P1} -> {info?.AccuracyAfter:P1}");
                        vt.OpenCommand.Execute(null);
                        await Task.Delay(500);
                        Console.WriteLine($"card: {vt.ModelText}");
                        Console.WriteLine($"button \"{vt.TrainText}\" | waiting {vt.HasPending} | {vt.ShareNote}");
                        Console.WriteLine($"options now: {string.Join("  ", vt.ShareOptions.Select(o => $"{o.Label}={o.TimeText}"))}");
                        Shot("t4-after.png");
                    }
                    Console.WriteLine($"measured step time saved: {string.Join(", ", StateStore.Load().TrainingStepSeconds.Select(p => $"{p.Key}={p.Value:0.00} s"))}");
                    window.Closed += (_, _) => app.Shutdown();
                    window.Close();
                    return;
                }
                if (args.Length > 3 && args[3] == "worc")
                {
                    // ---- a hard word said again: Review it, then teach the model (the user's own flow)
                    vm.SelectedModel = PersonalModel.Name;
                    var set = VoiceDataset.Open("en");
                    var old = set.Recordings.First(r => r.Text.Contains("orcestershire"));
                    var pcm = AudioFiles.WavToPcm(File.ReadAllBytes(set.PathOf(old)));
                    var source = new short[pcm.Length / 2];
                    Buffer.BlockCopy(pcm, 0, source, 0, pcm.Length);
                    var faster = new short[(int)(source.Length / 1.04)];   // said again, a little faster
                    for (int i = 0; i < faster.Length; i++)
                    {
                        var at = i * 1.04;
                        var k = Math.Min((int)at, source.Length - 2);
                        faster[i] = (short)(source[k] + (source[k + 1] - source[k]) * (at - k));
                    }
                    var bytes = new byte[faster.Length * 2];
                    Buffer.BlockCopy(faster, 0, bytes, 0, bytes.Length);

                    vm.NewHint = "Worcestershire";
                    vm.AddHintCommand.Execute(null);
                    typeof(MainViewModel).GetField("_reviewLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, "en");
                    typeof(MainViewModel).GetMethod("OnTranscribed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm,
                        [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Can you buy live or expensive is the raw search higher sauce?", 0.8f)
                            { Clip = new SpokenClip(AudioFiles.PcmToWav(bytes)) }]);
                    var said = vm.Transcript[^1];
                    said.ReviewCommand.Execute(null);
                    Console.WriteLine($"note: {said.ReviewNote}");
                    said.EditText = "Can you believe how expensive is the worcestershire sauce?";
                    said.SaveReviewCommand.Execute(null);
                    await Task.Delay(300);
                    var now = VoiceDataset.Open("en").Recordings.Where(r => r.Text.Contains("orcestershire")).ToList();
                    Console.WriteLine($"saved as \"{said.Text}\" | recordings of the sentence now: {now.Count} ({string.Join(" / ", now.Select(r => $"\"{r.Text}\" corrected={r.Corrected}"))})");
                    Console.WriteLine($"banner: {vt.PendingText} | {vt.LearnText}");

                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    vt.LearnCommand.Execute(null);
                    await Task.Delay(1500);
                    var last = "";
                    while (vt.Step == VoiceTrainingStep.Working && clock.Elapsed < TimeSpan.FromMinutes(12))
                    {
                        await Task.Delay(2000);
                        var head = vt.ProgressText.Split(':')[0];
                        if (head != last)
                            Console.WriteLine($"   [{clock.Elapsed:mm\\:ss}] {vt.ProgressText}");
                        last = head;
                    }
                    await Task.Delay(500);
                    Console.WriteLine($"finished after {clock.Elapsed:mm\\:ss}: {vt.Step} | {vt.IntroMessage}");
                    Console.WriteLine($"title : {vt.ResultTitle}");
                    Console.WriteLine($"detail: {vt.ResultDetail}");
                    Console.WriteLine($"tiles : {vt.BeforeLabel} {vt.BeforeText} -> {vt.AfterLabel} {vt.AfterText} | can use {vt.CanUse}");
                    foreach (var example in vt.Examples)
                        Console.WriteLine($"   \"{example.Sentence}\"\n      before: {example.Before}\n      after : {example.After}");
                    Shot("w1-result.png");
                    if (vt.CanUse)
                    {
                        vt.UseCommand.Execute(null);
                        await Task.Delay(500);
                        vt.OpenCommand.Execute(null);
                        await Task.Delay(500);
                        Console.WriteLine($"accepted. listed: {string.Join(" | ", vt.Corrections.Select(c => $"[{c.Status}] {c.Text}"))}");
                    }
                    window.Closed += (_, _) => app.Shutdown();
                    window.Close();
                    return;
                }
                vm.SelectedModel = PersonalModel.Name;

                // ---- A. the share of sentences
                Console.WriteLine("A. sentence share");
                vt.OpenCommand.Execute(null);
                await Task.Delay(3000);
                Console.WriteLine($"   {vt.CardText} | {vt.KitText}");
                foreach (var share in new[] { 100, 80, 60, 40, 20 })
                {
                    vt.Share = share;
                    await Task.Delay(150);
                    Console.WriteLine($"   {share}%: {vt.RecordedText}");
                    Console.WriteLine($"        {vt.ShareText}");
                    Console.WriteLine($"        button \"{vt.TrainText}\" enabled {vt.TrainCommand.CanExecute(null)} | {vt.TrainHint}");
                    if (share == 40)
                        Shot("1-share-40.png");
                }
                Console.WriteLine($"   options: {string.Join("  ", vt.ShareOptions.Select(o => $"{o.Label}={o.TimeText}{(o.IsSelected ? "*" : "")}"))}");
                Console.WriteLine($"   note: {vt.ShareNote}");
                Console.WriteLine($"   saved share: {StateStore.Load().VoiceTrainingShare}");
                vt.Share = 100;
                await Task.Delay(200);
                Shot("1-share-100.png");
                vt.CloseCommand.Execute(null);
                await Task.Delay(300);

                // ---- B. real engine: does a transcribed sentence carry its audio?
                Console.WriteLine("B. dictation with the voice model keeps the audio");
                var data = VoiceDataset.Open("en");
                var heldOut = data.Recordings.Where(r => r.HeldOut).ToList();
                var first = heldOut.First(r => r.Text.StartsWith("What do you want to do about the car"));
                var second = heldOut.First(r => r.Text.StartsWith("That was close"));
                var spoken = AudioFiles.WavToPcm(File.ReadAllBytes(data.PathOf(first)))
                    .Concat(AudioFiles.WavToPcm(File.ReadAllBytes(data.PathOf(second)))).ToArray();
                var truth = $"{first.Text} {second.Text}";

                var engine = await WhisperEngine.LoadAsync(PersonalModel.Name, "en");
                var segments = new List<TranscriptSegment>();
                async Task<List<TranscriptSegment>> Hear(bool keep)
                {
                    var heard = new List<TranscriptSegment>();
                    var transcriber = new LiveTranscriber(engine) { KeepAudio = keep };
                    transcriber.Transcribed += s => { lock (heard) heard.Add(s); };
                    var silence = new byte[16000 * 2 * 2];
                    foreach (var chunk in silence[..16000].Concat(spoken).Concat(silence).Chunk(1600))
                        transcriber.PushAudio(chunk);
                    await transcriber.DisposeAsync();
                    return heard;
                }
                var without = await Task.Run(() => Hear(false));
                segments = await Task.Run(() => Hear(true));
                await engine.DisposeAsync();
                Console.WriteLine($"   KeepAudio off: {without.Count} segment(s), with audio: {without.Count(s => s.Clip is not null)}");
                foreach (var s in segments)
                    Console.WriteLine($"   KeepAudio on : \"{s.Text}\" | audio {(s.Clip is null ? "none" : $"{s.Clip.Wav.Length / 32000.0:0.0} s")} | same clip as first: {ReferenceEquals(s.Clip, segments[0].Clip)}");
                Console.WriteLine($"   really said  : \"{truth}\"");

                // ---- C. the transcript: only the latest ten can be reviewed
                Console.WriteLine("C. transcript");
                typeof(MainViewModel).GetField("_reviewLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, "en");
                var transcribed = typeof(MainViewModel).GetMethod("OnTranscribed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                void Say(TranscriptSegment s) => transcribed.Invoke(vm, [s]);
                var filler = new SpokenClip(File.ReadAllBytes(data.PathOf(heldOut[0])));
                for (int i = 1; i <= 4; i++)
                    Say(new TranscriptSegment(TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), $"Older sentence number {i}.", 0.9f)
                        { Clip = new SpokenClip(filler.Wav) });
                Say(new TranscriptSegment(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7), "No audio was kept for this one.", 0.9f));
                Say(new TranscriptSegment(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9), "Send.", 0.9f) { Clip = new SpokenClip(filler.Wav) });
                for (int i = 5; i <= 12; i++)
                    Say(new TranscriptSegment(TimeSpan.FromSeconds(10 + i), TimeSpan.FromSeconds(11 + i), $"Recent sentence number {i}.", 0.9f)
                        { Clip = new SpokenClip(filler.Wav) });
                var breath = new SpokenClip(filler.Wav);
                Say(new TranscriptSegment(TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(26), "First half of one breath.", 0.9f) { Clip = breath });
                Say(new TranscriptSegment(TimeSpan.FromSeconds(26), TimeSpan.FromSeconds(28), "Second half of it.", 0.9f) { Clip = breath });
                foreach (var s in segments)
                    Say(s with { Start = TimeSpan.FromSeconds(30), End = TimeSpan.FromSeconds(36) });
                await Task.Delay(600);
                var reviewable = vm.Transcript.Where(l => l.CanReview).ToList();
                Console.WriteLine($"   {vm.Transcript.Count} lines, {reviewable.Count} with a Review button (limit {MainViewModel.ReviewableSentences}); first reviewable: \"{reviewable[0].Text}\"");
                Console.WriteLine($"   command line reviewable: {vm.Transcript.First(l => l.IsCommand).CanReview}; line without audio reviewable: {vm.Transcript.First(l => l.Text.StartsWith("No audio")).CanReview}");
                Shot("2-transcript.png");

                // ---- D. review
                Console.WriteLine("D. review");
                var other = vm.Transcript.First(l => l.Text == "Recent sentence number 12.");
                other.ReviewCommand.Execute(null);
                await Task.Delay(300);
                other.SaveReviewCommand.Execute(null);
                Console.WriteLine($"   saved unchanged -> \"{vm.StatusText}\" | still editing: {other.IsReviewing} | waiting: {vt.HasPending}");
                other.ReviewCommand.Execute(null);
                other.EditText = "   ";
                other.SaveReviewCommand.Execute(null);
                Console.WriteLine($"   saved empty -> note \"{other.ReviewNote}\" problem {other.ReviewProblem}");
                other.CancelReviewCommand.Execute(null);
                Console.WriteLine($"   cancelled -> editing {other.IsReviewing}, text \"{other.Text}\", button back {other.CanReview}");

                var before = vm.Transcript.Count;
                var line = vm.Transcript.Last(l => !l.IsCommand);
                line.ReviewCommand.Execute(null);
                await Task.Delay(500);
                Console.WriteLine($"   editor opens with \"{line.EditText}\"");
                Console.WriteLine($"   note: {line.ReviewNote}");
                Shot("3-reviewing.png");
                line.EditText = truth;
                await Task.Delay(200);
                line.SaveReviewCommand.Execute(null);
                await Task.Delay(500);
                var saved = VoiceDataset.Open("en").Recordings.FirstOrDefault(r => r.Text == truth);
                Console.WriteLine($"   saved -> line \"{line.Text}\" | corrected {line.IsCorrected} | lines {before} -> {vm.Transcript.Count}");
                Console.WriteLine($"   recording: {(saved is null ? "MISSING" : $"{saved.Seconds:0.0} s, held out {saved.HeldOut}, file exists {File.Exists(VoiceDataset.Open("en").PathOf(saved))}")}; recordings now {VoiceDataset.Open("en").Recordings.Count}");
                Console.WriteLine($"   status: {vm.StatusText}");
                Console.WriteLine($"   banner: {vt.HasPending} | {vt.PendingText} | button \"{vt.LearnText}\" enabled {vt.LearnCommand.CanExecute(null)}");
                Shot("4-saved.png");

                // correcting the same line a second time replaces the first correction
                line.ReviewCommand.Execute(null);
                line.EditText = truth + " Really.";
                line.SaveReviewCommand.Execute(null);
                var all = VoiceDataset.Open("en").Recordings;
                Console.WriteLine($"   corrected again -> recordings {all.Count}, old text kept {all.Any(r => r.Text == truth)}, new text kept {all.Any(r => r.Text == truth + " Really.")}");
                line.ReviewCommand.Execute(null);
                line.EditText = truth;
                line.SaveReviewCommand.Execute(null);
                Console.WriteLine($"   and back -> recordings {VoiceDataset.Open("en").Recordings.Count}, waiting: {vt.PendingText}");

                vm.ToggleThemeCommand.Execute(null);
                await Task.Delay(600);
                Shot("4-saved-light.png");
                vm.ToggleThemeCommand.Execute(null);

                // ---- the corrected sentences are listed in the Voice training dialog
                Console.WriteLine("G. list in Voice training");
                var breathLine = vm.Transcript.First(l => l.Text == "Recent sentence number 11.");
                breathLine.ReviewCommand.Execute(null);
                breathLine.EditText = "A second corrected sentence, a short one.";
                breathLine.SaveReviewCommand.Execute(null);
                vt.OpenCommand.Execute(null);
                await Task.Delay(1200);
                Console.WriteLine($"   listed: {string.Join(" | ", vt.Corrections.Select(c => $"[{c.Status}] {c.Text}"))}");
                var stored = VoiceDataset.Open("en").Recordings.Where(r => r.Corrected).Select(r => r.Text).ToList();
                Console.WriteLine($"   marked in the recordings file: {stored.Count}");
                var scroll = FindScroll(window, "Sentences you corrected");
                scroll?.ScrollToEnd();
                await Task.Delay(400);
                Shot("8-corrections.png");
                vt.RemoveCorrectionCommand.Execute(vt.Corrections.First(c => c.Text.StartsWith("A second")));
                await Task.Delay(300);
                Console.WriteLine($"   after deleting one: {vt.Corrections.Count} listed, recordings {VoiceDataset.Open("en").Recordings.Count}, waiting: {vt.PendingText}");
                vt.CloseCommand.Execute(null);
                await Task.Delay(300);

                // ---- E. teach the model
                if (learn)
                {
                    Console.WriteLine("E. teach my voice model");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    vt.LearnCommand.Execute(null);
                    await Task.Delay(1500);
                    Console.WriteLine($"   dialog open {vt.IsOpen}, step {vt.Step}");
                    var last = "";
                    var shot = false;
                    while (vt.Step == VoiceTrainingStep.Working && clock.Elapsed < TimeSpan.FromMinutes(10))
                    {
                        await Task.Delay(2000);
                        var now = vt.ProgressText.Length > 26 ? vt.ProgressText[..26] : vt.ProgressText;
                        if (now != last)
                            Console.WriteLine($"   [{clock.Elapsed:mm\\:ss}] {vt.ProgressText} | {vt.ProgressDetail}");
                        last = now;
                        if (!shot && vt.ProgressText.StartsWith("Training on your voice"))
                        {
                            Shot("5-working.png");
                            shot = true;
                        }
                    }
                    await Task.Delay(500);
                    Console.WriteLine($"   finished after {clock.Elapsed:mm\\:ss}: step {vt.Step} | {vt.IntroMessage}");
                    Console.WriteLine($"   {vt.ResultTitle}");
                    Console.WriteLine($"   {vt.ResultDetail}");
                    Console.WriteLine($"   {vt.BeforeLabel} {vt.BeforeText} -> {vt.AfterLabel} {vt.AfterText} | can use {vt.CanUse}");
                    foreach (var example in vt.Examples)
                        Console.WriteLine($"     \"{example.Sentence}\"\n        before: {example.Before}\n        after : {example.After}");
                    Shot("6-result.png");
                    if (vt.CanUse)
                    {
                        vt.UseCommand.Execute(null);
                        await Task.Delay(500);
                        var info = PersonalModel.Info;
                        Console.WriteLine($"   accepted: files known {info?.Files?.Count}, sentences {info?.Sentences}, includes the correction {info?.Files?.Contains(VoiceDataset.Open("en").Recordings.First(r => r.Text == truth).File)}");
                        Console.WriteLine($"   waiting now: {vt.HasPending} | model selected {vm.SelectedModel} | {vm.StatusText}");
                    }
                    Console.WriteLine($"   measured step time saved: {string.Join(", ", StateStore.Load().TrainingStepSeconds.Select(p => $"{p.Key}={p.Value:0.00} s"))}");
                    Shot("7-after.png");
                }

                // ---- F. two sentences said in one breath are corrected together
                Console.WriteLine("F. one breath, two lines");
                var half = vm.Transcript.First(l => l.Text == "Second half of it.");
                var count = vm.Transcript.Count;
                half.ReviewCommand.Execute(null);
                Console.WriteLine($"   editor opens with \"{half.EditText}\"");
                Console.WriteLine($"   note: {half.ReviewNote}");
                half.EditText = "First half of one breath, second half of it.";
                half.SaveReviewCommand.Execute(null);
                await Task.Delay(300);
                Console.WriteLine($"   saved -> lines {count} -> {vm.Transcript.Count}, first half still listed: {vm.Transcript.Any(l => l.Text == "First half of one breath.")}, line \"{half.Text}\"");
                Console.WriteLine($"   waiting: {vt.HasPending} | {vt.PendingText}");
                Console.WriteLine($"   still reviewable: {vm.Transcript.Count(l => l.CanReview)} lines");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex}");
            }
            window.Closed += (_, _) => app.Shutdown();
            window.Close();
        });
        app.Run();
    }

    // The scroll viewer that contains a text block with the given text.
    static ScrollViewer? FindScroll(DependencyObject root, string text)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block && block.Text == text)
            {
                DependencyObject? up = block;
                while (up is not null and not ScrollViewer)
                    up = VisualTreeHelper.GetParent(up);
                return up as ScrollViewer;
            }
            if (FindScroll(child, text) is { } found)
                return found;
        }
        return null;
    }

    static void Capture(Window window, string output)
    {
        var content = (Panel)window.Content;
        content.SetResourceReference(Panel.BackgroundProperty, ThemeManager.Current == AppTheme.Light ? "ChromeBrush" : "WindowBrush");
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using (var file = File.Create(output))
            encoder.Save(file);
        content.ClearValue(Panel.BackgroundProperty);
    }
}

// A picture of a window as Windows composes it (the WebView2 preview included, which WPF's own capture leaves out).
static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    struct RECT { public int Left, Top, Right, Bottom; }

    public static BitmapSource? PrintWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bmp);
        try
        {
            if (!PrintWindow(hwnd, dc, 2)) return null; // PW_RENDERFULLCONTENT
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bmp);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
