using System.Globalization;
using System.Text;

namespace WhisperStt.Core;

public enum VoiceCommand { Clear, Send }

// A sentence made of a single command word is an instruction, not text to write:
// "Clean" empties the fields, "Send" / "Entry" / "Envoyé" presses Enter in them.
// Case, accents and punctuation are ignored ("Send.", "envoye!"); a command inside a longer
// sentence is ordinary text, so pause briefly before and after saying one.
public static class VoiceCommands
{
    private static readonly Dictionary<string, VoiceCommand> Words = new()
    {
        ["clean"] = VoiceCommand.Clear,
        ["send"] = VoiceCommand.Send,
        ["entry"] = VoiceCommand.Send,
        ["enter"] = VoiceCommand.Send,     // how Whisper often writes "entry"
        ["envoye"] = VoiceCommand.Send,    // envoyé
        ["envoyer"] = VoiceCommand.Send,
        ["envoie"] = VoiceCommand.Send,
        ["envoyez"] = VoiceCommand.Send,   // how Whisper often writes "envoyé"
    };

    public static VoiceCommand? Match(string text)
    {
        var key = Normalize(text);
        return Words.TryGetValue(key, out var command) ? command : null;
    }

    // Lowercase letters only, accents removed: "Envoyé !" -> "envoye".
    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (char.IsLetter(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }
}
