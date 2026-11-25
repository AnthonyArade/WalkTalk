namespace WhisperStt.Core.Training;

// How a model did on a set of recordings.
public sealed record VoiceTestResult(IReadOnlyList<SentenceScore> Sentences)
{
    public int Words => Sentences.Sum(s => s.Words);
    public int Errors => Sentences.Sum(s => s.Errors);
    public double Accuracy => Words == 0 ? 0 : Math.Max(0, 1 - (double)Errors / Words);
}

public static class VoiceTester
{
    // Runs recordings through exactly what live dictation uses (voice detection, Whisper, text cleanup),
    // so the score is what the user will really get.
    public static async Task<VoiceTestResult> TestAsync(string model, string language, VoiceDataset data,
        IReadOnlyList<VoiceRecording> recordings, Action<double>? progress = null, CancellationToken ct = default)
    {
        await using var engine = await WhisperEngine.LoadAsync(model, language, "", null, ct).ConfigureAwait(false);
        var scores = new List<SentenceScore>();
        foreach (var recording in recordings)
        {
            ct.ThrowIfCancellationRequested();
            var wav = await File.ReadAllBytesAsync(data.PathOf(recording), ct).ConfigureAwait(false);
            var parts = new List<string>();
            using (var speech = engine.ExtractSpeech(wav))
            {
                if (speech is not null)
                    await foreach (var segment in engine.TranscribeAsync(speech.Wav, ct).ConfigureAwait(false))
                        parts.Add(segment.Text);
            }
            scores.Add(Scoring.Compare(recording.Text, string.Join(' ', parts)));
            progress?.Invoke((double)scores.Count / recordings.Count);
        }
        return new VoiceTestResult(scores);
    }
}
