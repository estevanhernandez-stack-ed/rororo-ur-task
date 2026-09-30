using System.Globalization;

namespace Labs626.UrTask.Hotkeys;

/// <summary>
/// The ur-task.log lines for an abort, naming what did it: the key (and what was in front when it
/// fired), a button in Ur Task's window, or a bridge StopMacro and its caller. Live 2026-09-30 there
/// were 8 aborts nobody could explain, one of them killing the ore-stop pulse mid-sweep with nobody
/// at the keyboard, and the log said only "Aborted.". Pure, so the wording is tested.
/// <para>RegisterHotKey's WM_HOTKEY does not say whether the key press was injected (only a
/// low-level keyboard hook sees LLKHF_INJECTED), so the line cannot say it either.</para>
/// </summary>
internal static class AbortLog
{
    public static string Line(bool aborted, string source)
        => aborted ? $"Aborted ({source})." : $"Abort ignored — nothing playing ({source}).";

    /// <summary>The key or control, then what was in front when it fired, when that is known.</summary>
    public static string Source(string key, string? foreground)
        => foreground is null ? key : $"{key}; {foreground}";

    public static string StopMacro(string? caller) => $"StopMacro from {caller}";

    /// <summary>"foreground 'title' pid N process", or "no foreground window". A process that is
    /// gone by the time it is looked up reads "(process gone)". The title is kept to one line.</summary>
    public static string Foreground(IntPtr hwnd, string? title, int pid, string? process)
    {
        if (hwnd == IntPtr.Zero) return "no foreground window";
        var oneLine = (title ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return string.Create(CultureInfo.InvariantCulture, $"foreground '{oneLine}' pid {pid} {process ?? "(process gone)"}");
    }
}
