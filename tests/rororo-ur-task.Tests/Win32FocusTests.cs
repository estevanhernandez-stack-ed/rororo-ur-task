using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class Win32FocusTests
{
    /// <summary>
    /// Guard for the seam the cadence runner depends on: a "no prior foreground was
    /// captured" sentinel (IntPtr.Zero) must never be treated as a real window handle
    /// to restore. RestoreForeground short-circuits to false without touching Win32.
    /// </summary>
    [Fact]
    public void RestoreForeground_ZeroHandle_ReturnsFalse()
    {
        Assert.False(Win32Focus.RestoreForeground(IntPtr.Zero));
    }
}
