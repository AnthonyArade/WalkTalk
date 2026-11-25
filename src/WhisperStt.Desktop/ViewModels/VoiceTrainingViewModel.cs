using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WhisperStt.Core;
using WhisperStt.Core.Training;
using WhisperStt.Desktop.Mvvm;
using WhisperStt.Desktop.Services;

namespace WhisperStt.Desktop.ViewModels;

public enum VoiceTrainingStep { Intro, Reading, Working, Results }

// What the main window gives voice training, and how it gets the outcome back.
public sealed record VoiceTrainingHost(
    Func<int> GetMicrophone,      // device number, -1 when none is selected
    Func<string> GetModel,
    Func<bool> CanRun,            // false while dictation is using the microphone
    Action<string> UseVoiceModel, // the personal model was accepted; argument: its language code
    Action<string> ModelsChanged, // the personal model appeared or went away; argument: the model it was made from
    Action Changed,               // something to save
    Action<Action> Post);         // run on the UI thread

// One held-back sentence the two models wrote differently.
public sealed record TrainingExample(string Sentence, string Before, string After);

// A sentence the user corrected with Review. Learned: the voice model has already been trained on it.
// File: the recording's file, which tells apart the same sentence corrected several times.
public sealed record CorrectionItem(string Text, bool Learned, string File)
{
    public string Status => Learned ? "Learned" : "Waiting";
}

// One choice of how much of the sentence list to use, with how long training on it should take.
public sealed class ShareOption(int percent) : ObservableObject
{
    private bool _isSelected;
    private string _timeText = "";

    public int Percent { get; } = percent;
    public string Label { get; } = $"{percent}%";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string TimeText
    {
        get => _timeText;
        set
        {
            if (SetProperty(ref _timeText, value))
                OnPropertyChanged(nameof(Description));
        }
    }

    public string Description => $"Use {Label} of the sentences, training takes {TimeText}";
}

// Voice training: the user reads sentences aloud, WalkTalk retrains its speech model on those recordings
// so it gets used to how this person pronounces things, then compares the model before and after on
// sentences that were kept out of the training. The new model is only used if the user accepts it.
public sealed class VoiceTrainingViewModel : ObservableObject
{
    public const int MinSentences = 30;
    private const int MaxExamples = 6;
    // Beyond this many new sentences, training on everything again is as quick as an update and learns more.
    private const int MaxUpdateSentences = 12;
    private static readonly int[] Shares = [20, 40, 60, 80, 100];

    private readonly VoiceTrainingHost _host;
    private readonly Dictionary<string, double> _stepSeconds;
    private Dictionary<string, int> _positions = [];
    private int _share;
    private VoiceDataset _data;
    private List<string> _pending = [];
    private MicRecorder? _recorder;
    private CancellationTokenSource? _work;
    private GraphicsCard? _card;
    private bool _cardChecked;
    private PersonalModelInfo? _candidate;

    private bool _isOpen, _readingProblem, _canUse;
    private VoiceTrainingStep _step;
    private SentenceSet _language;
    private string _newSentence = "", _introMessage = "";
    private string _currentSentence = "", _positionText = "", _readingMessage = "";
    private string _progressText = "", _progressDetail = "";
    private string _resultTitle = "", _resultDetail = "", _beforeText = "", _afterText = "";
    private string _beforeLabel = "Before training", _afterLabel = "With your voice model";
    private double _level, _progress;

