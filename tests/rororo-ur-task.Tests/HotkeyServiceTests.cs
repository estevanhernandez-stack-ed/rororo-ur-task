using Labs626.UrTask.Hotkeys;

namespace Labs626.UrTask.Tests;

public class HotkeyServiceTests
{
    [Fact]
    public void Start_RegistersChordHotkeysAndDisposeCleansUp()
    {
        var svc = new HotkeyService();
        // Start should not throw — registers Ctrl+Shift+R, Ctrl+Shift+P,
        // Ctrl+Shift+L, Ctrl+Shift+A. Bare Esc is registered on demand, not here.
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
    public void ChordHotkeyVkCodes_ContainsAbortChord_A()
    {
        // Ctrl+Shift+A is the always-on abort hotkey — the recorder must filter
        // it like the other chords so it isn't baked into a macro.
        Assert.Contains(0x41, HotkeyService.ChordHotkeyVkCodes); // VK_A
    }

    [Fact]
    public void ChordHotkeyVkCodes_ContainsRunRoutineChord_L()
    {
        // Ctrl+Shift+L runs the selected routine (recipe/loadout) — the
        // recorder must filter it like the other chords so it isn't baked
        // into a macro.
        Assert.Contains(0x4C, HotkeyService.ChordHotkeyVkCodes); // VK_L
    }

    [Fact]
    public void AbortVkCode_IsEscape()
    {
        Assert.Equal(0x1B, HotkeyService.AbortVkCode);
    }

    [Fact]
    public void EnableAndDisableAbortKey_AfterStart_AreIdempotentAndDoNotThrow()
    {
        using var svc = new HotkeyService();
        svc.Start();

        // Bare Esc is NOT registered at start — only while a macro is playing.
        // Enabling registers it on the pump thread; both calls are idempotent
        // and must never throw regardless of current registration state.
        svc.EnableAbortKey();
        svc.EnableAbortKey();
        svc.DisableAbortKey();
        svc.DisableAbortKey();
    }

    [Fact]
    public void EnableDisableAbortKey_BeforeStart_AreNoOps()
    {
        using var svc = new HotkeyService();
        // No pump thread yet — calls are safely ignored, never throw.
        svc.EnableAbortKey();
        svc.DisableAbortKey();
    }
}
