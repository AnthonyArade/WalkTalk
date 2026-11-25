namespace WhisperStt.Core;

// Keeps the microphone open and hands out what was recorded since the last Take(), as raw
// Whisper-format PCM. Used by voice training to record one sentence after another.
// Events are raised on a background thread.
public sealed class MicRecorder
{
    private readonly Lock _gate = new();
    private readonly MemoryStream _buffer = new();
    private readonly MicCapture _capture = new();

    public MicRecorder()
    {
        _capture.Data += (buffer, count) =>
        {
            lock (_gate)
                _buffer.Write(buffer, 0, count);
            LevelChanged?.Invoke(MathF.Min(1f, MathF.Sqrt(AudioFiles.Rms(buffer.AsSpan(0, count))) * 2.5f));
        };
        _capture.Failed += ex => Failed?.Invoke(ex);
    }

    public event Action<float>? LevelChanged;   // 0..1, for a level meter
    public event Action<Exception>? Failed;

    public void Start(int deviceNumber) => _capture.Start(deviceNumber);

    // Everything recorded since the previous call (or since Start), which is then forgotten.
    public byte[] Take()
    {
        lock (_gate)
        {
            var pcm = _buffer.ToArray();
            _buffer.SetLength(0);
            return pcm;
        }
    }

    public Task StopAsync() => Task.Run(_capture.Stop);
}
