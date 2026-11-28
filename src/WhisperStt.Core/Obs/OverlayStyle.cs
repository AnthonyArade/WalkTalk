using System.Globalization;

namespace WhisperStt.Core.Obs;

public enum TextCase { AsSaid, Uppercase, Lowercase, Capitalize }
public enum HorizontalAlign { Left, Center, Right }
public enum VerticalAlign { Top, Middle, Bottom }
public enum BackgroundType { None, Color, Gradient, Image }
// Slice: a 9-slice frame. The picture's corners keep their size and its edges stretch along the box (a
// dialogue box drawn as a small picture, as in pixel-art games). New values go at the end: settings store numbers.
public enum ImageFit { Cover, Contain, Stretch, Tile, Slice }
public enum RevealUnit { Character, Word }
public enum RevealEffect { Appear, Fade, Pop, Rise }
public enum ClearAnimation { None, Fade, SlideUp, SlideDown, Shrink }

// A sound the overlay plays: one of the built-in sounds (made by the page itself, nothing to download), a file
// the user chose ("file", copied into WalkTalk's data), or nothing ("").
public sealed class OverlaySound
{
    public string Kind { get; set; } = "";
    public string File { get; set; } = "";
    public double Volume { get; set; } = 0.6;

    public OverlaySound Clone() => (OverlaySound)MemberwiseClone();
}

