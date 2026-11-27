using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperStt.Core.Obs;

// Styles: the weights it comes in, with an "i" for italics ("400", "700", "400i"), as Google lists them.
public sealed record GoogleFont(string Family, string Category, int Popularity, IReadOnlyList<string> Styles);

// Google Fonts for the OBS overlay: the list to search in, and installing a family for good. An installed
// family is kept in %LOCALAPPDATA%\WhisperStt\fonts and served to OBS by the overlay server, so the
// overlay keeps working offline. Nothing is sent to Google except the request for the list or the font.
public static partial class GoogleFonts
{
    // The list Google's own site uses: every family, with its category and popularity rank. No key needed.
    private const string ListUrl = "https://fonts.google.com/metadata/fonts";
    private const string CssUrl = "https://fonts.googleapis.com/css2";
    // Google serves WOFF2 files only to browsers that say they can read them.
    private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateClient();
    private static IReadOnlyList<GoogleFont>? _list;

    // Settable so tests can work in a folder of their own.
    public static string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperStt", "fonts");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserAgent);
        return client;
    }

    // Every Google Fonts family, most popular first. Downloaded once per session.
    public static async Task<IReadOnlyList<GoogleFont>> ListAsync(CancellationToken ct = default)
    {
        if (_list is not null)
            return _list;
        var text = await Http.GetStringAsync(ListUrl, ct).ConfigureAwait(false);
        var start = text.IndexOf('{'); // the reply starts with ")]}'" to stop it being run as a script
        using var json = JsonDocument.Parse(text[start..]);
        var fonts = new List<GoogleFont>();
        foreach (var family in json.RootElement.GetProperty("familyMetadataList").EnumerateArray())
        {
            var name = family.GetProperty("family").GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var category = family.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
            var popularity = family.TryGetProperty("popularity", out var p) && p.TryGetInt32(out var rank) ? rank : int.MaxValue;
            var styles = family.TryGetProperty("fonts", out var f) && f.ValueKind == JsonValueKind.Object
                ? f.EnumerateObject().Select(s => s.Name).ToList()
                : [];
            fonts.Add(new GoogleFont(name, category, popularity, styles));
        }
        _list = fonts.OrderBy(f => f.Popularity).ToList();
        return _list;
    }

    public static IReadOnlyList<GoogleFont> Search(IReadOnlyList<GoogleFont> all, string query, int max) =>
        all.Where(f => f.Family.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => !f.Family.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Popularity)
            .Take(max)
            .ToList();

    // The families installed so far.
    public static IReadOnlyList<string> Installed()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        return System.IO.Directory.GetDirectories(Directory)
            .Select(d => Path.Combine(d, "family.txt"))
            .Where(File.Exists)
            .Select(f => File.ReadAllText(f).Trim())
            .Where(f => f.Length > 0)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsInstalled(string family) => File.Exists(Path.Combine(Directory, Slug(family), "family.txt"));

    // Downloads a family (every weight it has, with italics) and keeps it for the overlay.
    public static async Task InstallAsync(string family, CancellationToken ct = default)
    {
        var css = await FetchCssAsync(family, ct).ConfigureAwait(false);
        var slug = Slug(family);
        var folder = Path.Combine(Directory, slug);
        var temp = folder + ".part";
        if (System.IO.Directory.Exists(temp))
            System.IO.Directory.Delete(temp, recursive: true);
        System.IO.Directory.CreateDirectory(temp);

        // Each @font-face points at one file on Google's servers; keep each file and point at the copy.
        var files = 0;
        var local = new StringBuilder();
        var last = 0;
        foreach (Match match in FontUrl().Matches(css))
        {
            var bytes = await Http.GetByteArrayAsync(match.Groups[1].Value, ct).ConfigureAwait(false);
            var name = $"{files++}.woff2";
            await File.WriteAllBytesAsync(Path.Combine(temp, name), bytes, ct).ConfigureAwait(false);
            local.Append(css, last, match.Index - last).Append($"url(/fonts/{slug}/{name})");
            last = match.Index + match.Length;
        }
        local.Append(css, last, css.Length - last);
        if (files == 0)
            throw new InvalidOperationException($"Google Fonts sent no font files for \"{family}\".");

        await File.WriteAllTextAsync(Path.Combine(temp, "font.css"), local.ToString(), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(temp, "family.txt"), family, ct).ConfigureAwait(false);
        if (System.IO.Directory.Exists(folder))
            System.IO.Directory.Delete(folder, recursive: true);
        System.IO.Directory.Move(temp, folder);
    }

    public static void Uninstall(string family)
    {
        var folder = Path.Combine(Directory, Slug(family));
        if (System.IO.Directory.Exists(folder))
            System.IO.Directory.Delete(folder, recursive: true);
    }

    // All installed families' @font-face rules, for the overlay page.
    public static string Stylesheet()
    {
        if (!System.IO.Directory.Exists(Directory))
            return "";
        var css = new StringBuilder();
        foreach (var file in System.IO.Directory.GetFiles(Directory, "font.css", SearchOption.AllDirectories))
            css.AppendLine(File.ReadAllText(file));
        return css.ToString();
    }

    // The file behind a "/fonts/<family>/<n>.woff2" address, or null for anything else.
    public static string? FilePath(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 2 || !SafeName().IsMatch(parts[0]) || !FontFile().IsMatch(parts[1]))
            return null;
        var path = Path.Combine(Directory, parts[0], parts[1]);
        return File.Exists(path) ? path : null;
    }

    private static async Task<string> FetchCssAsync(string family, CancellationToken ct)
    {
        var name = Uri.EscapeDataString(family.Trim()).Replace("%20", "+");
        // Every style the family has, when the list says which (so every font weight can be used);
        // otherwise ask for the most likely ones, then for less until Google agrees.
        List<string> variants = [];
        if (_list?.FirstOrDefault(f => f.Family.Equals(family.Trim(), StringComparison.OrdinalIgnoreCase)) is { Styles.Count: > 0 } known)
        {
            var tuples = known.Styles
                .Select(s => (Italic: s.EndsWith('i') ? 1 : 0, Weight: int.TryParse(s.TrimEnd('i'), out var w) ? w : 0))
                .Where(s => s.Weight is >= 100 and <= 900)
                .Distinct()
                .OrderBy(s => s.Italic).ThenBy(s => s.Weight)
                .Select(s => $"{s.Italic},{s.Weight}")
                .ToList();
            if (tuples.Count > 0)
                variants.Add(":ital,wght@" + string.Join(';', tuples));
        }
        variants.AddRange([":ital,wght@0,400;0,700;1,400;1,700", ":wght@400;700", ""]);
        foreach (var variant in variants)
        {
            using var response = await Http.GetAsync($"{CssUrl}?family={name}{variant}&display=swap", ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Google Fonts has no family called \"{family}\".");
    }

    private static string Slug(string family) =>
        new(family.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    [GeneratedRegex(@"url\((https://fonts\.gstatic\.com/[^)]+)\)")]
    private static partial Regex FontUrl();

    [GeneratedRegex(@"^[a-z0-9-]+$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^\d+\.woff2$")]
    private static partial Regex FontFile();
}
