using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WhisperStt.Core.Obs;

namespace WhisperStt.Desktop.Controls;

// A color picker. The swatch opens a field where left to right is saturation and top to bottom brightness,
// a hue bar, an opacity bar and red/green/blue/opacity boxes; the box beside the swatch takes the color typed
// as #RRGGBB or #AARRGGBB. Color is that text, two-way bindable.
public partial class ColorField : UserControl
{
    private const double HandleRadius = 8;

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorField)d).ShowColor()));

    private static readonly DependencyPropertyKey SwatchBrushKey = DependencyProperty.RegisterReadOnly(
        nameof(SwatchBrush), typeof(Brush), typeof(ColorField), new PropertyMetadata(Brushes.White));
    public static readonly DependencyProperty SwatchBrushProperty = SwatchBrushKey.DependencyProperty;

    private static readonly DependencyPropertyKey SolidBrushKey = DependencyProperty.RegisterReadOnly(
        nameof(SolidBrush), typeof(Brush), typeof(ColorField), new PropertyMetadata(Brushes.White));
    public static readonly DependencyProperty SolidBrushProperty = SolidBrushKey.DependencyProperty;

    // The color as hue (0..360), saturation, brightness (0..1) and opacity (0..255). Kept apart from the RGB
    // text so the hue survives while the color is grey or black.
    private double _hue, _saturation = 0, _value = 1;
    private byte _alpha = 255;
    private bool _updating;

    public ColorField()
    {
        InitializeComponent();
        HexBox.TextChanged += (_, _) =>
        {
            if (_updating)
                return;
            var text = HexBox.Text.Trim();
            if (!text.StartsWith('#'))
                text = "#" + text;
            if (OverlayStyle.TryParse(text, out _, out _, out _, out _))
                Color = text.ToUpperInvariant();
        };
        foreach (var box in new[] { RedBox, GreenBox, BlueBox, AlphaBox })
            box.TextChanged += (_, _) => FromBoxes();
        Field.SizeChanged += (_, _) => PlaceHandles();
        HueBar.SizeChanged += (_, _) => PlaceHandles();
        AlphaBar.SizeChanged += (_, _) => PlaceHandles();
        ShowColor();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    // The color with its opacity (the swatch), and without it (the field's handle).
    public Brush SwatchBrush => (Brush)GetValue(SwatchBrushProperty);
    public Brush SolidBrush => (Brush)GetValue(SolidBrushProperty);

    // -- the color changed: show it everywhere

    private void ShowColor()
    {
        if (!OverlayStyle.TryParse(Color, out var a, out var r, out var g, out var b))
            return;
        var (h, s, v) = ToHsv(r, g, b);
        // Grey has no hue and black no saturation: keep the ones the user had chosen.
        if (s > 0 && v > 0)
            _hue = h;
        if (v > 0)
            _saturation = s;
        _value = v;
        _alpha = a;
        Render(r, g, b, a);
    }

    private void Render(byte r, byte g, byte b, byte a)
    {
        SetValue(SwatchBrushKey, new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b)));
        SetValue(SolidBrushKey, new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)));
        var (hr, hg, hb) = FromHsv(_hue, 1, 1);
        HueLayer.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(hr, hg, hb));
        AlphaLayer.Background = new LinearGradientBrush(System.Windows.Media.Color.FromArgb(0, r, g, b), System.Windows.Media.Color.FromRgb(r, g, b), 0);
        _updating = true;
        try
        {
            if (!HexBox.IsKeyboardFocusWithin)
                HexBox.Text = Color;
            if (!RedBox.IsKeyboardFocusWithin) RedBox.Text = r.ToString(CultureInfo.InvariantCulture);
            if (!GreenBox.IsKeyboardFocusWithin) GreenBox.Text = g.ToString(CultureInfo.InvariantCulture);
            if (!BlueBox.IsKeyboardFocusWithin) BlueBox.Text = b.ToString(CultureInfo.InvariantCulture);
            if (!AlphaBox.IsKeyboardFocusWithin) AlphaBox.Text = (a / 255.0).ToString("0.##", CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }
        PlaceHandles();
    }

    private void PlaceHandles()
    {
        Place(FieldHandle, _saturation * Field.ActualWidth, (1 - _value) * Field.ActualHeight);
        Place(HueHandle, _hue / 360 * HueBar.ActualWidth, HueBar.ActualHeight / 2);
        Place(AlphaHandle, _alpha / 255.0 * AlphaBar.ActualWidth, AlphaBar.ActualHeight / 2);
    }

    private static void Place(FrameworkElement handle, double x, double y)
    {
        Canvas.SetLeft(handle, x - HandleRadius);
        Canvas.SetTop(handle, y - HandleRadius);
    }

    // -- the user picks: from the field, the bars or the boxes

    private void Commit()
    {
        var (r, g, b) = FromHsv(_hue, _saturation, _value);
        var text = OverlayStyle.Hex(_alpha, r, g, b);
        if (text == Color)
            Render(r, g, b, _alpha); // same text (e.g. the hue moved on grey): still move the handles
        else
            Color = text; // ShowColor keeps _hue and _saturation where the color can't tell them
    }

    private void Field_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Field.CaptureMouse();
        FromField(e.GetPosition(Field));
    }

    private void Field_MouseMove(object sender, MouseEventArgs e)
    {
        if (Field.IsMouseCaptured)
            FromField(e.GetPosition(Field));
    }

    private void FromField(Point point)
    {
        _saturation = Math.Clamp(point.X / Math.Max(1, Field.ActualWidth), 0, 1);
        _value = Math.Clamp(1 - point.Y / Math.Max(1, Field.ActualHeight), 0, 1);
        Commit();
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueBar.CaptureMouse();
        FromHue(e.GetPosition(HueBar));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (HueBar.IsMouseCaptured)
            FromHue(e.GetPosition(HueBar));
    }

    private void FromHue(Point point)
    {
        _hue = Math.Clamp(point.X / Math.Max(1, HueBar.ActualWidth), 0, 1) * 360;
        if (_saturation == 0 && _value > 0)
            _saturation = 1; // picking a hue on white or grey: show that hue
        if (_value == 0)
            _value = 1;
        Commit();
    }

    private void Alpha_MouseDown(object sender, MouseButtonEventArgs e)
    {
        AlphaBar.CaptureMouse();
        FromAlpha(e.GetPosition(AlphaBar));
    }

    private void Alpha_MouseMove(object sender, MouseEventArgs e)
    {
        if (AlphaBar.IsMouseCaptured)
            FromAlpha(e.GetPosition(AlphaBar));
    }

    private void FromAlpha(Point point)
    {
        _alpha = (byte)Math.Round(Math.Clamp(point.X / Math.Max(1, AlphaBar.ActualWidth), 0, 1) * 255);
        Commit();
    }

    private void Drag_MouseUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void FromBoxes()
    {
        if (_updating)
            return;
        if (!byte.TryParse(RedBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(GreenBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(BlueBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ||
            !double.TryParse(AlphaBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) || a is < 0 or > 1)
            return;
        Color = OverlayStyle.Hex((byte)Math.Round(a * 255), r, g, b);
    }

    // -- HSV, the space the field and the hue bar work in

    private static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            if (max == rf) h = 60 * ((gf - bf) / delta % 6);
            else if (max == gf) h = 60 * ((bf - rf) / delta + 2);
            else h = 60 * ((rf - gf) / delta + 4);
        }
        if (h < 0)
            h += 360;
        return (h, max == 0 ? 0 : delta / max, max);
    }

    private static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
