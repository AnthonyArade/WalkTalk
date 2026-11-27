using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using WhisperStt.Core;
using WhisperStt.Desktop.Mvvm;
using WhisperStt.Desktop.Services;

namespace WhisperStt.Desktop.ViewModels;

public enum TwitchState { Disconnected, Connecting, Connected }

// The built-in Twitch destination: once the user has connected their Twitch account, every sentence is
// posted as a chat message in the chosen channel. Sign-in is OAuth 2: the user approves WalkTalk on
// twitch.tv in their browser, and the app only ever holds a token that allows writing chat messages.
public sealed class TwitchViewModel : ObservableObject
{
    public const string Name = "Twitch";

    // Settable so tests don't open a real browser.
    public static Action<string> OpenBrowser { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private readonly Action _changed;
    private readonly Action<Action> _post;
    private TwitchSession? _session;
    private CancellationTokenSource? _connecting;
    private Task _sending = Task.CompletedTask;   // messages go out one at a time, in the order spoken
    private (string Channel, string Id)? _channelId;
    private string _verificationUri = "";

    private TwitchState _state;
    private bool _isOn, _stopOnStreamEnd, _obsOn;
    private string _channel, _userCode = "", _statusText = "Ready";
    private TargetStatus _status;

    public TwitchViewModel(AppState state, Action changed, Action<Action> post)
    {
        _changed = changed;
        _post = post;
        _channel = state.TwitchChannel;
        _isOn = state.TwitchOn;
        _stopOnStreamEnd = state.TwitchStopOnStreamEnd;
        _session = TwitchTokenStore.Load();
        _state = _session is null ? TwitchState.Disconnected : TwitchState.Connected;

        ConnectCommand = new RelayCommand(() => _ = ConnectAsync(), () => State == TwitchState.Disconnected);
        CancelCommand = new RelayCommand(() => _connecting?.Cancel());
        OpenPageCommand = new RelayCommand(OpenPage);
        DisconnectCommand = new RelayCommand(Disconnect);
        TestCommand = new RelayCommand(() => Deliver(null, MainViewModel.TestText, null));
    }

    public ICommand ConnectCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenPageCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand TestCommand { get; }

    public TwitchState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
                return;
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ChannelHint));
            OnPropertyChanged(nameof(WatchesStream));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value))
                return;
            OnPropertyChanged(nameof(IsActive));
            _changed();
        }
    }

    // Sentences are posted only when an account is connected and the switch is on.
    public bool IsActive => IsOn && State == TwitchState.Connected;

    // The channel to write in, as in twitch.tv/<name>. Empty means the user's own channel.
    public string Channel
    {
        get => _channel;
        set
        {
            if (SetProperty(ref _channel, value.Trim().TrimStart('#', '@')))
                _changed();
        }
    }

    // "I'm streaming": stop listening when the user's own stream ends. It only makes sense with the OBS
    // overlay, so it can be switched on only while an OBS text template is on.
    public bool StopOnStreamEnd
    {
        get => _stopOnStreamEnd;
        set
        {
            if (!SetProperty(ref _stopOnStreamEnd, value))
                return;
            OnPropertyChanged(nameof(WatchesStream));
            OnPropertyChanged(nameof(StreamSwitch));
            _changed();
        }
    }

    // What the switch shows: off while OBS is off, even though the choice is kept for when it comes back on.
    public bool StreamSwitch
    {
        get => StopOnStreamEnd && ObsOn;
        set
        {
            if (ObsOn)
                StopOnStreamEnd = value;
        }
    }

    // Told by the main window whenever an OBS text template is switched on or off.
    public bool ObsOn
    {
        get => _obsOn;
        set
        {
            if (!SetProperty(ref _obsOn, value))
                return;
            OnPropertyChanged(nameof(StreamEndHint));
            OnPropertyChanged(nameof(WatchesStream));
            OnPropertyChanged(nameof(StreamSwitch));
        }
    }

    // True when the stream's end should stop listening: the option is on, OBS is on, and an account is connected.
    public bool WatchesStream => StopOnStreamEnd && ObsOn && State == TwitchState.Connected;

    public string StreamEndHint => ObsOn
        ? "When your Twitch stream ends, listening stops by itself."
        : "Turn on an OBS text template first.";

    // What the last stream check found, shown under the switch while listening ("" when not checking).
    public string StreamStatus
    {
        get => _streamStatus;
        set => SetProperty(ref _streamStatus, value);
    }
    private string _streamStatus = "";

    // Whether the signed-in user's own channel is live. Live is null when Twitch couldn't be asked, and
    // Problem then says why.
    public async Task<(bool? Live, string Problem)> IsLiveAsync()
    {
        try
        {
            const string clientId = TwitchApi.BuiltInClientId;
            var session = _session;
            if (session is null)
                return (null, "not connected to Twitch");
            if (session.ExpiresAt < DateTime.UtcNow.AddMinutes(1))
                session = await RenewAsync(clientId, session);
            try
            {
                return (await TwitchApi.IsLiveAsync(clientId, session, session.UserId), "");
            }
            catch (UnauthorizedAccessException)
            {
                session = await RenewAsync(clientId, session);
                return (await TwitchApi.IsLiveAsync(clientId, session, session.UserId), "");
            }
        }
        catch (Exception ex)
        {
            return (null, ex switch
            {
                TwitchException => ex.Message,
                UnauthorizedAccessException => "Twitch rejected the sign-in. Disconnect and connect again.",
                HttpRequestException or TaskCanceledException => "couldn't reach Twitch",
                _ => ex.Message
            });
        }
    }

    public string ChannelHint => _session is { } session ? $"{session.Login} (your own)" : "Channel name";

    public string Detail => State switch
    {
        TwitchState.Connected => $"Chat · signed in as {_session?.Login}",
        TwitchState.Connecting => "Approve WalkTalk on the Twitch page that opened.",
        _ => "Chat · not connected"
    };

    // The code Twitch shows on its approval page, so the user can check it is the same request.
    public string UserCode
    {
        get => _userCode;
        private set => SetProperty(ref _userCode, value);
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

    public void WriteTo(AppState state)
    {
        state.TwitchChannel = Channel;
        state.TwitchOn = IsOn;
        state.TwitchStopOnStreamEnd = StopOnStreamEnd;
    }

    private async Task ConnectAsync()
    {
        const string clientId = TwitchApi.BuiltInClientId;
        using var cancellation = new CancellationTokenSource();
        _connecting = cancellation;
        UserCode = "";
        State = TwitchState.Connecting;
        Report(TargetStatus.Ready, "Contacting Twitch…");
        try
        {
            var code = await TwitchApi.StartSignInAsync(clientId, cancellation.Token);
            _verificationUri = code.VerificationUri;
            UserCode = code.UserCode;
            Report(TargetStatus.Ready, "Waiting for your approval…");
            OpenPage();

            _session = await TwitchApi.WaitForApprovalAsync(clientId, code, cancellation.Token);
            TwitchTokenStore.Save(_session);
            _channelId = null;
            State = TwitchState.Connected;
            IsOn = true;
            Report(TargetStatus.Sent, $"Connected as {_session.Login}");
        }
        catch (OperationCanceledException)
        {
            State = TwitchState.Disconnected;
            Report(TargetStatus.Ready, "Ready");
        }
        catch (Exception ex) when (ex is TwitchException or HttpRequestException or IOException)
        {
            State = TwitchState.Disconnected;
            Report(TargetStatus.Failed, ex is TwitchException ? ex.Message : $"Couldn't reach Twitch: {ex.Message}");
        }
        finally
        {
            _connecting = null;
            UserCode = "";
        }
    }

    // Only ever opens a web address, never anything else a reply might contain.
    private void OpenPage()
    {
        if (!Uri.TryCreate(_verificationUri, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        try
        {
            OpenBrowser(uri.AbsoluteUri);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Report(TargetStatus.Failed, $"Couldn't open the browser. Go to {uri.GetLeftPart(UriPartial.Path)} and enter the code.");
        }
    }

    private void Disconnect()
    {
        var session = _session;
        _session = null;
        _channelId = null;
        TwitchTokenStore.Clear();
        State = TwitchState.Disconnected;
        Report(TargetStatus.Ready, "Disconnected");
        if (session is not null)
            _ = TwitchApi.SignOutAsync(TwitchApi.BuiltInClientId, session);
    }

    // Posts a sentence to chat. Voice commands have no meaning there (a message can't be taken back, and
    // each sentence is already sent as it is said), so they are simply ticked off.
    public void Deliver(VoiceCommand? command, string text, DeliveryViewModel? delivery)
    {
        if (command is not null)
        {
            if (delivery is not null)
                delivery.State = DeliveryState.Sent;
            return;
        }
        _sending = _sending.ContinueWith(_ => SendAsync(text, delivery), TaskScheduler.Default).Unwrap();
    }

    private async Task SendAsync(string text, DeliveryViewModel? delivery)
    {
        try
        {
            const string clientId = TwitchApi.BuiltInClientId;
            var session = _session ?? throw new TwitchException("Not connected to Twitch.");
            if (session.ExpiresAt < DateTime.UtcNow.AddMinutes(1))
                session = await RenewAsync(clientId, session);
            try
            {
                await TwitchApi.SendMessageAsync(clientId, session, await ChannelIdAsync(clientId, session), text);
            }
            catch (UnauthorizedAccessException)
            {
                // The access token ran out early (or was revoked): renew once and try again.
                session = await RenewAsync(clientId, session);
                await TwitchApi.SendMessageAsync(clientId, session, await ChannelIdAsync(clientId, session), text);
            }
            _post(() =>
            {
                Report(TargetStatus.Sent, $"Sent at {DateTime.Now:HH:mm:ss}");
                if (delivery is not null)
                    delivery.State = DeliveryState.Sent;
            });
        }
        catch (Exception ex) when (ex is TwitchException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            var message = ex switch
            {
                TwitchException => ex.Message,
                UnauthorizedAccessException => "Twitch rejected the sign-in. Disconnect and connect again.",
                _ => "Couldn't reach Twitch. Check your internet connection."
            };
            _post(() =>
            {
                if (ex is TwitchException { SignInLost: true })
                {
                    _session = null;
                    TwitchTokenStore.Clear();
                    State = TwitchState.Disconnected;
                }
                Report(TargetStatus.Failed, message);
                if (delivery is not null)
                {
                    delivery.State = DeliveryState.Failed;
                    delivery.Error = message;
                }
            });
        }
    }

    private async Task<TwitchSession> RenewAsync(string clientId, TwitchSession session)
    {
        var renewed = await TwitchApi.RefreshAsync(clientId, session);
        _session = renewed;
        TwitchTokenStore.Save(renewed); // the old refresh token no longer works
        return renewed;
    }

    private async Task<string> ChannelIdAsync(string clientId, TwitchSession session)
    {
        var channel = Channel;
        if (channel.Length == 0 || channel.Equals(session.Login, StringComparison.OrdinalIgnoreCase))
            return session.UserId;
        if (_channelId is { } known && known.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
            return known.Id;
        var id = await TwitchApi.FindChannelAsync(clientId, session, channel);
        _channelId = (channel, id);
        return id;
    }

    private void Report(TargetStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    public void Shutdown() => _connecting?.Cancel();
}