    public VoiceTrainingViewModel(AppState state, VoiceTrainingHost host)
    {
        _host = host;
        _language = TrainingSentences.For(state.VoiceCheckLanguage.Length > 0 ? state.VoiceCheckLanguage : state.TranscriptionLanguage);
        _data = VoiceDataset.Open(_language.Code);
        IndexSentences();
        foreach (var sentence in state.VoiceCheckSentences)
            CustomSentences.Add(sentence);
        _share = Shares.Contains(state.VoiceTrainingShare) ? state.VoiceTrainingShare : 100;
        _stepSeconds = new(state.TrainingStepSeconds);
        ShareOptions = Shares.Select(p => new ShareOption(p) { IsSelected = p == _share }).ToList();
        SelectShareCommand = new RelayCommand<ShareOption>(option => Share = option.Percent);
        RemoveCorrectionCommand = new RelayCommand<CorrectionItem>(RemoveCorrection);
        RefreshCorrections();
        LearnCommand = new RelayCommand(() => _ = LearnAsync(), () => host.CanRun() && Step == VoiceTrainingStep.Intro && !IsOpen && HasPending);
        HasPending = CanUpdate;

        OpenCommand = new RelayCommand(() => _ = OpenAsync(), host.CanRun);
        CloseCommand = new RelayCommand(Close);
        RecordCommand = new RelayCommand(StartReading, () => Remaining > 0);
        NextCommand = new RelayCommand(Next, () => Step == VoiceTrainingStep.Reading);
        RedoCommand = new RelayCommand(Redo);
        StopReadingCommand = new RelayCommand(StopReading);
        ClearRecordingsCommand = new RelayCommand(ClearRecordings, () => _data.Recordings.Count > 0);
        TrainCommand = new RelayCommand(() => _ = TrainAsync(CanUpdate), () => CanTrain);
        RetrainCommand = new RelayCommand(() => _ = TrainAsync(update: false), () => CanRetrain);
        UseCommand = new RelayCommand(Use, () => CanUse);
        DiscardCommand = new RelayCommand(Discard);
        RemoveModelCommand = new RelayCommand(RemoveModel, () => HasModel);
        AddSentenceCommand = new RelayCommand(AddSentence, () => !string.IsNullOrWhiteSpace(NewSentence));
        RemoveSentenceCommand = new RelayCommand<string>(sentence =>
        {
            CustomSentences.Remove(sentence);
            _host.Changed();
            RefreshIntro();
        });
    }

    public IReadOnlyList<SentenceSet> Languages => TrainingSentences.All;
    public ObservableCollection<string> CustomSentences { get; } = [];
    public ObservableCollection<TrainingExample> Examples { get; } = [];
    public IReadOnlyList<ShareOption> ShareOptions { get; }
    public ObservableCollection<CorrectionItem> Corrections { get; } = [];

    public ICommand RemoveCorrectionCommand { get; }
    public ICommand SelectShareCommand { get; }
    public ICommand LearnCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand StopReadingCommand { get; }
    public ICommand ClearRecordingsCommand { get; }
    public ICommand TrainCommand { get; }
    public ICommand RetrainCommand { get; }
    public ICommand UseCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand RemoveModelCommand { get; }
    public ICommand AddSentenceCommand { get; }
    public ICommand RemoveSentenceCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public VoiceTrainingStep Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    // The language the sentences are in: the one the user dictates in. Each language has its own recordings.
    public SentenceSet Language
    {
        get => _language;
        set
        {
            if (value is null || !SetProperty(ref _language, value))
                return;
            _data = VoiceDataset.Open(value.Code);
            IndexSentences();
            _host.Changed();
            RefreshIntro();
        }
    }

    // How much of the sentence list is read and trained on, in percent. Fewer sentences train faster but
    // teach the model less. The user's own sentences and corrections always count, whatever the share.
    public int Share
    {
        get => _share;
        set
        {
            if (!Shares.Contains(value) || !SetProperty(ref _share, value))
                return;
            foreach (var option in ShareOptions)
                option.IsSelected = option.Percent == value;
            _host.Changed();
            RefreshIntro();
        }
    }

    private void IndexSentences()
    {
        _positions = [];
        for (int i = 0; i < Language.Sentences.Count; i++)
            _positions.TryAdd(Language.Sentences[i], i);
    }

    // How many of the language's standard sentences a share covers.
    private int StandardCount(int percent) => (int)Math.Ceiling(Language.Sentences.Count * percent / 100.0);

    private bool IsStandard(string text) => _positions.ContainsKey(text);

    // A recording is left out only when it is a standard sentence beyond the chosen share.
    private bool IsUsed(VoiceRecording recording, int percent) =>
        !_positions.TryGetValue(recording.Text, out var position) || position < StandardCount(percent);

    public string NewSentence
    {
        get => _newSentence;
        set => SetProperty(ref _newSentence, value);
    }

    public string IntroMessage
    {
        get => _introMessage;
        private set => SetProperty(ref _introMessage, value);
    }

    // -- intro: where things stand

    // The sentences the chosen share asks for, and the recordings it uses.
    private IEnumerable<string> AllSentences => Language.Sentences.Take(StandardCount(Share)).Concat(CustomSentences);
    private List<VoiceRecording> Used => _data.Recordings.Where(r => IsUsed(r, Share)).ToList();
    private int Remaining => AllSentences.Count(s => !_data.Has(s));
    private int Recorded => _data.Recordings.Count(r => IsUsed(r, Share));
    private int LeftOut => _data.Recordings.Count - Recorded;

