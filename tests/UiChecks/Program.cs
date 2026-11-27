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
        ObsViewModel.PreviewDataFolder = Path.Combine(sandbox, "webview");
        VrChatChatbox.Endpoint = new IPEndPoint(IPAddress.Loopback, 9123);
        TwitchApi.AuthBase = "http://127.0.0.1:9/oauth2";
        TwitchApi.ApiBase = "http://127.0.0.1:9/helix";
        File.Delete(StateStore.FilePath);
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
                if (args.Length > 3 && args[3] == "obs")
                {
                    // ---- the OBS overlay: style it, install a Google font, send sentences, read what the page gets
                    var obs = vm.Obs;
                    obs.OpenCommand.Execute(null);
                    await Task.Delay(800);
                    Console.WriteLine($"dialog open {obs.IsOpen}, server running {obs.IsRunning}, url {obs.Url} | {obs.Status}");
                    obs.IsOn = true;
                    obs.CanvasWidth = 1280;
                    obs.CanvasHeight = 300;
                    obs.FontSize = 64;
                    obs.TextColor = "#FFE45C";
                    obs.TextColor = "not a color"; // ignored
                    obs.Case = WhisperStt.Core.Obs.TextCase.Uppercase;
                    obs.HorizontalAlign = WhisperStt.Core.Obs.HorizontalAlign.Center;
                    obs.VerticalAlign = WhisperStt.Core.Obs.VerticalAlign.Bottom;
                    obs.LineHeight = 1.1;
                    obs.ShadowX = 4; obs.ShadowY = 4; obs.ShadowBlur = 8; obs.ShadowColor = "#AA000000";
                    obs.OutlineWidth = 4; obs.OutlineColor = "#1A1A40";
                    obs.Lines = 2; obs.HideAfter = 0;
                    Console.WriteLine($"text color after a bad value: {obs.TextColor}");

                    obs.FontQuery = "bang";
                    for (int i = 0; i < 60 && obs.FontResults.Count == 0 && !obs.SearchStatus.StartsWith("Couldn't") && !obs.SearchStatus.StartsWith("No "); i++)
                        await Task.Delay(500);
                    Console.WriteLine($"search 'bang': {string.Join(", ", obs.FontResults.Take(5).Select(r => $"{r.Family} ({r.Category})"))} | {obs.SearchStatus}");
                    var bangers = obs.FontResults.FirstOrDefault(r => r.Family == "Bangers");
                    if (bangers is not null)
                    {
                        obs.FontActionCommand.Execute(bangers);
                        for (int i = 0; i < 120 && !bangers.IsInstalled && !obs.SearchStatus.StartsWith("Couldn't"); i++)
                            await Task.Delay(500);
                        Console.WriteLine($"install: installed {bangers.IsInstalled}, font now \"{obs.FontFamily}\", in the font list first: {obs.Families.FirstOrDefault()} | {obs.SearchStatus}");
                        Console.WriteLine($"files: {string.Join(", ", Directory.GetFiles(WhisperStt.Core.Obs.GoogleFonts.Directory, "*", SearchOption.AllDirectories).Select(Path.GetFileName))}");
                    }
                    await Task.Delay(300);
                    Shot("o1-dialog.png");
                    var preview = (Microsoft.Web.WebView2.Wpf.WebView2)window.FindName("ObsPreview");
                    for (int i = 0; i < 40 && preview.CoreWebView2 is null; i++)
                        await Task.Delay(250);
                    var message = (System.Windows.Controls.TextBlock)window.FindName("ObsPreviewMessage");
                    Console.WriteLine($"preview: browser ready {preview.CoreWebView2 is not null}, showing {preview.Source} | message: {(message.Visibility == Visibility.Visible ? message.Text : "none")}");

                    // what OBS would receive
                    using var http = new System.Net.Http.HttpClient();
                    var page = await http.GetStringAsync(obs.Url);
                    var css = await http.GetStringAsync(obs.Url + "fonts.css");
                    Console.WriteLine($"page: {page.Length} chars, has EventSource {page.Contains("EventSource")} | fonts.css: {css.Length} chars, points to local files {css.Contains("url(/fonts/bangers/")}");
                    using var stream = await http.GetStreamAsync(obs.Url + "events");
                    using var reader = new StreamReader(stream);
                    var events = new List<string>();
                    var reading = Task.Run(async () =>
                    {
                        string? line;
                        while ((line = await reader.ReadLineAsync()) is not null)
                            if (line.StartsWith("data:") || line.StartsWith("event:"))
                                lock (events) events.Add(line);
                    });
                    await Task.Delay(400);
                    var onTranscribed = typeof(MainViewModel).GetMethod("OnTranscribed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    onTranscribed.Invoke(vm, [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Hello chat, welcome to the stream!", 0.9f)]);
                    onTranscribed.Invoke(vm, [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Today we try the new overlay.", 0.9f)]);
                    await Task.Delay(600);
                    lock (events)
                        foreach (var e in events)
                            Console.WriteLine($"   {(e.Length > 160 ? e[..160] + "…" : e)}");
                    Console.WriteLine($"status: {obs.Status}");
                    Console.WriteLine("KEEP-OPEN: the overlay stays up for 45 s at " + obs.Url);
                    await Task.Delay(45_000);
                    obs.IsOpen = false;
                    obs.IsOn = false;
                    await Task.Delay(300);
                    Console.WriteLine($"after switching off and closing: server running {obs.IsRunning}");
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
