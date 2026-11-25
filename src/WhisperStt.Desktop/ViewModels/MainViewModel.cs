using System.Collections.ObjectModel;
using System.Threading.Channels;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using WhisperStt.Core;
using WhisperStt.Core.Training;
using WhisperStt.Desktop.Mvvm;
using WhisperStt.Desktop.Services;
using WhisperStt.Desktop.Targets;

namespace WhisperStt.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // What every Test button sends.
    public const string TestText = "Hello from WalkTalk.";
    // How many of the latest sentences keep their audio, so they can be reviewed.
    public const int ReviewableSentences = 10;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ReviewActions _reviewActions;
    // The language sentences are being kept for review in, or null when they aren't kept.
    private string? _reviewLanguage;
    // Writes to other apps run one at a time, in the order the sentences were spoken.
    private readonly Channel<Delivery> _deliveries = Channel.CreateUnbounded<Delivery>(new() { SingleReader = true });
    private WhisperEngine? _engine;
    private LiveTranscriber? _transcriber;
    private DateTime _sessionStart;
    private CancellationTokenSource? _pickCancellation;
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private (TargetViewModel Target, int Index)? _removed;
    private string _undoText = "";

    private string? _selectedMicrophone;
    private string _selectedModel;
    private LanguageOption _selectedLanguage;
    private string _newHint = "";
    private bool _isListening, _isLoading, _isSpeaking, _isBusy, _isPicking, _isHintsOpen;
    private double _level;
    private string _statusText = "Ready.";
    private string _pickMessage = "";

    public MainViewModel()
    {
        Microphones = LiveTranscriber.GetMicrophones();
        _reviewActions = new ReviewActions(StartReview, SaveReview, CancelReview);
        var state = StateStore.Load();
        _selectedMicrophone = Microphones.Contains(state.Microphone) ? state.Microphone : Microphones.FirstOrDefault();
        RefreshModels();
        _selectedModel = Models.Contains(state.Model) ? state.Model : "turbo";
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == state.TranscriptionLanguage)
            ?? Languages.First(l => l.Code == LanguageOption.DefaultCode);
        foreach (var hint in HintCorrector.ParseHints(state.WordHints))
            WordHintList.Add(hint);
        foreach (var target in state.Targets)
            Targets.Add(CreateTarget(target.Target, target.Name, target.IsOn, target.PressEnter, element: null));
        VoiceTraining = new VoiceTrainingViewModel(state, new VoiceTrainingHost(
            GetMicrophone: () => MicrophoneNumber,
            GetModel: () => SelectedModel,
            CanRun: () => CanChangeSettings && Microphones.Count > 0,
            UseVoiceModel: UseVoiceModel,
            ModelsChanged: madeFrom => RefreshModels(madeFrom),
            Changed: SaveState,
            Post: Post));
        VrChat = new VrChatViewModel(state.VrChat, SaveState);
        Twitch = new TwitchViewModel(state, SaveState, Post);
        Replacements = new ReplacementsViewModel(state.Replacements, () => SelectedModel, SaveState);
        ReadAloud = new ReadAloudViewModel(state, SaveState, Post);
        ReadAloud.ReadingChanged += OnReadingAloudChanged;
        ReadAloud.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReadAloudViewModel.IsReading))
                OnStateChanged();
        };

        if (Microphones.Count == 0)
            _statusText = "No microphone found. Connect one and restart the app.";

        AddTargetCommand = new AsyncRelayCommand(PickTargetAsync);
        CancelPickCommand = new RelayCommand(() => _pickCancellation?.Cancel(), () => IsPicking);
        ToggleListeningCommand = new AsyncRelayCommand(ToggleListeningAsync, () => Microphones.Count > 0);
        ClearTranscriptCommand = new RelayCommand(Transcript.Clear, () => Transcript.Count > 0);
        CopyTranscriptCommand = new RelayCommand(
            () => ClipboardHelper.TrySetText(string.Join(Environment.NewLine, Transcript.Where(l => !l.IsCommand).Select(l => l.Text))),
            () => Transcript.Count > 0);
        UndoRemoveCommand = new RelayCommand(UndoRemove, () => CanUndo);
        _undoTimer.Tick += (_, _) => ClearUndo();
        OpenHintsCommand = new RelayCommand(() => IsHintsOpen = true);
        CloseHintsCommand = new RelayCommand(() => IsHintsOpen = false);
        ToggleThemeCommand = new RelayCommand(() =>
        {
            ThemeManager.Apply(IsLightTheme ? AppTheme.Dark : AppTheme.Light);
            OnPropertyChanged(nameof(IsLightTheme));
            OnPropertyChanged(nameof(ThemeToggleText));
            SaveState();
        });
        CloseDialogsCommand = new RelayCommand(() =>
        {
            IsHintsOpen = false;
            Replacements.IsOpen = false;
            ReadAloud.IsOpen = false;
            if (VoiceTraining.IsOpen)
                VoiceTraining.Close();
        });
        AddHintCommand = new RelayCommand(AddHints, () => HintCorrector.ParseHints(NewHint).Count > 0);
        RemoveHintCommand = new RelayCommand<string>(RemoveHint);
        WordHintList.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WordHints));
            OnPropertyChanged(nameof(HintsSummary));
            SaveState();
        };

        _ = Task.Run(DeliverAsync);
    }

    // Raised after a new target is picked, so the window can offer to rename it.
    public event Action<TargetViewModel>? TargetAdded;

    public ObservableCollection<TargetViewModel> Targets { get; } = [];
    public ObservableCollection<TranscriptLine> Transcript { get; } = [];
    public IReadOnlyList<string> Microphones { get; }
    // The standard models, plus "my voice" once the user has trained one.
    public ObservableCollection<string> Models { get; } = [];
    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;

    public ICommand AddTargetCommand { get; }
    public ICommand CancelPickCommand { get; }
    public ICommand ToggleListeningCommand { get; }
    public ICommand ClearTranscriptCommand { get; }
    public ICommand CopyTranscriptCommand { get; }
    public ICommand UndoRemoveCommand { get; }
    public ICommand CloseDialogsCommand { get; }
    public ICommand ToggleThemeCommand { get; }

    // Dark is the original look; Light uses the WalkTalk brand colors.
    public bool IsLightTheme => ThemeManager.Current == AppTheme.Light;
    public string ThemeToggleText => IsLightTheme ? "Switch to dark theme" : "Switch to light theme";
    public ReadAloudViewModel ReadAloud { get; }
    public VoiceTrainingViewModel VoiceTraining { get; }
    public ReplacementsViewModel Replacements { get; }
    public VrChatViewModel VrChat { get; }
    public TwitchViewModel Twitch { get; }
    public ICommand OpenHintsCommand { get; }
    public ICommand CloseHintsCommand { get; }
    public ICommand AddHintCommand { get; }
    public ICommand RemoveHintCommand { get; }

    public string? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set { if (SetProperty(ref _selectedMicrophone, value)) SaveState(); }
    }

    // The selected microphone's device number, -1 when none is selected.
    private int MicrophoneNumber => SelectedMicrophone is null ? -1 : Microphones.ToList().IndexOf(SelectedMicrophone);

    // A level meter like a VU meter: it rises instantly with the voice and falls back gently instead of flickering.
    public static double MeterLevel(float heard, double shown) => Math.Max(heard, shown * 0.82);

    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetProperty(ref _selectedModel, value))
                return;
            SaveState();
            VoiceTraining?.Refresh(); // which model would be trained, and whether sentences wait for it
        }
    }

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set { if (SetProperty(ref _selectedLanguage, value)) SaveState(); }
    }

    // Names and terms Whisper should expect, so it spells them right (e.g. "Moonhollow, Twitch, GG").
    public ObservableCollection<string> WordHintList { get; } = [];

    // The list as Whisper receives it; a change takes effect the next time listening starts.
    public string WordHints => string.Join(", ", WordHintList);

    public string HintsSummary => WordHintList.Count switch { 0 => "None", 1 => "1 word", var n => $"{n} words" };

    public bool IsHintsOpen
    {
        get => _isHintsOpen;
        set => SetProperty(ref _isHintsOpen, value);
    }

    public string NewHint
    {
        get => _newHint;
        set => SetProperty(ref _newHint, value);
    }

    // Accepts one word or several separated by commas, so a pasted list works too.
    private void AddHints()
    {
        foreach (var hint in HintCorrector.ParseHints(NewHint))
            if (!WordHintList.Contains(hint, StringComparer.OrdinalIgnoreCase))
                WordHintList.Add(hint);
        NewHint = "";
        if (IsListening)
            StatusText = "New word hints apply the next time you press Start listening.";
    }

    private void RemoveHint(string hint) => WordHintList.Remove(hint);

    // Keeps the model list in step with whether a personal model exists. If the selected one was just
    // removed, falls back to the model it was trained from.
    private void RefreshModels(string madeFrom = "turbo")
    {
        var wanted = PersonalModel.Exists ? ModelCatalog.Names.Append(PersonalModel.Name) : ModelCatalog.Names;
        var selected = _selectedModel;
        foreach (var name in wanted.Where(n => !Models.Contains(n)).ToList())
            Models.Add(name);
        if (!PersonalModel.Exists && Models.Remove(PersonalModel.Name) && selected == PersonalModel.Name)
            SelectedModel = Models.Contains(madeFrom) ? madeFrom : "turbo";
    }

    // The user accepted the model trained on their voice: switch to it, in the language it was trained for.
    private void UseVoiceModel(string language)
    {
        SelectedModel = PersonalModel.Name;
        if (Languages.FirstOrDefault(l => l.Code == language) is { } option)
            SelectedLanguage = option;
        StatusText = $"Now using your voice model · {SelectedLanguage.Name}. Press Start listening to try it.";
    }

    public bool IsPicking
    {
        get => _isPicking;
        private set
        {
            if (SetProperty(ref _isPicking, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public string PickMessage
    {
        get => _pickMessage;
        private set => SetProperty(ref _pickMessage, value);
    }

    public bool IsListening
    {
        get => _isListening;
        private set { if (SetProperty(ref _isListening, value)) OnStateChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (SetProperty(ref _isLoading, value)) OnStateChanged(); }
    }

    public bool IsSpeaking
    {
        get => _isSpeaking;
        private set { if (SetProperty(ref _isSpeaking, value)) OnStateChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) OnStateChanged(); }
    }

    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool CanChangeSettings => !IsListening && !IsLoading;

    public string StartButtonText => IsLoading ? "Loading…" : IsListening ? "Stop" : "Start listening";

    public string StateText =>
        IsLoading ? "Loading model" :
        ReadAloud.IsReading ? "Reading aloud" :
        !IsListening ? "Idle" :
        IsSpeaking ? "Hearing speech" :
        IsBusy ? "Transcribing" :
        "Listening";

    // Shown under the last transcript line while a sentence is still on its way.
    public string PendingText => !IsListening ? "" : IsSpeaking ? "Hearing speech…" : IsBusy ? "Transcribing…" : "";

    private void OnStateChanged()
    {
        OnPropertyChanged(nameof(CanChangeSettings));
        OnPropertyChanged(nameof(StartButtonText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(PendingText));
        // Others in VRChat see the typing bubble while a sentence is being heard or written out.
        VrChat?.SetTyping(IsListening && (IsSpeaking || IsBusy));
    }

    private async Task ToggleListeningAsync()
    {
        if (IsListening)
        {
            await StopAsync();
            return;
        }

        var deviceNumber = MicrophoneNumber;
        if (deviceNumber < 0)
        {
            StatusText = "Choose a microphone first.";
            return;
        }

        IsLoading = true;
        try
        {
            if (_engine is null || _engine.ModelName != SelectedModel || _engine.Language != SelectedLanguage.Code ||
                _engine.Prompt != WordHints)
            {
                if (_engine is not null)
                    await _engine.DisposeAsync();
                _engine = null;
                _engine = await WhisperEngine.LoadAsync(SelectedModel, SelectedLanguage.Code, WordHints,
                    message => Post(() => StatusText = message));
            }

            var transcriber = new LiveTranscriber(_engine);
            // With the voice model, the audio of each sentence is kept for a while, so a transcript the
            // user corrects can be learned from. It needs a voice model that can be updated, used in the
            // language it was trained for.
            _reviewLanguage = _engine.ModelName == PersonalModel.Name && PersonalModel.Info is { Files: not null } info &&
                              info.Language == _engine.Language
                ? info.Language
                : null;
            transcriber.KeepAudio = _reviewLanguage is not null;
            transcriber.LevelChanged += level => Post(() => Level = MeterLevel(level, Level));
            transcriber.SpeakingChanged += speaking => Post(() => IsSpeaking = speaking);
            transcriber.BusyChanged += busy => Post(() => IsBusy = busy);
            transcriber.Transcribed += segment => Post(() => OnTranscribed(segment));
            transcriber.TranscriptionFailed += ex => Post(() => StatusText = $"Could not transcribe a sentence: {ex.Message}");
            transcriber.MicrophoneFailed += ex => Post(() => OnMicrophoneFailed(ex));

            _sessionStart = DateTime.Now;
            transcriber.StartMicrophone(deviceNumber);
            _transcriber = transcriber;
            IsListening = true;
            StatusText = $"Listening on {SelectedMicrophone} · model {SelectedModel} on the {_engine.Backend} · {SelectedLanguage.Name}" +
                (_engine.Backend == "CPU" ? " · no usable GPU found, so this will be slower" : "");
        }
        catch (Exception ex)
        {
            StatusText = $"Could not start: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task StopAsync()
    {
        var transcriber = _transcriber;
        if (transcriber is null)
            return;
        _transcriber = null;

        StatusText = "Finishing the last sentence…";
        await transcriber.DisposeAsync();

        // Free the graphics card's memory for other apps (games...) while not listening.
        // The next Start loads the model again, which takes a few seconds.
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }

        IsListening = false;
        IsSpeaking = false;
        IsBusy = false;
        Level = 0;
        StatusText = "Stopped. The model was unloaded to free the graphics card's memory.";
    }

    private async void OnMicrophoneFailed(Exception ex)
    {
        await StopAsync();
        StatusText = $"The microphone stopped: {ex.Message}";
    }

    public async Task ShutdownAsync()
    {
        _pickCancellation?.Cancel();
        await VoiceTraining.ShutdownAsync();
        await StopAsync();
        if (_engine is not null)
            await _engine.DisposeAsync();
        _engine = null;
        ReadAloud.Dispose();
        VrChat.Dispose();
        Twitch.Shutdown();
    }

    // While a sentence is read aloud through speakers, the microphone would hear it: pause it if asked to,
    // and resume a moment after the voice stops so the room's echo isn't picked up either.
    private async void OnReadingAloudChanged(bool reading)
    {
        if (reading)
        {
            if (ReadAloud.PauseMic && _transcriber is not null)
                _transcriber.Muted = true;
            return;
        }
        await Task.Delay(300);
        if (!ReadAloud.IsReading && _transcriber is not null)
            _transcriber.Muted = false;
    }

    // A sentence that is only a command word ("Clean", "Send") acts on the fields instead of being written.
    private void OnTranscribed(TranscriptSegment segment)
    {
        // A command is recognised from what was actually said; anything else gets the user's replacements
        // ("by the way" written as "btw") before it is shown, written to the fields or read aloud.
        var command = VoiceCommands.Match(segment.Text);
        var text = command is null ? WordReplacements.Apply(segment.Text, Replacements.Items) : segment.Text;
        var targets = Targets.Where(t => t.IsOn).ToList();
        var deliveries = targets.Select(t => new DeliveryViewModel(t.Name)).ToList();
        var vrChat = VrChat.IsOn ? new DeliveryViewModel(VrChatViewModel.Name) : null;
        var twitch = Twitch.IsActive ? new DeliveryViewModel(TwitchViewModel.Name) : null;
        if (vrChat is not null)
            deliveries.Add(vrChat);
        if (twitch is not null)
            deliveries.Add(twitch);
        Transcript.Add(new TranscriptLine(_sessionStart + segment.Start, text, deliveries, command,
            heard: segment.Text, clip: segment.Clip, language: _reviewLanguage ?? "", review: _reviewActions));
        ForgetOldAudio();
        for (int i = 0; i < targets.Count; i++)
            _deliveries.Writer.TryWrite(new Delivery(targets[i], command, text, deliveries[i]));
        if (vrChat is not null)
            VrChat.Deliver(command, text, vrChat);
        if (twitch is not null)
            Twitch.Deliver(command, text, twitch);
        if (command is null)
            ReadAloud.Speak(text);
    }

    // -- reviewing a sentence: the user corrects what was written, and the voice model learns from it

    // The audio of only the latest sentences is kept. Sentences said in one breath share their audio,
    // so they are let go together.
    private void ForgetOldAudio()
    {
        while (Transcript.Count(l => l.Clip is not null && !l.IsCommand) > ReviewableSentences)
        {
            var oldest = Transcript.First(l => l.Clip is not null && !l.IsCommand);
            var together = SaidTogether(oldest);
            if (together.Any(l => l.IsReviewing))
                return; // being corrected right now: keep it until that is done
            foreach (var line in together)
                line.Clip = null;
        }
        // Voice commands aren't counted above; far enough back, their audio goes too.
        for (int i = Transcript.Count - 3 * ReviewableSentences - 1; i >= 0; i--)
            if (Transcript[i].IsCommand && !Transcript[i].IsReviewing)
                Transcript[i].Clip = null;
    }

    private List<TranscriptLine> SaidTogether(TranscriptLine line) =>
        Transcript.Where(l => ReferenceEquals(l.Clip, line.Clip)).ToList();

    private static string HeardText(IEnumerable<TranscriptLine> lines) => string.Join(" ", lines.Select(l => l.Heard));

    private void StartReview(TranscriptLine line)
    {
        if (line.Clip is null)
            return;
        foreach (var other in Transcript.Where(l => l.IsReviewing).ToList())
            CancelReview(other);
        var together = SaidTogether(line);
        line.EditText = HeardText(together);
        line.ReviewNote = together.Count > 1
            ? "Said in one breath with the sentence next to it, so they are corrected together. Type exactly what you said, with capitals where they belong."
            : "Type exactly what you said, with capitals where they belong, then press Save.";
        line.ReviewProblem = false;
        line.IsReviewing = true;
    }

    private void CancelReview(TranscriptLine line)
    {
        line.IsReviewing = false;
        ForgetOldAudio();
    }

    // The corrected text and the sentence's audio become a recording for voice training.
    private void SaveReview(TranscriptLine line)
    {
        if (line.Clip is not { } clip)
        {
            line.IsReviewing = false;
            return;
        }
        var text = string.Join(' ', line.EditText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            line.ReviewNote = "Type what you said, or press Cancel.";
            line.ReviewProblem = true;
            return;
        }
        var together = SaidTogether(line);
        if (text == HeardText(together))
        {
            CancelReview(line);
            StatusText = "Nothing was changed, so there is nothing to learn.";
            return;
        }

        // A word from the word hints gets the spelling it has there ("worcestershire" becomes
        // "Worcestershire"): the model learns a word faster in the form it already knows.
        text = HintCorrector.Apply(text, WordHintList.ToList());
        var (file, problem) = VoiceTraining.AddCorrection(line.Language, text, clip.Wav, line.CorrectionFile);
        if (problem is not null)
        {
            line.ReviewNote = problem;
            line.ReviewProblem = true;
            return;
        }
        line.CorrectionFile = file;

        // The audio now belongs to this one line: sentences said with it are folded into it.
        foreach (var other in together.Where(l => l != line))
        {
            other.Clip = null;
            if (!other.IsCommand)
                Transcript.Remove(other);
        }
        line.Heard = text;
        line.Text = WordReplacements.Apply(text, Replacements.Items);
        line.IsCorrected = true;
        line.IsReviewing = false;
        ForgetOldAudio();
        StatusText = !VoiceTraining.HasPending
            ? "Correction saved. Your voice model learns it the next time you retrain it in Voice training."
            : IsListening
                ? "Correction saved. Stop listening, then press “Teach my voice model” so it learns it."
                : "Correction saved. Press “Teach my voice model” so it learns it.";
    }

    private sealed record Delivery(TargetViewModel Target, VoiceCommand? Command, string Text, DeliveryViewModel? Status);

    private async Task DeliverAsync()
    {
        await foreach (var (target, command, text, delivery) in _deliveries.Reader.ReadAllAsync())
        {
            try
            {
                Action report;
                switch (command)
                {
                    case VoiceCommand.Clear:
                        target.Clear();
                        report = () => target.ReportDone("Cleared");
                        break;
                    case VoiceCommand.Send:
                        target.PressEnterKey();
                        report = () => target.ReportDone("Sent with Enter");
                        break;
                    default:
                        var method = target.Write(text);
                        report = () => target.ReportSent(method);
                        break;
                }
                Post(() =>
                {
                    report();
                    if (delivery is not null)
                        delivery.State = DeliveryState.Sent;
                });
            }
            catch (Exception ex)
            {
                var message = ex is ElementNotAvailableException ? "The field went away. Is the app still open?" : ex.Message;
                Post(() =>
                {
                    target.ReportFailed(message);
                    if (delivery is not null)
                    {
                        delivery.State = DeliveryState.Failed;
                        delivery.Error = message;
                    }
                    StatusText = $"{target.Name}: {message}";
                });
            }
        }
    }

    private async Task PickTargetAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        _pickCancellation = timeout;
        IsPicking = true;
        PickMessage = "Click the text field you want to write to, in any app.";
        try
        {
            while (true)
            {
                var result = await TargetPicker.PickAsync(timeout.Token);
                if (result.Element is null)
                {
                    PickMessage = result.Problem!;
                    continue;
                }

                var element = result.Element;
                var info = await Task.Run(() => TargetLocator.Describe(element));
                var existing = Targets.FirstOrDefault(t => IsSameField(t.Info, info));
                if (existing is not null)
                {
                    StatusText = $"That field is already in the list as \"{existing.Name}\".";
                    return;
                }

                var target = CreateTarget(info, UniqueName(info.DefaultName), isOn: true, pressEnter: false, element);
                Targets.Add(target);
                SaveState();
                StatusText = $"Added \"{target.Name}\". Click its name to rename it.";
                TargetAdded?.Invoke(target);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Adding a field was cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't read that field: {ex.Message}";
        }
        finally
        {
            _pickCancellation = null;
            IsPicking = false;
        }
    }

    private static bool IsSameField(TargetInfo a, TargetInfo b) =>
        a.ProcessName == b.ProcessName && a.ControlTypeId == b.ControlTypeId &&
        a.AutomationId == b.AutomationId && a.ElementName == b.ElementName && a.ClassName == b.ClassName;

    private string UniqueName(string name)
    {
        var candidate = name;
        for (int n = 2; Targets.Any(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{name} ({n})";
        return candidate;
    }

    private TargetViewModel CreateTarget(TargetInfo info, string name, bool isOn, bool pressEnter, AutomationElement? element) =>
        new(info, name, isOn, pressEnter, element, SaveState, RemoveTarget,
            target => _deliveries.Writer.TryWrite(new Delivery(target, null, TestText, null)));

    // Deleting is immediate; a short-lived Undo replaces a confirmation dialog.
    private void RemoveTarget(TargetViewModel target)
    {
        var index = Targets.IndexOf(target);
        Targets.Remove(target);
        SaveState();
        _removed = (target, index);
        UndoText = $"Removed “{target.Name}”";
        OnPropertyChanged(nameof(CanUndo));
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void UndoRemove()
    {
        if (_removed is not { } removed)
            return;
        Targets.Insert(Math.Min(removed.Index, Targets.Count), removed.Target);
        SaveState();
        ClearUndo();
    }

    private void ClearUndo()
    {
        _undoTimer.Stop();
        _removed = null;
        OnPropertyChanged(nameof(CanUndo));
    }

    public bool CanUndo => _removed is not null;

    public string UndoText
    {
        get => _undoText;
        private set => SetProperty(ref _undoText, value);
    }

    private void SaveState()
    {
        try
        {
            var state = new AppState
            {
                Microphone = SelectedMicrophone,
                Model = SelectedModel,
                TranscriptionLanguage = SelectedLanguage.Code,
                WordHints = WordHints,
                Theme = ThemeManager.Current.ToString(),
                Targets = Targets.Select(t => new TargetState { Name = t.Name, IsOn = t.IsOn, PressEnter = t.PressEnter, Target = t.Info }).ToList()
            };
            // These are null only while the constructor is still running; keep what was loaded then.
            if (VoiceTraining is null || ReadAloud is null || Replacements is null || VrChat is null || Twitch is null)
                return;
            state.Replacements = Replacements.Items.ToList();
            state.VrChat = VrChat.IsOn;
            Twitch.WriteTo(state);
            VoiceTraining.WriteTo(state);
            ReadAloud.WriteTo(state);
            StateStore.Save(state);
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save your fields: {ex.Message}";
        }
    }

    private void Post(Action action) => _dispatcher.BeginInvoke(action);
}
