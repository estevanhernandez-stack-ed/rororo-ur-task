using System;
using System.IO;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>
/// A macro written by a newer Ur Task must be refused, not run. On 2026-09-29 an installed 0.8.0
/// (schema 3) was handed a schema-4 macro whose keys live in `steps`, a field 0.8.0 has never heard
/// of. It loaded as an empty macro, started playback and never finished — a keep-alive that looked
/// fine in the log and kept nobody alive. Refusing at load turns that into a named load failure.
/// </summary>
public class MacroSchemaVersionTests
{
    private static string MacroJson(int schemaVersion) => $$"""
    {
      "schemaVersion": {{schemaVersion}},
      "id": "a1000000-0000-4000-8000-000000000099",
      "name": "from the future",
      "recordMode": "PerWindow",
      "recordedAgainstUserId": 42,
      "recordedAgainstDisplayName": "Alt",
      "interAltDelayMs": null,
      "recordedAtUnixMs": 1750000000000,
      "coordSpace": "client",
      "events": []
    }
    """;

    [Fact]
    public void NewerSchema_IsRefused_WithAReasonThatSaysSo()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MacroV1Migrator.LoadAndMigrate(MacroJson(Macro.CurrentSchemaVersion + 1)));
        Assert.Contains("newer version of Ur Task", ex.Message);
        Assert.Contains($"schema {Macro.CurrentSchemaVersion + 1}", ex.Message);
    }

    [Fact]
    public void CurrentSchema_StillLoads()
    {
        var macro = MacroV1Migrator.LoadAndMigrate(MacroJson(Macro.CurrentSchemaVersion));
        Assert.Equal("from the future", macro.Name);
    }

    [Fact]
    public void NewerSchema_InTheStore_IsALoadFailure_AndTheRestStillLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "future.json"), MacroJson(Macro.CurrentSchemaVersion + 1));
            File.WriteAllText(Path.Combine(dir, "today.json"),
                MacroJson(Macro.CurrentSchemaVersion).Replace("a1000000-0000-4000-8000-000000000099",
                    "a1000000-0000-4000-8000-000000000098"));

            var result = new MacroStore(dir).LoadAll();

            Assert.Single(result.Macros);
            var failure = Assert.Single(result.Failures);
            Assert.EndsWith("future.json", failure.Path);
            Assert.Contains("newer version of Ur Task", failure.Reason);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
