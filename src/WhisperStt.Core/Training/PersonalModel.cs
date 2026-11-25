using System.Text.Json;

namespace WhisperStt.Core.Training;

// What the personal model was made from and how much it helped (share of words right, before and after,
// on sentences it was not trained on). Files: the recordings it has learned from, so that sentences
// recorded later can be added to it without training on everything again.
// TestSentences: how many of the trained sentences are also the before/after test (0 for a model made
// when those were kept out of the training altogether). AccuracyAfter is then what the model scored on
// them before it trained on them, the only fair figure.
public sealed record PersonalModelInfo(string BaseModel, string Language, DateTime Trained, int Sentences,
    double AccuracyBefore, double AccuracyAfter, IReadOnlyList<string>? Files = null, int TestSentences = 0)
{
    public bool Includes(VoiceRecording recording) => Files is not null && Files.Contains(recording.File);
}

// The Whisper model retrained on the user's voice. There is one at a time; training again replaces it.
// A freshly trained model is first a "candidate", and only becomes the personal model once accepted.
public static class PersonalModel
{
    public const string Name = "my voice";
    public const string CandidateName = "my voice (new)";

    // Settable so tests can work in a folder of their own instead of on the user's voice model.
    public static string Directory { get; set; } = ModelCatalog.ModelsDirectory;

    public static string FilePath => Path.Combine(Directory, "ggml-voice.bin");
    public static string CandidatePath => Path.Combine(Directory, "ggml-voice-new.bin");
    private static string InfoPath => Path.Combine(Directory, "ggml-voice.json");

    public static bool Exists => File.Exists(FilePath);

    // The file behind one of the two names above, or null for any other model name.
    public static string? PathFor(string modelName) => modelName switch
    {
        Name => FilePath,
        CandidateName => CandidatePath,
        _ => null
    };

    // The window asks for this many times a second (every button that depends on it), so the file is
    // only read again when it has changed.
    private static (string Path, DateTime Written, PersonalModelInfo? Info)? _read;

    public static PersonalModelInfo? Info
    {
        get
        {
            var path = InfoPath;
            if (!Exists || !File.Exists(path))
                return null;
            var written = File.GetLastWriteTimeUtc(path);
            if (_read is { } read && read.Path == path && read.Written == written)
                return read.Info;

            PersonalModelInfo? info;
            try
            {
                info = JsonSerializer.Deserialize<PersonalModelInfo>(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                info = null;
            }
            _read = (path, written, info);
            return info;
        }
    }

    public static void AcceptCandidate(PersonalModelInfo info)
    {
        File.Move(CandidatePath, FilePath, overwrite: true);
        File.WriteAllText(InfoPath, JsonSerializer.Serialize(info));
    }

    public static void DiscardCandidate() => File.Delete(CandidatePath);

    public static void Remove()
    {
        File.Delete(FilePath);
        File.Delete(InfoPath);
    }
}
