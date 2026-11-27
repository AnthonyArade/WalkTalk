using System.Globalization;

namespace WhisperStt.Core.Obs;

public enum TextCase { AsSaid, Uppercase, Lowercase, Capitalize }
public enum HorizontalAlign { Left, Center, Right }
public enum VerticalAlign { Top, Middle, Bottom }

// How the transcript looks in OBS. Colors are "#RRGGBB" or "#AARRGGBB" (alpha first, as in WPF).
public sealed class OverlayStyle
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 220;

    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 48;
    public bool Bold { get; set; } = true;
    public string Color { get; set; } = "#FFFFFF";
    public TextCase Case { get; set; } = TextCase.AsSaid;
    public HorizontalAlign HorizontalAlign { get; set; } = HorizontalAlign.Center;
    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Bottom;
    public double LineHeight { get; set; } = 1.2;

    public bool Shadow { get; set; } = true;
    public double ShadowX { get; set; } = 2;
    public double ShadowY { get; set; } = 2;
    public double ShadowBlur { get; set; } = 6;
    public string ShadowColor { get; set; } = "#CC000000";

    public double OutlineWidth { get; set; } = 3;
    public string OutlineColor { get; set; } = "#000000";

    // How many of the latest sentences stay on screen, and for how long (0 = until the next one).
    public int Lines { get; set; } = 2;
    public double HideAfterSeconds { get; set; } = 8;

    public OverlayStyle Clone() => (OverlayStyle)MemberwiseClone();

    // What the overlay page receives: ready-to-use CSS values.
    public object ToPage() => new
    {
        width = Math.Clamp(Width, 50, 7680),
        height = Math.Clamp(Height, 20, 4320),
        fontFamily = CssFamily(FontFamily),
        fontSize = Math.Clamp(FontSize, 4, 400),
        fontWeight = Bold ? 700 : 400,
        color = Css(Color, "#FFFFFF"),
        textTransform = Case switch
        {
            TextCase.Uppercase => "uppercase",
            TextCase.Lowercase => "lowercase",
            TextCase.Capitalize => "capitalize",
            _ => "none"
        },
        textAlign = HorizontalAlign.ToString().ToLowerInvariant(),
        verticalAlign = VerticalAlign.ToString().ToLowerInvariant(),
        lineHeight = Math.Clamp(LineHeight, 0.5, 4),
        textShadow = Shadow ? $"{N(ShadowX)}px {N(ShadowY)}px {N(Math.Max(0, ShadowBlur))}px {Css(ShadowColor, "#000000")}" : "none",
        outlineWidth = Math.Clamp(OutlineWidth, 0, 40),
        outlineColor = Css(OutlineColor, "#000000"),
        lines = Math.Clamp(Lines, 1, 10),
        hideAfter = Math.Max(0, HideAfterSeconds),
    };

    // "#AARRGGBB" (WPF order) to CSS rgba(); anything that isn't a color gives the fallback.
    public static string Css(string? color, string fallback)
    {
        if (!TryParse(color, out var a, out var r, out var g, out var b) && !TryParse(fallback, out a, out r, out g, out b))
            return "transparent";
        return a == 255 ? $"rgb({r},{g},{b})" : $"rgba({r},{g},{b},{N(a / 255.0)})";
    }

    public static bool TryParse(string? color, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255; r = g = b = 0;
        var hex = color?.Trim().TrimStart('#') ?? "";
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        if (hex.Length == 8)
            a = (byte)(value >> 24);
        r = (byte)(value >> 16);
        g = (byte)(value >> 8);
        b = (byte)value;
        return true;
    }

    // Quoted for CSS, with a generic family behind it in case the font is missing.
    private static string CssFamily(string family)
    {
        var name = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family.Trim().Replace("\"", "").Replace("\\", "");
        return $"\"{name}\", \"Segoe UI\", sans-serif";
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
