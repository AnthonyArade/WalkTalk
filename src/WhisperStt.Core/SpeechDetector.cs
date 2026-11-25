using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WhisperStt.Core;

public sealed record SpeechSegment(byte[] Wav, TimeSpan Offset);

// Splits a continuous 16 kHz mono PCM stream into utterances using loudness, so each
// sentence can be transcribed as soon as the speaker pauses.
public sealed class SpeechDetector
{
    private static readonly int BytesPerSecond = AudioFiles.WhisperFormat.AverageBytesPerSecond;
    private static readonly int PreRollBytes = BytesPerSecond * 3 / 10;      // keep 300 ms before speech starts
    private static readonly int EndSilenceBytes = BytesPerSecond * 6 / 10;   // 600 ms of quiet ends an utterance
    private static readonly int MinSpeechBytes = BytesPerSecond / 4;         // ignore clicks shorter than 250 ms
    private static readonly int MaxSegmentBytes = BytesPerSecond * 20;       // force a cut during long monologues
    private static readonly int MinWavBytes = BytesPerSecond * 11 / 10;      // whisper.cpp skips clips under 1 s
    // Loudness (RMS, 0..1) a chunk must exceed to count as speech.
    private const float MinThreshold = 0.01f;

    private readonly Lock _gate = new();
    private readonly Queue<byte[]> _preRoll = new();
    private readonly List<byte[]> _segment = [];
    private int _preRollBytes, _segmentBytes, _speechBytes, _silenceBytes;
    private long _totalBytes, _segmentStart;
    private bool _inSpeech;
    private float _noiseFloor = 0.003f;

    public event Action<float>? LevelChanged;       // 0..1, for a level meter
    public event Action<bool>? SpeakingChanged;
    public event Action<SpeechSegment>? SegmentReady;

    public void Process(ReadOnlySpan<byte> pcm)
    {
        var chunk = pcm.ToArray();
        var rms = Rms(chunk);
        LevelChanged?.Invoke(MathF.Min(1f, MathF.Sqrt(rms) * 2.5f));

        lock (_gate)
        {
            var loud = rms > MathF.Max(MinThreshold, _noiseFloor * 3f);
            if (!_inSpeech)
            {
                if (loud)
                {
                    _inSpeech = true;
                    _segmentStart = _totalBytes - _preRollBytes;
                    _segment.AddRange(_preRoll);
                    _segmentBytes = _preRollBytes;
                    _preRoll.Clear();
                    _preRollBytes = 0;
                    _speechBytes = _silenceBytes = 0;
                    SpeakingChanged?.Invoke(true);
                }
                else
                {
                    _noiseFloor += (rms - _noiseFloor) * 0.05f;
                    _preRoll.Enqueue(chunk);
                    _preRollBytes += chunk.Length;
                    while (_preRollBytes > PreRollBytes)
                        _preRollBytes -= _preRoll.Dequeue().Length;
                }
            }

            if (_inSpeech)
            {
                _segment.Add(chunk);
                _segmentBytes += chunk.Length;
                if (loud) { _speechBytes += chunk.Length; _silenceBytes = 0; }
                else _silenceBytes += chunk.Length;
                // Adapt very slowly while speaking so steady background noise can't hold the segment open forever.
                _noiseFloor += (rms - _noiseFloor) * 0.0005f;

                if (_silenceBytes >= EndSilenceBytes || _segmentBytes >= MaxSegmentBytes)
                    EndSegment();
            }

            _totalBytes += chunk.Length;
        }
    }

    // Emits whatever is still being spoken, e.g. when recording stops mid-sentence.
    public void Flush()
    {
        lock (_gate)
        {
            if (_inSpeech)
                EndSegment();
            _preRoll.Clear();
            _preRollBytes = 0;
        }
    }

    private void EndSegment()
    {
        _inSpeech = false;
        SpeakingChanged?.Invoke(false);
        if (_speechBytes >= MinSpeechBytes)
            SegmentReady?.Invoke(new SpeechSegment(ToWav(_segment), TimeSpan.FromSeconds((double)_segmentStart / BytesPerSecond)));
        _segment.Clear();
        _segmentBytes = 0;
    }

    private static byte[] ToWav(List<byte[]> chunks)
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, AudioFiles.WhisperFormat))
        {
            foreach (var chunk in chunks)
                writer.Write(chunk, 0, chunk.Length);
            var missing = MinWavBytes - (int)writer.Length;
            if (missing > 0)
                writer.Write(new byte[missing], 0, missing);
        }
        return output.ToArray();
    }

    private static float Rms(byte[] pcm)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        if (samples.Length == 0)
            return 0;
        double sum = 0;
        foreach (var s in samples)
        {
            var v = s / 32768.0;
            sum += v * v;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
