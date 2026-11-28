using System.Globalization;
using System.Text;

namespace WhisperStt.Core.Obs;

// What importing a template gave: the new text templates, whether fonts were added, and what couldn't be brought over.
public sealed record TemplateImport(IReadOnlyList<OverlayStyle> Templates, bool FontsAdded, IReadOnlyList<string> Notes);

// Turns a template made in Curses (another speech-to-text captions app; a ".cursestmp" file) into WalkTalk text
// templates. The file is a Yjs document holding the layout, every element's settings and the files they use
// (fonts, pictures, sounds). Each text element becomes a text template; an image element becomes its avatar.
// The files are copied into WalkTalk's data, the fonts installed for the overlay.
public static class CursesTemplate
{
    public const string Extension = ".cursestmp";

    public static bool IsTemplate(string path) => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    public static TemplateImport Import(string path)
    {
        Dictionary<string, object?> doc;
        try
        {
            doc = YjsReader.Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("This file isn't a Curses template WalkTalk can read.", ex);
        }
        if (Map(doc, "template") is not { } template)
            throw new InvalidDataException("This file isn't a Curses template.");

        var files = Files(template, doc);
        var scene = Text(template, "activeScene") is { Length: > 0 } active ? active : "main";
        var elements = Map(template, "elements") ?? [];
        // The order the elements are listed in (back to front), then any the list forgot.
        var order = (List(template, "elementsIds") ?? []).OfType<string>().Where(elements.ContainsKey).ToList();
        order.AddRange(elements.Keys.Except(order));

        var texts = new List<(Dictionary<string, object?> Element, Dictionary<string, object?> Data, Rect Rect)>();
        var images = new List<(Dictionary<string, object?> Element, Dictionary<string, object?> Data, Rect Rect)>();
        foreach (var id in order)
        {
            if (Map(elements, id) is not { } element || SceneOf(element, scene) is not { } placed)
                continue;
            var entry = (element, Map(placed, "data") ?? [], RectOf(Map(placed, "rect")));
            switch (Text(element, "type"))
            {
                case "text": texts.Add(entry); break;
                case "image": images.Add(entry); break;
            }
        }
        if (texts.Count == 0)
            throw new InvalidDataException("This template has no text element to import.");

        var baseName = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        if (baseName.Length == 0)
            baseName = "Imported template";
        var notes = new List<string>();
        var fontsAdded = false;
        var installedFonts = new Dictionary<string, string>(); // Curses file id -> family name
        var result = new List<OverlayStyle>();

        foreach (var (element, data, rect) in texts)
        {
            var style = new OverlayStyle
            {
                Name = texts.Count == 1 ? baseName : $"{baseName} · {Text(element, "name") ?? "text"}",
                Width = (int)Math.Round(Math.Clamp(rect.W, 50, 7680)),
                Height = (int)Math.Round(Math.Clamp(rect.H, 20, 4320)),
            };
            ApplyText(style, data, files, installedFonts, notes, ref fontsAdded);
            ApplyBox(style, data, files, notes);
            ApplyBehavior(style, data);
            ApplySounds(style, data, files, notes);
            // The first image element is the first text template's avatar, placed where it was relative to the text.
            if (result.Count == 0 && images.Count > 0)
                ApplyAvatar(style, images[0].Data, images[0].Rect, rect, files, notes);
            result.Add(style);
        }
        if (images.Count > 1)
            notes.Add($"{images.Count - 1} more image element(s) weren't imported: a text template has one avatar.");
        return new TemplateImport(result, fontsAdded, notes.Distinct().ToList());
    }

    // -- the text element's settings

