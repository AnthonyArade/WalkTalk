using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WhisperStt.Desktop.Motion;
using WhisperStt.Desktop.Services;
using WhisperStt.Desktop.ViewModels;

namespace WhisperStt.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _shutdownDone;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Keep the newest sentence in view.
        _viewModel.Transcript.CollectionChanged += (_, _) => ScrollTranscriptToEnd();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.PendingText))
                ScrollTranscriptToEnd();
            // Opening the hints dialog puts the cursor straight in its input box.
            if (e.PropertyName == nameof(MainViewModel.IsHintsOpen) && _viewModel.IsHintsOpen)
                Dispatcher.BeginInvoke(() => NewHintBox.Focus(), DispatcherPriority.Input);
        };
        _viewModel.TargetAdded += OnTargetAdded;
        // The replacements dialog opens with the cursor in the search box; picking a suggested word
        // moves it on to the box for what to write instead.
        _viewModel.Replacements.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReplacementsViewModel.IsOpen) && _viewModel.Replacements.IsOpen)
                Dispatcher.BeginInvoke(() => ReplacementSearchBox.Focus(), DispatcherPriority.Input);
        };
        _viewModel.Replacements.Picked += () => Dispatcher.BeginInvoke(() => ReplacementCustomBox.Focus(), DispatcherPriority.Input);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWindowMaterial();
        ThemeManager.Changed += _ => ApplyWindowMaterial();
    }

    // Dark: translucent Mica behind the toolbar and sidebar. Light: solid sky blue, title bar included.
    private void ApplyWindowMaterial()
    {
        if (ThemeManager.Current == AppTheme.Dark && WindowMaterial.TryApplyMica(this))
        {
            Background = Brushes.Transparent;
            return;
        }

        SetResourceReference(BackgroundProperty, ThemeManager.Current == AppTheme.Light ? "ChromeBrush" : "WindowBrush");
        if (ThemeManager.Current == AppTheme.Light && TryFindResource("ChromeBrush") is SolidColorBrush chrome
            && TryFindResource("ChromeTextBrush") is SolidColorBrush text)
            WindowMaterial.ApplySolidTitleBar(this, chrome.Color, Color.FromRgb(text.Color.R, text.Color.G, text.Color.B));
    }

    private void ScrollTranscriptToEnd() =>
        Dispatcher.BeginInvoke(TranscriptScroll.ScrollToEnd, DispatcherPriority.Background);

    // Bring the app back and select the new field's name, so it can be renamed right away.
    private void OnTargetAdded(TargetViewModel target)
    {
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            if (TargetList.ItemContainerGenerator.ContainerFromItem(target) is ContentPresenter presenter &&
                presenter.ContentTemplate.FindName("NameBox", presenter) is TextBox nameBox)
            {
                nameBox.Focus();
                nameBox.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    // A click on the dimmed area around a dialog closes it; clicks inside the dialog don't.
    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender)
            _viewModel.CloseDialogsCommand.Execute(null);
    }

    // Reviewing a sentence puts the cursor at the end of its text, ready to fix it.
    private void ReviewBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }, DispatcherPriority.Input);
    }

    // Enter saves the new name, Escape restores the old one.
    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || e.Key is not (Key.Enter or Key.Escape))
            return;
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        if (e.Key == Key.Enter)
            binding?.UpdateSource();
        else
            binding?.UpdateTarget();
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(box), null);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownDone)
            return;

        // Let the microphone and model shut down cleanly before the window goes away.
        e.Cancel = true;
        await _viewModel.ShutdownAsync();
        _shutdownDone = true;
        // The shutdown may finish synchronously, and Close() can't be called while still inside Closing.
        _ = Dispatcher.BeginInvoke(Close);
    }
}

// Visible when the bound value's name equals the converter parameter, e.g. a step of a wizard.
// With Invert, visible when it differs (e.g. a text that isn't empty).
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString() ?? "", parameter?.ToString() ?? "", StringComparison.Ordinal) != Invert
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