    public string RecordedText
    {
        get
        {
            var total = Recorded + Remaining;
            if (_data.Recordings.Count == 0)
                return $"{total} sentences to read, about {Math.Max(1, total / 10)} minutes. You can stop at any time and continue later.";
            var minutes = Used.Sum(r => r.Seconds) / 60;
            var text = $"{Recorded} of {total} sentences recorded · {(minutes < 1 ? "under a minute" : $"{minutes:0} min")} of your voice";
            return LeftOut == 0 ? text : $"{text} · {LeftOut} more recorded, not used at {Share}%";
        }
    }

    public double RecordedFraction => Recorded + Remaining == 0 ? 0 : (double)Recorded / (Recorded + Remaining);
    public string RecordText => Recorded == 0 ? "Record sentences" : Remaining == 0 ? "All recorded" : "Continue recording";
    public bool HasRecordings => _data.Recordings.Count > 0;

    // -- how long training takes, per share

    // Seconds per training step: measured on this PC during the last training of that model, or a default.
    private double StepSeconds => _stepSeconds.TryGetValue(BaseModel, out var seconds) ? seconds : VoiceTrainer.DefaultStepSeconds(BaseModel);

    // Loading the model, measuring it before, saving it and the before/after test: about the same every time.
    private double FixedSeconds => 60 + 8 * StepSeconds;

    private double TrainSeconds(int percent)
    {
        var used = _data.Recordings.Where(r => IsUsed(r, percent)).ToList();
        var unread = Language.Sentences.Take(StandardCount(percent)).Concat(CustomSentences).Count(s => !_data.Has(s));
        // About one sentence in five is held back while the right length of training is worked out.
        var trained = used.Count(r => !r.HeldOut) + (int)Math.Round(unread * 0.8);
        var heldBack = used.Count + unread - trained;
        return FixedSeconds + VoiceTrainer.TrainSteps(trained, heldBack) * StepSeconds;
    }

    private double UpdateSeconds => FixedSeconds +
        VoiceTrainer.UpdateSteps(NewRecordings.Count, Used.Count(r => !r.HeldOut) - NewRecordings.Count) * StepSeconds;

    private static string About(double seconds) => seconds < 90 ? "about 1 min" : $"about {Math.Round(seconds / 60):0} min";

    public string ShareText
    {
        get
        {
            var standard = StandardCount(Share);
            var own = CustomSentences.Count(s => !IsStandard(s));
            var text = Share == 100
                ? $"All {Language.Sentences.Count} sentences"
                : $"The first {standard} of {Language.Sentences.Count} sentences";
            if (own > 0)
                text += $", plus your {own} own";
            return text + (Share == 100
                ? ": the most complete training."
                : ". Fewer sentences train faster, but the model learns less about your voice.");
        }
    }

    public string ShareNote => _stepSeconds.ContainsKey(BaseModel)
        ? "Times are estimates from your last training on this PC."
        : "Times are rough estimates; they adjust to this PC after the first training.";

    public string CardText =>
        !_cardChecked ? "Looking for a graphics card…" :
        _card is null ? "No NVIDIA graphics card found. Voice training needs one, so it can't run on this PC." :
        _card.MemoryMb < TrainingKit.MinMemoryMb ? $"{_card.Name} has too little memory for voice training ({_card.MemoryMb / 1024.0:0.#} GB)." :
        $"Graphics card: {_card.Name} · {_card.MemoryMb / 1024.0:0.#} GB";

    public bool CardProblem => _cardChecked && (_card is null || _card.MemoryMb < TrainingKit.MinMemoryMb);

    public string KitText => TrainingKit.IsInstalled
        ? "Training tools: installed"
        : "Training tools: downloaded the first time you train (about 5 GB, one time only)";

    // The model that gets retrained: the one in use, or the one the current voice model was made from.
    public string BaseModel
    {
        get
        {
            var model = _host.GetModel();
            return model == PersonalModel.Name ? PersonalModel.Info?.BaseModel ?? "turbo" : model;
        }
    }

    public string BaseModelText => _host.GetModel() == PersonalModel.Name
        ? $"Model to train: {BaseModel} (the one your voice model was made from)"
        : $"Model to train: {BaseModel} (the one selected in the toolbar)";

    public bool HasModel => PersonalModel.Exists;

