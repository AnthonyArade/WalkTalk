using WhisperStt.Core;
using WhisperStt.Core.Training;

// How do the standard model and a voice model write one recording, and slightly altered copies of it
// (faster, slower, noisier, quieter)? Works on copies only.
// Usage: wsdiag <models sandbox> <wav> <model names, comma separated> [hint]
// Usage: ModelChecks runtime <wav>
// Loads Whisper with only the Visual C++ runtime WalkTalk carries, never Windows' own copy, as on a PC
// that has none, then transcribes the recording with the tiny model.
if (args[0] == "runtime")
{
    string[] names = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];
    string Loaded() => string.Join(" | ", names.Select(n =>
    {
        var module = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
            .FirstOrDefault(m => m.ModuleName.Equals(n, StringComparison.OrdinalIgnoreCase));
        return $"{n}: {(module is null ? "not loaded" : Path.GetDirectoryName(module.FileName))}";
    }));
    Console.WriteLine($"app folder: {AppContext.BaseDirectory}");
    Console.WriteLine($"carried files present: {string.Join(", ", names.Select(n => $"{n}={File.Exists(Path.Combine(AppContext.BaseDirectory, "Native", n))}"))}");
    Console.WriteLine($"a copy beside the exe (would hide Windows' own): {names.Any(n => File.Exists(Path.Combine(AppContext.BaseDirectory, n)))}");
    Console.WriteLine($"before: {Loaded()}");
    NativeRuntime.UseSystemCopy = false;
    await using (var tiny = await WhisperEngine.LoadAsync("tiny", "en"))
    {
        Console.WriteLine($"after loading Whisper on the {tiny.Backend}: {Loaded()}");
        var said = new List<string>();
        using (var speech = tiny.ExtractSpeech(File.ReadAllBytes(args[1])))
        {
            if (speech is not null)
                await foreach (var segment in tiny.TranscribeAsync(speech.Wav))
                    said.Add(segment.Text);
        }
        Console.WriteLine($"transcribed: {string.Join(' ', said)}");
    }
    return;
}

PersonalModel.Directory = args[0];
VoiceDataset.Root = Path.Combine(args[0], "no-voice");
var pcm = AudioFiles.WavToPcm(File.ReadAllBytes(args[1]));
var samples = new short[pcm.Length / 2];
Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);

short[] Speed(short[] source, double rate)
{
    var result = new short[(int)(source.Length / rate)];
    for (int i = 0; i < result.Length; i++)
    {
        var at = i * rate;
        var k = Math.Min((int)at, source.Length - 2);
        result[i] = (short)(source[k] + (source[k + 1] - source[k]) * (at - k));
    }
    return result;
}

short[] Noise(short[] source, double db, int seed)
{
    var random = new Random(seed);
    var level = Math.Sqrt(source.Average(s => (double)s * s)) * Math.Pow(10, -db / 20);
    return source.Select(s => (short)Math.Clamp(s + (random.NextDouble() * 2 - 1) * level * 1.7, short.MinValue, short.MaxValue)).ToArray();
}

short[] Gain(short[] source, double gain) => source.Select(s => (short)Math.Clamp(s * gain, short.MinValue, short.MaxValue)).ToArray();

var variants = new (string Name, short[] Audio)[]
{
    ("as recorded", samples),
    ("8% slower", Speed(samples, 0.92)),
    ("4% slower", Speed(samples, 0.96)),
    ("4% faster", Speed(samples, 1.04)),
    ("8% faster", Speed(samples, 1.08)),
    ("12% faster", Speed(samples, 1.12)),
    ("noise 20 dB", Noise(samples, 20, 1)),
    ("noise 12 dB", Noise(samples, 12, 2)),
    ("half as loud", Gain(samples, 0.5)),
    ("slower + noise", Noise(Speed(samples, 0.94), 18, 3)),
};

byte[] Wav(short[] audio)
{
    var bytes = new byte[audio.Length * 2];
    Buffer.BlockCopy(audio, 0, bytes, 0, bytes.Length);
    return AudioFiles.PcmToWav(bytes);
}

var hints = args.Length > 3 ? new[] { "", args[3] } : new[] { "" };
foreach (var model in args[2].Split(','))
    foreach (var hint in hints)
    {
        await using var engine = await WhisperEngine.LoadAsync(model, "en", hint);
        Console.WriteLine($"== model: {model}{(hint.Length > 0 ? $" | word hint: {hint}" : "")}");
        foreach (var (name, audio) in variants)
        {
            var parts = new List<string>();
            using (var speech = engine.ExtractSpeech(Wav(audio)))
            {
                if (speech is not null)
                    await foreach (var segment in engine.TranscribeAsync(speech.Wav))
                        parts.Add(segment.Text);
            }
            Console.WriteLine($"   {name,-15} {string.Join(' ', parts)}");
        }
    }
