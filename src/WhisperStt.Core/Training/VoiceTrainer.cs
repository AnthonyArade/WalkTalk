using System.Text;
using System.Text.Json;

namespace WhisperStt.Core.Training;

public sealed record TrainingProgress(string Text, double? Fraction = null);

// Improved is false when no amount of training did better than the model it started from (on the
// held-back sentences, or for an update on the new sentences); no model file is written then.
// Steps: how many training steps ran, so the time one step takes on this PC can be learned.
// FairTexts: what the model wrote for the held-back sentences before the final training included them
// (in their order); the only fair measure of the result once every recording has been trained on.
public sealed record TrainingResult(bool Improved, TimeSpan Duration, int Steps = 0, IReadOnlyList<string>? FairTexts = null);

// Retrains a Whisper model on the user's recordings by running train.py with the training kit's Python.
public static class VoiceTrainer
{
    private const int NewSentenceRepeats = 3;   // how often each new sentence is seen per round of an update
    private const int MinReminders = 8;         // already learned recordings mixed in, so they aren't forgotten
    private const int MaxReminders = 40;
    private const int BatchSize = 4;            // sentences per training step (train.py's default)
    private const int UsualRounds = 4;          // a training usually peaks at round 2 and stops after round 4 (6 at most)
    private const int UsualBestRound = 2;
    // An update goes on until the model writes the new sentences right, then one round more: usually four
    // rounds, never more than eight. (Three fixed rounds turned out too few for a word said in an unusual way.)
    private const int UsualUpdateRounds = 4;
    private const int MaxUpdateRounds = 8;

    // -- how long a training is expected to take

    // Steps a training usually runs: the search for the right number of rounds on the trained sentences,
    // then that many rounds again on all of them, the held-back ones included.
    public static int TrainSteps(int trained, int heldBack) =>
        UsualRounds * (int)Math.Ceiling(trained / (double)BatchSize) +
        UsualBestRound * (int)Math.Ceiling((trained + heldBack) / (double)BatchSize);

    // Steps of an update that adds `added` sentences to a model that already knows `known`.
    public static int UpdateSteps(int added, int known) =>
        UsualUpdateRounds * (int)Math.Ceiling((added * NewSentenceRepeats + Math.Min(known, Reminders(added))) / (double)BatchSize);

    // Seconds one training step takes before this PC has been measured: the turbo figure was measured on
    // an 8 GB RTX 3050, the others are scaled from it by model size.
    public static double DefaultStepSeconds(string model) => model switch
    {
        "tiny" => 0.3,
        "base" => 0.5,
        "small" => 1.0,
        "medium" => 2.4,
        "large" => 4.6,
        _ => 3.7
    };

    private static int Reminders(int added) => Math.Clamp(added * 2, MinReminders, MaxReminders);

    // Trains a standard model in two passes. The first trains on `train` only and checks against heldOut
    // after every round, which shows how many rounds help before the model starts memorising. The second
    // repeats the training for that many rounds on every recording, heldOut included, so all of the
    // user's recordings end up in the model.
    // own: the user's own sentences and corrections among `train`. They hold the words the user most
    // wants right, so each is seen several times per round, and any the finished model still writes
    // wrong gets extra practice until it comes out right.
    public static async Task<TrainingResult> TrainAsync(string baseModel, string language, VoiceDataset data,
        IReadOnlyList<VoiceRecording> train, IReadOnlyList<VoiceRecording> heldOut, IReadOnlyList<VoiceRecording> own,
        string outputPath, Action<TrainingProgress> progress, CancellationToken ct = default)
    {
        var lesson = train.SelectMany(r => Enumerable.Repeat(r, own.Contains(r) ? NewSentenceRepeats : 1));
        return await RunAsync(new Dictionary<string, object>
        {
            ["base"] = await ModelCatalog.EnsureModelAsync(baseModel, null, ct).ConfigureAwait(false),
            ["out"] = outputPath,
            ["language"] = language,
            ["train"] = Items(data, lesson),
            ["holdout"] = Items(data, heldOut),
            ["own"] = Items(data, own),
            ["own_rounds"] = MaxUpdateRounds,
            ["final_on_all"] = true,
        }, outputPath, progress, ct).ConfigureAwait(false);
    }

