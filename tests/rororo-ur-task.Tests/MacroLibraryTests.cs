using System;
using System.Collections.Generic;
using System.Linq;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>Search filter + group ordering for the macro library list.</summary>
public class MacroLibraryTests
{
    private static Macro M(string? name, string? gameName = null, long recordedAt = 1) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(),
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: recordedAt,
        Events: new List<MacroEvent>(),
        CoordSpace: Macro.CoordSpaceScreen,
        RecordedPlaceId: gameName is null ? null : 1,
        RecordedGameName: gameName);

    private static List<string?> Names(IEnumerable<Macro> macros) => macros.Select(m => m.Name).ToList();

    // ---------- search ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_EmptyQuery_PassesEverything(string? query)
    {
        var all = new[] { M("a"), M("b"), M(null) };

        Assert.Equal(3, MacroSearch.Filter(all, query).Count);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var all = new[] { M("PLZ | booth 3/3 | Submit + Yes"), M("ANY | jump (keep alive)") };

        Assert.Equal(new[] { "PLZ | booth 3/3 | Submit + Yes" }, Names(MacroSearch.Filter(all, "SUBMIT")));
    }

    [Fact]
    public void Search_EveryWordMustMatch_InAnyOrder()
    {
        var all = new[]
        {
            M("ANY | trade 8/8 | self-trade ok"),
            M("ANY | trade 5/8 | self-trade yes"),
            M("PLZ | booth 3/3 | Submit + Yes"),
        };

        Assert.Equal(new[] { "ANY | trade 8/8 | self-trade ok" }, Names(MacroSearch.Filter(all, "ok trade")));
        Assert.Empty(MacroSearch.Filter(all, "trade booth"));
    }

    [Fact]
    public void Search_MatchesTheRecordedGameName()
    {
        var all = new[] { M("farm", gameName: "Pet Simulator 99"), M("farm") };

        Assert.Single(MacroSearch.Filter(all, "simulator farm"));
    }

    [Fact]
    public void Search_MatchesTheAccountTag()
    {
        var all = new[]
        {
            M("HW Z4 | egg 6/6 | walk to egg [CECPapa] (from squared arrival)"),
            M("HW Z4 | egg 6/6 | walk to egg [main] (from squared arrival)"),
        };

        var hit = Assert.Single(MacroSearch.Filter(all, "cecpapa egg"));
        Assert.Contains("[CECPapa]", hit.Name);
    }

    [Fact]
    public void Search_UnnamedMacro_DoesNotThrow()
    {
        Assert.Empty(MacroSearch.Filter(new[] { M(null) }, "x"));
    }

    // ---------- ordering ----------

    [Fact]
    public void Groups_AreAlphabetical_WithUngroupedLast()
    {
        var groups = MacroLibrary.Group(new[]
        {
            M("Crystal"),
            M("PLZ | booth close picker (X)"),
            M("ANY | jump (keep alive)"),
            M("HW Z4 | egg 1/6 | Hatch Wars button"),
            M("alpha plain name"),
        });

        Assert.Equal(new[] { "ANY", "HW Z4", "PLZ", MacroName.UngroupedGroup }, groups.Select(g => g.Name).ToArray());
        Assert.Equal(2, groups[^1].Macros.Count);
    }

    [Fact]
    public void Groups_AreCaseInsensitive()
    {
        var groups = MacroLibrary.Group(new[] { M("PLZ | a"), M("plz | b") });

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Macros.Count);
    }

    [Fact]
    public void InsideAGroup_RouteStepsComeFirst_ByRouteThenStep_ThenTheRestByName()
    {
        var groups = MacroLibrary.Group(new[]
        {
            M("PLZ | booth skin (paintbrush, paste name, pick, Confirm) (clipboard = skin)", recordedAt: 9),
            M("PLZ | trader 1/4 | in plaza -> Pro spawn (re-teleport)", recordedAt: 8),
            M("PLZ | booth 3/3 | Submit + Yes", recordedAt: 7),
            M("PLZ | booth close picker (X)", recordedAt: 6),
            M("PLZ | booth 1/3 | Make Listing + paste search + pick + Confirm (clipboard = name)", recordedAt: 5),
            M("PLZ | booth 2/3 | paste price (clipboard = price)", recordedAt: 4),
        });

        var plz = Assert.Single(groups);
        Assert.Equal(new[]
        {
            "PLZ | booth 1/3 | Make Listing + paste search + pick + Confirm (clipboard = name)",
            "PLZ | booth 2/3 | paste price (clipboard = price)",
            "PLZ | booth 3/3 | Submit + Yes",
            "PLZ | trader 1/4 | in plaza -> Pro spawn (re-teleport)",
            "PLZ | booth close picker (X)",
            "PLZ | booth skin (paintbrush, paste name, pick, Confirm) (clipboard = skin)",
        }, Names(plz.Macros));
    }

    [Fact]
    public void StepsSortNumerically_NotAsText()
    {
        var groups = MacroLibrary.Group(new[] { M("A | r 10/12 | x"), M("A | r 2/12 | x"), M("A | r 1/12 | x") });

        Assert.Equal(new[] { "A | r 1/12 | x", "A | r 2/12 | x", "A | r 10/12 | x" }, Names(groups[0].Macros));
    }

    [Fact]
    public void SameStep_TiesBreakByName()
    {
        var groups = MacroLibrary.Group(new[]
        {
            M("HW Z4 | egg 3/6 | walk to boss pad [main]"),
            M("HW Z4 | egg 3/6 | fight only [main]"),
            M("HW Z4 | egg 2/6 | teleport"),
        });

        Assert.Equal(new[]
        {
            "HW Z4 | egg 2/6 | teleport",
            "HW Z4 | egg 3/6 | fight only [main]",
            "HW Z4 | egg 3/6 | walk to boss pad [main]",
        }, Names(groups[0].Macros));
    }

    [Fact]
    public void RouteWithoutStep_SortsAfterThatRoutesSteps()
    {
        var groups = MacroLibrary.Group(new[] { M("HW | egg | free roam"), M("HW | egg 1/2 | start") });

        Assert.Equal(new[] { "HW | egg 1/2 | start", "HW | egg | free roam" }, Names(groups[0].Macros));
    }

    [Fact]
    public void RecencyNoLongerDrivesOrder()
    {
        var groups = MacroLibrary.Group(new[] { M("Zeta", recordedAt: 100), M("alpha", recordedAt: 1) });

        Assert.Equal(new[] { "alpha", "Zeta" }, Names(groups[0].Macros));
    }

    [Fact]
    public void Empty_HasNoGroups()
    {
        Assert.Empty(MacroLibrary.Group(Array.Empty<Macro>()));
    }
}
