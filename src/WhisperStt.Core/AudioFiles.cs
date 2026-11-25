using NAudio.Wave;

namespace WhisperStt.Core;

public static class AudioFiles
{
    // Whisper expects 16 kHz, 16-bit, mono PCM.
    public static WaveFormat WhisperFormat { get; } = new(16000, 16, 1);

    public static MemoryStream CreateSilence(TimeSpan duration) =>
        ToWav(new float[(int)(WhisperFormat.SampleRate * duration.TotalSeconds)]);

    // Raw Whisper-format PCM (as the microphone delivers it) wrapped in a WAV header.
    public static byte[] PcmToWav(byte[] pcm)
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, WhisperFormat))
            writer.Write(pcm, 0, pcm.Length);
        return output.ToArray();
    }

    // The raw PCM inside a Whisper-format WAV.
    public static byte[] WavToPcm(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var pcm = new byte[reader.Length];
        reader.ReadExactly(pcm);
        return pcm;
    }

    // Loudness of raw Whisper-format PCM, 0..1 (root mean square).
    public static float Rms(ReadOnlySpan<byte> pcm)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
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

    // Samples of a Whisper-format WAV as floats in -1..1.
    public static float[] ReadSamples(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        var pcm = new byte[reader.Length];
        reader.ReadExactly(pcm);
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return samples;
    }

    // Whisper-format WAV from float samples, padded with silence to at least minDuration.
    public static MemoryStream ToWav(ReadOnlySpan<float> samples, TimeSpan minDuration = default)
    {
        var count = Math.Max(samples.Length, (int)(WhisperFormat.SampleRate * minDuration.TotalSeconds));
        var pcm = new byte[count * 2];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), (short)Math.Clamp(samples[i] * 32768f, short.MinValue, short.MaxValue));
        var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, WhisperFormat))
            writer.Write(pcm, 0, pcm.Length);
        output.Position = 0;
        return output;
    }

    // Decodes any format Media Foundation understands (wav, mp3, m4a, mp4, ...) into a Whisper-ready WAV.
    public static MemoryStream LoadAsWhisperWav(string path)
    {
        using var reader = new MediaFoundationReader(path);
        using var resampler = new MediaFoundationResampler(reader, WhisperFormat);
        var output = new MemoryStream();
        WaveFileWriter.WriteWavFileToStream(output, resampler);
        output.Position = 0;
        return output;
    }
}
