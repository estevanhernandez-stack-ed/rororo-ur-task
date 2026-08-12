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

    /// <summary>
    /// CRITICAL 2 (load-time half): MacroEvent.TimestampMs is a bare long off
    /// user-editable (or corrupted) on-disk JSON, with no schema-level bound. A
    /// value above int.MaxValue ms (~24.8 days) or negative used to reach
    /// MacroPlayer's playback loop unclamped and throw
    /// ArgumentOutOfRangeException off the `(int)wait` cast — see
    /// MacroPlayerClientSpaceTests.ScreenMacro_PathologicallyLargeTimestamp...
    /// for the downstream half of this defense-in-depth pair. Sanitizing here, at
    /// the one place every macro file passes through on its way into memory,
    /// means a hand-edited or corrupted macro can never reach ANY playback path
    /// with an out-of-range timestamp, regardless of which one reads it.
    ///
    /// Verified by hand: reverting MacroV1Migrator's SanitizeTimestamps call on
    /// the v2+ path turns both assertions below RED — the loaded macro carries
    /// the raw -500 and 5,000,000,000 values straight through unclamped.
    /// </summary>
    [Fact]
    public void V2Macro_PathologicalTimestamps_ClampedOnLoad()
    {
        var json = """
        {
          "schemaVersion": 2,
          "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
          "name": "pathological",
          "recordMode": "PerWindow",
          "recordedAgainstUserId": null,
          "recordedAgainstDisplayName": null,
          "interAltDelayMs": null,
          "recordedAtUnixMs": 1750000000000,
          "events": [
            { "timestampMs": -500, "kind": "KeyDown", "virtualKeyCode": 32, "x": 0, "y": 0, "mouseButton": 0, "wheelDelta": 0 },
            { "timestampMs": 5000000000, "kind": "KeyUp", "virtualKeyCode": 32, "x": 0, "y": 0, "mouseButton": 0, "wheelDelta": 0 }
          ]
        }
        """;

        var macro = MacroV1Migrator.LoadAndMigrate(json);

        Assert.Equal(2, macro.Events.Count);
        Assert.Equal(0L, macro.Events[0].TimestampMs);                  // negative clamped up to 0
        Assert.Equal((long)int.MaxValue, macro.Events[1].TimestampMs);  // absurd clamped down to int.MaxValue
    }

    /// <summary>
    /// Same defense-in-depth clamp, but through the v1 migration branch — a
    /// separate code path (manually-built events list, not a straight Macro
    /// deserialize) that calls the same SanitizeTimestamps helper independently.
    /// Verified by hand: reverting the v1 branch's SanitizeTimestamps call turns
    /// both assertions below RED.
    /// </summary>
    [Fact]
    public void V1Macro_PathologicalTimestamps_ClampedOnLoad()
    {
        var json = """
        {
          "schemaVersion": 1,
          "id": "11111111-2222-3333-4444-555555555555",
          "name": "pathological-v1",
          "boundUserId": 1,
          "boundDisplayName": "x",
          "recordedAtUnixMs": 1746000000000,
          "events": [
            { "kind": 0, "timestampMs": -1, "virtualKeyCode": 32 },
            { "kind": 1, "timestampMs": 9999999999, "virtualKeyCode": 32 }
          ]
        }
        """;

        var macro = MacroV1Migrator.LoadAndMigrate(json);

        Assert.Equal(2, macro.Events.Count);
        Assert.Equal(0L, macro.Events[0].TimestampMs);
        Assert.Equal((long)int.MaxValue, macro.Events[1].TimestampMs);
    }
}
