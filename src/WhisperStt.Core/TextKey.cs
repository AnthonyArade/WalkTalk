namespace WhisperStt.Core;

// The forms text is compared in, so that capitals, punctuation and spacing don't matter.
public static class TextKey
{
    // Lowercase letters and digits only: "Moon-Hollow!" and "moonhollow" compare equal.
    public static string Plain(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // The words of a text, each in lowercase without accents or punctuation.
    public static IEnumerable<string> Words(string text) =>
        HintCorrector.Word().Matches(text).Select(m => HintCorrector.Key(m.Value)).Where(k => k.Length > 0);
}
