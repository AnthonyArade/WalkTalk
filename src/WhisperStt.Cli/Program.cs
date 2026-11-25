using System.Globalization;
using System.Text;
using NAudio.Wave;
using WhisperStt.Core;

// WalkTalk command line: offline speech-to-text with Whisper.
//   WalkTalk-cli                     -> record from the microphone (Enter to start/stop)
//   WalkTalk-cli <audio file>        -> transcribe a file (wav, mp3, m4a, mp4, ...)
// Options: --model tiny|base|small|medium|turbo|large   --lang en|auto|fr|...  (default: turbo, en)

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

string modelName = "turbo";
string language = "en";
string? inputFile = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model" when i + 1 < args.Length: modelName = args[++i].ToLowerInvariant(); break;
        case "--lang" when i + 1 < args.Length: language = args[++i].ToLowerInvariant(); break;
        default: inputFile = args[i]; break;
    }
}

if (!ModelCatalog.Names.Contains(modelName))
{
    Console.Error.WriteLine($"Unknown model '{modelName}'. Use {string.Join(", ", ModelCatalog.Names)}.");
    return 1;
}

await using var engine = await WhisperEngine.LoadAsync(modelName, language, log: Console.WriteLine);
Console.WriteLine($"Running on the {engine.Backend}.");

if (inputFile is not null)
{
    if (!File.Exists(inputFile))
    {
        Console.Error.WriteLine($"File not found: {inputFile}");
        return 1;
    }
    using var wav = AudioFiles.LoadAsWhisperWav(inputFile);
    await TranscribeAsync(engine, wav);
    return 0;
}

if (WaveIn.DeviceCount == 0)
{
    Console.Error.WriteLine("No microphone found.");
    return 1;
}

while (true)
{
    Console.WriteLine();
    Console.Write("Press Enter to start recording (or type q + Enter to quit): ");
    if (Console.ReadLine()?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true)
        return 0;

    Console.WriteLine("Recording... press Enter to stop.");
    using var recording = RecordFromMicrophone();
    if (recording is null)
    {
        Console.WriteLine("Recording too short, try again.");
        continue;
    }
    Console.WriteLine("Transcribing...");
    await TranscribeAsync(engine, recording);
}

// Returns null when the recording is shorter than half a second.
static MemoryStream? RecordFromMicrophone()
{
    var format = AudioFiles.WhisperFormat;
    var output = new MemoryStream();
    var writer = new WaveFileWriter(output, format); // leaves the caller's stream open
    var microphone = new MicCapture();
    microphone.Data += (buffer, count) => writer.Write(buffer, 0, count);

    microphone.Start(0); // the default microphone
    Console.ReadLine();
    microphone.Stop();

    var dataBytes = writer.Length;
    writer.Dispose(); // finalizes the WAV header
    if (dataBytes < format.AverageBytesPerSecond / 2)
    {
        output.Dispose();
        return null;
    }
    output.Position = 0;
    return output;
}

static async Task TranscribeAsync(WhisperEngine engine, Stream wav)
{
    var heardSpeech = false;
    await foreach (var segment in engine.TranscribeAsync(wav))
    {
        heardSpeech = true;
        Console.WriteLine($"[{segment.Start:mm\\:ss} -> {segment.End:mm\\:ss}] {segment.Text}");
    }
    if (!heardSpeech)
        Console.WriteLine("(no speech detected)");
}
