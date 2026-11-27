using WhisperStt.Core.Obs;
using WhisperStt.Desktop.Mvvm;

namespace WhisperStt.Desktop.ViewModels;

// One of a canvas's four sounds (typing, new sentence, show, hide): which sound and how loud.
public sealed class SoundViewModel(CanvasViewModel canvas, string slot, string title, string hint, Func<OverlaySound> sound) : ObservableObject
{
    // The built-in sounds, made by the overlay page itself, plus a file of the user's own.
    public static IReadOnlyList<Choice<string>> Kinds { get; } =
    [
        new("", "None"), new("click", "Click"), new("tick", "Tick"), new("typewriter", "Typewriter"), new("bubble", "Bubble"),
        new("pop", "Pop"), new("chime", "Chime"), new("ding", "Ding"), new("whoosh-in", "Whoosh in"), new("whoosh-out", "Whoosh out"),
        new("swoosh", "Swoosh"), new("file", "My sound file…")
    ];

    public CanvasViewModel Canvas { get; } = canvas;
    public string Slot { get; } = slot;
    public string Title { get; } = title;
    public string Hint { get; } = hint;
    private OverlaySound Sound => sound();

    public string Kind
    {
        get => Sound.Kind;
        set
        {
            value ??= "";
            if (Sound.Kind == value)
                return;
            Sound.Kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFile));
            OnPropertyChanged(nameof(HasSound));
            OnPropertyChanged(nameof(FileText));
            Canvas.SoundChanged(false);
            if (value == "file" && Sound.File.Length == 0)
                FileWanted?.Invoke(this); // ask for the file right away
        }
    }

    public bool IsFile => Kind == "file";
    public bool HasSound => Kind.Length > 0;
    public string FileText => Sound.File.Length == 0 ? "No file chosen" : "File chosen";

    public double Volume
    {
        get => Sound.Volume;
        set
        {
            var rounded = Math.Round(Math.Clamp(value, 0, 1), 2);
            if (Sound.Volume == rounded)
                return;
            Sound.Volume = rounded;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeText));
            Canvas.SoundChanged(false);
        }
    }

    public string VolumeText => $"{Volume:P0}";

    // A sound file was copied for this moment of this canvas.
    public void SetFile(string name)
    {
        Sound.File = name;
        OnPropertyChanged(nameof(FileText));
        Canvas.SoundChanged(true);
    }

    // Raised when the user picks "My sound file…" and none is chosen yet.
    public static event Action<SoundViewModel>? FileWanted;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
