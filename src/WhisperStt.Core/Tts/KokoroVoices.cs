namespace WhisperStt.Core.Tts;

public enum VoiceAccent { American, British }

// SpeakerId: the voice's index in Kokoro v1.0's voices.bin (read from the model's id2speaker metadata).
public sealed record KokoroVoice(string Id, int SpeakerId, VoiceAccent Accent, bool IsFemale, bool Recommended)
{
    public string Name => char.ToUpperInvariant(Id[3]) + Id[4..];

    public string DisplayName =>
        $"{Name} · {Accent} {(IsFemale ? "female" : "male")}" + (Recommended ? " ★" : "");

    public override string ToString() => DisplayName;
}

// Kokoro v1.0's American and British English voices. ★ marks the ones Kokoro's authors rate best.
public static class KokoroVoices
{
    public static IReadOnlyList<KokoroVoice> All { get; } =
    [
        new("af_heart", 3, VoiceAccent.American, true, true),
        new("af_bella", 2, VoiceAccent.American, true, true),
        new("af_nicole", 6, VoiceAccent.American, true, false),
        new("af_aoede", 1, VoiceAccent.American, true, false),
        new("af_kore", 5, VoiceAccent.American, true, false),
        new("af_sarah", 9, VoiceAccent.American, true, false),
        new("af_alloy", 0, VoiceAccent.American, true, false),
        new("af_nova", 7, VoiceAccent.American, true, false),
        new("af_sky", 10, VoiceAccent.American, true, false),
        new("af_jessica", 4, VoiceAccent.American, true, false),
        new("af_river", 8, VoiceAccent.American, true, false),
        new("am_michael", 16, VoiceAccent.American, false, true),
        new("am_fenrir", 14, VoiceAccent.American, false, false),
        new("am_puck", 18, VoiceAccent.American, false, false),
        new("am_echo", 12, VoiceAccent.American, false, false),
        new("am_eric", 13, VoiceAccent.American, false, false),
        new("am_liam", 15, VoiceAccent.American, false, false),
        new("am_onyx", 17, VoiceAccent.American, false, false),
        new("am_santa", 19, VoiceAccent.American, false, false),
        new("am_adam", 11, VoiceAccent.American, false, false),
        new("bf_emma", 21, VoiceAccent.British, true, true),
        new("bf_isabella", 22, VoiceAccent.British, true, false),
        new("bf_alice", 20, VoiceAccent.British, true, false),
        new("bf_lily", 23, VoiceAccent.British, true, false),
        new("bm_george", 26, VoiceAccent.British, false, true),
        new("bm_fable", 25, VoiceAccent.British, false, false),
        new("bm_lewis", 27, VoiceAccent.British, false, false),
        new("bm_daniel", 24, VoiceAccent.British, false, false),
    ];

    public static KokoroVoice Default => All[0];

    public static KokoroVoice Find(string? id) => All.FirstOrDefault(v => v.Id == id) ?? Default;
}