    public string ModelText
    {
        get
        {
            if (PersonalModel.Info is not { } info)
                return HasModel ? "You have a voice model." : "";
            var language = TrainingSentences.All.FirstOrDefault(l => l.Code == info.Language)?.Name ?? info.Language;
            return info.TestSentences > 0
                ? $"Trained {info.Trained:d MMM yyyy} on {info.Sentences} sentences, {info.TestSentences} of them also used as the before/after test · " +
                  $"{info.BaseModel} · {language} · {info.AccuracyBefore:P0} → {info.AccuracyAfter:P0} of words right on sentences it had not practised yet"
                : $"Trained {info.Trained:d MMM yyyy} on {info.Sentences} sentences · {info.BaseModel} · {language} · " +
                  $"{info.AccuracyBefore:P0} → {info.AccuracyAfter:P0} of words right";
        }
    }

    // Recordings made since the voice model was trained. They can be added to it with a short training,
    // as long as it is the same language and the same underlying model.
    private IReadOnlyList<VoiceRecording> NewRecordings =>
        PersonalModel.Info is { Files: not null } info && info.Language == Language.Code && info.BaseModel == BaseModel
            ? _data.Recordings.Where(r => !r.HeldOut && IsUsed(r, Share) && !info.Includes(r)).ToList()
            : [];

    // The fewest sentences a training needs: 30, or the whole share when it holds fewer than that.
    private int Needed => Math.Min(MinSentences, Recorded + Remaining);
    // A training needs sentences to learn from and at least one kept apart to check the result on.
    private bool HasEnough => Recorded >= Needed && Used.Any(r => r.HeldOut) && Used.Any(r => !r.HeldOut);

    public bool CanUpdate => NewRecordings.Count is > 0 and <= MaxUpdateSentences;
    private bool CanRun => Step == VoiceTrainingStep.Intro && _cardChecked && !CardProblem;
    public bool CanRetrain => CanRun && HasEnough;
    public bool CanTrain => CanRun && (CanUpdate || HasEnough);

    public string TrainText => (CanUpdate ? NewRecordings.Count : 0) switch
    {
        0 => HasModel ? "Retrain my voice model" : "Train my voice model",
        1 => "Add 1 new sentence to my model",
        var n => $"Add {n} new sentences to my model"
    };

    public string TrainHint =>
        CanUpdate ? $"Only the new sentences are trained, on top of your voice model: {About(UpdateSeconds)}." :
        Recorded < Needed ? $"Record at least {Needed} sentences to train ({Recorded} so far)." :
        !HasEnough ? "Record a few more sentences: one in five is kept apart to check the result, and none has been yet." :
        $"Training on all {Recorded} sentences takes {About(TrainSeconds(Share))}; {Used.Count(r => r.HeldOut)} of them are also the before/after test." +
            (Recorded < MinSentences ? " With this few sentences the model learns little, and the before/after check is rough." :
             Remaining > 0 ? " The more sentences you record, the better it learns your voice." : "");

    // -- sentences waiting to be learned, shown in the main window (corrections made in the transcript end up here)

    // Kept as a field: the main window's button asks for it on every keystroke and mouse move.
    public bool HasPending { get; private set; }

    public string PendingText => NewRecordings.Count == 1
        ? "1 sentence is waiting to be learned by your voice model."
        : $"{NewRecordings.Count} sentences are waiting to be learned by your voice model.";

    public string LearnText => $"Teach my voice model · {About(UpdateSeconds)}";

    // Called when something outside the dialog changed what it shows (the selected model, for one).
    public void Refresh() => RefreshIntro();

