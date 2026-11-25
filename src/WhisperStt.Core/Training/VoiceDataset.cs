using System.Text.Json;

namespace WhisperStt.Core.Training;

public enum RecordingProblem { None, Silent, TooLoud }

// One sentence the user read aloud. File is the recording's name inside the dataset folder.
// HeldOut: never trained on, so it can show honestly whether training helped on unseen sentences.
// Corrected: not read from the list but said during dictation, with a transcript the user corrected.
public sealed record VoiceRecording(string Text, string File, double Seconds, bool HeldOut = false, bool Corrected = false);

// The user's recordings for one language, kept on disk so reading can be spread over several sittings
// and the model can be trained again later. Lives in %LOCALAPPDATA%\WhisperStt\voice\<language>.
public sealed class VoiceDataset
{
    private const int FrameBytes = 1600;                    // 50 ms
    private const int PaddingFrames = 6;                    // 300 ms kept around the voice
    private const int MaxBytes = 30 * 16000 * 2;            // Whisper listens to at most 30 s at a time
    private const int HoldOutEvery = 5;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly List<VoiceRecording> _recordings;

    private VoiceDataset(string language, string directory, List<VoiceRecording> recordings)
    {
        Language = language;
        _directory = directory;
        _recordings = recordings;
    }

    // Settable so tests can work in a folder of their own instead of on the user's recordings.
    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "voice");

    public string Language { get; }
    public IReadOnlyList<VoiceRecording> Recordings => _recordings;
    public TimeSpan Duration => TimeSpan.FromSeconds(_recordings.Sum(r => r.Seconds));

    public static VoiceDataset Open(string language)
    {
        var directory = Path.Combine(Root, language);
        var recordings = new List<VoiceRecording>();
        var index = Path.Combine(directory, "recordings.json");
        if (File.Exists(index))
        {
            try
            {
                recordings = JsonSerializer.Deserialize<List<VoiceRecording>>(File.ReadAllText(index)) ?? [];
            }
            catch (JsonException)
            {
                // An unreadable list: start again rather than fail; the audio files are simply re-recorded.
            }
            recordings.RemoveAll(r => !File.Exists(Path.Combine(directory, r.File)));
            // Recordings made before the held-back mark was stored: every fifth one, as it was decided then.
            if (recordings.Count >= HoldOutEvery && !recordings.Any(r => r.HeldOut))
                for (int i = HoldOutEvery - 1; i < recordings.Count; i += HoldOutEvery)
                    recordings[i] = recordings[i] with { HeldOut = true };
        }
        return new VoiceDataset(language, directory, recordings);
    }

    public bool Has(string text) => _recordings.Any(r => r.Text == text);

    public string PathOf(VoiceRecording recording) => Path.Combine(_directory, recording.File);

    // Stores what the microphone recorded while the sentence was on screen (raw 16 kHz PCM), cut down to
    // the voice itself. A sentence read again replaces its earlier recording.
    // About one recording in five is held back from training. canHoldOut is false for the user's own
    // sentences: those carry the names and words they want learned, so they are always trained on.
    // corrected: a sentence from dictation whose transcript the user corrected. Those are kept side by
    // side even when the text is the same: the same sentence said several times, each a little
    // differently, is what teaches the model a word the user says in an unusual way.
    public VoiceRecording Add(string text, byte[] pcm, bool canHoldOut = true, bool corrected = false)
    {
        var voice = TrimSilence(pcm);
        if (!corrected)
            foreach (var old in _recordings.Where(r => r.Text == text && !r.Corrected).ToList())
                Delete(old);
        Directory.CreateDirectory(_directory);
        var file = $"{Guid.NewGuid():N}.wav";
        File.WriteAllBytes(Path.Combine(_directory, file), AudioFiles.PcmToWav(voice));
        var heldOut = canHoldOut && _recordings.Count(r => r.HeldOut) < (_recordings.Count + 1) / HoldOutEvery;
        var recording = new VoiceRecording(text, file, voice.Length / 2 / 16000.0, heldOut, corrected);
        _recordings.Add(recording);
        Save();
        return recording;
    }

    // Removes one recording, by the name of its file.
    public void RemoveFile(string file)
    {
        foreach (var old in _recordings.Where(r => r.File == file).ToList())
            Delete(old);
        Save();
    }

    private void Delete(VoiceRecording recording)
    {
        _recordings.Remove(recording);
        File.Delete(PathOf(recording));
    }

    public void Clear()
    {
        foreach (var recording in _recordings)
            File.Delete(PathOf(recording));
        _recordings.Clear();
        Save();
    }

    private void Save()
    {
        if (!Directory.Exists(_directory))
            return;
        File.WriteAllText(Path.Combine(_directory, "recordings.json"), JsonSerializer.Serialize(_recordings, Options));
    }

    // Whether a recording is fit to learn from: something must have been said, and without distortion.
    public static RecordingProblem Check(byte[] pcm)
    {
        var loudest = 0f;
        for (int offset = 0; offset + FrameBytes <= pcm.Length; offset += FrameBytes)
            loudest = MathF.Max(loudest, AudioFiles.Rms(pcm.AsSpan(offset, FrameBytes)));
        if (loudest < 0.006f)
            return RecordingProblem.Silent;

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        var clipped = 0;
        foreach (var sample in samples)
            if (sample >= 32000 || sample <= -32000)
                clipped++;
        return clipped > samples.Length / 1000 ? RecordingProblem.TooLoud : RecordingProblem.None;
    }

    // Keeps from just before the first loud frame to just after the last one.
    private static byte[] TrimSilence(byte[] pcm)
    {
        var frames = pcm.Length / FrameBytes;
        var levels = new float[frames];
        for (int i = 0; i < frames; i++)
            levels[i] = AudioFiles.Rms(pcm.AsSpan(i * FrameBytes, FrameBytes));
        if (frames == 0)
            return pcm;

        var threshold = MathF.Max(0.004f, levels.Max() * 0.08f);
        var first = Array.FindIndex(levels, l => l >= threshold);
        var last = Array.FindLastIndex(levels, l => l >= threshold);
        if (first < 0)
            return pcm;

        var from = Math.Max(0, first - PaddingFrames) * FrameBytes;
        var to = Math.Min(frames, last + 1 + PaddingFrames) * FrameBytes;
        return pcm.AsSpan(from, Math.Min(to - from, MaxBytes)).ToArray();
    }
}
