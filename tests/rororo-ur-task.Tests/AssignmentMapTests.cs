using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Covers the pairing model: each ALT pairs with at most one macro, but each
/// MACRO can be paired with multiple alts. Same macro running on alt 1 + alt 2
/// + alt 3 in the round-robin is the expected case. Tests the pure helper so
/// we don't have to spin up PluginRuntime's gRPC + HotkeyService.
/// </summary>
public class AssignmentMapTests
{
    private static Macro Make(string id, string name = "m") => new(
        SchemaVersion: 2,
        Id: id,
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: 0,
        Events: new List<MacroEvent>());

    [Fact]
    public void ApplyAssignment_NewPair_SetsKey()
    {
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");

        AssignmentMap.ApplyAssignment(map, altPid: 100, macro: m);

        Assert.Single(map);
        Assert.Equal(m, map[100]);
    }

    [Fact]
    public void ApplyAssignment_SameMacroOnDifferentAlt_BothAltsHoldIt()
    {
        // One-to-many: assigning the same macro to a second alt doesn't displace
        // the first alt. Both alts now play the same macro in the round-robin.
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");

        AssignmentMap.ApplyAssignment(map, 100, m);
        AssignmentMap.ApplyAssignment(map, 200, m);

        Assert.Equal(2, map.Count);
        Assert.Equal(m, map[100]);
        Assert.Equal(m, map[200]);
    }

    [Fact]
    public void ApplyAssignment_DifferentMacroOnSameAlt_OverwritesAlt()
    {
        // Each alt holds at most one macro. Assigning macro N to alt A when it
        // held macro M just overwrites — M is unpaired from A (but stays paired
        // with any OTHER alt that holds it).
        var map = new Dictionary<int, Macro?>();
        var m1 = Make("m1", "first");
        var m2 = Make("m2", "second");

        AssignmentMap.ApplyAssignment(map, 100, m1);
        AssignmentMap.ApplyAssignment(map, 100, m2);

        Assert.Single(map);
        Assert.Equal(m2, map[100]);
    }

    [Fact]
    public void ApplyAssignment_SameMacroSameAlt_NoOp()
    {
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");

        AssignmentMap.ApplyAssignment(map, 100, m);
        AssignmentMap.ApplyAssignment(map, 100, m);

        Assert.Single(map);
        Assert.Equal(m, map[100]);
    }

    [Fact]
    public void ApplyAssignment_NullMacro_ClearsAlt()
    {
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");
        AssignmentMap.ApplyAssignment(map, 100, m);

        AssignmentMap.ApplyAssignment(map, 100, macro: null);

        Assert.Empty(map);
    }

    [Fact]
    public void ApplyAssignment_OneMacroSpreadAcrossManyAlts_AllPersist()
    {
        // Stress case: same macro on 5 alts. All should remain paired.
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");

        for (int pid = 100; pid < 105; pid++)
        {
            AssignmentMap.ApplyAssignment(map, pid, m);
        }

        Assert.Equal(5, map.Count);
        Assert.All(map.Values, v => Assert.Equal(m, v));
    }

    [Fact]
    public void ApplyAssignment_TwoMacrosTwoAlts_BothPersist()
    {
        // Sanity check: different macros on different alts coexist.
        var map = new Dictionary<int, Macro?>();
        var m1 = Make("m1");
        var m2 = Make("m2");

        AssignmentMap.ApplyAssignment(map, 100, m1);
        AssignmentMap.ApplyAssignment(map, 200, m2);

        Assert.Equal(2, map.Count);
        Assert.Equal(m1, map[100]);
        Assert.Equal(m2, map[200]);
    }
}
