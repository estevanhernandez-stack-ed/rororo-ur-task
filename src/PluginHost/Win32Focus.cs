using System.Diagnostics;
using System.Runtime.InteropServices;
using Labs626.UrTask.Macros;

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

    /// <summary>What is in front right now, for the abort line: its title, pid and process name
    /// (Hotkeys.AbortLog.Foreground). Best-effort: a process that exits meanwhile reads as gone.</summary>
    public static string DescribeForeground()
    {
        var (hwnd, title, pid, process) = ReadForeground();
        return Hotkeys.AbortLog.Foreground(hwnd, title, pid, process);
    }

    /// <summary>Who is in front, short enough for a keep-alive log line: title (cut to 60 characters)
    /// in single quotes, then the process in parentheses. "nothing" when no window has the foreground.</summary>
    public static string DescribeForegroundShort()
    {
        var (hwnd, title, _, process) = ReadForeground();
        if (hwnd == IntPtr.Zero) return "nothing";
        var t = title ?? "";
        if (t.Length > 60) t = t[..60];
        return $"'{t}' ({process ?? "unknown"})";
    }

    private static (IntPtr hwnd, string? title, int pid, string? process) ReadForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (hwnd, null, 0, null);
        var text = new System.Text.StringBuilder(256);
        _ = GetWindowTextW(hwnd, text, text.Capacity);
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        string? process = null;
        try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return (hwnd, text.ToString(), (int)pid, process);
    }

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

    public static FocusAttempt AttachAndFocus(int pid)
    {
        try
        {
            var hwnd = Process.GetProcessById(pid).MainWindowHandle;
            if (hwnd == IntPtr.Zero) return new FocusAttempt(false, MissCause.WindowGone, "MainWindowHandle is null.");
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            return FocusHwnd(hwnd)
                ? new FocusAttempt(true, null, null)
                : new FocusAttempt(false, MissCause.FocusRefused, "Windows refused focus");
        }
        catch (ArgumentException) { return new FocusAttempt(false, MissCause.WindowGone, "Process not found (pid stale)."); }
        catch (Exception ex) { return new FocusAttempt(false, MissCause.FocusRefused, ex.Message); }
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
            var accepted = SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            return accepted;
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
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
