using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace WhisperStt.Core;

// Probability: Whisper's average confidence in the segment's words, 0..1.
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text, float Probability)
{
    // The audio the sentence was heard in, when the transcriber was asked to keep it.
    public SpokenClip? Clip { get; init; }
}

// What the model listened to for one stretch of speech (16 kHz mono WAV). Sentences said in one breath
// share the same clip. Kept so a transcript the user corrects can be learned from.
public sealed class SpokenClip(byte[] wav)
{
    public byte[] Wav { get; } = wav;
}

// The speech part of a clip, ready for Whisper. Offset: where it starts within the original clip.
public sealed class SpeechClip(MemoryStream wav, TimeSpan offset) : IDisposable
{
    public MemoryStream Wav { get; } = wav;
    public TimeSpan Offset { get; } = offset;
    public void Dispose() => Wav.Dispose();
}

// A loaded Whisper model plus a Silero voice detector. Calls must run one at a time.
public sealed partial class WhisperEngine : IAsyncDisposable
{
    // Real speech scores about 0.75-0.95; junk "heard" in noise (e.g. "K K") about 0.3.
    private const float MinProbability = 0.4f;
    private static readonly TimeSpan MinSpeech = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SpeechPadding = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinClip = TimeSpan.FromSeconds(1.1); // whisper.cpp skips clips under 1 s

    // Credits from the subtitled videos Whisper was trained on, which it "hears" in quiet audio.
    private static readonly string[] SubtitleCredits =
        ["amara.org", "sous-titrage", "sous-titres réalisés", "sous-titres par", "subtitles by", "subtitled by", "transcribed by"];

    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly WhisperVadFactory _vadFactory;
    private readonly WhisperVadProcessor _vad;
    private readonly IReadOnlyList<string> _hints;

    private WhisperEngine(WhisperFactory factory, WhisperProcessor processor, WhisperVadFactory vadFactory, WhisperVadProcessor vad,
        string modelName, string language, string prompt)
    {
        _factory = factory;
        _processor = processor;
        _vadFactory = vadFactory;
        _vad = vad;
        ModelName = modelName;
        Language = language;
        Prompt = prompt;
        _hints = HintCorrector.ParseHints(prompt);
        Backend = $"{RuntimeOptions.LoadedLibrary}" switch
        {
            "Vulkan" => "GPU (Vulkan)",
            "Cuda" => "GPU (CUDA)",
            var other when other.StartsWith("Cpu", StringComparison.Ordinal) => "CPU",
            var other => other
        };
    }

    public string ModelName { get; }
    public string Language { get; }
    public string Prompt { get; }

    // Where the model runs, e.g. "GPU (Vulkan)" or "CPU". Whisper.net uses the GPU when it can and falls back to the CPU.
    public string Backend { get; }

    // prompt: comma-separated words Whisper should expect (names, jargon). They nudge Whisper while it
    // listens, and HintCorrector then restores their exact spelling in what it wrote.
    public static async Task<WhisperEngine> LoadAsync(string modelName, string language, string prompt = "",
        Action<string>? log = null, CancellationToken ct = default)
    {
        var path = await ModelCatalog.EnsureModelAsync(modelName, log, ct).ConfigureAwait(false);
        var vadPath = await ModelCatalog.EnsureVadModelAsync(ct).ConfigureAwait(false);
        log?.Invoke($"Loading the '{modelName}' model...");
        var engine = await Task.Run(() =>
        {
            NativeRuntime.EnsureLoaded();
            var factory = WhisperFactory.FromPath(path);
            // No temperature fallback: on silence or noise Whisper otherwise retries up to five times,
            // producing junk to its length limit each time, which can stall live dictation for 20+ seconds.
            // No context: each sentence is decoded on its own, so one invented phrase can't snowball into the next.
            var builder = factory.CreateBuilder()
                .WithLanguage(language)
                .WithTemperatureInc(0f)
                .WithNoContext()
                .WithProbabilities();
            var hints = HintCorrector.ParseHints(prompt);
            if (hints.Count > 0)
                builder = builder.WithPrompt(string.Join(", ", hints) + ".");

            var vadFactory = WhisperVadFactory.FromPath(vadPath);
            var vad = vadFactory.CreateBuilder().WithUseGpu(false).Build();
            return new WhisperEngine(factory, builder.Build(), vadFactory, vad, modelName, language, prompt.Trim());
        }, ct).ConfigureAwait(false);

        // The first transcription on a GPU compiles its shaders, which can take many seconds the very first time.
        // Do it now on silence so the first real sentence isn't delayed.
        log?.Invoke($"Warming up the '{modelName}' model on the {engine.Backend}...");
        using var silence = AudioFiles.CreateSilence(MinClip);
        await foreach (var _ in engine._processor.ProcessAsync(silence, ct).ConfigureAwait(false)) { }
        return engine;
    }

    // Keeps only the part of a clip where Silero hears a human voice, or returns null if there is none
    // (clicks, typing, coughs, fans). Whisper invents text such as "Thank you." when given non-speech.
    public SpeechClip? ExtractSpeech(byte[] wav)
    {
        var samples = AudioFiles.ReadSamples(wav);
        var speech = _vad.DetectSpeech(samples).ToList();
        var total = speech.Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.End - s.Start));
        if (speech.Count == 0 || total < MinSpeech)
            return null;

        var rate = AudioFiles.WhisperFormat.SampleRate;
        var from = Math.Max(0, (int)((speech[0].Start - SpeechPadding).TotalSeconds * rate));
        var to = Math.Min(samples.Length, (int)((speech[^1].End + SpeechPadding).TotalSeconds * rate));

        // Very short words are centred in silence: trailing silence alone invites invented endings.
        var length = to - from;
        var padded = new float[Math.Max(length, (int)(MinClip.TotalSeconds * rate))];
        var lead = (padded.Length - length) / 2;
        Array.Copy(samples, from, padded, lead, length);
        return new SpeechClip(AudioFiles.ToWav(padded), TimeSpan.FromSeconds((double)(from - lead) / rate));
    }

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(Stream wav, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var segment in _processor.ProcessAsync(wav, ct).ConfigureAwait(false))
        {
            var text = CleanText(segment.Text, _hints);
            if (text is not null && segment.Probability >= MinProbability)
                yield return new TranscriptSegment(segment.Start, segment.End, HintCorrector.Apply(text, _hints), segment.Probability);
        }
    }

    // Removes sound captions ("*click*", "[Music]", "(rires)", "♪") and drops text that is only
    // a caption, punctuation, a subtitle credit or Whisper reciting the whole hint list back.
    // A single hint said on its own ("Moonhollow.") is real speech and is kept. Returns null when nothing is left.
    public static string? CleanText(string raw, IReadOnlyList<string>? hints = null)
    {
        var text = Whitespace().Replace(SoundCaptions().Replace(raw, " "), " ").Trim();
        if (!text.Any(char.IsLetterOrDigit))
            return null;
        if (SubtitleCredits.Any(credit => text.Contains(credit, StringComparison.OrdinalIgnoreCase)))
            return null;
        if (hints is { Count: >= 2 } && TextKey.Plain(text) == TextKey.Plain(string.Concat(hints)))
            return null;
        return text;
    }

    [GeneratedRegex(@"\*[^*]*\*|\[[^\]]*\]|\([^)]*\)|[♪♫]+")]
    private static partial Regex SoundCaptions();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public async ValueTask DisposeAsync()
    {
        await _processor.DisposeAsync().ConfigureAwait(false);
        _factory.Dispose();
        await _vad.DisposeAsync().ConfigureAwait(false);
        _vadFactory.Dispose();
    }
}
