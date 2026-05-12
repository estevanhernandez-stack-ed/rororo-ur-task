using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Covers the 1:1 enforcement rule introduced in rc10: each macro pairs with
/// at most one alt; each alt pairs with at most one macro. Tests the pure
/// helper so we don't have to spin up PluginRuntime's gRPC + HotkeyService.
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
    public void ApplyAssignment_NewPair_SetsKey_NoDisplacement()
    {
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");

        var displaced = AssignmentMap.ApplyAssignment(map, altPid: 100, macro: m);

        Assert.Empty(displaced);
        Assert.Single(map);
        Assert.Equal(m, map[100]);
    }

    [Fact]
    public void ApplyAssignment_SameMacroOnDifferentAlt_DisplacesPriorAlt()
    {
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");

        // Initial: alt 100 → m
        AssignmentMap.ApplyAssignment(map, 100, m);

        // Now assign same macro to alt 200 — alt 100 should be displaced.
        var displaced = AssignmentMap.ApplyAssignment(map, 200, m);

        Assert.Single(displaced);
        Assert.Equal(100, displaced[0]);
        Assert.False(map.ContainsKey(100));
        Assert.Equal(m, map[200]);
    }

    [Fact]
    public void ApplyAssignment_DifferentMacroOnSameAlt_NoDisplacement_OverwritesAlt()
    {
        var map = new Dictionary<int, Macro?>();
        var m1 = Make("m1", "first");
        var m2 = Make("m2", "second");

        AssignmentMap.ApplyAssignment(map, 100, m1);
        var displaced = AssignmentMap.ApplyAssignment(map, 100, m2);

        // No other alt held m2, so nothing is displaced.
        // The prior macro on alt 100 (m1) is just overwritten — that's not "displacement"
        // because the macro itself is now unpaired but the slot wasn't on another alt.
        Assert.Empty(displaced);
        Assert.Single(map);
        Assert.Equal(m2, map[100]);
    }

    [Fact]
    public void ApplyAssignment_SameMacroSameAlt_NoOp()
    {
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");

        AssignmentMap.ApplyAssignment(map, 100, m);
        var displaced = AssignmentMap.ApplyAssignment(map, 100, m);

        Assert.Empty(displaced);
        Assert.Single(map);
        Assert.Equal(m, map[100]);
    }

    [Fact]
    public void ApplyAssignment_NullMacro_ClearsAlt_NoDisplacement()
    {
        var map = new Dictionary<int, Macro?>();
        var m = Make("m1");
        AssignmentMap.ApplyAssignment(map, 100, m);

        var displaced = AssignmentMap.ApplyAssignment(map, 100, macro: null);

        Assert.Empty(displaced);
        Assert.Empty(map);
    }

    [Fact]
    public void ApplyAssignment_MacroIdMatchAcrossInstances_StillDisplaces()
    {
        // Two distinct Macro records with the same Id should still be treated as
        // the same macro for displacement (Id is the identity, not reference).
        var map = new Dictionary<int, Macro?>();
        var original = Make("m1");
        var copy = Make("m1"); // same Id, different reference

        AssignmentMap.ApplyAssignment(map, 100, original);
        var displaced = AssignmentMap.ApplyAssignment(map, 200, copy);

        Assert.Single(displaced);
        Assert.Equal(100, displaced[0]);
        Assert.False(map.ContainsKey(100));
        Assert.Equal(copy, map[200]);
    }

    [Fact]
    public void ApplyAssignment_TwoMacrosTwoAlts_BothPersist()
    {
        // Sanity check that different macros happily coexist across different alts —
        // 1:1 only fires when the SAME macro is reassigned.
        var map = new Dictionary<int, Macro?>();
        var m1 = Make("m1");
        var m2 = Make("m2");

        AssignmentMap.ApplyAssignment(map, 100, m1);
        var displaced = AssignmentMap.ApplyAssignment(map, 200, m2);

        Assert.Empty(displaced);
        Assert.Equal(2, map.Count);
        Assert.Equal(m1, map[100]);
        Assert.Equal(m2, map[200]);
    }
}
