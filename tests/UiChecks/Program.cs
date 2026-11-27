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
        File.Delete(PersonalModel.CandidatePath);
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
