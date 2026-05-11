using Labs626.UrTask.Hotkeys;

namespace Labs626.UrTask.Tests;

public class HotkeyServiceTests
{
    [Fact]
    public void Start_RegistersChordHotkeysAndDisposeCleansUp()
    {
        var svc = new HotkeyService();
        // Start should not throw — registers Ctrl+Shift+R, Ctrl+Shift+P, Esc.
        svc.Start();

        try
        {
            // Dispose should unregister cleanly. Re-starting after dispose should also work.
            svc.Dispose();
            using var svc2 = new HotkeyService();
            svc2.Start();
        }
        finally
        {
            svc.Dispose();
        }
    }

    [Fact]
    public void ChordHotkeyVkCodes_ContainsRAndP()
    {
        Assert.Contains(0x52, HotkeyService.ChordHotkeyVkCodes); // VK_R
        Assert.Contains(0x50, HotkeyService.ChordHotkeyVkCodes); // VK_P
        Assert.DoesNotContain(0x77, HotkeyService.ChordHotkeyVkCodes); // VK_F8 — retired
    }

    [Fact]
    public void AbortVkCode_IsEscape()
    {
        Assert.Equal(0x1B, HotkeyService.AbortVkCode);
    }
}
