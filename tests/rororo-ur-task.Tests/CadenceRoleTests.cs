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

    // ---------------------------------------------------------------------------
    // Assignment.ResolveRole — the pure function both Task-8 Criticals live in.
    // PluginRuntime's PLAY-time assignment build and RecorderViewModel's row
    // display both read off this exact function, so these tests cover the real
    // regressions directly rather than through the two call sites (neither
    // PluginRuntime nor RecorderViewModel is unit-test seamed).
    // ---------------------------------------------------------------------------

    /// <summary>
    /// CRITICAL 1: a fresh alt with no explicit role choice yet, the instant it
    /// gets a macro assigned, must resolve to Active — this is exactly what makes
    /// "assign a macro, press PLAY" farm it. Before the fix, row construction
    /// wrote a KeepAlive override before any macro existed, and nothing ever
    /// re-derived Role when the macro showed up later — this alone doesn't catch
    /// that (it's a UI wiring bug, not a ResolveRole bug), but it pins the truth
    /// table RecorderViewModel.SeedRowRole and PluginRuntime's assignment build
    /// are both supposed to honor.
    /// </summary>
    [Fact]
    public void ResolveRole_NoOverride_MacroAssigned_IsActive()
    {
        var role = Assignment.ResolveRole(NewMacro(), userOverride: null);
        Assert.Equal(CadenceRole.Active, role);
    }

    [Fact]
    public void ResolveRole_NoOverride_NoMacro_IsKeepAlive()
    {
        var role = Assignment.ResolveRole(macro: null, userOverride: null);
        Assert.Equal(CadenceRole.KeepAlive, role);
    }

    /// <summary>Non-destructive backgrounding: an explicit KeepAlive choice with a
    /// macro still assigned must stay KeepAlive — the macro pauses, it isn't
    /// dropped.</summary>
    [Fact]
    public void ResolveRole_ExplicitKeepAlive_MacroPresent_StaysKeepAlive()
    {
        var role = Assignment.ResolveRole(NewMacro(), CadenceRole.KeepAlive);
        Assert.Equal(CadenceRole.KeepAlive, role);
    }

    /// <summary>
    /// CRITICAL 2, the load-bearing case: an explicit Active choice can never
    /// survive a null macro. This is what makes the foreground-hijack loop
    /// unreachable — without this coercion, CadenceScheduler.Decide has no
    /// deadline gate for Active, so a macro-less "Active" alt gets refocused
    /// continuously (~1s per attempt) forever, farms nothing, and never gets a
    /// keep-alive Space either.
    /// </summary>
    [Fact]
    public void ResolveRole_ExplicitActive_NoMacro_IsCoercedToKeepAlive()
    {
        var role = Assignment.ResolveRole(macro: null, userOverride: CadenceRole.Active);
        Assert.Equal(CadenceRole.KeepAlive, role);
    }

    /// <summary>Round-trip: backgrounding and resuming an alt must be perfectly
    /// reversible — the same macro rides along through both flips, and Active
    /// resumes farming without anything being re-picked.</summary>
    [Fact]
    public void ResolveRole_RoundTrip_ActiveToKeepAliveToActive_MacroSurvivesThroughout()
    {
        var macro = NewMacro();
        var alt = Alt();

        var active = new Assignment(alt, macro, Assignment.ResolveRole(macro, CadenceRole.Active));
        var backgrounded = active with { Role = Assignment.ResolveRole(macro, CadenceRole.KeepAlive) };
        var resumed = backgrounded with { Role = Assignment.ResolveRole(macro, CadenceRole.Active) };

        Assert.Equal(CadenceRole.Active, active.Role);
        Assert.Equal(CadenceRole.KeepAlive, backgrounded.Role);
        Assert.Equal(CadenceRole.Active, resumed.Role);
        Assert.Same(macro, backgrounded.Macro);
        Assert.Same(macro, resumed.Macro);
    }
}
