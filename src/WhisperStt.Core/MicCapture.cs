using NAudio.Wave;

namespace WhisperStt.Core;

// A microphone, opened in the format Whisper wants. Sound arrives every 50 ms on a background thread.
public sealed class MicCapture
{
    private readonly ManualResetEventSlim _firstData = new();
    private readonly ManualResetEventSlim _stopped = new();
    private WaveIn? _waveIn;
    private bool _stopRequested;

    public event Action<byte[], int>? Data;     // a buffer, and how many of its bytes were just recorded
    public event Action<Exception>? Failed;     // the microphone stopped by itself (unplugged, taken away)

    public void Start(int deviceNumber)
    {
        _waveIn = new WaveIn
        {
            DeviceNumber = deviceNumber,
            WaveFormat = AudioFiles.WhisperFormat,
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += (_, e) =>
        {
            Data?.Invoke(e.Buffer, e.BytesRecorded);
            _firstData.Set();
        };
        _waveIn.RecordingStopped += (_, e) =>
        {
            _stopped.Set();
            if (e.Exception is not null && !_stopRequested)
                Failed?.Invoke(e.Exception);
        };
        _waveIn.StartRecording();
    }

    // Returns once capture has really stopped. Does nothing if it never started or was stopped before.
    public void Stop()
    {
        if (_waveIn is null || _stopRequested)
            return;
        _stopRequested = true;
        // Stopping before capture has really started can lose the RecordingStopped event.
        _firstData.Wait(TimeSpan.FromSeconds(1));
        _waveIn.StopRecording();
        _stopped.Wait(TimeSpan.FromSeconds(3));
        _waveIn.Dispose();
        _waveIn = null;
    }
}