    private static void ApplyText(OverlayStyle style, Dictionary<string, object?> data, Dictionary<string, EmbeddedFile> files,
        Dictionary<string, string> installed, List<string> notes, ref bool fontsAdded)
    {
        var family = Text(data, "textFontFamily") ?? "";
        // The font is one of the template's files when a file's family is that name; otherwise an installed font.
        if (files.Values.FirstOrDefault(f => f.Type.StartsWith("font/", StringComparison.Ordinal) &&
                (f.Family == family || f.Name == family)) is { } font)
        {
            if (!installed.TryGetValue(font.Id, out var name))
            {
                name = InstallFont(font, notes);
                if (name is not null)
                {
                    installed[font.Id] = name;
                    fontsAdded = true;
                }
            }
            if (name is not null)
                style.FontFamily = name;
        }
        else if (family.Length > 0 && !family.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            style.FontFamily = family;
            if (!GoogleFonts.IsInstalled(family))
                notes.Add($"The font \"{family}\" isn't in the template: if it doesn't show, install it in the Text section.");
        }

        if (Number(data, "textFontSize") is { } size) style.FontSize = Math.Clamp(size, 4, 400);
        if (Number(data, "textFontWeight") is { } weight) style.FontWeight = Math.Clamp((int)Math.Round(weight / 100) * 100, 100, 900);
        if (Color(Text(data, "textColor")) is { } color) style.Color = color;
        if (Number(data, "textLineHeight") is { } lineHeight) style.LineHeight = Math.Clamp(lineHeight, 0.5, 4);
        style.Case = Text(data, "textCase") switch
        {
            "uppercase" => TextCase.Uppercase,
            "lowercase" => TextCase.Lowercase,
            "capitalize" => TextCase.Capitalize,
            _ => TextCase.AsSaid
        };

        // Curses places the text inside the box, and the box (when it shrinks to the text) inside the element.
        // WalkTalk has one alignment for both: the box's when it shrinks that way, the text's otherwise.
        style.AutoWidth = Bool(data, "boxAutoWidth") ?? false;
        style.AutoHeight = Bool(data, "boxAutoHeight") ?? false;
        style.HorizontalAlign = Text(data, style.AutoWidth ? "boxAlignH" : "textAlignH") switch
        {
            "center" => HorizontalAlign.Center,
            "end" or "right" => HorizontalAlign.Right,
            _ => HorizontalAlign.Left
        };
        style.VerticalAlign = Text(data, style.AutoHeight ? "boxAlignV" : "textAlignV") switch
        {
            "center" => VerticalAlign.Middle,
            "end" or "bottom" => VerticalAlign.Bottom,
            _ => VerticalAlign.Top
        };

        var shadowX = Number(data, "textShadowX") ?? 0;
        var shadowY = Number(data, "textShadowY") ?? 0;
        var shadowBlur = Number(data, "textShadowZ") ?? 0;
        style.Shadow = shadowX != 0 || shadowY != 0 || shadowBlur != 0;
        style.ShadowX = shadowX;
        style.ShadowY = shadowY;
        style.ShadowBlur = Math.Max(0, shadowBlur);
        if (Color(Text(data, "textShadowColor")) is { } shadowColor) style.ShadowColor = shadowColor;

        var stroke = Number(data, "textStroke") ?? 0;
        style.Outline = stroke > 0;
        style.OutlineWidth = stroke > 0 ? Math.Clamp(stroke, 1, 40) : style.OutlineWidth;
        if (Color(Text(data, "textStrokeColor")) is { } strokeColor) style.OutlineColor = strokeColor;
    }

    private static void ApplyBox(OverlayStyle style, Dictionary<string, object?> data, Dictionary<string, EmbeddedFile> files, List<string> notes)
    {
        var padding = Number(data, "boxPadding") ?? 0;
        style.PaddingX = style.PaddingY = Math.Clamp(padding, 0, 400);

        var type = Text(data, "boxBackgroundType") ?? "";
        var picture = Text(data, "boxImageFileId") is { Length: > 0 } pictureId ? files.GetValueOrDefault(pictureId) : null;
        if (type is "slice" or "image" && picture is not null && SaveImage(picture, notes, ext => OverlayImages.SaveImage(style.Id, ext, picture.Bytes)) is { } name)
        {
            style.Background = BackgroundType.Image;
            style.BackgroundImage = name;
            if (type == "slice")
            {
                style.ImageFit = ImageFit.Slice;
                // Curses gives each side its own size; WalkTalk uses one for all four.
                var sides = new[] { "boxSliceTop", "boxSliceRight", "boxSliceBottom", "boxSliceLeft" }
                    .Select(k => Number(data, k) ?? 0).Where(v => v > 0).ToList();
                style.SliceSize = sides.Count > 0 ? Math.Round(sides.Average()) : 16;
                style.SliceScale = Math.Clamp(Number(data, "boxSliceTileSize") is { } scale and > 0 ? scale : 1, 0.25, 16);
                if (sides.Distinct().Count() > 1)
                    notes.Add("The frame picture's sides had different sizes; WalkTalk uses one size for all four.");
            }
            else
                style.ImageFit = ImageFit.Cover;
        }
        else if (type is "color" or "solid" && Color(Text(data, "boxColor")) is { } color)
        {
            style.Background = BackgroundType.Color;
            style.BackgroundColor = color;
        }
        else
            style.Background = BackgroundType.None;

        var borderWidth = Number(data, "boxBorderWidth") ?? 0;
        var radius = Number(data, "boxBorderRadius") ?? 0;
        // With a frame picture the picture is the border. Otherwise a border, or rounded corners alone (size 0).
        style.Border = style.ImageFit != ImageFit.Slice && (borderWidth > 0 || (radius > 0 && style.Background != BackgroundType.None));
        style.BorderWidth = Math.Clamp(borderWidth, 0, 100);
        style.BorderRadius = Math.Clamp(radius, 0, 1000);
        if (Color(Text(data, "boxBorderColor")) is { } borderColor) style.BorderColor = borderColor;
    }

