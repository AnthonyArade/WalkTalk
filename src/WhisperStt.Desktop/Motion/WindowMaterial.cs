using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WhisperStt.Desktop.Motion;

// Window chrome through the Windows 11 desktop compositor (DWM):
// - Dark theme: the translucent Mica material, the platform's equivalent of Apple's window materials.
// - Light theme: a solid brand-colored title bar that continues the sky-blue toolbar and sidebar.
// On older Windows or in high contrast nothing is changed and the window's own background shows.
public static class WindowMaterial
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2; // Mica
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    private static bool Supported => Environment.OSVersion.Version.Build >= 22621 && !SystemParameters.HighContrast;

    // Returns true when Mica is applied (the window background must then be transparent).
    public static bool TryApplyMica(Window window)
    {
        if (!Supported)
            return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        Set(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Set(hwnd, DWMWA_CAPTION_COLOR, DWMWA_COLOR_DEFAULT);
        Set(hwnd, DWMWA_TEXT_COLOR, DWMWA_COLOR_DEFAULT);
        if (Set(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW) != 0)
            return false;

        // Let the material show through WPF's own surface.
        HwndSource.FromHwnd(hwnd)!.CompositionTarget!.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        return true;
    }

    // Paints the title bar in a solid color with the given text color and turns Mica off.
    public static void ApplySolidTitleBar(Window window, Color caption, Color text)
    {
        if (!Supported)
            return;
        var hwnd = new WindowInteropHelper(window).Handle;
        Set(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE);
        Set(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1); // light minimize/maximize/close glyphs on the colored bar
        Set(hwnd, DWMWA_CAPTION_COLOR, ToColorRef(caption));
        Set(hwnd, DWMWA_TEXT_COLOR, ToColorRef(text));
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static int Set(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
