using System.Windows.Automation;
using System.Windows.Input;
using WhisperStt.Desktop.Mvvm;
using WhisperStt.Desktop.Targets;

namespace WhisperStt.Desktop.ViewModels;

public enum TargetStatus { Ready, Sent, Failed }

// A text box in another app. While it is on, every new sentence is written into it.
public sealed class TargetViewModel : ObservableObject
{
    private readonly Action _changed;
    private AutomationElement? _element; // only touched from the delivery worker after creation
    private string _name;
    private bool _isOn;
    private bool _pressEnter;
    private TargetStatus _status;
    private string _statusText = "Ready";

    public TargetViewModel(TargetInfo info, string name, bool isOn, bool pressEnter, AutomationElement? element,
        Action changed, Action<TargetViewModel> remove, Action<TargetViewModel> test)
    {
        Info = info;
        _name = name;
        _isOn = isOn;
        _pressEnter = pressEnter;
        _element = element;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
        TestCommand = new RelayCommand(() => test(this));
    }

    public TargetInfo Info { get; }

    public string Name
    {
        get => _name;
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? Info.DefaultName : value.Trim();
            if (SetProperty(ref _name, name))
                _changed();
            else if (name != value)
                OnPropertyChanged(); // show the restored name after an empty edit
        }
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value))
                _changed();
        }
    }

    // Presses Enter after each sentence, which sends it in chat apps (Twitch, Discord...).
    public bool PressEnter
    {
        get => _pressEnter;
        set
        {
            if (SetProperty(ref _pressEnter, value))
                _changed();
        }
    }

    public string Detail => $"{Info.AppName} · {Info.Label}";

    public TargetStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand RemoveCommand { get; }
    public ICommand TestCommand { get; }

    // These run on the delivery worker thread.
    public SendMethod Write(string text)
    {
        var pressEnter = PressEnter;
        return WithElement(element => TextSender.Append(element, text, pressEnter));
    }

    public void Clear() => WithElement(element => { TextSender.Clear(element); return true; });

    public void PressEnterKey() => WithElement(element => { TextSender.PressEnter(element); return true; });

    private T WithElement<T>(Func<AutomationElement, T> action)
    {
        var element = _element ?? Locate();
        try
        {
            return action(element);
        }
        catch (ElementNotAvailableException)
        {
            // The box was closed or recreated since we last used it: look for it again once.
            _element = null;
            return action(Locate());
        }
    }

    private AutomationElement Locate() =>
        _element = TargetLocator.Find(Info)
            ?? throw new InvalidOperationException($"Couldn't find it. Is {Info.AppName} open with this field visible?");

    public void ReportSent(SendMethod method) =>
        SetStatus(TargetStatus.Sent, $"Sent at {DateTime.Now:HH:mm:ss}" +
            (method == SendMethod.Typing ? " (typed)" : "") + (PressEnter ? " + Enter" : ""));

    public void ReportDone(string action) => SetStatus(TargetStatus.Sent, $"{action} at {DateTime.Now:HH:mm:ss}");

    public void ReportFailed(string message) => SetStatus(TargetStatus.Failed, message);

    private void SetStatus(TargetStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }
}
