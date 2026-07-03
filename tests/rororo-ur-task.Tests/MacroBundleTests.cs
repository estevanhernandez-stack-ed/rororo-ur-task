using System;
using System.Collections.Generic;
using System.Linq;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MacroBundleTests
{
    private static Macro MakeMacro(string name, string? coordSpace = null) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(),
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: 42,
        RecordedAgainstDisplayName: "Goldnail8",
        InterAltDelayMs: 250,
        RecordedAtUnixMs: 1750000000000,
        Events: new List<MacroEvent>
        {
            new(TimestampMs: 10, Kind: MacroEventKind.KeyDown, VirtualKeyCode: 65, X: 0, Y: 0, MouseButton: 0, WheelDelta: 0),
            new(TimestampMs: 90, Kind: MacroEventKind.KeyUp, VirtualKeyCode: 65, X: 0, Y: 0, MouseButton: 0, WheelDelta: 0),
        },
        CoordSpace: coordSpace ?? Macro.CoordSpaceScreen,
        RecordedClientW: coordSpace == Macro.CoordSpaceClient ? 816 : null,
        RecordedClientH: coordSpace == Macro.CoordSpaceClient ? 638 : null);

    [Fact]
    public void RoundTrip_ExportThenParse_PreservesMacros()
    {
        var macros = new List<Macro> { MakeMacro("farm loop"), MakeMacro("craft cycle", Macro.CoordSpaceClient) };

        var json = MacroBundle.Serialize(macros, exportedAtUnixMs: 1751500000000);
        var result = MacroBundle.Parse(json);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Macros.Count);
        Assert.Contains("\"bundleVersion\": 1", json);

        var farm = result.Macros.Single(m => m.Name == "farm loop");
        Assert.Equal(2, farm.Events.Count);
        Assert.Equal(MacroEventKind.KeyDown, farm.Events[0].Kind);
        Assert.Equal(250, farm.InterAltDelayMs);

        var craft = result.Macros.Single(m => m.Name == "craft cycle");
        Assert.True(craft.IsClientSpace);
        Assert.Equal(816, craft.RecordedClientW);
        Assert.Equal(638, craft.RecordedClientH);
    }

    [Fact]
    public void Parse_BundleWithV1Entry_MigratesToV3()
    {
        // A friend on v0.1 hand-builds a bundle around their old macro file —
        // entries go through the same migrator as the on-disk store.
        const string bundle = """
        {
          "bundleVersion": 1,
          "macros": [
            {
              "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
              "name": "old macro",
              "boundUserId": 7,
              "boundDisplayName": "OldAlt",
              "recordedAtUnixMs": 1740000000000,
              "events": []
            }
          ]
        }
        """;

        var result = MacroBundle.Parse(bundle);

        Assert.Empty(result.Failures);
        var macro = Assert.Single(result.Macros);
        Assert.Equal(Macro.CurrentSchemaVersion, macro.SchemaVersion);
        Assert.Equal(Macro.CoordSpaceScreen, macro.CoordSpace);
        Assert.Equal(7, macro.RecordedAgainstUserId);
        Assert.Equal("OldAlt", macro.RecordedAgainstDisplayName);
    }

    [Fact]
    public void Parse_BareSingleMacroFile_Imports()
    {
        // Someone shares a file straight out of their macros folder — no bundle
        // envelope. Parse treats a root object without "macros" as one macro.
        const string single = """
        {
          "schemaVersion": 2,
          "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
          "name": "shared solo",
          "recordMode": "PerWindow",
          "recordedAtUnixMs": 1750000000000,
          "events": []
        }
        """;

        var result = MacroBundle.Parse(single);

        Assert.Empty(result.Failures);
        var macro = Assert.Single(result.Macros);
        Assert.Equal("shared solo", macro.Name);
        Assert.Equal(Macro.CurrentSchemaVersion, macro.SchemaVersion);
    }

    [Fact]
    public void Parse_BrokenEntry_FailsAloneOthersImport()
    {
        const string bundle = """
        {
          "bundleVersion": 1,
          "macros": [
            { "name": "broken one" },
            {
              "schemaVersion": 2,
              "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
              "name": "good one",
              "recordMode": "PerWindow",
              "recordedAtUnixMs": 1750000000000,
              "events": []
            }
          ]
        }
        """;

        var result = MacroBundle.Parse(bundle);

        var macro = Assert.Single(result.Macros);
        Assert.Equal("good one", macro.Name);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("broken one", failure.Label);
    }

    [Fact]
    public void Parse_NotJson_ReturnsSingleFailure()
    {
        var result = MacroBundle.Parse("this is not json");
        Assert.Empty(result.Macros);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void PrepareForImport_MintsNewGuid_AndDedupesName()
    {
        var incoming = MakeMacro("farm loop");
        var originalId = incoming.Id;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Farm Loop" };

        var first = MacroBundle.PrepareForImport(incoming, taken);
        Assert.NotEqual(originalId, first.Id);
        Assert.True(Guid.TryParse(first.Id, out _)); // MacroStore.PathFor requires Guid ids
        Assert.Equal("farm loop (2)", first.Name);

        // Importing the same macro again keeps walking the suffix.
        var second = MacroBundle.PrepareForImport(incoming, taken);
        Assert.Equal("farm loop (3)", second.Name);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void PrepareForImport_UnnamedMacro_PassesThrough()
    {
        var incoming = MakeMacro("x") with { Name = null };
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var prepared = MacroBundle.PrepareForImport(incoming, taken);

        Assert.Null(prepared.Name);
        Assert.Empty(taken);
    }
}
