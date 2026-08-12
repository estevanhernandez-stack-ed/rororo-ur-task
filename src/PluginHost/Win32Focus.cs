using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Forces a target window to the foreground even when the user is idle. The
/// AttachThreadInput trick alone is NOT enough on modern Windows: with no recent
/// user input the foreground-lock timeout makes SetForegroundWindow silently
/// no-op. The remedy layers three moves: attach to the foreground thread's input
/// queue, temporarily zero the system foreground-lock timeout (restored right
/// after), and BringWindowToTop. Callers still verify the foreground actually
/// became the target pid before synthesizing input (the safety invariant), so a
/// focus that still fails degrades to a skipped action, never a stray keystroke.
/// Ported from ur-afk v0.5.2 (rororo-ur-afk/src/PluginHost/Win32Focus.cs).
/// </summary>
internal static class Win32Focus
{
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const int SW_RESTORE = 9;

    /// <summary>The window that currently owns the foreground. IntPtr.Zero if none.</summary>
    public static IntPtr CaptureForeground() => GetForegroundWindow();

    /// <summary>
    /// Put the foreground back where we found it after a keep-alive tap. Uses the same
    /// foreground-lock dance as AttachAndFocus — restoring focus while the user is idle
    /// hits the identical SetForegroundWindow no-op if you skip it. Best-effort: a failed
    /// restore is annoying, never fatal, so the caller logs and carries on.
    /// </summary>
    public static bool RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try { return FocusHwnd(hwnd); }
        catch { return false; }
    }

    public static (bool ok, string? error) AttachAndFocus(int pid)
    {
        try
        {
            var hwnd = Process.GetProcessById(pid).MainWindowHandle;
            if (hwnd == IntPtr.Zero) return (false, "MainWindowHandle is null.");
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            FocusHwnd(hwnd);
            return (true, null);
        }
        catch (ArgumentException) { return (false, "Process not found (pid stale)."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Force hwnd to the foreground even with no recent user input. AttachThreadInput
    /// alone is NOT enough on modern Windows — the foreground-lock timeout makes
    /// SetForegroundWindow silently no-op. Attach to the foreground thread's input
    /// queue, temporarily zero the lock timeout (restored right after), BringWindowToTop.
    /// </summary>
    private static bool FocusHwnd(IntPtr hwnd)
    {
        var fgHwnd = GetForegroundWindow();
        var fgThreadId = fgHwnd != IntPtr.Zero ? GetWindowThreadProcessId(fgHwnd, out _) : 0u;
        var ourThreadId = GetCurrentThreadId();
        bool attached = false;
        if (fgThreadId != 0 && fgThreadId != ourThreadId)
            attached = AttachThreadInput(fgThreadId, ourThreadId, true);

        uint savedTimeout = 0;
        bool loweredLock = false;
        try
        {
            if (SystemParametersInfoGet(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref savedTimeout, 0))
            {
                SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
                loweredLock = true;
            }
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            return true;
        }
        finally
        {
            if (loweredLock)
                SystemParametersInfoSet(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, new IntPtr(savedTimeout), SPIF_SENDCHANGE);
            if (attached) AttachThreadInput(fgThreadId, ourThreadId, false);
        }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoGet(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoSet(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
