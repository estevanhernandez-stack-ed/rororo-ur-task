using Labs626.UrTask.Hotkeys;

namespace Labs626.UrTask.Tests;

/// <summary>The abort lines name what did it (live 2026-09-30: 8 unexplained aborts, one killing the
/// ore-stop pulse mid-sweep with nobody at the keyboard).</summary>
public class AbortLogTests
{
    private const string Roblox = "foreground 'Roblox' pid 6808 RobloxPlayerBeta";

    [Fact]
    public void An_abort_names_the_key_and_the_foreground()
    {
        Assert.Equal("Aborted (Esc; foreground 'Roblox' pid 6808 RobloxPlayerBeta).",
            AbortLog.Line(true, AbortLog.Source("Esc", Roblox)));
        Assert.Equal("Aborted (Ctrl+Shift+F12; foreground 'Roblox' pid 6808 RobloxPlayerBeta).",
            AbortLog.Line(true, AbortLog.Source("Ctrl+Shift+F12", Roblox)));
    }

    [Fact]
    public void An_ignored_abort_names_them_too()
        => Assert.Equal("Abort ignored — nothing playing (Esc; foreground 'Roblox' pid 6808 RobloxPlayerBeta).",
            AbortLog.Line(false, AbortLog.Source("Esc", Roblox)));

    [Fact]
    public void A_bridge_stop_names_its_caller()
    {
        Assert.Equal("Aborted (StopMacro from 626labs.ur-ocr).", AbortLog.Line(true, AbortLog.StopMacro("626labs.ur-ocr")));
        Assert.Equal("Abort ignored — nothing playing (StopMacro from 626labs.ur-mcp).", AbortLog.Line(false, AbortLog.StopMacro("626labs.ur-mcp")));
    }

    [Fact]
    public void The_foreground_reads_as_title_pid_and_process()
    {
        Assert.Equal(Roblox, AbortLog.Foreground(new IntPtr(1), "Roblox", 6808, "RobloxPlayerBeta"));
        Assert.Equal("foreground '' pid 6808 (process gone)", AbortLog.Foreground(new IntPtr(1), "", 6808, null));
        Assert.Equal("no foreground window", AbortLog.Foreground(IntPtr.Zero, null, 0, null));
    }

    [Fact]
    public void A_title_is_kept_to_one_line()
        => Assert.Equal("foreground 'a b' pid 1 x", AbortLog.Foreground(new IntPtr(1), "a\r\nb", 1, "x"));

    [Fact]
    public void Without_a_foreground_the_source_is_the_key_alone()
        => Assert.Equal("Ur Task button", AbortLog.Source("Ur Task button", null));

    [Theory]
    [InlineData(3, "Esc")]
    [InlineData(4, "Ctrl+Shift+F12")]
    [InlineData(1, "Ctrl+Shift+R")]
    [InlineData(2, "Ctrl+Shift+P")]
    [InlineData(5, "Ctrl+Shift+L")]
    [InlineData(99, "hotkey 99")]
    public void Each_hotkey_id_has_its_key_name(int id, string name)
        => Assert.Equal(name, HotkeyService.KeyName(id));
}
