using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class CadenceRoleTests
{
    // AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId,
    //             long PlaceId = 0, string PlaceName = "")
    private static AccountRegistry.AccountInfo Alt(int pid = 1, long userId = 100)
        => new(pid, userId, "Alt", $"acct-{pid}");

    private static Macro NewMacro() => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: "m",
        RecordMode: "PerWindow", RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    [Fact]
    public void WithDerivedRole_MacroPresent_IsActive()
    {
        var a = Assignment.WithDerivedRole(Alt(), NewMacro());
        Assert.Equal(CadenceRole.Active, a.Role);
    }

    /// The load-bearing one: a no-macro assignment means "just keep it alive."
    /// If this ever comes back Active it gets spun back-to-back every ~1.25s,
    /// which is precisely the bug the cadence scheduler exists to kill.
    [Fact]
    public void WithDerivedRole_NoMacro_IsKeepAlive()
    {
        var a = Assignment.WithDerivedRole(Alt(), macro: null);
        Assert.Equal(CadenceRole.KeepAlive, a.Role);
        Assert.Null(a.Macro);
    }

    [Fact]
    public void ExplicitRole_IsHonoured_AndMacroSurvivesBackgrounding()
    {
        // Backgrounding must NOT be destructive — the macro is preserved, paused.
        var macro = NewMacro();
        var a = new Assignment(Alt(), macro, CadenceRole.KeepAlive);
        Assert.Equal(CadenceRole.KeepAlive, a.Role);
        Assert.Same(macro, a.Macro);
    }
}
