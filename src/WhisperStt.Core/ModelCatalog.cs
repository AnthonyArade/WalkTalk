using Whisper.net.Ggml;
using WhisperStt.Core.Training;

namespace WhisperStt.Core;

public static class ModelCatalog
{
    public static IReadOnlyList<string> Names { get; } = ["tiny", "base", "small", "medium", "turbo", "large"];

    // Shared by the console and desktop apps so each model is downloaded only once.
    // Still named after the app's original name, so existing models (several GB) and settings are kept.
    public static string ModelsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "models");

    private static string FileOf(string name) => Path.Combine(ModelsDirectory, $"ggml-{name}.bin");

    // Where a model's file is, or null when it has not been downloaded (or trained) yet.
    public static string? PathIfDownloaded(string name)
    {
        var path = PersonalModel.PathFor(name) ?? FileOf(name);
        return File.Exists(path) ? path : null;
    }

    public static async Task<string> EnsureModelAsync(string name, Action<string>? log = null, CancellationToken ct = default)
    {
        // The model trained on the user's voice is made on this PC, not downloaded.
        if (PersonalModel.PathFor(name) is { } personal)
            return File.Exists(personal)
                ? personal
                : throw new FileNotFoundException("Your voice model is missing. Choose another model, or train it again in Voice training.");

        var type = name switch
        {
            "tiny" => GgmlType.Tiny,
            "base" => GgmlType.Base,
            "small" => GgmlType.Small,
            "medium" => GgmlType.Medium,
            "turbo" => GgmlType.LargeV3Turbo,
            "large" => GgmlType.LargeV3,
            _ => throw new ArgumentException($"Unknown model '{name}'. Use {string.Join(", ", Names)}.", nameof(name))
        };

        Directory.CreateDirectory(ModelsDirectory);
        var path = FileOf(name);
        if (File.Exists(path))
            return path;

        log?.Invoke($"Downloading the '{name}' model (one time only)...");
        var partial = path + ".part";
        await using (var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: ct).ConfigureAwait(false))
        await using (var file = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long total = 0, lastReport = 0;
            int read;
            while ((read = await model.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                if (total - lastReport >= 10 << 20)
                {
                    lastReport = total;
                    log?.Invoke($"Downloading the '{name}' model (one time only)... {total >> 20} MB");
                }
            }
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    // Kokoro v1.0 text-to-speech, as packaged for sherpa-onnx. Downloaded and unpacked once.
    private const string KokoroArchive = "kokoro-multi-lang-v1_0";
    public const string KokoroModelFile = "model.onnx";

    // The speech model is shared by every voice (each voice itself is only about 0.5 MB inside it).
    // An interrupted download resumes where it stopped the next time.
    public static async Task<string> EnsureKokoroAsync(Action<string>? log = null, Action<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(ModelsDirectory, KokoroArchive);
        if (File.Exists(Path.Combine(dir, KokoroModelFile)) && File.Exists(Path.Combine(dir, "voices.bin")))
            return dir;

        Directory.CreateDirectory(ModelsDirectory);
        var archive = Path.Combine(ModelsDirectory, KokoroArchive + ".tar.bz2");
        var url = $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{KokoroArchive}.tar.bz2";
        if (!File.Exists(archive))
        {
            log?.Invoke("Downloading the voice model, shared by all voices (one time only)...");
            await Downloader.DownloadAsync(url, archive, progress, ct).ConfigureAwait(false);
        }

        // Windows 10+ ships bsdtar, which unpacks .tar.bz2 natively.
        log?.Invoke("Unpacking the voice model...");
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tar)
               {
                   ArgumentList = { "-xjf", archive, "-C", ModelsDirectory },
                   CreateNoWindow = true,
                   UseShellExecute = false
               })!)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                // A damaged download: remove it so the next attempt downloads a fresh copy.
                File.Delete(archive);
                throw new IOException("The downloaded voice model was damaged. Turn TTS off and on to download it again.");
            }
        }
        File.Delete(archive);
        return dir;
    }

    // Silero VAD: a tiny model (under 1 MB) that recognises human voice, used to ignore clicks and noise.
    public static async Task<string> EnsureVadModelAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var path = Path.Combine(ModelsDirectory, "ggml-silero-vad-v6.bin");
        if (File.Exists(path))
            return path;

        var partial = path + ".part";
        await using (var model = await WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V6_2_0, ct).ConfigureAwait(false))
        await using (var file = File.Create(partial))
        {
            await model.CopyToAsync(file, ct).ConfigureAwait(false);
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }
}
