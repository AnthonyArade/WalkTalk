using System.Net;
using System.Text;
using System.Text.Json;

namespace WhisperStt.Core.Obs;

// A small web server on this PC that OBS shows as Browser Sources, one page per canvas
// (http://127.0.0.1:<port>/c/<canvas id>). Each page draws the latest sentences with its canvas's style and
// gets each new sentence the moment it is said (server-sent events). It listens on 127.0.0.1 only, so
// nothing outside this PC can reach it.
public sealed class OverlayServer : IDisposable
{
    public const int DefaultPort = 7480;

    private readonly HttpListener _listener = new();
    private readonly List<(HttpListenerResponse Response, string Canvas)> _clients = [];
    private readonly Dictionary<string, OverlayStyle> _canvases = [];
    private readonly Dictionary<string, long> _imageVersions = [];
    private readonly List<(string Text, DateTime Said)> _recent = [];
    private readonly Lock _gate = new();
    private readonly Timer _heartbeat;
    private bool _disposed;

    public OverlayServer(int port = DefaultPort)
    {
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start(); // throws HttpListenerException when the port is taken
        _ = Task.Run(AcceptAsync);
        // A comment line now and then: lets a dead connection (OBS closed) be noticed and dropped.
        _heartbeat = new Timer(_ => Broadcast(null, ": keep-alive\n\n"), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public int Port { get; }

    public string UrlOf(string canvasId) => $"http://127.0.0.1:{Port}/c/{canvasId}";

    // Adds or updates a canvas; its open pages restyle at once.
    public void SetCanvas(OverlayStyle canvas, bool imageChanged = false)
    {
        string style;
        lock (_gate)
        {
            _canvases[canvas.Id] = canvas.Clone();
            if (imageChanged || !_imageVersions.ContainsKey(canvas.Id))
                _imageVersions[canvas.Id] = DateTime.UtcNow.Ticks;
            style = StyleEvent(canvas.Id);
        }
        Broadcast(canvas.Id, style);
    }

    public void RemoveCanvas(string canvasId)
    {
        lock (_gate)
            _canvases.Remove(canvasId);
        Broadcast(canvasId, "event: clear\ndata: {}\n\n");
    }

    // A new sentence for every canvas that is on.
    public void Show(string text)
    {
        lock (_gate)
        {
            _recent.Add((text, DateTime.UtcNow));
            if (_recent.Count > 10)
                _recent.RemoveAt(0);
        }
        var line = LineEvent(text);
        foreach (var id in CanvasesOn())
            Broadcast(id, line);
    }

    // Empties every canvas ("Clean" voice command).
    public void Clear()
    {
        lock (_gate)
            _recent.Clear();
        Broadcast(null, "event: clear\ndata: {}\n\n");
    }

    // Tells open pages to load the fonts again (one was just installed).
    public void FontsChanged() => Broadcast(null, "event: fonts\ndata: {}\n\n");

    private List<string> CanvasesOn()
    {
        lock (_gate)
            return _canvases.Values.Where(c => c.IsOn).Select(c => c.Id).ToList();
    }

    private string StyleEvent(string canvasId) =>
        $"event: style\ndata: {JsonSerializer.Serialize(_canvases[canvasId].ToPage(_imageVersions.GetValueOrDefault(canvasId)))}\n\n";

    private static string LineEvent(string text) => $"event: line\ndata: {JsonSerializer.Serialize(new { text })}\n\n";

    // What is on screen right now, for a page that connects late (OBS reloads a source when its scene shows).
    private List<string> StillShown(OverlayStyle canvas)
    {
        if (!canvas.IsOn)
            return [];
        var keep = Math.Clamp(canvas.Lines, 1, 10);
        var since = canvas.HideAfterSeconds > 0 ? DateTime.UtcNow.AddSeconds(-canvas.HideAfterSeconds) : DateTime.MinValue;
        return _recent.Count > 0 && _recent[^1].Said >= since ? _recent.TakeLast(keep).Select(r => r.Text).ToList() : [];
    }

    private async Task AcceptAsync()
    {
        while (!_disposed)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (_disposed || ex is HttpListenerException or ObjectDisposedException)
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
                var id = context.Request.QueryString["c"] ?? "";
                string style;
                List<string> lines;
                lock (_gate)
                {
                    if (!_canvases.TryGetValue(id, out var canvas))
                    {
                        response.StatusCode = 404;
                        response.Close();
                        return;
                    }
                    style = StyleEvent(id);
                    lines = StillShown(canvas);
                }
                response.ContentType = "text/event-stream";
                response.Headers["Cache-Control"] = "no-cache";
                response.SendChunked = true;
                Write(response, style + string.Concat(lines.Select(LineEvent)));
                lock (_gate)
                    _clients.Add((response, id));
                return; // stays open; new sentences are written to it as they come
            }

            if (path.StartsWith("/c/", StringComparison.Ordinal))
                Send(response, "text/html; charset=utf-8", Page.Value);
            else if (path == "/fonts.css")
                Send(response, "text/css; charset=utf-8", Encoding.UTF8.GetBytes(GoogleFonts.Stylesheet()));
            else if (path.StartsWith("/fonts/", StringComparison.Ordinal) && GoogleFonts.FilePath(path["/fonts/".Length..]) is { } font)
                Send(response, GoogleFonts.ContentType(font), File.ReadAllBytes(font));
            else if (path.StartsWith("/bg/", StringComparison.Ordinal) && ImageOf(path["/bg/".Length..]) is { } image)
                Send(response, OverlayImages.ContentType(image), File.ReadAllBytes(image));
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

    private string? ImageOf(string canvasId)
    {
        lock (_gate)
            return _canvases.TryGetValue(canvasId, out var canvas) ? OverlayImages.PathOf(canvas.BackgroundImage) : null;
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

    // Sends to the pages of one canvas, or of every canvas when canvasId is null.
    private void Broadcast(string? canvasId, string text)
    {
        List<(HttpListenerResponse Response, string Canvas)> clients;
        lock (_gate)
            clients = _clients.Where(c => canvasId is null || c.Canvas == canvasId).ToList();
        foreach (var client in clients)
        {
            try
            {
                Write(client.Response, text);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                lock (_gate)
                    _clients.Remove(client);
                try { client.Response.Abort(); } catch (ObjectDisposedException) { }
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
                try { client.Response.Abort(); } catch (ObjectDisposedException) { }
            _clients.Clear();
        }
        _listener.Close();
    }
}
