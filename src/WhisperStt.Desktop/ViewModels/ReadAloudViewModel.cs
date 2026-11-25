using System.Windows.Input;
using WhisperStt.Core.Tts;
using WhisperStt.Desktop.Mvvm;
using WhisperStt.Desktop.Services;

namespace WhisperStt.Desktop.ViewModels;

// TTS (text-to-speech): speaks each transcribed sentence with a Kokoro voice, on this PC.
public sealed class ReadAloudViewModel : ObservableObject, IDisposable
{
    private const string PreviewText = "Hi! This is how I sound when I read your sentences aloud.";

    private readonly Action _changed;
    private readonly Action<Action> _post;
    private Task<TextToSpeech?>? _loading;
    private TextToSpeech? _tts;
    private bool _isOpen, _isEnabled, _pauseMic, _isReading;
    private KokoroVoice _voice;
    private double _speed, _volume;
    private string _status = "Off";
    private string? _busySummary; // shown on the toolbar button while downloading or loading

    public ReadAloudViewModel(AppState state, Action changed, Action<Action> post)
    {
        _changed = changed;
        _post = post;
        _isEnabled = state.ReadAloud;
        _voice = KokoroVoices.Find(state.ReadAloudVoice);
        _speed = Math.Clamp(state.ReadAloudSpeed, 0.6, 1.6);
        _volume = Math.Clamp(state.ReadAloudVolume, 0, 1);
        _pauseMic = state.PauseMicWhileReading;

        OpenCommand = new RelayCommand(() => IsOpen = true);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        if (_isEnabled)
            _ = EnsureLoadedAsync();
    }

    // Raised with true when a sentence starts playing and false when the queue is empty.
    public event Action<bool>? ReadingChanged;

    public IReadOnlyList<KokoroVoice> Voices => KokoroVoices.All;
    public ICommand OpenCommand { get; }
    public ICommand PreviewCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
                return;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(ToggleText));
            _changed();
            if (value && _tts is not null)
                Status = "Ready"; // already loaded earlier in this session
            else if (value)
                _ = EnsureLoadedAsync();
            else
            {
                _tts?.Stop();
                Status = "Off";
            }
        }
    }

    public KokoroVoice Voice
    {
        get => _voice;
        set
        {
            if (value is null || !SetProperty(ref _voice, value))
                return;
            if (_tts is not null)
            {
                _tts.Voice = value;
                _ = _tts.PreloadAsync(value.Accent);
            }
            OnPropertyChanged(nameof(Summary));
            _changed();
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            if (!SetProperty(ref _speed, Math.Round(value, 2)))
                return;
            if (_tts is not null)
                _tts.Speed = (float)_speed;
            OnPropertyChanged(nameof(SpeedText));
            _changed();
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, Math.Round(value, 2)))
                return;
            if (_tts is not null)
                _tts.Volume = (float)_volume;
            OnPropertyChanged(nameof(VolumeText));
            _changed();
        }
    }

    // Through speakers the microphone would hear the voice and transcribe it; not needed with headphones.
    public bool PauseMic
    {
        get => _pauseMic;
        set
        {
            if (SetProperty(ref _pauseMic, value))
                _changed();
        }
    }

    public bool IsReading
    {
        get => _isReading;
        private set => SetProperty(ref _isReading, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string SpeedText => $"{Speed:0.00}×";
    public string VolumeText => $"{Volume:P0}";
    public string Summary => _busySummary ?? (IsEnabled ? Voice.Name : "Off");

    // The switch's label names what flipping it will do.
    public string ToggleText => IsEnabled ? "Disable TTS" : "Enable TTS";

    private void SetBusySummary(string? text)
    {
        _busySummary = text;
        OnPropertyChanged(nameof(Summary));
    }

    public void WriteTo(AppState state)
    {
        state.ReadAloud = IsEnabled;
        state.ReadAloudVoice = Voice.Id;
        state.ReadAloudSpeed = Speed;
        state.ReadAloudVolume = Volume;
        state.PauseMicWhileReading = PauseMic;
    }

    // Reads a transcribed sentence, if read-aloud is on and the voice has finished loading.
    public void Speak(string text)
    {
        if (IsEnabled)
            _tts?.Speak(text);
    }

    private async Task PreviewAsync()
    {
        var tts = await EnsureLoadedAsync();
        if (tts is null)
            return;
        tts.Stop();
        tts.Speak(PreviewText);
    }

    private Task<TextToSpeech?> EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task<TextToSpeech?> LoadAsync()
    {
        Status = "Loading the voice...";
        SetBusySummary("Loading…");
        try
        {
            var tts = await TextToSpeech.LoadAsync(Voice,
                message => _post(() =>
                {
                    Status = message;
                    SetBusySummary(message.StartsWith("Unpacking") ? "Unpacking…" : "Loading…");
                }),
                progress => _post(() =>
                {
                    Status = $"Downloading the voice model, shared by all voices (one time only): {progress}";
                    SetBusySummary(progress.Fraction is { } done ? $"Downloading {done:P0}" : "Downloading…");
                }));
            tts.Speed = (float)Speed;
            tts.Volume = (float)Volume;
            tts.SpeakingChanged += reading => _post(() =>
            {
                IsReading = reading;
                ReadingChanged?.Invoke(reading);
            });
            tts.Failed += ex => _post(() => Status = $"Couldn't read a sentence: {ex.Message}");
            _tts = tts;
            Status = "Ready";
            return tts;
        }
        catch (Exception ex)
        {
            _loading = null; // allow another try; a partial download resumes
            Status = $"Couldn't load the voice: {ex.Message}";
            return null;
        }
        finally
        {
            SetBusySummary(null);
        }
    }

    public void Dispose() => _tts?.Dispose();
}