    private static void ApplyBehavior(OverlayStyle style, Dictionary<string, object?> data)
    {
        style.Animate = Bool(data, "animateEnable") ?? false;
        style.RevealUnit = RevealUnit.Character;
        style.RevealEffect = RevealEffect.Appear; // Curses types the letters out, no fading
        if (Number(data, "animateDelayChar") is { } character) style.CharacterInterval = Math.Clamp(character, 0, 2000);
        if (Number(data, "animateDelayWord") is { } word) style.WordPause = Math.Clamp(word, 0, 5000);
        if (Number(data, "animateDelaySentence") is { } sentence) style.SentenceInterval = Math.Clamp(sentence, 0, 20000);
        if (Number(data, "behaviorClearTimer") is { } clear) style.HideAfterSeconds = Math.Max(0, Math.Round(clear / 1000));
        if (Number(data, "behaviorClearDelay") is { } fade) style.ClearDuration = Math.Clamp(fade, 0, 5000);
        style.ClearAnimation = ClearAnimation.Fade;
        style.ReplacePrevious = Bool(data, "behaviorLastSentence") ?? false;
        if (style.ReplacePrevious)
            style.Lines = 1;
    }

    private static void ApplySounds(OverlayStyle style, Dictionary<string, object?> data, Dictionary<string, EmbeddedFile> files, List<string> notes)
    {
        if (Bool(data, "soundEnable") == false)
            return;
        var volume = Math.Clamp(Number(data, "soundVolume") ?? 1, 0, 1);
        foreach (var (key, slot, sound) in new[]
                 {
                     ("soundFile", "typing", style.TypingSound), ("soundFileNewSentence", "sentence", style.SentenceSound),
                     ("soundFileOnShow", "show", style.ShowSound), ("soundFileOnHide", "hide", style.HideSound)
                 })
        {
            if (Text(data, key) is not { Length: > 0 } id || files.GetValueOrDefault(id) is not { } file)
                continue;
            var extension = SoundExtension(file);
            if (extension is null)
            {
                notes.Add($"The sound \"{file.Name}\" is in a format WalkTalk can't play.");
                continue;
            }
            sound.File = OverlayImages.SaveSound(style.Id, slot, extension, file.Bytes);
            sound.Kind = "file";
            sound.Volume = volume;
        }
        if ((Number(data, "soundDetuneMin") ?? 0) != 0 || (Number(data, "soundDetuneMax") ?? 0) != 0 ||
            (Number(data, "soundPlaybackMin") ?? 1) != 1 || (Number(data, "soundPlaybackMax") ?? 1) != 1)
            notes.Add("The typing sound's random pitch isn't used: WalkTalk plays it as it is.");
    }