    // Adds newly recorded sentences to the existing voice model: a short training that starts from that
    // model and sees the new sentences several times, mixed with a sample of the ones it already knows.
    // Much quicker than training on everything again. known: the recordings the model was already trained on.
    public static async Task<TrainingResult> UpdateAsync(string language, VoiceDataset data,
        IReadOnlyList<VoiceRecording> known, IReadOnlyList<VoiceRecording> heldOut, IReadOnlyList<VoiceRecording> added,
        string outputPath, Action<TrainingProgress> progress, CancellationToken ct = default)
    {
        var wanted = Reminders(added.Count);
        IEnumerable<VoiceRecording> reminders = known.Count <= wanted
            ? known
            : Enumerable.Range(0, wanted).Select(i => known[(int)((long)i * known.Count / wanted)]);
        // Earlier recordings of the same sentences are practised along with the new ones: the same words
        // said a little differently each time is what teaches the model the word rather than the recording.
        var texts = added.Select(r => TextKey.Plain(r.Text)).ToHashSet();
        var sameSentence = known.Where(r => texts.Contains(TextKey.Plain(r.Text)));
        var lesson = Enumerable.Repeat(added, NewSentenceRepeats).SelectMany(a => a)
            .Concat(sameSentence.Concat(reminders).Distinct());

        return await RunAsync(new Dictionary<string, object>
        {
            ["base"] = PersonalModel.FilePath,
            ["out"] = outputPath,
            ["language"] = language,
            ["train"] = Items(data, lesson),
            ["holdout"] = Items(data, heldOut),
            ["focus"] = Items(data, added),
            ["epochs"] = MaxUpdateRounds,
        }, outputPath, progress, ct).ConfigureAwait(false);
    }

    private static object Items(VoiceDataset data, IEnumerable<VoiceRecording> recordings) =>
        recordings.Select(r => new { audio = data.PathOf(r), text = r.Text }).ToList();

    private static async Task<TrainingResult> RunAsync(Dictionary<string, object> settings, string outputPath,
        Action<TrainingProgress> progress, CancellationToken ct)
    {
        var script = Path.Combine(TrainingKit.Directory, "train.py");
        await using (var resource = typeof(VoiceTrainer).Assembly.GetManifestResourceStream("WhisperStt.Core.Training.train.py")!)
        await using (var file = File.Create(script))
            await resource.CopyToAsync(file, ct).ConfigureAwait(false);

        var job = Path.Combine(TrainingKit.Directory, "job.json");
        await File.WriteAllTextAsync(job, JsonSerializer.Serialize(settings), ct).ConfigureAwait(false);
        File.Delete(outputPath);

        TrainingResult? result = null;
        string? error = null;
        List<string>? fairTexts = null;
        var twoPasses = settings.ContainsKey("final_on_all");
        var other = new StringBuilder();
        progress(new TrainingProgress("Starting the training…"));
        var exit = await TrainingKit.RunPythonAsync([script, job], line =>
        {
            if (!line.StartsWith('{'))
            {
                other.AppendLine(line); // warnings, or the reason for a crash
                return;
            }
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            switch (root.GetProperty("stage").GetString())
            {
                case "loading":
                    progress(new TrainingProgress("Loading the model onto the graphics card…"));
                    break;
                case "baseline":
                    progress(new TrainingProgress("Measuring how well the model understands you now…"));
                    break;
                case "train":
                    int step = root.GetProperty("step").GetInt32(), total = root.GetProperty("total").GetInt32();
                    var pass = root.TryGetProperty("phase", out var phase) ? phase.GetInt32() : 1;
                    var text = !twoPasses ? $"Training on your voice: step {step} of {total}"
                        : pass == 3 ? $"Training on your voice (extra practice on your own sentences): step {step} of {total}"
                        : pass == 2 ? $"Training on your voice (2 of 2, every sentence): step {step} of {total}"
                        : $"Training on your voice (1 of 2, finding how long to train): step {step} of {total}";
                    if (step > 0 && root.TryGetProperty("seconds", out var seconds))
                        text += $" · {TimeLeft(seconds.GetDouble() / step * (total - step))}";
                    // The first pass can stop early, the second is shorter, the extra practice shorter still.
                    var done = (double)step / Math.Max(1, total);
                    progress(new TrainingProgress(text,
                        !twoPasses ? done : pass == 3 ? 0.9 + 0.1 * done : pass == 2 ? 0.6 + 0.3 * done : 0.6 * done));
                    break;
                case "fair":
                    fairTexts = root.GetProperty("texts").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
                    break;
                case "saving":
                    progress(new TrainingProgress("Saving your voice model…", 1));
                    break;
                case "done":
                    result = new TrainingResult(root.GetProperty("improved").GetBoolean(),
                        TimeSpan.FromSeconds(root.GetProperty("seconds").GetDouble()),
                        root.TryGetProperty("steps", out var steps) ? steps.GetInt32() : 0, fairTexts);
                    break;
                case "error":
                    error = root.GetProperty("message").GetString();
                    break;
            }
        }, ct).ConfigureAwait(false);

        if (error is not null)
            throw new InvalidOperationException(error);
        if (exit != 0 || result is null)
            throw new InvalidOperationException("The training stopped unexpectedly: " + TrainingKit.LastLine(other.ToString()));
        return result;
    }

    // Training can stop early once it no longer improves, so this is an upper bound.
    private static string TimeLeft(double seconds) =>
        seconds > 90 ? $"at most {Math.Ceiling(seconds / 60)} min left" : $"at most {Math.Max(1, (int)seconds)} s left";
}
