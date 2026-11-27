using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WhisperStt.Core.Obs;

namespace WhisperStt.Desktop.Controls;

// A color picker: the swatch opens red, green, blue and opacity sliders with a few preset colors, and the
// box beside it takes the color typed as #RRGGBB or #AARRGGBB. Color is that text, two-way bindable.
public partial class ColorField : UserControl
{
    private static readonly string[] PresetColors =
    [
        "#FFFFFF", "#000000", "#FFE45C", "#FF5C5C", "#5CFF8A", "#5CC8FF", "#B48CFF", "#FF9F43",
        "#CCFFFFFF", "#99000000", "#00000000", "#1A1A40", "#9146FF", "#00F5D4", "#FF4FA3", "#808080"
    ];

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorField)d).ShowColor()));

    private static readonly DependencyPropertyKey SwatchBrushKey = DependencyProperty.RegisterReadOnly(
        nameof(SwatchBrush), typeof(Brush), typeof(ColorField), new PropertyMetadata(Brushes.White));

    public static readonly DependencyProperty SwatchBrushProperty = SwatchBrushKey.DependencyProperty;

    private bool _updating;

    public ColorField()
    {
        InitializeComponent();
        Presets.ItemsSource = PresetColors;
        foreach (var slider in new[] { Red, Green, Blue, Alpha })
            slider.ValueChanged += (_, _) => FromSliders();
        foreach (var (box, slider) in new[] { (RedBox, Red), (GreenBox, Green), (BlueBox, Blue), (AlphaBox, Alpha) })
            box.TextChanged += (_, _) =>
            {
                if (!_updating && byte.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    slider.Value = value;
            };
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
        ShowColor();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public Brush SwatchBrush => (Brush)GetValue(SwatchBrushProperty);

    // The color changed (from outside, the sliders or a preset): show it everywhere but where it came from.
    private void ShowColor()
    {
        if (!OverlayStyle.TryParse(Color, out var a, out var r, out var g, out var b))
            return;
        SetValue(SwatchBrushKey, new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b)));
        _updating = true;
        try
        {
            if (!HexBox.IsKeyboardFocusWithin)
                HexBox.Text = Color;
            Red.Value = r;
            Green.Value = g;
            Blue.Value = b;
            Alpha.Value = a;
            RedBox.Text = r.ToString(CultureInfo.InvariantCulture);
            GreenBox.Text = g.ToString(CultureInfo.InvariantCulture);
            BlueBox.Text = b.ToString(CultureInfo.InvariantCulture);
            AlphaBox.Text = a.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }
    }

    private void FromSliders()
    {
        if (_updating)
            return;
        Color = OverlayStyle.Hex((byte)Alpha.Value, (byte)Red.Value, (byte)Green.Value, (byte)Blue.Value);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string color })
            Color = color;
    }
}

