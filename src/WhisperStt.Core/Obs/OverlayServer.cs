using System.Net;
using System.Text;
using System.Text.Json;

namespace WhisperStt.Core.Obs;

// A small web server on this PC that OBS shows as a Browser Source: the page draws the latest sentences
// with the user's style and gets each new sentence the moment it is said (server-sent events).
// It listens on 127.0.0.1 only, so nothing outside this PC can reach it.
public sealed class OverlayServer : IDisposable
{
    public const int DefaultPort = 7480;

    private readonly HttpListener _listener = new();
    private readonly List<HttpListenerResponse> _clients = [];
    private readonly Lock _gate = new();
    private readonly Timer _heartbeat;
    private readonly List<(string Text, DateTime Said)> _recent = [];
    private OverlayStyle _current = new();
    private string _style = "{}";
    private bool _disposed;

    public OverlayServer(int port = DefaultPort)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start(); // throws HttpListenerException when the port is taken
        _ = Task.Run(AcceptAsync);
        // A comment line now and then: lets a dead connection (OBS closed) be noticed and dropped.
        _heartbeat = new Timer(_ => Broadcast(": keep-alive\n\n"), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";

    public void SetStyle(OverlayStyle style)
    {
        _current = style.Clone();
        _style = JsonSerializer.Serialize(style.ToPage());
        Broadcast($"event: style\ndata: {_style}\n\n");
    }

    // A new sentence for the screen.
    public void Show(string text)
    {
        lock (_gate)
        {
            _recent.Add((text, DateTime.UtcNow));
            if (_recent.Count > 10)
                _recent.RemoveAt(0);
        }
        Broadcast(LineEvent(text));
    }

    // Empties the screen ("Clean" voice command).
    public void Clear()
    {
        lock (_gate)
            _recent.Clear();
        Broadcast("event: clear\ndata: {}\n\n");
    }

    private static string LineEvent(string text) => $"event: line\ndata: {JsonSerializer.Serialize(new { text })}\n\n";

    // What is on screen right now, for a page that connects late (OBS reloads a source when its scene shows).
    private List<string> StillShown()
    {
        var keep = Math.Clamp(_current.Lines, 1, 10);
        var since = _current.HideAfterSeconds > 0 ? DateTime.UtcNow.AddSeconds(-_current.HideAfterSeconds) : DateTime.MinValue;
        lock (_gate)
            return _recent.Count > 0 && _recent[^1].Said >= since
                ? _recent.TakeLast(keep).Select(r => r.Text).ToList()
                : [];
    }

    // Tells open pages to load the fonts again (one was just installed).
    public void FontsChanged() => Broadcast("event: fonts\ndata: {}\n\n");

    private async Task AcceptAsync()
    {
        while (!_disposed)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/events")
            {
                response.ContentType = "text/event-stream";
                response.Headers["Cache-Control"] = "no-cache";
                response.SendChunked = true;
                Write(response, $"event: style\ndata: {_style}\n\n");
                foreach (var text in StillShown())
                    Write(response, LineEvent(text));
                lock (_gate)
                    _clients.Add(response);
                return; // stays open; new sentences are written to it as they come
            }

            if (path is "/" or "/index.html")
                Send(response, "text/html; charset=utf-8", Page.Value);
            else if (path == "/fonts.css")
                Send(response, "text/css; charset=utf-8", Encoding.UTF8.GetBytes(GoogleFonts.Stylesheet()));
            else if (path.StartsWith("/fonts/", StringComparison.Ordinal) && GoogleFonts.FilePath(path["/fonts/".Length..]) is { } file)
                Send(response, "font/woff2", File.ReadAllBytes(file));
            else
            {
                response.StatusCode = 404;
                response.Close();
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            try { response.Abort(); } catch (ObjectDisposedException) { }
        }
    }

    private static void Send(HttpListenerResponse response, string type, byte[] body)
    {
        response.ContentType = type;
        response.Headers["Cache-Control"] = "no-cache";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
        response.Close();
    }

    private static void Write(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.OutputStream.Write(bytes);
        response.OutputStream.Flush();
    }

    private void Broadcast(string text)
    {
        List<HttpListenerResponse> clients;
        lock (_gate)
            clients = [.. _clients];
        foreach (var client in clients)
        {
            try
            {
                Write(client, text);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                lock (_gate)
                    _clients.Remove(client);
                try { client.Abort(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private static readonly Lazy<byte[]> Page = new(() =>
    {
        using var stream = typeof(OverlayServer).Assembly.GetManifestResourceStream("WhisperStt.Core.Obs.overlay.html")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _heartbeat.Dispose();
        lock (_gate)
        {
            foreach (var client in _clients)
                try { client.Abort(); } catch (ObjectDisposedException) { }
            _clients.Clear();
        }
        _listener.Close();
    }
}
