using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Thin Win32 implementation of <see cref="IWindowMetrics"/>. No logic beyond
/// marshalling — anything decision-shaped lives in WindowSpaceMath /
/// WindowArranger so it can be unit-tested with fakes.
/// </summary>
internal sealed class WindowMetrics : IWindowMetrics
{
    public IntPtr HwndForPid(int pid)
    {
        try { return Process.GetProcessById(pid).MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    public (int X, int Y)? ClientOrigin(IntPtr hwnd)
    {
        var pt = new POINT { x = 0, y = 0 };
        return ClientToScreen(hwnd, ref pt) ? (pt.x, pt.y) : null;
    }

    public (int W, int H)? ClientSize(IntPtr hwnd)
        => GetClientRect(hwnd, out var r) ? (r.right - r.left, r.bottom - r.top) : null;

    public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd)
        => GetWindowRect(hwnd, out var r) ? (r.left, r.top, r.right - r.left, r.bottom - r.top) : null;

    public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        => SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

    // ShowWindow returns the *previous* visibility, not success. For a resolvable
    // hwnd the call itself doesn't fail, so report true — callers already gate on
    // HwndForPid != Zero.
    public bool Minimize(IntPtr hwnd) { ShowWindow(hwnd, SW_MINIMIZE); return true; }

    public bool Restore(IntPtr hwnd) { ShowWindow(hwnd, SW_RESTORE); return true; }

    public void Maximize(IntPtr hwnd) => ShowWindow(hwnd, SW_MAXIMIZE);

    public void RestoreDown(IntPtr hwnd) => ShowWindow(hwnd, SW_RESTORE);

    public bool IsMaximized(IntPtr hwnd) => IsZoomed(hwnd);

    public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var wa = info.rcWork;
            return (wa.left, wa.top, wa.right - wa.left, wa.bottom - wa.top);
        }
        // Degenerate fallback: primary work area via SystemParametersInfo is more
        // interop for a case that only occurs when the window is gone — return a
        // conservative default instead.
        return (0, 0, 1920, 1080);
    }

    // ---------- Win32 interop ----------

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_MINIMIZE = 6;
    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
