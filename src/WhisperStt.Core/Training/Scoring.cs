namespace WhisperStt.Core.Training;

// How one read sentence came out. Errors counts wrong, missing and extra words.
public sealed record SentenceScore(string Reference, string Heard, int Errors, int Words);

public static class Scoring
{
    // Word-level edit distance between the sentence that was read and what was written.
    // Case, accents and punctuation don't count.
    public static SentenceScore Compare(string reference, string heard)
    {
        var r = TextKey.Words(reference).ToList();
        var h = TextKey.Words(heard).ToList();

        var distance = new int[r.Count + 1, h.Count + 1];
        for (int i = 0; i <= r.Count; i++)
            distance[i, 0] = i;
        for (int j = 0; j <= h.Count; j++)
            distance[0, j] = j;
        for (int i = 1; i <= r.Count; i++)
            for (int j = 1; j <= h.Count; j++)
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j], distance[i, j - 1]) + 1,
                    distance[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));

        return new SentenceScore(reference, heard, distance[r.Count, h.Count], r.Count);
    }
}
