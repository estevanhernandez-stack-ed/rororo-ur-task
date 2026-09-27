using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.PluginHost;

/// <summary>Reads what is on screen over a window's client area. It sees whatever is on top,
/// so callers bring the target to the front first. Null = window gone, minimised, or capture failed.</summary>
public interface IScreenSampler
{
    PixelBlock? Capture(IntPtr hwnd, int clientX, int clientY, int w, int h);
}
