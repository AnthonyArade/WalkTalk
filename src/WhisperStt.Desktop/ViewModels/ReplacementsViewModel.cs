using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using WhisperStt.Core;
using WhisperStt.Desktop.Mvvm;

namespace WhisperStt.Desktop.ViewModels;

// Word replacements: the user looks up a word the model knows (or types any word or phrase) and gives
// the text to write in its place, such as an abbreviation or slang. Applied to every dictated sentence.
public sealed class ReplacementsViewModel : ObservableObject
{
    private const int MaxSuggestions = 14;

    private readonly Func<string> _getModel;
    private readonly Action _changed;
    private ModelVocabulary _vocabulary = ModelVocabulary.Empty;
    private string? _vocabularyFrom;
    private bool _isOpen, _isLoading, _noModel;
    private string _query = "", _custom = "", _note = "";

    public ReplacementsViewModel(IEnumerable<Replacement> saved, Func<string> getModel, Action changed)
    {
        _getModel = getModel;
        _changed = changed;
        foreach (var replacement in saved)
            Items.Add(replacement);

        OpenCommand = new RelayCommand(Open);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        AddCommand = new RelayCommand(Add, () => WordReplacements.KeyOf(Query).Length > 0 && Custom.Trim().Length > 0);
        RemoveCommand = new RelayCommand<Replacement>(replacement =>
        {
            Items.Remove(replacement);
            Saved();
        });
        PickCommand = new RelayCommand<string>(word =>
        {
            Query = word;
            Picked?.Invoke();
        });
    }

    // Raised when a suggested word was chosen, so the window can move the cursor to the replacement box.
    public event Action? Picked;

    public ObservableCollection<Replacement> Items { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];

    public ICommand OpenCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand PickCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    // The word or phrase to listen for. Typing searches the model's word list.
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
                UpdateSuggestions();
        }
    }

    // What to write instead.
    public string Custom
    {
        get => _custom;
        set => SetProperty(ref _custom, value);
    }

    // One line under the search box: what the list is, or why a word isn't in it.
    public string Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    public string Summary => Items.Count switch { 0 => "", var n => $" ({n})" };

    private void Open()
    {
        IsOpen = true;
        _ = LoadVocabularyAsync();
    }

    // The word list comes from the selected model's file; any downloaded model will do, since they
    // share the same vocabulary.
    private async Task LoadVocabularyAsync()
    {
        var path = ModelCatalog.PathIfDownloaded(_getModel())
                   ?? ModelCatalog.Names.Select(ModelCatalog.PathIfDownloaded).FirstOrDefault(p => p is not null);
        _noModel = path is null;
        if (path is null || path == _vocabularyFrom)
        {
            UpdateSuggestions();
            return;
        }

        _isLoading = true;
        UpdateSuggestions();
        try
        {
            _vocabulary = await Task.Run(() => ModelVocabulary.Load(path));
            _vocabularyFrom = path;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _noModel = true; // unreadable: typing a word by hand still works
        }
        _isLoading = false;
        UpdateSuggestions();
    }

    private void UpdateSuggestions()
    {
        var found = _vocabulary.Search(Query, MaxSuggestions);
        Suggestions.Clear();
        foreach (var word in found)
            Suggestions.Add(word);

        var typed = Query.Trim();
        Note =
            _isLoading ? "Reading the model's word list…" :
            _noModel ? "No model is downloaded yet, so there is no word list to search. You can still type any word or phrase." :
            typed.Length == 0 ? $"{_vocabulary.Count:N0} words in the model's list. Any other word, or a phrase like “by the way”, works too: just type it." :
            typed.Contains(' ') ? "A phrase: it is replaced when these words are heard together." :
            found.Count == 0 ? "Not in the list as a single word. The model can still write it, so you can add it." :
            "";
    }

    private void Add()
    {
        var replacement = new Replacement(Query.Trim(), Custom.Trim());
        // One replacement per word or phrase: adding it again changes what is written.
        var key = WordReplacements.KeyOf(replacement.From);
        foreach (var old in Items.Where(r => WordReplacements.KeyOf(r.From) == key).ToList())
            Items.Remove(old);
        Items.Insert(0, replacement);
        Query = "";
        Custom = "";
        Saved();
    }

    private void Saved()
    {
        OnPropertyChanged(nameof(Summary));
        _changed();
    }
}