    // An image element: its picture (and a second one for while talking, when it has one) at its place.
    private static void ApplyAvatar(OverlayStyle style, Dictionary<string, object?> data, Rect image, Rect text,
        Dictionary<string, EmbeddedFile> files, List<string> notes)
    {
        // The picture settings are whichever keys name one of the template's files; one with "active",
        // "talk" or "speak" in its name is the picture for while talking.
        var pictures = data.Where(p => p.Value is string id && files.TryGetValue(id, out var f) && f.Type.StartsWith("image/", StringComparison.Ordinal))
            .Select(p => (Key: p.Key.ToLowerInvariant(), File: files[(string)p.Value!]))
            .ToList();
        static bool IsActive(string key) => key.Contains("active") || key.Contains("talk") || key.Contains("speak");
        var basePicture = pictures.FirstOrDefault(p => !IsActive(p.Key)).File;
        var activePicture = pictures.FirstOrDefault(p => IsActive(p.Key)).File;
        if (basePicture is null && activePicture is null)
            return;

        style.Avatar = true;
        style.AvatarX = (int)Math.Round(image.X - text.X);
        style.AvatarY = (int)Math.Round(image.Y - text.Y);
        style.AvatarWidth = (int)Math.Round(Math.Clamp(image.W, 1, 7680));
        style.AvatarHeight = (int)Math.Round(Math.Clamp(image.H, 1, 4320));
        if (basePicture is not null && SaveImage(basePicture, notes, ext => OverlayImages.SaveAvatar(style.Id, "base", ext, basePicture.Bytes)) is { } b)
            style.AvatarImage = b;
        if (activePicture is not null && SaveImage(activePicture, notes, ext => OverlayImages.SaveAvatar(style.Id, "active", ext, activePicture.Bytes)) is { } a)
            style.AvatarActiveImage = a;
        foreach (var (key, value) in data)
        {
            if (Number(data, key) is not { } n)
                continue;
            var k = key.ToLowerInvariant();
            var opacity = n > 1 ? n / 100 : n; // as a fraction or a percentage
            if (k.Contains("opacity") && IsActive(k)) style.AvatarActiveOpacity = Math.Clamp(opacity, 0, 1);
            else if (k.Contains("opacity")) style.AvatarOpacity = Math.Clamp(opacity, 0, 1);
            else if (k.Contains("transition")) style.AvatarTransition = Math.Clamp(n, 0, 5000);
        }
        if (style.AvatarX < 0 || style.AvatarY < 0 || style.AvatarX + style.AvatarWidth > style.Width || style.AvatarY + style.AvatarHeight > style.Height)
            notes.Add("The avatar sits partly outside the text area, so part of it is cut off: move it in the Avatar section, or make the size bigger.");
    }

    // -- files

    private sealed record EmbeddedFile(string Id, string Type, string Name, string Family, byte[] Bytes);

    // The template's files: their details in "filesMeta", their bytes in "files", in the same order.
    private static Dictionary<string, EmbeddedFile> Files(Dictionary<string, object?> template, Dictionary<string, object?> doc)
    {
        var metas = (List(template, "filesMeta") ?? []).OfType<Dictionary<string, object?>>().ToList();
        var blobs = (List(doc, "files") ?? []).OfType<byte[]>().ToList();
        var result = new Dictionary<string, EmbeddedFile>();
        for (var i = 0; i < metas.Count; i++)
        {
            var meta = metas[i];
            if (Text(meta, "id") is not { Length: > 0 } id)
                continue;
            // Normally the same position; when the sizes say otherwise, the file whose size matches.
            var size = Number(meta, "size");
            var bytes = i < blobs.Count && (size is null || blobs[i].Length == (int)size) ? blobs[i]
                : blobs.FirstOrDefault(b => size is not null && b.Length == (int)size);
            if (bytes is null)
                continue;
            result[id] = new EmbeddedFile(id, Text(meta, "type") ?? "", Text(meta, "name") ?? id,
                Text(Map(meta, "meta") ?? [], "family") ?? "", bytes);
        }
        return result;
    }

