using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperStt.Core.Training;

public sealed record GraphicsCard(string Name, int MemoryMb);

// Fraction is null when there is nothing to measure (unpacking, installing).
public sealed record KitProgress(string Text, double? Fraction = null);

// The tools voice training needs: a private copy of Python with PyTorch and Whisper's reference code.
// About 5 GB, downloaded once on first use into %LOCALAPPDATA%\WhisperStt\training; nothing else on the
// PC is touched and deleting that folder removes it.
public static partial class TrainingKit
{
    // Raise when the list of tools changes, so existing installs are refreshed.
    private const string Version = "1";
    private const string PythonVersion = "3.13.17";
    private const string TorchVersion = "2.14.1";
    // CUDA 12.6 builds run on the widest range of NVIDIA drivers still in use.
    private const string TorchIndex = "https://download.pytorch.org/whl/cu126";
    private const string WhisperVersion = "20250625";
    private static readonly string[] Pinned = ["numpy==2.5.4", "numba==0.68.1", "llvmlite==0.50.0", "tiktoken==0.14.0"];

    public const int MinMemoryMb = 3500;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "training");

    private static string PythonDirectory => Path.Combine(Directory, "python");
    public static string PythonPath => Path.Combine(PythonDirectory, "tools", "python.exe");
    private static string MarkerPath => Path.Combine(Directory, "installed.txt");

    public static bool IsInstalled =>
        File.Exists(PythonPath) && File.Exists(MarkerPath) && File.ReadAllText(MarkerPath).Trim() == Version;

    // The NVIDIA card training would run on, or null when this PC has none (PyTorch trains on NVIDIA
    // cards only; on a processor alone a training run would take hours).
    public static async Task<GraphicsCard?> FindGraphicsCardAsync(CancellationToken ct = default)
    {
        var tool = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(tool))
            return null;
        try
        {
            string? first = null;
            var exit = await RunAsync(tool, ["--query-gpu=name,memory.total", "--format=csv,noheader,nounits"],
                line => first ??= line, ct).ConfigureAwait(false);
            var parts = first?.Split(',', StringSplitOptions.TrimEntries);
            if (exit != 0 || parts is not { Length: 2 } || !int.TryParse(parts[1], out var memory))
                return null;
            return new GraphicsCard(parts[0], memory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public static async Task InstallAsync(Action<KitProgress> progress, CancellationToken ct = default)
    {
        if (IsInstalled)
            return;
        System.IO.Directory.CreateDirectory(Directory);
        File.Delete(MarkerPath);

        if (!File.Exists(PythonPath))
        {
            // The official Python build, as published by the Python Software Foundation on nuget.org.
            var package = Path.Combine(Directory, "python.zip");
            await Downloader.DownloadAsync(
                $"https://api.nuget.org/v3-flatcontainer/python/{PythonVersion}/python.{PythonVersion}.nupkg", package,
                p => progress(new KitProgress($"Downloading Python: {p}", p.Fraction * 0.02)), ct).ConfigureAwait(false);
            progress(new KitProgress("Unpacking Python…", 0.02));
            if (System.IO.Directory.Exists(PythonDirectory))
                System.IO.Directory.Delete(PythonDirectory, recursive: true);
            await Task.Run(() => ZipFile.ExtractToDirectory(package, PythonDirectory), ct).ConfigureAwait(false);
            File.Delete(package);
        }

        var constraints = Path.Combine(Directory, "constraints.txt");
        await File.WriteAllLinesAsync(constraints, Pinned, ct).ConfigureAwait(false);

        // PyTorch is one 2.5 GB file; everything else is small.
        await PipAsync(["install", $"torch=={TorchVersion}", "--index-url", TorchIndex],
            "Downloading the training tools", 0.02, 0.88, progress, ct).ConfigureAwait(false);
        await PipAsync(["install", $"openai-whisper=={WhisperVersion}", "--constraint", constraints],
            "Downloading the speech tools", 0.88, 0.97, progress, ct).ConfigureAwait(false);

        progress(new KitProgress("Checking the training tools…", 0.98));
        var problem = new StringBuilder();
        var exit = await RunPythonAsync(["-c", "import torch, whisper; print(torch.cuda.is_available())"],
            line => problem.AppendLine(line), ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("The training tools were installed but don't start: " + LastLine(problem.ToString()));

        await File.WriteAllTextAsync(MarkerPath, Version, ct).ConfigureAwait(false);
        progress(new KitProgress("Training tools ready.", 1));
    }

    private static async Task PipAsync(string[] arguments, string what, double from, double to,
        Action<KitProgress> progress, CancellationToken ct)
    {
        progress(new KitProgress($"{what}…", from));
        var output = new StringBuilder();
        long biggest = 0;
        string[] all = ["-m", "pip", .. arguments, "--no-cache-dir", "--no-warn-script-location",
            "--disable-pip-version-check", "--no-input", "--progress-bar", "raw"];
        var exit = await RunPythonAsync(all, line =>
        {
            // "Progress 1048576 of 2600000000" while a file downloads
            var match = PipProgress().Match(line);
            if (match.Success)
            {
                var received = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var total = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (total >= biggest && total > 50 << 20)
                {
                    biggest = total;
                    var fraction = (double)received / total;
                    progress(new KitProgress($"{what}: {received / 1e9:0.0} of {total / 1e9:0.0} GB", from + (to - from) * 0.9 * fraction));
                }
                return;
            }
            output.AppendLine(line);
            if (line.StartsWith("Installing collected packages", StringComparison.Ordinal))
                progress(new KitProgress("Installing… this takes a minute or two.", from + (to - from) * 0.9));
        }, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new InvalidOperationException("Couldn't install the training tools: " + LastLine(output.ToString()));
    }

    // Runs the private Python, cut off from any other Python installed on the PC.
    internal static Task<int> RunPythonAsync(IEnumerable<string> arguments, Action<string> onLine, CancellationToken ct) =>
        RunAsync(PythonPath, arguments, onLine, ct, environment =>
        {
            environment.Remove("PYTHONPATH");
            environment.Remove("PYTHONHOME");
            environment["PYTHONNOUSERSITE"] = "1";
            environment["PYTHONUTF8"] = "1";
            environment["PYTHONUNBUFFERED"] = "1";
        });

    // Runs a program hidden, passing each line it prints to onLine. Cancelling stops it and anything it started.
    internal static async Task<int> RunAsync(string program, IEnumerable<string> arguments, Action<string> onLine,
        CancellationToken ct, Action<IDictionary<string, string?>>? configure = null)
    {
        var start = new ProcessStartInfo(program)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        configure?.Invoke(start.Environment);

        using var process = new Process { StartInfo = start };
        void Receive(object _, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                onLine(e.Data);
        }
        process.OutputDataReceived += Receive;
        process.ErrorDataReceived += Receive;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // it ended on its own in the meantime
            }
            throw;
        }
        return process.ExitCode;
    }

    internal static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "unknown error";

    [GeneratedRegex(@"^Progress (\d+) of (\d+)$")]
    private static partial Regex PipProgress();
}
