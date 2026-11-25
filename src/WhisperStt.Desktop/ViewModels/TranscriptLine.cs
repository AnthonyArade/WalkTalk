using System.Windows.Input;
using WhisperStt.Core;
using WhisperStt.Desktop.Mvvm;

namespace WhisperStt.Desktop.ViewModels;

// What the transcript does when the user reviews a line: open the editor, save the correction, or give up.
public sealed record ReviewActions(Action<TranscriptLine> Start, Action<TranscriptLine> Save, Action<TranscriptLine> Cancel);

// One sentence of the transcript.
// Command: set when the sentence was a voice command ("Clean", "Send") rather than text to write.
// Heard: what the model wrote, before the user's word replacements. Clip: the audio it was heard in,
// kept for the latest sentences while the voice model is in use, so a wrong transcript can be corrected
// and learned from. Language: the language that audio was dictated in.
public sealed class TranscriptLine : ObservableObject
{
    private readonly VoiceCommand? _command;
    private string _text, _editText = "", _reviewNote = "";
    private SpokenClip? _clip;
    private bool _isReviewing, _reviewProblem, _isCorrected;

    public TranscriptLine(DateTime time, string text, IReadOnlyList<DeliveryViewModel> deliveries, VoiceCommand? command = null,
        string? heard = null, SpokenClip? clip = null, string language = "", ReviewActions? review = null)
    {
        TimeText = time.ToString("HH:mm:ss");
        _text = text;
        Deliveries = deliveries;
        _command = command;
        Heard = heard ?? text;
        _clip = clip;
        Language = language;
        ReviewCommand = new RelayCommand(() => review?.Start(this), () => CanReview);
        SaveReviewCommand = new RelayCommand(() => review?.Save(this), () => IsReviewing);
        CancelReviewCommand = new RelayCommand(() => review?.Cancel(this), () => IsReviewing);
    }

    public string TimeText { get; }
    public IReadOnlyList<DeliveryViewModel> Deliveries { get; }
    public bool HasDeliveries => Deliveries.Count > 0;
    public bool IsCommand => _command is not null;
    public string Language { get; }
    public string Heard { get; set; }
    // The recording a saved correction of this line became, so correcting the line again replaces it.
    public string? CorrectionFile { get; set; }

    public ICommand ReviewCommand { get; }
    public ICommand SaveReviewCommand { get; }
    public ICommand CancelReviewCommand { get; }

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public string CommandText => _command switch
    {
        VoiceCommand.Clear => "Voice command: clear the fields",
        VoiceCommand.Send => "Voice command: send (Enter)",
        _ => ""
    };

    public SpokenClip? Clip
    {
        get => _clip;
        set
        {
            if (SetProperty(ref _clip, value))
                OnPropertyChanged(nameof(CanReview));
        }
    }

    // The Review button shows while the sentence's audio is still kept.
    public bool CanReview => Clip is not null && !IsCommand && !IsReviewing;

    public bool IsReviewing
    {
        get => _isReviewing;
        set
        {
            if (SetProperty(ref _isReviewing, value))
                OnPropertyChanged(nameof(CanReview));
        }
    }

    // The sentence as the user is retyping it.
    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    // The hint under the editor, or what went wrong when saving.
    public string ReviewNote
    {
        get => _reviewNote;
        set => SetProperty(ref _reviewNote, value);
    }

    public bool ReviewProblem
    {
        get => _reviewProblem;
        set => SetProperty(ref _reviewProblem, value);
    }

    // True once a correction of this sentence was saved for the voice model.
    public bool IsCorrected
    {
        get => _isCorrected;
        set => SetProperty(ref _isCorrected, value);
    }
}

public enum DeliveryState { Pending, Sent, Failed }

// Whether one sentence reached one field.
public sealed class DeliveryViewModel(string targetName) : ObservableObject
{
    private DeliveryState _state;
    private string? _error;

    public string TargetName { get; } = targetName;

    public DeliveryState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(Label));
        }
    }

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    public string Label => State switch
    {
        DeliveryState.Sent => $"{TargetName} ✓",
        DeliveryState.Failed => $"{TargetName} ✕",
        _ => $"{TargetName} …"
    };
}
