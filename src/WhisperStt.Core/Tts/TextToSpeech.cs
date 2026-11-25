using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;

namespace WhisperStt.Core.Tts;

// Reads text aloud with Kokoro, one queued sentence at a time, on the default speakers.
// Runs on the CPU (full-precision model: about 1.5x faster than real time on a Ryzen 5 5600G;
// the int8 model was 3x slower there). Events are raised on background threads.
public sealed class TextToSpeech : IDisposable
{
    private readonly string _modelDir;
    private readonly Dictionary<VoiceAccent, OfflineTts> _engines = [];
    private readonly Lock _enginesGate = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly Task _worker;
    private volatile WaveOut? _playing;
    private int _pending; // sentences queued or playing (a single-reader channel can't count itself)

    private TextToSpeech(string modelDir)
    {
        _modelDir = modelDir;
        _worker = Task.Run(RunAsync);
    }

    public KokoroVoice Voice { get; set; } = KokoroVoices.Default;
    public float Speed { get; set; } = 1f;     // 0.5 = half speed, 2 = twice as fast
    public float Volume { get; set; } = 1f;    // 0..1

    public event Action<bool>? SpeakingChanged;   // true while audio is playing
    public event Action<Exception>? Failed;

    // Downloads the shared voice model on first use (334 MB, resumable) and loads the voice's accent.
    public static async Task<TextToSpeech> LoadAsync(KokoroVoice voice, Action<string>? log = null,
        Action<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var dir = await ModelCatalog.EnsureKokoroAsync(log, progress, ct).ConfigureAwait(false);
        log?.Invoke("Loading the voice...");
        var tts = new TextToSpeech(dir) { Voice = voice };
        // The first sentence an engine generates is about twice as slow; get that out of the way now.
        await Task.Run(() => tts.EngineFor(voice.Accent).Generate("Ready.", 1f, voice.SpeakerId), ct).ConfigureAwait(false);
        return tts;
    }

    // Loads an accent's pronunciation dictionary ahead of time (a few seconds), e.g. right after a voice is picked.
    public Task PreloadAsync(VoiceAccent accent) => Task.Run(() => EngineFor(accent));

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(text.Trim()))
            Interlocked.Decrement(ref _pending);
    }

    // Drops everything waiting and cuts off the sentence being played.
    public void Stop()
    {
        while (_queue.Reader.TryRead(out _))
            Interlocked.Decrement(ref _pending);
        _playing?.Stop();
    }

    private async Task RunAsync()
    {
        await foreach (var text in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var voice = Voice;
                var audio = EngineFor(voice.Accent).Generate(text, Speed, voice.SpeakerId);
                if (audio.NumSamples == 0)
                    throw new InvalidOperationException("The voice couldn't pronounce that sentence.");
                SpeakingChanged?.Invoke(true);
                await PlayAsync(audio.Samples, audio.SampleRate).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex);
            }
            finally
            {
                if (Interlocked.Decrement(ref _pending) <= 0)
                    SpeakingChanged?.Invoke(false);
            }
        }
    }

    private async Task PlayAsync(float[] samples, int sampleRate)
    {
        var volume = Math.Clamp(Volume, 0f, 1f);
        var pcm = new byte[samples.Length * sizeof(float)];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.TryWriteBytes(pcm.AsSpan(i * sizeof(float)), samples[i] * volume);

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new RawSourceWaveStream(new MemoryStream(pcm), WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        using var output = new WaveOut();
        output.PlaybackStopped += (_, _) => finished.TrySetResult();
        output.Init(stream);
        _playing = output;
        output.Play();
        await finished.Task.ConfigureAwait(false);
        _playing = null;
    }

    // American and British voices need their own pronunciation dictionary, so each accent gets its own engine.
    private OfflineTts EngineFor(VoiceAccent accent)
    {
        lock (_enginesGate)
        {
            if (_engines.TryGetValue(accent, out var engine))
                return engine;

            var config = new OfflineTtsConfig();
            config.Model.Kokoro.Model = Path.Combine(_modelDir, ModelCatalog.KokoroModelFile);
            config.Model.Kokoro.Voices = Path.Combine(_modelDir, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(_modelDir, "tokens.txt");
            config.Model.Kokoro.DictDir = Path.Combine(_modelDir, "dict");
            // espeak-ng pronounces words missing from the dictionary, such as usernames.
            config.Model.Kokoro.DataDir = Path.Combine(_modelDir, "espeak-ng-data");
            config.Model.Kokoro.Lexicon = Path.Combine(_modelDir, accent == VoiceAccent.British ? "lexicon-gb-en.txt" : "lexicon-us-en.txt");
            // espeak-ng's British English voice is plain "en" ("en-gb" isn't in the bundled data and fails).
            config.Model.Kokoro.Lang = accent == VoiceAccent.British ? "en" : "en-us";
            // More than 4 threads was slower on a 6-core Ryzen: the model is too small to split further.
            config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;

            engine = new OfflineTts(config);
            _engines[accent] = engine;
            return engine;
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        Stop();
        _worker.Wait(TimeSpan.FromSeconds(2));
        lock (_enginesGate)
        {
            foreach (var engine in _engines.Values)
                engine.Dispose();
            _engines.Clear();
        }
    }
}
