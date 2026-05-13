using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Thin Win32 helper that bypasses Windows' cross-process focus-stealing
/// restriction via the AttachThreadInput pattern.
///
/// Windows refuses a bare SetForegroundWindow call from a process that isn't
/// already the foreground owner. The standard workaround: temporarily attach
/// our thread's input queue to the current foreground thread's queue so the
/// system treats our call as if it originated from the foreground app.
/// </summary>
internal static class Win32Focus
{
    /// <summary>
    /// Focus the main window of the process identified by <paramref name="pid"/>
    /// using the AttachThreadInput workaround. Returns immediately after the
    /// Win32 call — callers should add a settle delay (~150 ms) before relying
    /// on foreground state.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the call reached SetForegroundWindow without error;
    /// <c>false</c> with an error description otherwise.
    /// </returns>
    public static (bool ok, string? error) AttachAndFocus(int pid)
    {
        try
        {
            var hwnd = Process.GetProcessById(pid).MainWindowHandle;
            if (hwnd == IntPtr.Zero) return (false, "MainWindowHandle is null.");

            var fgHwnd = GetForegroundWindow();
            var fgThreadId = fgHwnd != IntPtr.Zero
                ? GetWindowThreadProcessId(fgHwnd, out _)
                : 0u;
            var ourThreadId = GetCurrentThreadId();
            bool attached = false;
            if (fgThreadId != 0 && fgThreadId != ourThreadId)
            {
                attached = AttachThreadInput(fgThreadId, ourThreadId, true);
            }
            try
            {
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(fgThreadId, ourThreadId, false);
            }
            return (true, null);
        }
        catch (ArgumentException) { return (false, "Process not found (pid stale)."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
