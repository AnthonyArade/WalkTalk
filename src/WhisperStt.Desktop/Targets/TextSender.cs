using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using static WhisperStt.Desktop.Targets.NativeMethods;

namespace WhisperStt.Desktop.Targets;

public enum SendMethod { EditMessage, SetValue, Typing }

// Appends text to the end of a text box in another app. Tries the methods that don't steal focus first.
public static class TextSender
{
    private const uint MessageTimeoutMs = 2000;

    // pressEnter: press Enter afterwards, which sends the message in chat apps.
    public static SendMethod Append(AutomationElement element, string text, bool pressEnter = false)
    {
        var current = element.Current;
        SendMethod method;
        if (current.NativeWindowHandle != 0 && IsNativeEdit(current.ClassName) &&
            TryAppendWithEditMessages(new IntPtr(current.NativeWindowHandle), text))
            method = SendMethod.EditMessage;
        else if (!IsWebContent(current.FrameworkId) && TryAppendWithValuePattern(element, text))
            method = SendMethod.SetValue;
        else
        {
            AppendByTyping(element, text, pressEnter);
            return SendMethod.Typing;
        }

        if (pressEnter)
            PressEnter(element);
        return method;
    }

    // Empties the text box (voice command "Clean"). Undoable in the target app where it supports undo.
    public static void Clear(AutomationElement element)
    {
        var current = element.Current;
        if (current.NativeWindowHandle != 0 && IsNativeEdit(current.ClassName))
        {
            var hwnd = new IntPtr(current.NativeWindowHandle);
            SendMessageTimeout(hwnd, EM_SETSEL, IntPtr.Zero, -1, SMTO_ABORTIFHUNG, MessageTimeoutMs, out _);
            SendMessageTimeout(hwnd, EM_REPLACESEL, 1, "", SMTO_ABORTIFHUNG, MessageTimeoutMs, out _);
            if (GetTextLength(hwnd) == 0)
                return;
        }

        if (!IsWebContent(current.FrameworkId) &&
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly)
        {
            try
            {
                ((ValuePattern)pattern).SetValue("");
                return;
            }
            catch (InvalidOperationException)
            {
                // Fall back to the keyboard below.
            }
        }

        // Web pages and rich editors: select everything in the box and delete it, like a user would.
        FocusOrThrow(element, "cleared");
        Send([
            Key(VK_CONTROL, 0), Key(VK_A, 0), Key(VK_A, KEYEVENTF_KEYUP), Key(VK_CONTROL, KEYEVENTF_KEYUP),
            Key(VK_BACK, 0), Key(VK_BACK, KEYEVENTF_KEYUP)
        ]);
    }

    // Presses Enter in the text box (voice command "Send"), which sends the message in chat apps.
    public static void PressEnter(AutomationElement element)
    {
        FocusOrThrow(element, "sent");
        Send([Key(VK_RETURN, 0), Key(VK_RETURN, KEYEVENTF_KEYUP)]);
    }

    // Browsers and Electron apps (Discord, Slack...). Their pages keep their own copy of a box's text
    // (React, Slate, ProseMirror) and only update it from real input, so setting the value through
    // accessibility changes what is drawn but not what gets sent. They must be typed into.
    private static bool IsWebContent(string frameworkId) =>
        frameworkId.Equals("Chrome", StringComparison.OrdinalIgnoreCase) ||
        frameworkId.Equals("Gecko", StringComparison.OrdinalIgnoreCase);

    // Classic Win32 / WinForms edit boxes and RichEdit (including Windows 11 Notepad).
    private static bool IsNativeEdit(string className) =>
        className.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
        className.Contains(".EDIT.", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase);

    private static bool TryAppendWithEditMessages(IntPtr hwnd, string text)
    {
        var before = GetTextLength(hwnd);
        if (before < 0)
            return false;
        var existing = before == 0 ? "" : GetText(hwnd, before);
        var piece = Join(existing, text);

        // The length may count CR/LF differently from the selection, but a larger value is clamped to the end.
        SendMessageTimeout(hwnd, EM_SETSEL, before, before, SMTO_ABORTIFHUNG, MessageTimeoutMs, out _);
        if (SendMessageTimeout(hwnd, EM_REPLACESEL, 1, piece, SMTO_ABORTIFHUNG, MessageTimeoutMs, out _) == IntPtr.Zero)
            return false;
        return GetTextLength(hwnd) > before;
    }

    private static int GetTextLength(IntPtr hwnd) =>
        SendMessageTimeout(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, MessageTimeoutMs, out var length) == IntPtr.Zero
            ? -1
            : (int)length;

    private static string GetText(IntPtr hwnd, int length)
    {
        var buffer = new StringBuilder(length + 1);
        SendMessageTimeout(hwnd, WM_GETTEXT, buffer.Capacity, buffer, SMTO_ABORTIFHUNG, MessageTimeoutMs, out _);
        return buffer.ToString();
    }

    // Most modern controls (WPF, UWP, browser <input> and <textarea>) accept a new value without focus.
    private static bool TryAppendWithValuePattern(AutomationElement element, string text)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            return false;
        var value = (ValuePattern)pattern;
        if (value.Current.IsReadOnly)
            return false;
        try
        {
            var before = value.Current.Value;
            value.SetValue(before + Join(before, text));
            // Browsers report the new value a moment later. Any change means it was accepted
            // (the app may have reformatted it), so typing it again would duplicate the text.
            for (int i = 0; i < 16; i++)
            {
                if (value.Current.Value != before)
                    return true;
                Thread.Sleep(50);
            }
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // Last resort for rich editors: focus the box, jump to the end and type the text as keystrokes.
    // Focus stays in that box afterwards: keystrokes still queued would follow focus to another window.
    private static void AppendByTyping(AutomationElement element, string text, bool pressEnter)
    {
        FocusOrThrow(element, "typed");
        var existing = ReadText(element);
        var piece = existing is null ? " " + text : Join(existing, text);

        var inputs = new List<INPUT>
        {
            Key(VK_CONTROL, 0), Key(VK_END, KEYEVENTF_EXTENDEDKEY),
            Key(VK_END, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP), Key(VK_CONTROL, KEYEVENTF_KEYUP)
        };
        foreach (var c in piece)
        {
            inputs.Add(Unicode(c, 0));
            inputs.Add(Unicode(c, KEYEVENTF_KEYUP));
        }
        if (pressEnter)
        {
            inputs.Add(Key(VK_RETURN, 0));
            inputs.Add(Key(VK_RETURN, KEYEVENTF_KEYUP));
        }
        Send(inputs.ToArray());
    }

    // Never send keystrokes unless they are sure to land in this box.
    private static void FocusOrThrow(AutomationElement element, string action)
    {
        element.SetFocus();
        Thread.Sleep(120);
        var focused = AutomationElement.FocusedElement;
        if (focused is null || !Automation.Compare(focused, element))
            throw new InvalidOperationException($"Couldn't put the cursor in the field, so nothing was {action}.");
    }

    private static void Send(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException("Windows blocked the typing. Is the app running as administrator?");
    }

    private static string? ReadText(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
                return ((ValuePattern)value).Current.Value;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
                return ((TextPattern)textPattern).DocumentRange.GetText(-1);
        }
        catch (InvalidOperationException) { }
        return null;
    }

    // Separates sentences with a space unless the box is empty or already ends with whitespace.
    private static string Join(string existing, string text) =>
        existing.Length == 0 || char.IsWhiteSpace(existing[^1]) ? text : " " + text;

    private static INPUT Key(ushort vk, uint flags) =>
        new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };

    private static INPUT Unicode(char c, uint flags) =>
        new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | flags } } };
}
