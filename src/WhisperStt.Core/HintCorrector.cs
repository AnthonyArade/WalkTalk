using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperStt.Core;

// Word hints only nudge Whisper, so names still come out close-but-wrong ("Moon Hollow", "Moonholo").
// This puts the user's exact spelling back: any run of one to three words whose letters are within
// about 30% of a hint (same first letter) is replaced by the hint. Hints shorter than 5 letters only
// get their capitalisation fixed, since short words are too easy to confuse.
public static partial class HintCorrector
{
    private const int MinFuzzyLength = 5;
    private const double MaxDistanceRatio = 0.3;
    private const int MaxWordsPerMatch = 3;

    public static IReadOnlyList<string> ParseHints(string hints) =>
        hints.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.TrimEnd('.'))
            .Where(h => Key(h).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Apply(string text, IReadOnlyList<string> hints)
    {
        if (hints.Count == 0)
            return text;
        var words = Word().Matches(text);
        var keys = hints.Select(Key).ToList();
        var result = new StringBuilder();
        int copied = 0, i = 0;
        while (i < words.Count)
        {
            var match = FindMatch(words, i, keys);
            if (match is { } m)
            {
                var start = words[i].Index;
                var end = words[i + m.WordCount - 1].Index + words[i + m.WordCount - 1].Length;
                result.Append(text, copied, start - copied).Append(hints[m.Hint]);
                copied = end;
                i += m.WordCount;
            }
            else
            {
                i++;
            }
        }
        return result.Append(text, copied, text.Length - copied).ToString();
    }

    // The closest run of one to three words starting here, so "Moon Hollow" becomes one name but
    // "Moon Hollow for" doesn't swallow "for". Ties go to fewer words.
    private static (int Hint, int WordCount)? FindMatch(MatchCollection words, int start, List<string> keys)
    {
        (int Hint, int WordCount, int Distance)? best = null;
        for (int count = 1; count <= Math.Min(MaxWordsPerMatch, words.Count - start); count++)
        {
            var candidate = string.Concat(Enumerable.Range(start, count).Select(k => Key(words[k].Value)));
            for (int h = 0; h < keys.Count; h++)
            {
                var key = keys[h];
                int distance;
                if (candidate == key)
                    distance = 0;
                else if (key.Length >= MinFuzzyLength && candidate.Length > 0 && candidate[0] == key[0])
                    distance = Distance(candidate, key);
                else
                    continue;
                if (distance <= (int)(key.Length * MaxDistanceRatio) && (best is null || distance < best.Value.Distance))
                    best = (h, count, distance);
            }
        }
        return best is { } b ? (b.Hint, b.WordCount) : null;
    }

    // Lowercase letters and digits only, without accents: "Moon-Hollow" and "moonhollow" compare equal.
    internal static string Key(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*")]
    internal static partial Regex Word();
}
