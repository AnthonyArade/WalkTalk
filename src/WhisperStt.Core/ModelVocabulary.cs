using System.Text;

namespace WhisperStt.Core;

// The whole words a Whisper model has in its vocabulary, for searching.
// Whisper's vocabulary is about 50,000 "pieces": common words are one piece each ("because", "tomorrow"),
// rarer words are built from several ("Moon" + "h" + "ollow"). Only the one-piece words can be listed;
// the model can still write any other word, so the search is a help, not a limit.
public sealed class ModelVocabulary
{
    private const int GgmlMagic = 0x67676D6C;
    private readonly (string Word, string Key)[] _words; // most common first

    private ModelVocabulary((string, string)[] words, int pieces)
    {
        _words = words;
        Pieces = pieces;
    }

    public static ModelVocabulary Empty { get; } = new([], 0);

    public int Pieces { get; }
    public int Count => _words.Length;

    // Reads the vocabulary stored at the start of a model file (a few hundred KB; the weights are not read).
    public static ModelVocabulary Load(string modelPath)
    {
        using var reader = new BinaryReader(File.OpenRead(modelPath));
        if (reader.ReadInt32() != GgmlMagic)
            throw new InvalidDataException("This is not a Whisper model file.");
        reader.BaseStream.Seek(11 * sizeof(int), SeekOrigin.Current);            // model sizes
        int melRows = reader.ReadInt32(), melColumns = reader.ReadInt32();
        reader.BaseStream.Seek((long)melRows * melColumns * sizeof(float), SeekOrigin.Current);

        var pieces = reader.ReadInt32();
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var seen = new Dictionary<string, int>();
        var words = new List<(string, string)>();
        for (int i = 0; i < pieces; i++)
        {
            var bytes = reader.ReadBytes(reader.ReadInt32());
            string piece;
            try
            {
                piece = strict.GetString(bytes);
            }
            catch (ArgumentException)
            {
                continue; // half of a multi-byte character: not a word
            }

            // A piece that starts a new word begins with a space. It is a whole word when the rest is letters.
            if (piece.Length < 2 || piece[0] != ' ')
                continue;
            var word = piece[1..];
            if (!word.All(c => char.IsLetter(c) || c is '\'' or '’') || !char.IsLetter(word[0]))
                continue;
            // The vocabulary has "the", "The" and "THE": list the word once, at the place of its most
            // common form, written in lowercase when that form exists.
            var lower = word.ToLowerInvariant();
            if (seen.TryGetValue(lower, out var index))
            {
                if (word == lower)
                    words[index] = (word, words[index].Item2);
            }
            else
            {
                seen[lower] = words.Count;
                words.Add((word, HintCorrector.Key(word)));
            }
        }
        // Pieces are stored roughly from most to least common, which makes a good order for results.
        return new ModelVocabulary([.. words], pieces);
    }

    // Words starting with the query first, then words containing it. Case and accents don't matter.
    public IReadOnlyList<string> Search(string query, int max)
    {
        var key = HintCorrector.Key(query);
        if (key.Length == 0)
            return [];
        var starts = _words.Where(w => w.Key.StartsWith(key, StringComparison.Ordinal)).Select(w => w.Word).Take(max).ToList();
        if (starts.Count < max && key.Length >= 3)
            starts.AddRange(_words.Where(w => w.Key.Contains(key, StringComparison.Ordinal) && !w.Key.StartsWith(key, StringComparison.Ordinal))
                .Select(w => w.Word).Take(max - starts.Count));
        return starts;
    }

    public bool Contains(string word)
    {
        var key = HintCorrector.Key(word);
        return key.Length > 0 && _words.Any(w => w.Key == key);
    }
}
