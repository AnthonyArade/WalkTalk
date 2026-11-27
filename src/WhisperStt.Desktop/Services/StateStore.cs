using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using WhisperStt.Core;
using WhisperStt.Desktop.Targets;
using WhisperStt.Desktop.ViewModels;

namespace WhisperStt.Desktop.Services;

public sealed class AppState
{
    public string? Microphone { get; set; }
    public string Model { get; set; } = "turbo";
    // Saved under a new name so earlier choices (from when French was preselected) reset to English once.
    public string TranscriptionLanguage { get; set; } = LanguageOption.DefaultCode;
    public string WordHints { get; set; } = "";
    public string Theme { get; set; } = nameof(AppTheme.Dark);
    public bool ReadAloud { get; set; }
    public string ReadAloudVoice { get; set; } = "af_heart";
    public double ReadAloudSpeed { get; set; } = 1.0;
    public double ReadAloudVolume { get; set; } = 0.8;
    public bool PauseMicWhileReading { get; set; } = true;
    // Voice training: the language of the sentences and the user's own sentences. (The property names
    // date from when this was called the voice check; they are kept so saved settings still load.)
    public string VoiceCheckLanguage { get; set; } = "";
    public List<string> VoiceCheckSentences { get; set; } = [];
    // How much of the sentence list voice training uses (20, 40, 60, 80 or 100 percent).
    public int VoiceTrainingShare { get; set; } = 100;
    // Seconds one training step took on this PC, per model, measured during the last training.
    public Dictionary<string, double> TrainingStepSeconds { get; set; } = [];
    // "When you hear From, write To": abbreviations, slang...
    public List<Replacement> Replacements { get; set; } = [];
    // The built-in VRChat chatbox destination.
    public bool VrChat { get; set; }
    // The built-in Twitch chat destination. The sign-in itself is kept apart, encrypted (TwitchTokenStore).
    public bool TwitchOn { get; set; }
    public string TwitchChannel { get; set; } = "";
    // The OBS overlay: its canvases and the port it is served on. (Obs and ObsOn hold the single canvas of
    // earlier versions; they are read once and turned into the first canvas.)
    public List<WhisperStt.Core.Obs.OverlayStyle> ObsCanvases { get; set; } = [];
    public int ObsPort { get; set; } = WhisperStt.Core.Obs.OverlayServer.DefaultPort;
    public bool ObsOn { get; set; }
    public WhisperStt.Core.Obs.OverlayStyle? Obs { get; set; }
    public List<TargetState> Targets { get; set; } = [];
}

public sealed class TargetState
{
    public string Name { get; set; } = "";
    public bool IsOn { get; set; } = true;
    public bool PressEnter { get; set; }
    public TargetInfo Target { get; set; } = new();
}

// Targets and settings live in %LOCALAPPDATA%\WhisperStt\desktop-state.json.
public static class StateStore
{
    // Settable so tests can keep their settings apart from the user's.
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "desktop-state.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppState Load()
    {
        if (!File.Exists(FilePath))
            return new AppState();
        try
        {
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Options) ?? new AppState();
        }
        catch (JsonException)
        {
            // Keep the unreadable file instead of overwriting the user's targets on the next save.
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            return new AppState();
        }
    }

    public static void Save(AppState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        File.Move(temp, FilePath, overwrite: true);
    }
}

public static class ClipboardHelper
{
    public static void TrySetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another app is holding the clipboard; the user can simply click again.
        }
    }
}
