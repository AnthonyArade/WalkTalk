using System.Text;

namespace WhisperStt.Core;

// "When you hear From, write To": an abbreviation, slang, an emoji... From can be several words.
public sealed record Replacement(string From, string To);

public static class WordReplacements
{
    // What a word or phrase is matched by: its words in lowercase without accents or punctuation.
    // Empty when there is nothing to listen for (only punctuation, say).
    public static string KeyOf(string words) => string.Join(' ', TextKey.Words(words));

    // Replaces whole words only ("be" does not touch "because"), whatever their capitals or accents,
    // and writes To exactly as the user typed it. Longer phrases win over shorter ones, so
    // "by the way" is replaced as a whole even if "way" has a replacement of its own.
    // Punctuation around the words stays where it was.
    public static string Apply(string text, IReadOnlyList<Replacement> replacements)
    {
        if (replacements.Count == 0)
            return text;

        var rules = replacements
            .Select(r => (Keys: TextKey.Words(r.From).ToArray(), r.To))
            .Where(r => r.Keys.Length > 0)
            .OrderByDescending(r => r.Keys.Length)
            .ToList();
        var words = HintCorrector.Word().Matches(text);
        var keys = words.Select(w => HintCorrector.Key(w.Value)).ToArray();

        var result = new StringBuilder();
        int copied = 0, i = 0;
        while (i < words.Count)
        {
            var rule = rules.FirstOrDefault(r => i + r.Keys.Length <= keys.Length && r.Keys.AsSpan().SequenceEqual(keys.AsSpan(i, r.Keys.Length)));
            if (rule.Keys is null)
            {
                i++;
                continue;
            }
            var last = words[i + rule.Keys.Length - 1];
            result.Append(text, copied, words[i].Index - copied).Append(rule.To);
            copied = last.Index + last.Length;
            i += rule.Keys.Length;
        }
        return result.Append(text, copied, text.Length - copied).ToString();
    }
}
