namespace Labs626.UrTask.PluginHost;

/// <summary>Windows display scale for the monitor hosting a window, in percent (100, 125, 150…).</summary>
public interface IDisplayScale
{
    int ScalePercentFor(IntPtr hwnd);
}