// One OBS canvas: a Browser Source page with its own size and look. Colors are "#RRGGBB" or "#AARRGGBB"
// (alpha first, as in WPF).
//
// Layout: the canvas is the Browser Source's area. Inside it sits the box (background, border, padding),
// which holds the text. The box fills the canvas, or with auto width / auto height shrinks to the text and
// is placed by the text alignment.
public sealed class OverlayStyle
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "Text template";
    public bool IsOn { get; set; } = true;

    // -- canvas
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 220;
    // How many of the latest sentences stay on screen, and for how long (0 = until the next one).
    public int Lines { get; set; } = 2;
    public double HideAfterSeconds { get; set; } = 8;

    // -- box
    public bool AutoWidth { get; set; }
    public bool AutoHeight { get; set; }
    public double PaddingX { get; set; } = 16;
    public double PaddingY { get; set; } = 8;

    // -- text
    public string FontFamily { get; set; } = "Segoe UI";
    public int FontWeight { get; set; } = 700;
    // Older settings had only bold or not; read once, then FontWeight is used.
    public bool? Bold { get; set; }
    public double FontSize { get; set; } = 48;
    public string Color { get; set; } = "#FFFFFF";
    public TextCase Case { get; set; } = TextCase.AsSaid;
    public HorizontalAlign HorizontalAlign { get; set; } = HorizontalAlign.Center;
    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Bottom;
    public double LineHeight { get; set; } = 1.2;

    // -- shadow
    public bool Shadow { get; set; } = true;
    public double ShadowX { get; set; } = 2;
    public double ShadowY { get; set; } = 2;
    public double ShadowBlur { get; set; } = 6;
    public string ShadowColor { get; set; } = "#CC000000";

    // -- outline
    public bool Outline { get; set; } = true;
    public double OutlineWidth { get; set; } = 3;
    public string OutlineColor { get; set; } = "#000000";

    // -- background
    public BackgroundType Background { get; set; } = BackgroundType.None;
    public string BackgroundColor { get; set; } = "#99000000";
    public string BackgroundColor2 { get; set; } = "#00000000";
    public double GradientAngle { get; set; } = 180;
    // A copy of the chosen picture, kept with WalkTalk's data (see OverlayImages).
    public string BackgroundImage { get; set; } = "";
    public ImageFit ImageFit { get; set; } = ImageFit.Cover;
    // With the 9-slice fit: how far the corners reach into the picture (its pixels), and how much bigger it is drawn.
    public double SliceSize { get; set; } = 16;
    public double SliceScale { get; set; } = 1;

    // -- border
    public bool Border { get; set; }
    public double BorderWidth { get; set; } = 2;
    public string BorderColor { get; set; } = "#FFFFFF";
    public double BorderRadius { get; set; } = 12;

    // -- animation: each sentence is revealed a character or a word at a time
    public bool Animate { get; set; }
    public RevealUnit RevealUnit { get; set; } = RevealUnit.Character;
    public RevealEffect RevealEffect { get; set; } = RevealEffect.Fade;
    public double CharacterInterval { get; set; } = 35;    // ms between two characters (or words)
    public double WordPause { get; set; } = 60;            // extra ms after each word
    public double SentenceInterval { get; set; } = 400;    // ms before the next waiting sentence starts
    // A new sentence takes the place of the one on screen instead of being added under it.
    public bool ReplacePrevious { get; set; }
    public ClearAnimation ClearAnimation { get; set; } = ClearAnimation.Fade;
    public double ClearDuration { get; set; } = 400;       // ms the clearing takes

    // -- sounds
    public OverlaySound TypingSound { get; set; } = new();
    public OverlaySound SentenceSound { get; set; } = new();
    public OverlaySound ShowSound { get; set; } = new();
    public OverlaySound HideSound { get; set; } = new();

    // -- avatar: a picture placed anywhere on the canvas (a portrait beside the text), which the text keeps clear of.
    // While a sentence is being written the base and active pictures take turns, each shown AvatarTransition ms. Like
    // the box, the avatar clears AvatarClearDelaySeconds after the last sentence (0 = it stays), fading over
    // AvatarClearDuration ms. Both pictures are copies kept with WalkTalk's data (see OverlayImages).
    public bool Avatar { get; set; }
    public int AvatarX { get; set; }
    public int AvatarY { get; set; }
    public int AvatarWidth { get; set; } = 160;
    public int AvatarHeight { get; set; } = 160;
    public string AvatarImage { get; set; } = "";
    public double AvatarOpacity { get; set; } = 1;
    public string AvatarActiveImage { get; set; } = "";
    public double AvatarActiveOpacity { get; set; } = 1;
    public double AvatarClearDelaySeconds { get; set; }
    public double AvatarClearDuration { get; set; } = 400;
    public double AvatarTransition { get; set; } = 150;

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    public OverlayStyle Clone()
    {
        var copy = (OverlayStyle)MemberwiseClone();
        copy.TypingSound = (TypingSound ?? new()).Clone();
        copy.SentenceSound = (SentenceSound ?? new()).Clone();
        copy.ShowSound = (ShowSound ?? new()).Clone();
        copy.HideSound = (HideSound ?? new()).Clone();
        return copy;
    }

    // The four sounds by the name the page and the sound files use.
    public IEnumerable<(string Slot, OverlaySound Sound)> Sounds() =>
        [("typing", TypingSound), ("sentence", SentenceSound), ("show", ShowSound), ("hide", HideSound)];

    // Fixes values from older settings files.
    public void Upgrade()
    {
        if (Bold is { } bold)
        {
            FontWeight = bold ? 700 : 400;
            Bold = null;
        }
        if (string.IsNullOrWhiteSpace(Id))
            Id = NewId();
        // These were called canvases at first: the names WalkTalk gave them follow the new name.
        if (Name == "Canvas" || (Name.StartsWith("Canvas ", StringComparison.Ordinal) && int.TryParse(Name[7..], out _)))
            Name = "Text template" + Name[6..];
        TypingSound ??= new();
        SentenceSound ??= new();
        ShowSound ??= new();
        HideSound ??= new();
    }

    // What the overlay page receives: ready-to-use CSS values. imageVersion changes when the picture does,
    // so OBS doesn't keep showing an old one.
    public bool IsSliced => Background == BackgroundType.Image && ImageFit == ImageFit.Slice && BackgroundImage.Length > 0;

    // The 9-slice frame is drawn as the box's border (CSS border-image): its width is the corners' size, drawn bigger
    // by the scale, and "fill" puts the picture's middle behind the text.
    private double SliceWidth => Math.Clamp(SliceSize, 1, 500) * Math.Clamp(SliceScale, 0.25, 16);

    // The gap kept between the avatar and a box that moves aside for it.
    private const double AvatarGap = 8;

    public bool HasAvatar => Avatar && (AvatarImage.Length > 0 || AvatarActiveImage.Length > 0);

    // Where the text may go, so it never runs under the avatar (top, right, bottom, left, in px). The avatar belongs to
    // the canvas side it is nearest to, and that side is kept clear up to the avatar's far edge:
    //  - a box that fills the canvas that way keeps its background under the avatar (a portrait inside a dialogue
    //    box) and gets more padding on that side, so its text starts past the avatar, as far from it as from the edge;
    //  - a box that shrinks to its text that way is placed in the rest of the canvas instead.
    public (double[] Padding, double[] CanvasPadding) Layout()
    {
        double[] padding = [Math.Clamp(PaddingY, 0, 400), Math.Clamp(PaddingX, 0, 400), Math.Clamp(PaddingY, 0, 400), Math.Clamp(PaddingX, 0, 400)];
        double[] canvas = [0, 0, 0, 0];
        if (!HasAvatar)
            return (padding, canvas);

        double width = Math.Clamp(Width, 50, 7680), height = Math.Clamp(Height, 20, 4320);
        double x = AvatarX, y = AvatarY, w = Math.Max(1, AvatarWidth), h = Math.Max(1, AvatarHeight);
        var border = IsSliced ? SliceWidth : Border && BorderWidth > 0 ? Math.Clamp(BorderWidth, 0, 100) : 0;
        double centerX = (x + w / 2) / width, centerY = (y + h / 2) / height;
        // 0 top, 1 right, 2 bottom, 3 left: how far the avatar's middle is from each side, as a share of the canvas.
        var distances = new[] { centerY, 1 - centerX, 1 - centerY, centerX };
        var side = Array.IndexOf(distances, distances.Min());
        // How far into the canvas, from that side, the avatar reaches.
        var reach = side switch { 0 => y + h, 1 => width - x, 2 => height - y, _ => x + w };
        if (reach <= 0)
            return (padding, canvas); // outside the canvas: nothing to avoid
        var shrinks = side is 1 or 3 ? AutoWidth : AutoHeight;
        if (shrinks)
            canvas[side] = Math.Min(reach + AvatarGap, (side is 1 or 3 ? width : height) - 20);
        else
            padding[side] = Math.Max(padding[side], Math.Min(reach + padding[side] - border, (side is 1 or 3 ? width : height) - 2 * border - 20));
        return (padding, canvas);
    }

    public object ToPage(long imageVersion = 0) => new
    {
        width = Math.Clamp(Width, 50, 7680),
        height = Math.Clamp(Height, 20, 4320),
        lines = Math.Clamp(Lines, 1, 10),
        hideAfter = Math.Max(0, HideAfterSeconds),

        autoWidth = AutoWidth,
        autoHeight = AutoHeight,
        padding = string.Join(' ', Layout().Padding.Select(p => N(p) + "px")),
        canvasPadding = string.Join(' ', Layout().CanvasPadding.Select(p => N(p) + "px")),

        fontFamily = CssFamily(FontFamily),
        fontWeight = Math.Clamp(FontWeight / 100 * 100, 100, 900),
        fontSize = Math.Clamp(FontSize, 4, 400),
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
        outlineWidth = Outline ? Math.Clamp(OutlineWidth, 0, 40) : 0,
        outlineColor = Css(OutlineColor, "#000000"),

        background = IsSliced ? "transparent" : Background switch
        {
            BackgroundType.Color => Css(BackgroundColor, "#000000"),
            BackgroundType.Gradient => $"linear-gradient({N(GradientAngle)}deg, {Css(BackgroundColor, "#000000")}, {Css(BackgroundColor2, "#000000")})",
            BackgroundType.Image when BackgroundImage.Length > 0 => $"url(/bg/{Id}?v={imageVersion}) center / {ImageFit switch
            {
                ImageFit.Contain => "contain no-repeat",
                ImageFit.Stretch => "100% 100% no-repeat",
                ImageFit.Tile => "auto repeat",
                _ => "cover no-repeat"
            }}",
            _ => "transparent"
        },
        border = IsSliced ? $"{N(SliceWidth)}px solid transparent"
            : Border && BorderWidth > 0 ? $"{N(Math.Clamp(BorderWidth, 0, 100))}px solid {Css(BorderColor, "#FFFFFF")}" : "none",
        borderImage = IsSliced ? $"url(/bg/{Id}?v={imageVersion}) {N(Math.Clamp(SliceSize, 1, 500))} fill / {N(SliceWidth)}px stretch" : "none",
        // Pixel art drawn bigger stays sharp instead of blurring.
        imageRendering = IsSliced && SliceScale > 1 ? "pixelated" : "auto",
        // The corners belong to the border: with it off (or a frame picture instead), the box keeps square corners.
        borderRadius = Border && !IsSliced ? Math.Clamp(BorderRadius, 0, 1000) : 0,

        animate = Animate,
        revealUnit = RevealUnit.ToString().ToLowerInvariant(),
        revealEffect = RevealEffect.ToString().ToLowerInvariant(),
        characterInterval = Math.Clamp(CharacterInterval, 0, 2000),
        wordPause = Math.Clamp(WordPause, 0, 5000),
        sentenceInterval = Math.Clamp(SentenceInterval, 0, 20000),
        replacePrevious = ReplacePrevious,
        clearAnimation = ClearAnimation switch
        {
            ClearAnimation.SlideUp => "slide-up",
            ClearAnimation.SlideDown => "slide-down",
            ClearAnimation.Shrink => "shrink",
            ClearAnimation.None => "none",
            _ => "fade"
        },
        clearDuration = Math.Clamp(ClearDuration, 0, 5000),
        sounds = Sounds().ToDictionary(s => s.Slot, s => new
        {
            kind = s.Sound.Kind == "file" && s.Sound.File.Length == 0 ? "" : s.Sound.Kind,
            url = s.Sound.Kind == "file" && s.Sound.File.Length > 0 ? $"/sound/{Id}/{s.Slot}?v={imageVersion}" : "",
            volume = Math.Clamp(s.Sound.Volume, 0, 1),
        }),
        avatar = new
        {
            on = HasAvatar,
            x = Math.Clamp(AvatarX, -7680, 7680),
            y = Math.Clamp(AvatarY, -4320, 4320),
            width = Math.Clamp(AvatarWidth, 1, 7680),
            height = Math.Clamp(AvatarHeight, 1, 4320),
            baseUrl = AvatarImage.Length > 0 ? $"/avatar/{Id}/base?v={imageVersion}" : "",
            baseOpacity = Math.Clamp(AvatarOpacity, 0, 1),
            activeUrl = AvatarActiveImage.Length > 0 ? $"/avatar/{Id}/active?v={imageVersion}" : "",
            activeOpacity = Math.Clamp(AvatarActiveOpacity, 0, 1),
            clearDelay = Math.Max(0, AvatarClearDelaySeconds),
            clearDuration = Math.Clamp(AvatarClearDuration, 0, 5000),
            transition = Math.Clamp(AvatarTransition, 0, 5000),
        },
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

    public static string Hex(byte a, byte r, byte g, byte b) => a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    // Quoted for CSS, with a generic family behind it in case the font is missing.
    private static string CssFamily(string family)
    {
        var name = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family.Trim().Replace("\"", "").Replace("\\", "");
        return $"\"{name}\", \"Segoe UI\", sans-serif";
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