    private void RefreshIntro()
    {
        HasPending = CanUpdate;
        RefreshCorrections();
        foreach (var option in ShareOptions)
            option.TimeText = About(TrainSeconds(option.Percent));
        foreach (var name in new[]
                 {
                     nameof(RecordedText), nameof(RecordedFraction), nameof(RecordText), nameof(HasRecordings), nameof(CardText),
                     nameof(CardProblem), nameof(KitText), nameof(BaseModelText), nameof(HasModel), nameof(ModelText),
                     nameof(CanTrain), nameof(TrainHint), nameof(TrainText), nameof(CanUpdate), nameof(ShareText), nameof(ShareNote),
                     nameof(HasPending), nameof(PendingText), nameof(LearnText)
                 })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task OpenAsync()
    {
        IntroMessage = "";
        Step = VoiceTrainingStep.Intro;
        IsOpen = true;
        RefreshIntro();
        if (_cardChecked)
            return;
        _card = await TrainingKit.FindGraphicsCardAsync();
        _cardChecked = true;
        RefreshIntro();
    }

    // Opens the dialog straight on the short training that adds the waiting sentences to the voice model.
    private async Task LearnAsync()
    {
        await OpenAsync();
        if (CanTrain && CanUpdate)
            await TrainAsync(update: true);
    }

    // A sentence from live dictation whose transcript the user corrected. Its audio joins the recordings
    // (always trained on, never held back), so the voice model can learn how the user says it.
    // The same sentence can be corrected many times: each time it was said is kept, which is what
    // teaches the model a word the user says in an unusual way.
    // replaces: the file of an earlier correction of this very line, when the user corrects it again.
    // Returns the saved recording's file, or a problem to show.
    public (string? File, string? Problem) AddCorrection(string language, string text, byte[] wav, string? replaces = null)
    {
        if (Step != VoiceTrainingStep.Intro)
            return (null, "Voice training is busy. Try again when it has finished.");
        if (TrainingSentences.All.FirstOrDefault(l => l.Code == language) is not { } set)
            return (null, "Voice training doesn't cover this language.");
        if (Language != set)
            Language = set;

        var pcm = AudioFiles.WavToPcm(wav);
        if (VoiceDataset.Check(pcm) == RecordingProblem.TooLoud)
            return (null, "That recording is too distorted to learn from.");
        string file;
        try
        {
            if (replaces is not null)
                _data.RemoveFile(replaces);
            file = _data.Add(text, pcm, canHoldOut: false, corrected: true).File;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Couldn't save the recording: {ex.Message}");
        }
        RefreshIntro();
        return (file, null);
    }

    // -- the sentences corrected with Review, listed in the dialog

    // A recording that came from Review. Ones saved before they were marked are recognised by their text:
    // neither a standard sentence nor one of the user's own.
    private bool IsCorrection(VoiceRecording recording) =>
        recording.Corrected || (!IsStandard(recording.Text) && !CustomSentences.Contains(recording.Text));

    public bool HasCorrections => Corrections.Count > 0;

    private void RefreshCorrections()
    {
        var info = PersonalModel.Info;
        var items = _data.Recordings.Where(IsCorrection)
            .Select(r => new CorrectionItem(r.Text, info is not null && info.Includes(r), r.File))
            .ToList();
        if (items.SequenceEqual(Corrections))
            return;
        Corrections.Clear();
        foreach (var item in items)
            Corrections.Add(item);
        OnPropertyChanged(nameof(HasCorrections));
    }

    // Deletes the recording. What the voice model already learned from it stays until the next full training.
    private void RemoveCorrection(CorrectionItem item)
    {
        try
        {
            _data.RemoveFile(item.File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IntroMessage = $"Couldn't delete the recording: {ex.Message}";
        }
        RefreshIntro();
    }

    public void WriteTo(AppState state)
    {
        state.VoiceCheckLanguage = Language.Code;
        state.VoiceCheckSentences = CustomSentences.ToList();
        state.VoiceTrainingShare = Share;
        state.TrainingStepSeconds = new(_stepSeconds);
    }

    private void AddSentence()
    {
        var sentence = NewSentence.Trim();
        if (!CustomSentences.Contains(sentence, StringComparer.OrdinalIgnoreCase))
            CustomSentences.Add(sentence);
        NewSentence = "";
        _host.Changed();
        RefreshIntro();
    }

    private void ClearRecordings()
    {
        _data.Clear();
        IntroMessage = "";
        RefreshIntro();
    }

    private void RemoveModel()
    {
        var madeFrom = BaseModel;
        PersonalModel.Remove();
        _host.ModelsChanged(madeFrom);
        RefreshIntro();
    }

    // -- reading

    public string CurrentSentence
    {
        get => _currentSentence;
        private set => SetProperty(ref _currentSentence, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string ReadingMessage
    {
        get => _readingMessage;
        private set => SetProperty(ref _readingMessage, value);
    }

    public bool ReadingProblem
    {
        get => _readingProblem;
        private set => SetProperty(ref _readingProblem, value);
    }

    public double Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    private void StartReading()
    {
        var device = _host.GetMicrophone();
        if (device < 0)
        {
            IntroMessage = "Choose a microphone in the toolbar first.";
            return;
        }

        _pending = AllSentences.Where(s => !_data.Has(s)).ToList();
        if (_pending.Count == 0)
            return;
        var recorder = new MicRecorder();
        recorder.LevelChanged += level => _host.Post(() => Level = MainViewModel.MeterLevel(level, Level));
        recorder.Failed += ex => _host.Post(() => ShowReadingProblem($"The microphone stopped: {ex.Message}"));
        try
        {
            recorder.Start(device);
        }
        catch (Exception ex)
        {
            IntroMessage = $"Couldn't open the microphone: {ex.Message}";
            return;
        }

        _recorder = recorder;
        IntroMessage = "";
        ShowSentence();
        Step = VoiceTrainingStep.Reading;
    }

    private void ShowSentence()
    {
        CurrentSentence = _pending[0];
        PositionText = $"Sentence {Recorded + 1} of {Recorded + _pending.Count}";
        ReadingMessage = "Read it aloud the way you normally speak, then press Next (or Enter).";
        ReadingProblem = false;
    }

    private void ShowReadingProblem(string message)
    {
        ReadingMessage = message;
        ReadingProblem = true;
    }

    private void Next()
    {
        if (_recorder is null || Step != VoiceTrainingStep.Reading)
            return;
        var pcm = _recorder.Take();
        switch (VoiceDataset.Check(pcm))
        {
            case RecordingProblem.Silent:
                ShowReadingProblem("I didn't hear anything. Check the microphone, then read the sentence again.");
                return;
            case RecordingProblem.TooLoud:
                ShowReadingProblem("That was too loud and distorted. Move back a little or lower the microphone's level, then read it again.");
                return;
        }

        try
        {
            _data.Add(_pending[0], pcm, canHoldOut: Language.Sentences.Contains(_pending[0]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowReadingProblem($"Couldn't save the recording: {ex.Message}");
            return;
        }
        _pending.RemoveAt(0);
        if (_pending.Count > 0)
        {
            ShowSentence();
            return;
        }

        StopReading();
        IntroMessage = "";
    }

    // Forget what was recorded for the current sentence and let the user read it again.
    private void Redo()
    {
        _recorder?.Take();
        ShowSentence();
    }

    // Back to the overview; what was read so far is kept.
    private void StopReading()
    {
        _ = StopRecorderAsync();
        Step = VoiceTrainingStep.Intro;
        RefreshIntro();
    }

    private async Task StopRecorderAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        Level = 0;
        if (recorder is not null)
            await recorder.StopAsync();
    }

    // -- training

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string ProgressDetail
    {
        get => _progressDetail;
        private set => SetProperty(ref _progressDetail, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    private void Report(string text, double? fraction) => _host.Post(() =>
    {
        ProgressText = text;
        if (fraction is { } value)
            Progress = Math.Clamp(value, 0, 1);
    });

    // Installs the training tools if needed, trains, then measures before and after on the held-back
    // sentences with the same engine live dictation uses.
    // update: add the recordings made since the last training to the existing voice model (quick);
    // otherwise train the standard model on every recording.
    public async Task TrainAsync(bool update = false)
    {
        var baseModel = BaseModel;
        var language = Language.Code;
        var data = _data;
        var used = Used;
        IReadOnlyList<VoiceRecording> train = used.Where(r => !r.HeldOut).ToList();
        IReadOnlyList<VoiceRecording> heldOut = used.Where(r => r.HeldOut).ToList();
        var added = update && CanUpdate ? NewRecordings : [];
        var existing = PersonalModel.Info;
        update = added.Count > 0 && existing is not null;
        IReadOnlyList<VoiceRecording> known = train.Except(added).ToList();
        // The user's own sentences and corrections: practised more, and until they come out right.
        IReadOnlyList<VoiceRecording> own = train.Where(r => r.Corrected || !IsStandard(r.Text)).ToList();
        // What the new model is compared with: the voice model it updates, or the standard model.
        var reference = update ? PersonalModel.Name : baseModel;

        Step = VoiceTrainingStep.Working;
        Progress = 0;
        BeforeLabel = update ? "Before learning" : $"Base model ({baseModel})";
        AfterLabel = update ? "After learning" : "Your trained model";
        using var cancellation = new CancellationTokenSource();
        _work = cancellation;
        var ct = cancellation.Token;
        try
        {
            if (!TrainingKit.IsInstalled)
            {
                ProgressDetail = "One-time download of about 5 GB. It continues where it stopped if you cancel.";
                await Task.Run(() => TrainingKit.InstallAsync(p => Report(p.Text, p.Fraction), ct), ct);
            }

            Progress = 0;
            ProgressDetail = update
                ? $"Only the new sentences are trained, on top of your voice model: {About(UpdateSeconds)}. Keep games closed meanwhile."
                : $"Training on all {used.Count} sentences takes {About(TrainSeconds(Share))}: first with {heldOut.Count} held back to find how long to train, then on every one. " +
                  "Keep games closed meanwhile: training needs the whole graphics card.";
            var result = await Task.Run(() => update
                ? VoiceTrainer.UpdateAsync(language, data, known, heldOut, added, PersonalModel.CandidatePath, p => Report(p.Text, p.Fraction), ct)
                : VoiceTrainer.TrainAsync(baseModel, language, data, train, heldOut, own, PersonalModel.CandidatePath, p => Report(p.Text, p.Fraction), ct), ct);
            // What a step really takes on this PC makes the next time estimate better. A short update isn't
            // measured: its checks between rounds weigh too much in so few steps.
            if (!update && result.Steps > 0)
            {
                _stepSeconds[baseModel] = result.Duration.TotalSeconds / result.Steps;
                _host.Changed();
            }

            Examples.Clear();
            if (!result.Improved)
            {
                ShowNoGain(update
                    ? "Your voice model already writes the new sentences as well as a short training could make it, so it stays as it is."
                    : "The model already understands these recordings as well as training could make it.");
                return;
            }

            // The model as it was and the new one write out the same sentences: for an update the sentences
            // just added (the test sentences were trained on long ago, so they would show nothing), for a
            // full training the ones held back.
            IReadOnlyList<VoiceRecording> tested = update ? added : heldOut;
            ProgressDetail = update
                ? $"Your voice model, before and after learning, writes out the new {(added.Count == 1 ? "sentence" : "sentences")}."
                : $"The base model and your trained model each write out the {heldOut.Count} test sentences.";
            var firstText = update ? "Testing your voice model as it was…" : "Testing the model as it was…";
            Report(firstText, 0);
            var before = await Task.Run(() => VoiceTester.TestAsync(reference, language, data, tested, p => Report(firstText, p / 2), ct), ct);
            var after = await Task.Run(() => VoiceTester.TestAsync(PersonalModel.CandidateName, language, data, tested,
                p => Report(update ? "Testing the updated model…" : "Testing your voice model…", 0.5 + p / 2), ct), ct);

            BeforeText = $"{before.Accuracy:P0}";
            AfterText = $"{after.Accuracy:P0}";
            // An update shows every sentence it added; a full training the test sentences that changed.
            for (int i = 0; i < tested.Count && Examples.Count < MaxExamples; i++)
                if (update || before.Sentences[i].Heard != after.Sentences[i].Heard)
                    Examples.Add(new TrainingExample(tested[i].Text, Shown(before.Sentences[i].Heard), Shown(after.Sentences[i].Heard)));

            // A full training ends by training on the test sentences too, so the finished model has practised
            // them. What it wrote for them just before that is the fair measure, and what the verdict uses.
            var fair = !update && result.FairTexts is { } texts && texts.Count == heldOut.Count
                ? new VoiceTestResult(heldOut.Select((r, i) => Scoring.Compare(r.Text, texts[i])).ToList())
                : null;
            var judged = fair ?? after;
            if (update)
            {
                var sentences = added.Count == 1 ? "sentence" : $"{added.Count} sentences";
                ResultDetail = $"Share of words written correctly in the {sentences} you added, before and after your voice model practised " +
                               $"{(added.Count == 1 ? "it" : "them")}.";
                if (after.Errors > before.Errors)
                {
                    ResultTitle = "The update made the new sentences worse, so nothing changes";
                    CanUse = false;
                    PersonalModel.DiscardCandidate();
                }
                else
                {
                    ResultTitle = after.Errors < before.Errors
                        ? $"Your voice model learned the new {(added.Count == 1 ? "sentence" : "sentences")}"
                        : added.Count == 1 ? "The new sentence comes out the same as before" : "The new sentences come out the same as before";
                    CanUse = true;
                }
            }
            else
            {
                ResultDetail = $"Share of words written correctly in the {heldOut.Count} sentences used as the before/after test.";
                if (fair is not null)
                    ResultDetail += $" Your voice model has trained on all {used.Count} of your sentences, these {heldOut.Count} included, so its score here " +
                                    $"is flattering. Before it had practised them it wrote {fair.Accuracy:P0} of their words right: that is the fair measure.";
                if (judged.Errors < before.Errors)
                {
                    ResultTitle = "Your voice model understands you better";
                    CanUse = true;
                }
                else if (judged.Errors == before.Errors)
                {
                    ResultTitle = "No difference on the test sentences";
                    ResultDetail += " The model was already doing as well as it could on them; more recordings may show a difference.";
                    CanUse = true;
                }
                else
                {
                    ResultTitle = "Training made it worse, so nothing changes";
                    ResultDetail += " This can happen with few recordings. Record more sentences and train again.";
                    CanUse = false;
                    PersonalModel.DiscardCandidate();
                }
            }
            // The recordings the model now knows: an update adds its new ones to those it had; a full
            // training knows exactly the ones the chosen share used.
            var files = update
                ? (existing!.Files ?? []).Concat(added.Select(r => r.File)).Distinct().ToList()
                : used.Select(r => r.File).ToList();
            // The score kept with the model is the fair one from its full training; an update doesn't
            // measure the model as a whole, so it leaves that score as it was.
            _candidate = update
                ? existing! with { Trained = DateTime.Now, Sentences = existing.Sentences + added.Count, Files = files }
                : new PersonalModelInfo(baseModel, language, DateTime.Now, fair is null ? train.Count : used.Count,
                    before.Accuracy, judged.Accuracy, files, fair is null ? 0 : heldOut.Count);
            Step = VoiceTrainingStep.Results;
        }
        catch (OperationCanceledException)
        {
            PersonalModel.DiscardCandidate(); // Close() already reset the dialog
        }
        catch (Exception ex)
        {
            PersonalModel.DiscardCandidate();
            Step = VoiceTrainingStep.Intro;
            IntroMessage = ex.Message;
            RefreshIntro();
        }
        finally
        {
            _work = null;
        }
    }

    private static string Shown(string heard) => heard.Length == 0 ? "(nothing)" : heard;

    private void ShowNoGain(string detail)
    {
        BeforeText = AfterText = "";
        ResultTitle = "Training didn't change anything";
        ResultDetail = detail;
        CanUse = false;
        _candidate = null;
        Step = VoiceTrainingStep.Results;
    }

    // -- results

    public string ResultTitle
    {
        get => _resultTitle;
        private set => SetProperty(ref _resultTitle, value);
    }

    public string ResultDetail
    {
        get => _resultDetail;
        private set => SetProperty(ref _resultDetail, value);
    }

    public string BeforeText
    {
        get => _beforeText;
        private set
        {
            if (SetProperty(ref _beforeText, value))
                OnPropertyChanged(nameof(HasScores));
        }
    }

    public string AfterText
    {
        get => _afterText;
        private set => SetProperty(ref _afterText, value);
    }

    public string BeforeLabel
    {
        get => _beforeLabel;
        private set => SetProperty(ref _beforeLabel, value);
    }

    public string AfterLabel
    {
        get => _afterLabel;
        private set => SetProperty(ref _afterLabel, value);
    }

    public bool HasScores => BeforeText.Length > 0;

    public bool CanUse
    {
        get => _canUse;
        private set
        {
            if (SetProperty(ref _canUse, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Use()
    {
        if (_candidate is not { } info || !File.Exists(PersonalModel.CandidatePath))
            return;
        PersonalModel.AcceptCandidate(info);
        _candidate = null;
        _host.ModelsChanged(info.BaseModel);
        _host.UseVoiceModel(info.Language);
        Step = VoiceTrainingStep.Intro;
        IsOpen = false;
        RefreshIntro();
    }

    private void Discard()
    {
        PersonalModel.DiscardCandidate();
        _candidate = null;
        Step = VoiceTrainingStep.Intro;
        RefreshIntro();
    }

    // Cancels whatever is running (recording, download or training) and closes the dialog. Recordings are kept.
    public void Close()
    {
        _work?.Cancel();
        _ = StopRecorderAsync();
        if (Step == VoiceTrainingStep.Results)
            PersonalModel.DiscardCandidate();
        Step = VoiceTrainingStep.Intro;
        IsOpen = false;
        RefreshIntro();
    }

    public async Task ShutdownAsync()
    {
        _work?.Cancel();
        await StopRecorderAsync();
    }
}
