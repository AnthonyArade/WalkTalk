using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using WhisperStt.Desktop.Services;

namespace WhisperStt.Desktop;

public partial class App : Application
{
    // The app is English-only: run it in English even on a Windows set to another language,
    // so built-in text (text box menus, number and date formats, framework messages) matches.
    static App()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(english.IetfLanguageTag)));
    }

    // Start in the theme chosen last time, before the window is created.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Apply(ThemeManager.Parse(StateStore.Load().Theme));
    }
}
