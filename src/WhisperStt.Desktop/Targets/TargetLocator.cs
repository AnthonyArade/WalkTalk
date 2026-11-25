using System.Diagnostics;
using System.Windows.Automation;

namespace WhisperStt.Desktop.Targets;

// What we remember about a text box in another app, so it can be found again after a restart.
public sealed class TargetInfo
{
    public string ProcessName { get; set; } = "";
    public string AppName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string FrameworkId { get; set; } = "";
    public int ControlTypeId { get; set; }
    public string AutomationId { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ElementName { get; set; } = "";

    // Short human description of the box, e.g. "Search" or "text field".
    public string Label =>
        ElementName.Length > 0 ? Truncate(ElementName, 40) :
        AutomationId.Length > 0 ? Truncate(AutomationId, 40) :
        "text field";

    public string DefaultName => $"{AppName} – {Label}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

// All UI Automation calls here can block on the other app, so call them off the UI thread.
public static class TargetLocator
{
    // Frameworks whose class names are stable. Browsers report the HTML class attribute, which changes,
    // and WinForms class names end with a suffix that differs on every run.
    private static readonly HashSet<string> StableClassNameFrameworks =
        new(StringComparer.OrdinalIgnoreCase) { "Win32", "WPF", "DirectUI", "XAML" };

    public static bool IsTextInput(AutomationElement element)
    {
        var current = element.Current;
        if (current.ControlType == ControlType.Edit)
            return true;
        if (!current.IsKeyboardFocusable && !current.HasKeyboardFocus)
            return false;
        if (current.ControlType == ControlType.Document)
            return true;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly)
            return true;
        // Rich editors in browsers (contenteditable) expose text but no editable value.
        return current.HasKeyboardFocus && element.TryGetCurrentPattern(TextPattern.Pattern, out _);
    }

    // The text box at a screen point: the focused element if the click put focus in a text box,
    // otherwise the closest text box under the cursor.
    public static AutomationElement? FindTextInputAt(int x, int y, int processId)
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is not null && focused.Current.ProcessId == processId && IsTextInput(focused))
            return focused;

        var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        for (int depth = 0; element is not null && depth < 8; depth++)
        {
            if (element.Current.ProcessId != processId)
                break;
            if (IsTextInput(element))
                return element;
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return null;
    }

    public static TargetInfo Describe(AutomationElement element)
    {
        var current = element.Current;
        using var process = Process.GetProcessById(current.ProcessId);
        return new TargetInfo
        {
            ProcessName = process.ProcessName,
            AppName = GetAppName(process),
            WindowTitle = GetTopLevelWindow(element)?.Current.Name ?? "",
            FrameworkId = current.FrameworkId,
            ControlTypeId = current.ControlType.Id,
            AutomationId = current.AutomationId,
            ClassName = current.ClassName,
            ElementName = current.Name.Trim()
        };
    }

    // Looks for the box in the app's open windows, preferring the window it was picked in.
    public static AutomationElement? Find(TargetInfo target)
    {
        var condition = BuildCondition(target);
        var windows = GetTopLevelWindows(target.ProcessName)
            .OrderByDescending(w => w.Current.Name == target.WindowTitle);
        foreach (var window in windows)
        {
            var found = window.FindFirst(TreeScope.Descendants, condition);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static Condition BuildCondition(TargetInfo target)
    {
        var conditions = new List<Condition>
        {
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.LookupById(target.ControlTypeId))
        };
        if (target.AutomationId.Length > 0)
            conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, target.AutomationId));
        else if (target.ElementName.Length > 0)
            conditions.Add(new PropertyCondition(AutomationElement.NameProperty, target.ElementName));
        if (target.ClassName.Length > 0 && StableClassNameFrameworks.Contains(target.FrameworkId))
            conditions.Add(new PropertyCondition(AutomationElement.ClassNameProperty, target.ClassName));
        return conditions.Count == 1 ? conditions[0] : new AndCondition(conditions.ToArray());
    }

    private static List<AutomationElement> GetTopLevelWindows(string processName)
    {
        var processIds = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToHashSet();
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (processIds.Contains(pid) && NativeMethods.IsWindowVisible(hwnd))
                handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        var windows = new List<AutomationElement>();
        foreach (var hwnd in handles)
        {
            try { windows.Add(AutomationElement.FromHandle(hwnd)); }
            catch (ElementNotAvailableException) { }
        }
        return windows;
    }

    private static AutomationElement? GetTopLevelWindow(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        var root = AutomationElement.RootElement;
        AutomationElement? current = element;
        while (current is not null)
        {
            var parent = walker.GetParent(current);
            if (parent is null || Automation.Compare(parent, root))
                return current;
            current = parent;
        }
        return null;
    }

    private static string GetAppName(Process process)
    {
        try
        {
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
                return description.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Elevated or protected processes don't let us read their file details.
        }
        return process.ProcessName;
    }
}