    private static string? SaveImage(EmbeddedFile file, List<string> notes, Func<string, string> save)
    {
        var extension = file.Type switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => null
        };
        if (extension is null)
        {
            notes.Add($"The picture \"{file.Name}\" is in a format WalkTalk can't use ({file.Type}).");
            return null;
        }
        return save(extension);
    }

    private static string? SoundExtension(EmbeddedFile file) => file.Type switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/mp4" or "audio/x-m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/flac" => ".flac",
        "audio/webm" => ".webm",
        _ => null
    };

    // Installs a font from the template; returns its family name (from inside the file when it says), or null.
    private static string? InstallFont(EmbeddedFile font, List<string> notes)
    {
        var extension = font.Type switch
        {
            "font/ttf" or "font/sfnt" or "application/x-font-ttf" => ".ttf",
            "font/otf" => ".otf",
            "font/woff" => ".woff",
            "font/woff2" => ".woff2",
            _ => null
        };
        if (extension is null)
        {
            notes.Add($"The font \"{font.Name}\" is in a format WalkTalk can't use.");
            return null;
        }
        var family = FamilyName(font.Bytes) ?? (font.Family.Length > 0 ? font.Family : font.Name);
        var temp = Path.Combine(Path.GetTempPath(), $"walktalk-font-{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(temp, font.Bytes);
            GoogleFonts.InstallFile(family, temp);
            return family.Trim().Replace("\"", "").Replace("\\", "");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // The family name inside a TrueType or OpenType file (its "name" table), or null for other files.
    public static string? FamilyName(byte[] font)
    {
        try
        {
            if (font.Length < 12)
                return null;
            int U16(int at) => (font[at] << 8) | font[at + 1];
            int U32(int at) => (font[at] << 24) | (font[at + 1] << 16) | (font[at + 2] << 8) | font[at + 3];
            var tables = U16(4);
            for (var t = 0; t < tables; t++)
            {
                var record = 12 + t * 16;
                if (Encoding.ASCII.GetString(font, record, 4) != "name")
                    continue;
                var table = U32(record + 8);
                var count = U16(table + 2);
                var strings = table + U16(table + 4);
                string? best = null;
                var bestScore = -1;
                for (var i = 0; i < count; i++)
                {
                    var r = table + 6 + i * 12;
                    int platform = U16(r), language = U16(r + 4), nameId = U16(r + 6), length = U16(r + 8), offset = U16(r + 10);
                    if (nameId is not (1 or 16) || platform is not (1 or 3))
                        continue;
                    // The typographic family (16) over the old one (1); Windows English over the rest.
                    var score = (nameId == 16 ? 4 : 0) + (platform == 3 ? 2 : 0) + (language is 0x409 or 0 ? 1 : 0);
                    if (score <= bestScore)
                        continue;
                    var text = platform == 3
                        ? Encoding.BigEndianUnicode.GetString(font, strings + offset, length)
                        : Encoding.Latin1.GetString(font, strings + offset, length);
                    if (text.Trim().Length == 0)
                        continue;
                    best = text.Trim();
                    bestScore = score;
                }
                return best;
            }
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    // -- reading the values (Curses keeps many numbers as text)

    private readonly record struct Rect(double X, double Y, double W, double H);

    private static Rect RectOf(Dictionary<string, object?>? rect) => rect is null ? new Rect(0, 0, 500, 200)
        : new Rect(Number(rect, "x") ?? 0, Number(rect, "y") ?? 0, Number(rect, "w") ?? 500, Number(rect, "h") ?? 200);

    private static Dictionary<string, object?>? SceneOf(Dictionary<string, object?> element, string scene) =>
        Map(element, "scenes") is { } scenes
            ? Map(scenes, scene) ?? scenes.Values.OfType<Dictionary<string, object?>>().FirstOrDefault()
            : null;

    private static Dictionary<string, object?>? Map(Dictionary<string, object?> map, string key) =>
        map.GetValueOrDefault(key) as Dictionary<string, object?>;

    private static List<object?>? List(Dictionary<string, object?> map, string key) => map.GetValueOrDefault(key) as List<object?>;

    private static string? Text(Dictionary<string, object?> map, string key) => map.GetValueOrDefault(key) as string;

    private static double? Number(Dictionary<string, object?> map, string key) => map.GetValueOrDefault(key) switch
    {
        double d when double.IsFinite(d) => d,
        long l => l,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) => v,
        _ => null
    };

    private static bool? Bool(Dictionary<string, object?> map, string key) => map.GetValueOrDefault(key) switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var v) => v,
        _ => null
    };

    // A CSS color ("rgba(10,39,69,1)", "rgb(...)", "#RRGGBB" or "#RRGGBBAA") as WalkTalk writes it (#AARRGGBB).
    public static string? Color(string? css)
    {
        var text = css?.Trim() ?? "";
        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length == 3)
                hex = string.Concat(hex.Select(c => $"{c}{c}"));
            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                return hex.Length == 6
                    ? OverlayStyle.Hex(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value)
                    : OverlayStyle.Hex((byte)value, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8));
            return null;
        }
        var open = text.IndexOf('(');
        if (open < 0 || !text.EndsWith(')'))
            return null;
        var parts = text[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return null;
        var channels = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
                return null;
            channels[i] = (byte)Math.Clamp(Math.Round(parts[i].EndsWith('%') ? c * 2.55 : c), 0, 255);
        }
        var alpha = 1.0;
        if (parts.Length > 3 && !double.TryParse(parts[3].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out alpha))
            return null;
        if (parts.Length > 3 && parts[3].EndsWith('%'))
            alpha /= 100;
        return OverlayStyle.Hex((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255), channels[0], channels[1], channels[2]);
    }
}
