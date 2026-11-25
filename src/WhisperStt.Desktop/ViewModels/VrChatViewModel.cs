using System.Net.Sockets;
using System.Windows.Input;
using WhisperStt.Core;
using WhisperStt.Desktop.Mvvm;

namespace WhisperStt.Desktop.ViewModels;

// The built-in VRChat destination: while it is on, every sentence goes to VRChat's chatbox.
// Unlike the other fields it needs no picking: VRChat listens on a fixed local port.
public sealed class VrChatViewModel : ObservableObject, IDisposable
{
    public const string Name = "VRChat";
    private readonly VrChatChatbox _chatbox = new();
    private readonly Action _changed;
    private bool _isOn, _typing;
    private TargetStatus _status;
    private string _statusText = "Ready";

    public VrChatViewModel(bool isOn, Action changed)
    {
        _isOn = isOn;
        _changed = changed;
        TestCommand = new RelayCommand(() => Deliver(null, MainViewModel.TestText, null));
    }

    public ICommand TestCommand { get; }

    public string Detail => $"Chatbox · {VrChatChatbox.Endpoint}";

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value))
                return;
            if (!value)
                SetTyping(false);
            _changed();
        }
    }

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

    // Sends a sentence, or acts on a voice command: "Clean" empties the chatbox; "Send" has nothing to do,
    // since every sentence is shown as soon as it is said.
    public void Deliver(VoiceCommand? command, string text, DeliveryViewModel? delivery)
    {
        if (command == VoiceCommand.Send)
        {
            if (delivery is not null)
                delivery.State = DeliveryState.Sent;
            return;
        }

        try
        {
            _typing = false; // a new message ends the typing bubble in VRChat
            if (command == VoiceCommand.Clear)
                _chatbox.Clear();
            else
                _chatbox.Send(text);

            // The message can't be confirmed, but if the game isn't even running it certainly didn't arrive.
            if (!VrChatChatbox.IsVrChatRunning)
            {
                Fail("VRChat isn't running.", delivery);
                return;
            }
            Status = TargetStatus.Sent;
            StatusText = $"{(command == VoiceCommand.Clear ? "Cleared" : "Sent")} at {DateTime.Now:HH:mm:ss}";
            if (delivery is not null)
                delivery.State = DeliveryState.Sent;
        }
        catch (SocketException ex)
        {
            Fail($"Couldn't send: {ex.Message}", delivery);
        }
    }

    private void Fail(string message, DeliveryViewModel? delivery)
    {
        Status = TargetStatus.Failed;
        StatusText = message;
        if (delivery is not null)
        {
            delivery.State = DeliveryState.Failed;
            delivery.Error = message;
        }
    }

    // Shows or hides the typing bubble while a sentence is being heard or transcribed.
    public void SetTyping(bool typing)
    {
        if (typing == _typing || (typing && !IsOn))
            return;
        _typing = typing;
        try
        {
            _chatbox.SetTyping(typing);
        }
        catch (SocketException)
        {
            // only a cosmetic signal; the next sentence reports a real problem
        }
    }

    public void Dispose()
    {
        SetTyping(false);
        _chatbox.Dispose();
    }
}
