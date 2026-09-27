using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>Monitor effective DPI (MonitorFromWindow + shcore GetDpiForMonitor) for the monitor
/// hosting a window. This reads the MONITOR's scale, not the target window's own DPI awareness —
/// GetDpiForWindow would instead report the window's per-awareness virtualized DPI, which reads
/// 96 (100%) for a DPI-unaware window (Roblox's awareness is unverified) even on a scaled monitor.
/// Falls back to 100 when the window is gone, the monitor lookup fails, or GetDpiForMonitor
/// returns a non-zero HRESULT.</summary>
internal sealed class DisplayScale : IDisplayScale
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    public int ScalePercentFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 100;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return 100;
        int hr = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _);
        if (hr != 0 || dpiX == 0) return 100;
        return (int)Math.Round(dpiX * 100 / 96.0);
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
