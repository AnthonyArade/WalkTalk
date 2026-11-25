using System.Threading.Channels;
using NAudio.Wave;

namespace WhisperStt.Core;

// Captures the microphone, cuts the stream into utterances and transcribes them in order
// on a background worker. Events are raised on background threads.
public sealed class LiveTranscriber : IAsyncDisposable
{
    private readonly WhisperEngine _engine;
    private readonly SpeechDetector _detector;
    private readonly Channel<SpeechSegment> _queue = Channel.CreateUnbounded<SpeechSegment>(new() { SingleReader = true });
    private readonly Task _worker;
    private readonly MicCapture _microphone = new();
    private int _pending;
    private bool _stopRequested;

    public LiveTranscriber(WhisperEngine engine)
    {
        _engine = engine;
        _detector = new SpeechDetector();
        _detector.LevelChanged += level => LevelChanged?.Invoke(level);
        _detector.SpeakingChanged += speaking => SpeakingChanged?.Invoke(speaking);
        _detector.SegmentReady += segment =>
        {
            Interlocked.Increment(ref _pending);
            BusyChanged?.Invoke(true);
            _queue.Writer.TryWrite(segment);
        };
        // Muted: feed silence of the same length, so timestamps stay right and the app's own voice isn't transcribed.
        _microphone.Data += (buffer, count) => _detector.Process(Muted ? new byte[count] : buffer.AsSpan(0, count));
        _microphone.Failed += ex => MicrophoneFailed?.Invoke(ex);
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event Action<float>? LevelChanged;
    public event Action<bool>? SpeakingChanged;
    public event Action<bool>? BusyChanged;               // true while utterances wait to be transcribed
    public event Action<TranscriptSegment>? Transcribed;  // times are relative to the start of capture
    public event Action<Exception>? TranscriptionFailed;
    public event Action<Exception>? MicrophoneFailed;

    // While true the microphone is ignored, e.g. while text-to-speech plays through speakers.
    public bool Muted { get; set; }

    // While true every transcribed sentence carries the audio it was heard in (TranscriptSegment.Clip).
    public bool KeepAudio { get; set; }

    public static IReadOnlyList<string> GetMicrophones() =>
        Enumerable.Range(0, WaveIn.DeviceCount).Select(i => WaveIn.GetCapabilities(i).ProductName).ToList();

    public void StartMicrophone(int deviceNumber) => _microphone.Start(deviceNumber);

    // Feeds 16 kHz mono 16-bit PCM directly, bypassing the microphone.
    public void PushAudio(ReadOnlySpan<byte> pcm) => _detector.Process(pcm);

    // Stops capture, then waits until everything already spoken has been transcribed.
    public async Task StopAsync()
    {
        if (_stopRequested)
        {
            await _worker.ConfigureAwait(false);
            return;
        }
        _stopRequested = true;

        await Task.Run(_microphone.Stop).ConfigureAwait(false);

        _detector.Flush();
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var segment in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                // Loudness alone also catches clicks and noise; only send what Silero confirms is a voice.
                using var speech = _engine.ExtractSpeech(segment.Wav);
                if (speech is null)
                    continue;
                var offset = segment.Offset + speech.Offset;
                var clip = KeepAudio ? new SpokenClip(speech.Wav.ToArray()) : null;
                await foreach (var s in _engine.TranscribeAsync(speech.Wav).ConfigureAwait(false))
                    Transcribed?.Invoke(s with { Start = s.Start + offset, End = s.End + offset, Clip = clip });
            }
            catch (Exception ex)
            {
                TranscriptionFailed?.Invoke(ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) == 0)
                    BusyChanged?.Invoke(false);
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
