using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WhisperStt.Core.Obs;
using WhisperStt.Desktop.Mvvm;

namespace WhisperStt.Desktop.ViewModels;

// One choice in a list, with the words shown for it.
public sealed record Choice<T>(T Value, string Label);

// One OBS canvas, as edited in the OBS dialog. Every change goes to the overlay at once (changed).
public sealed class CanvasViewModel : ObservableObject
{
    private readonly Action<CanvasViewModel, bool> _changed;

    public CanvasViewModel(OverlayStyle style, Action<CanvasViewModel, bool> changed)
    {
        Style = style;
        _changed = changed;
        LoadThumbnail();
    }

    // A picture of the canvas taken from its live preview (the real page, fonts and effects included), shown on
    // its tile. Null until the canvas has been opened once; the tile then draws a simpler picture itself.
    public ImageSource? Thumbnail { get; private set; }
    public bool HasThumbnail => Thumbnail is not null;

    public void LoadThumbnail()
    {
        var path = OverlayImages.ThumbnailPath(Id);
        Thumbnail = null;
        if (File.Exists(path))
        {
            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't keep the file locked
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                Thumbnail = bitmap;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
            }
        }
        OnPropertyChanged(nameof(Thumbnail));
        OnPropertyChanged(nameof(HasThumbnail));
    }

    // The settings themselves; changed through the properties below.
    public OverlayStyle Style { get; }
    public string Id => Style.Id;
    public string Url { get; private set; } = "";

    public void SetUrl(string url)
    {
        Url = url;
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(PreviewUrl));
    }

    public string PreviewUrl => Url + "?preview=1";

    // -- what the tile shows

    public string SizeText => $"{Style.Width} × {Style.Height}";
    public double TileWidth => Style.Width >= Style.Height ? 132 : 132.0 * Style.Width / Style.Height;
    public double TileHeight => Style.Width >= Style.Height ? Math.Max(10, 132.0 * Style.Height / Style.Width) : 132;
    public Brush TextBrush => Solid(Style.Color);
    public Brush OutlineBrush => Style.Outline && Style.OutlineWidth > 0 ? Solid(Style.OutlineColor) : Brushes.Transparent;

    public Brush BackgroundBrush => Style.Background switch
    {
        BackgroundType.Color => Solid(Style.BackgroundColor),
        BackgroundType.Gradient => new LinearGradientBrush(Color(Style.BackgroundColor), Color(Style.BackgroundColor2), Style.GradientAngle - 90),
        BackgroundType.Image when OverlayImages.PathOf(Style.BackgroundImage) is { } path => Image(path),
        _ => Brushes.Transparent
    };

    public Brush BorderBrush => Style.Border ? Solid(Style.BorderColor) : Brushes.Transparent;
    public System.Windows.HorizontalAlignment TileAlign => Style.HorizontalAlign switch
    {
        HorizontalAlign.Left => System.Windows.HorizontalAlignment.Left,
        HorizontalAlign.Right => System.Windows.HorizontalAlignment.Right,
        _ => System.Windows.HorizontalAlignment.Center
    };
    public System.Windows.VerticalAlignment TileVerticalAlign => Style.VerticalAlign switch
    {
        VerticalAlign.Top => System.Windows.VerticalAlignment.Top,
        VerticalAlign.Middle => System.Windows.VerticalAlignment.Center,
        _ => System.Windows.VerticalAlignment.Bottom
    };

    private static System.Windows.Media.Color Color(string text) =>
        OverlayStyle.TryParse(text, out var a, out var r, out var g, out var b) ? System.Windows.Media.Color.FromArgb(a, r, g, b) : Colors.Transparent;

    private static Brush Solid(string text) => new SolidColorBrush(Color(text));

    private Brush Image(string path)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't keep the file locked
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = 320;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            return new ImageBrush(bitmap)
            {
                Stretch = Style.ImageFit switch { ImageFit.Contain => Stretch.Uniform, ImageFit.Stretch => Stretch.Fill, _ => Stretch.UniformToFill }
            };
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return Brushes.Transparent;
        }
    }

    // -- the settings

    public string Name { get => Style.Name; set => Set(Style.Name, string.IsNullOrWhiteSpace(value) ? "Canvas" : value.Trim(), v => Style.Name = v); }
    public bool IsOn { get => Style.IsOn; set => Set(Style.IsOn, value, v => Style.IsOn = v); }

    public int CanvasWidth { get => Style.Width; set => Set(Style.Width, Math.Clamp(value, 50, 7680), v => Style.Width = v); }
    public int CanvasHeight { get => Style.Height; set => Set(Style.Height, Math.Clamp(value, 20, 4320), v => Style.Height = v); }
    public double Lines { get => Style.Lines; set => Set(Style.Lines, (int)Math.Round(value), v => Style.Lines = v, nameof(LinesText)); }
    public string LinesText => Style.Lines == 1 ? "1 sentence" : $"{Style.Lines} sentences";
    public double HideAfter { get => Style.HideAfterSeconds; set => Set(Style.HideAfterSeconds, Math.Round(value), v => Style.HideAfterSeconds = v, nameof(HideAfterText)); }
    public string HideAfterText => HideAfter == 0 ? "Never" : $"{HideAfter:0} s";

    public bool AutoWidth { get => Style.AutoWidth; set => Set(Style.AutoWidth, value, v => Style.AutoWidth = v); }
    public bool AutoHeight { get => Style.AutoHeight; set => Set(Style.AutoHeight, value, v => Style.AutoHeight = v); }
    public double PaddingX { get => Style.PaddingX; set => Set(Style.PaddingX, Math.Round(value), v => Style.PaddingX = v, nameof(PaddingXText)); }
    public string PaddingXText => $"{PaddingX:0} px";
    public double PaddingY { get => Style.PaddingY; set => Set(Style.PaddingY, Math.Round(value), v => Style.PaddingY = v, nameof(PaddingYText)); }
    public string PaddingYText => $"{PaddingY:0} px";

    public string FontFamily
    {
        get => Style.FontFamily;
        set => Set(Style.FontFamily, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value.Trim(), v => Style.FontFamily = v);
    }

    public int FontWeight { get => Style.FontWeight; set => Set(Style.FontWeight, Math.Clamp(value / 100 * 100, 100, 900), v => Style.FontWeight = v); }
    public double FontSize { get => Style.FontSize; set => Set(Style.FontSize, Math.Round(value), v => Style.FontSize = v, nameof(FontSizeText)); }
    public string FontSizeText => $"{FontSize:0} px";
    public string TextColor { get => Style.Color; set => SetColor(Style.Color, value, v => Style.Color = v); }
    public TextCase Case { get => Style.Case; set => Set(Style.Case, value, v => Style.Case = v); }
    public HorizontalAlign HorizontalAlign { get => Style.HorizontalAlign; set => Set(Style.HorizontalAlign, value, v => Style.HorizontalAlign = v); }
    public VerticalAlign VerticalAlign { get => Style.VerticalAlign; set => Set(Style.VerticalAlign, value, v => Style.VerticalAlign = v); }
    public double LineHeight { get => Style.LineHeight; set => Set(Style.LineHeight, Math.Round(value, 2), v => Style.LineHeight = v, nameof(LineHeightText)); }
    public string LineHeightText => $"{LineHeight:0.00}×";

    public bool Shadow { get => Style.Shadow; set => Set(Style.Shadow, value, v => Style.Shadow = v); }
    public double ShadowX { get => Style.ShadowX; set => Set(Style.ShadowX, Math.Round(value), v => Style.ShadowX = v, nameof(ShadowXText)); }
    public string ShadowXText => $"{ShadowX:0} px";
    public double ShadowY { get => Style.ShadowY; set => Set(Style.ShadowY, Math.Round(value), v => Style.ShadowY = v, nameof(ShadowYText)); }
    public string ShadowYText => $"{ShadowY:0} px";
    public double ShadowBlur { get => Style.ShadowBlur; set => Set(Style.ShadowBlur, Math.Round(value), v => Style.ShadowBlur = v, nameof(ShadowBlurText)); }
    public string ShadowBlurText => $"{ShadowBlur:0} px";
    public string ShadowColor { get => Style.ShadowColor; set => SetColor(Style.ShadowColor, value, v => Style.ShadowColor = v); }

    public bool Outline { get => Style.Outline; set => Set(Style.Outline, value, v => Style.Outline = v); }
    public double OutlineWidth { get => Style.OutlineWidth; set => Set(Style.OutlineWidth, Math.Round(value), v => Style.OutlineWidth = v, nameof(OutlineWidthText)); }
    public string OutlineWidthText => $"{OutlineWidth:0} px";
    public string OutlineColor { get => Style.OutlineColor; set => SetColor(Style.OutlineColor, value, v => Style.OutlineColor = v); }

    public BackgroundType Background
    {
        get => Style.Background;
        set
        {
            Set(Style.Background, value, v => Style.Background = v);
            OnPropertyChanged(nameof(IsColorBackground));
            OnPropertyChanged(nameof(IsGradientBackground));
            OnPropertyChanged(nameof(IsImageBackground));
        }
    }

    public bool IsColorBackground => Background is BackgroundType.Color or BackgroundType.Gradient;
    public bool IsGradientBackground => Background == BackgroundType.Gradient;
    public bool IsImageBackground => Background == BackgroundType.Image;
    public string BackgroundColor { get => Style.BackgroundColor; set => SetColor(Style.BackgroundColor, value, v => Style.BackgroundColor = v); }
    public string BackgroundColor2 { get => Style.BackgroundColor2; set => SetColor(Style.BackgroundColor2, value, v => Style.BackgroundColor2 = v); }
    public double GradientAngle { get => Style.GradientAngle; set => Set(Style.GradientAngle, Math.Round(value), v => Style.GradientAngle = v, nameof(GradientAngleText)); }
    public string GradientAngleText => $"{GradientAngle:0}°";
    public ImageFit ImageFit { get => Style.ImageFit; set => Set(Style.ImageFit, value, v => Style.ImageFit = v); }
    public string ImageText => Style.BackgroundImage.Length == 0 ? "No picture chosen" : "Picture chosen";

    // A picture was copied for this canvas (or removed).
    public void SetImage(string name)
    {
        Style.BackgroundImage = name;
        Refresh();
        _changed(this, true);
    }

    public bool Border { get => Style.Border; set => Set(Style.Border, value, v => Style.Border = v); }
    public double BorderWidth { get => Style.BorderWidth; set => Set(Style.BorderWidth, Math.Round(value), v => Style.BorderWidth = v, nameof(BorderWidthText)); }
    public string BorderWidthText => $"{BorderWidth:0} px";
    public string BorderColor { get => Style.BorderColor; set => SetColor(Style.BorderColor, value, v => Style.BorderColor = v); }
    public double BorderRadius { get => Style.BorderRadius; set => Set(Style.BorderRadius, Math.Round(value), v => Style.BorderRadius = v, nameof(BorderRadiusText)); }
    public string BorderRadiusText => $"{BorderRadius:0} px";

    // Every property again (after a reset), and the tile.
    public void Refresh() => OnPropertyChanged(string.Empty);

    private void Set<T>(T current, T value, Action<T> apply, string? also = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        apply(value);
        OnPropertyChanged(name);
        if (also is not null)
            OnPropertyChanged(also);
        // The tile reflects the look too.
        foreach (var tile in new[] { nameof(SizeText), nameof(TileWidth), nameof(TileHeight), nameof(TextBrush), nameof(OutlineBrush),
                     nameof(BackgroundBrush), nameof(BorderBrush), nameof(TileAlign), nameof(TileVerticalAlign) })
            OnPropertyChanged(tile);
        _changed(this, false);
    }

    // A color typed as #RRGGBB or #AARRGGBB; anything else is ignored until it becomes a color.
    private void SetColor(string current, string value, Action<string> apply, [CallerMemberName] string? name = null)
    {
        var text = (value ?? "").Trim();
        if (!text.StartsWith('#'))
            text = "#" + text;
        if (OverlayStyle.TryParse(text, out _, out _, out _, out _))
            Set(current, text.ToUpperInvariant(), apply, null, name);
    }
}
