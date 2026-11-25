using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using static WhisperStt.Desktop.Targets.NativeMethods;

namespace WhisperStt.Desktop.Targets;

public sealed record PickResult(AutomationElement? Element, string? Problem);

public static class TargetPicker
{
    // Waits for the next left click outside this app and returns the text box that was clicked.
    // Must be called on a thread with a message loop (the UI thread), which the mouse hook needs.
    public static async Task<PickResult> PickAsync(CancellationToken ct)
    {
        var click = new TaskCompletionSource<(POINT Point, uint ProcessId)>(TaskCreationOptions.RunContinuationsAsynchronously);
        LowLevelMouseProc proc = (nCode, wParam, lParam) =>
        {
            if (nCode >= 0 && wParam == WM_LBUTTONUP)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var window = GetAncestor(WindowFromPoint(info.pt), GA_ROOT);
                GetWindowThreadProcessId(window, out var processId);
                if (processId != Environment.ProcessId) // clicks in our own window (e.g. Cancel) don't count
                    click.TrySetResult((info.pt, processId));
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        };

        var hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            using (ct.Register(() => click.TrySetCanceled(ct)))
            {
                var (point, processId) = await click.Task;
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;

                // Let the clicked app move focus into the box before asking what has focus.
                await Task.Delay(250, ct);
                return await Task.Run(() =>
                {
                    try
                    {
                        var element = TargetLocator.FindTextInputAt(point.X, point.Y, (int)processId);
                        return element is null
                            ? new PickResult(null, "That doesn't look like a text field. Click inside the box you want to write to.")
                            : new PickResult(element, null);
                    }
                    catch (ElementNotAvailableException)
                    {
                        return new PickResult(null, "That window closed before it could be read. Try again.");
                    }
                }, ct);
            }
        }
        finally
        {
            if (hook != IntPtr.Zero)
                UnhookWindowsHookEx(hook);
            GC.KeepAlive(proc);
        }
    }
}
