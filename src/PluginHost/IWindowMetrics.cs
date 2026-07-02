namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Window geometry seam for client-space recording/playback and window
/// arranging. All coordinates are physical pixels (the process is
/// PerMonitorV2 DPI-aware — see app.manifest). Null returns mean the
/// window is gone or the Win32 call failed; callers treat that as
/// refuse/skip, never crash.
/// </summary>
public interface IWindowMetrics
{
    /// <summary>Main window handle for a pid; <see cref="IntPtr.Zero"/> when unresolvable.</summary>
    IntPtr HwndForPid(int pid);

    /// <summary>Screen position of the window's client (0,0).</summary>
    (int X, int Y)? ClientOrigin(IntPtr hwnd);

    /// <summary>Client-area size in physical pixels.</summary>
    (int W, int H)? ClientSize(IntPtr hwnd);

    /// <summary>Outer window rect (position + size).</summary>
    (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd);

    /// <summary>Move/resize the outer rect. Returns false on Win32 failure.</summary>
    bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h);

    /// <summary>Work area (taskbar-excluded) of the monitor hosting the window.</summary>
    (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd);
}
