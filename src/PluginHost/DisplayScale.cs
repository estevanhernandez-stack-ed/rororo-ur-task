using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>GetDpiForWindow; the process is PerMonitorV2-aware (app.manifest), so this is the
/// window's own monitor. Falls back to 100 when the window is gone.</summary>
internal sealed class DisplayScale : IDisplayScale
{
    public int ScalePercentFor(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 0u : GetDpiForWindow(hwnd);
        return dpi == 0 ? 100 : (int)Math.Round(dpi * 100 / 96.0);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
