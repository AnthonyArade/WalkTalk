using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace WhisperStt.Core;

// Total is null when the server doesn't say how big the file is.
public sealed record DownloadProgress(long Received, long? Total, double BytesPerSecond)
{
    public double? Fraction => Total > 0 ? (double)Received / Total.Value : null;

    public TimeSpan? Remaining =>
        Total is { } total && Received < total && BytesPerSecond > 1 ? TimeSpan.FromSeconds((total - Received) / BytesPerSecond) : null;

    // e.g. "26 of 334 MB (8%) · 2.7 MB/s · about 2 min left"
    public override string ToString()
    {
        var text = Total is { } total
            ? $"{Received >> 20} of {total >> 20} MB ({Fraction:P0})"
            : $"{Received >> 20} MB";
        if (BytesPerSecond > 1)
            text += $" · {BytesPerSecond / (1 << 20):0.0} MB/s";
        if (Remaining is { } left)
            text += left.TotalSeconds > 90 ? $" · about {Math.Ceiling(left.TotalMinutes)} min left" : $" · about {Math.Max(1, (int)left.TotalSeconds)} s left";
        return text;
    }
}

public static class Downloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    // Downloads url to path. Data goes to path + ".part" first; if that file is left over from an
    // interrupted download, the transfer continues where it stopped instead of starting again.
    public static async Task DownloadAsync(string url, string path, Action<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var partial = path + ".part";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The leftover file is already complete (the app closed right before finishing).
            File.Move(partial, path, overwrite: true);
            return;
        }
        response.EnsureSuccessStatusCode();

        var resumed = response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
            existing = 0; // the server ignored the resume request: start over
        var total = resumed ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 17];
            var received = existing;
            var clock = Stopwatch.StartNew();
            long sinceReport = 0;
            double speed = 0;
            var lastReport = TimeSpan.Zero;
            progress?.Invoke(new DownloadProgress(received, total, 0));

            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                sinceReport += read;

                var elapsed = clock.Elapsed - lastReport;
                if (elapsed >= TimeSpan.FromMilliseconds(500))
                {
                    // Smoothed speed, so the time left doesn't jump around.
                    var current = sinceReport / elapsed.TotalSeconds;
                    speed = speed == 0 ? current : speed * 0.7 + current * 0.3;
                    progress?.Invoke(new DownloadProgress(received, total, speed));
                    lastReport = clock.Elapsed;
                    sinceReport = 0;
                }
            }
            progress?.Invoke(new DownloadProgress(received, total, speed));
        }
        File.Move(partial, path, overwrite: true);
    }
}
