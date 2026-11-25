using System.Windows;

namespace WhisperStt.Desktop.Services;

public enum AppTheme { Dark, Light }

// Swaps the app's color dictionary (Themes/Dark.xaml or Themes/Light.xaml) and the Windows 11
// control theme at runtime. Colors are used through DynamicResource, so open windows update at once.
public static class ThemeManager
{
    private static ResourceDictionary? _active;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event Action<AppTheme>? Changed;

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        var merged = app.Resources.MergedDictionaries;
        // The dictionary App.xaml starts with, until the first switch replaces it.
        _active ??= merged.FirstOrDefault(d => d.Source?.OriginalString == "Themes/Dark.xaml");

        var next = new ResourceDictionary { Source = new Uri($"/WalkTalk;component/Themes/{theme}.xaml", UriKind.Relative) };
        if (_active is not null && merged.IndexOf(_active) is var index and >= 0)
            merged[index] = next;
        else
            merged.Add(next);
        _active = next;

        // Text boxes, dropdowns and scroll bars follow Windows' own light or dark style.
        app.ThemeMode = theme == AppTheme.Light ? ThemeMode.Light : ThemeMode.Dark;
        Current = theme;
        Changed?.Invoke(theme);
    }

    public static AppTheme Parse(string? name) => name == nameof(AppTheme.Light) ? AppTheme.Light : AppTheme.Dark;
}
