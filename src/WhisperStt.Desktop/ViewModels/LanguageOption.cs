namespace WhisperStt.Desktop.ViewModels;

public sealed record LanguageOption(string Code, string Name)
{
    public const string DefaultCode = "en";

    // The language you speak. Choosing one tells Whisper to write in it (it translates other speech),
    // so it should match what you actually say; Auto-detect works it out per sentence.
    public static IReadOnlyList<LanguageOption> All { get; } =
    [
        new("en", "English"),
        new("auto", "Auto-detect"),
        new("ar", "Arabic"),
        new("zh", "Chinese"),
        new("nl", "Dutch"),
        new("fr", "French"),
        new("de", "German"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("pt", "Portuguese"),
        new("es", "Spanish"),
    ];

    // What screen readers announce for each item of the language list.
    public override string ToString() => Name;
}
