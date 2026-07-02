using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MacroV3MigrationTests
{
    private const string V2MouseMacroJson = """
    {
      "schemaVersion": 2,
      "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
      "name": "old mouse macro",
      "recordMode": "PerWindow",
      "recordedAgainstUserId": 42,
      "recordedAgainstDisplayName": "Goldnail8",
      "interAltDelayMs": null,
      "recordedAtUnixMs": 1750000000000,
      "events": [
        { "timestampMs": 10, "kind": "MouseDown", "virtualKeyCode": 0, "x": 500, "y": 600, "mouseButton": 1, "wheelDelta": 0 }
      ]
    }
    """;

    [Fact]
    public void V2Macro_MigratesToV3_ScreenSpace()
    {
        var macro = MacroV1Migrator.LoadAndMigrate(V2MouseMacroJson);
        Assert.Equal(3, macro.SchemaVersion);
        Assert.Equal(Macro.CoordSpaceScreen, macro.CoordSpace);
        Assert.False(macro.IsClientSpace);
        Assert.Null(macro.RecordedClientW);
        Assert.Single(macro.Events);
        Assert.Equal(500, macro.Events[0].X); // absolute coords untouched
    }

    [Fact]
    public void V3ClientMacro_RoundTripsThroughStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        var store = new MacroStore(dir);
        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: "client macro",
            RecordMode: "PerWindow",
            RecordedAgainstUserId: null,
            RecordedAgainstDisplayName: null,
            InterAltDelayMs: null,
            RecordedAtUnixMs: 1,
            Events: new List<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceClient,
            RecordedClientW: 816,
            RecordedClientH: 638);
        store.Save(macro);

        var loaded = store.LoadAll();
        Assert.Empty(loaded.Failures);
        var back = Assert.Single(loaded.Macros);
        Assert.True(back.IsClientSpace);
        Assert.Equal(816, back.RecordedClientW);
        Assert.Equal(638, back.RecordedClientH);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ScreenSpaceMacro_SerializesWithoutClientSizeFields()
    {
        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: null, RecordMode: "PerWindow",
            RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
            InterAltDelayMs: null, RecordedAtUnixMs: 1,
            Events: new List<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceScreen);
        var json = JsonSerializer.Serialize(macro, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        Assert.DoesNotContain("recordedClientW", json);
        Assert.DoesNotContain("recordedClientH", json);
        Assert.Contains("\"coordSpace\":\"screen\"", json.Replace(" ", ""));
    }

    [Fact]
    public void V3Json_MissingCoordSpace_DefaultsToScreen()
    {
        // Hand-edited or partial v3 file: coordSpace absent must not crash and must
        // default to screen so playback takes the legacy path.
        var json = V2MouseMacroJson.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3");
        var macro = MacroV1Migrator.LoadAndMigrate(json);
        Assert.Equal(Macro.CoordSpaceScreen, macro.CoordSpace);
    }
}
