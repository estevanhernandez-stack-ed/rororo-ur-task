using System;
using System.Collections.Generic;
using System.Linq;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MacroBundleTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
    }

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

    // ---------- L5-03: import has limits ----------
    // A 7 MB bundle of 100,000 empty macros froze the window on import, then slowed every
    // later library load. Each limit refuses with one plain sentence for the activity log.

    private static string EntryJson(string name, string body = "\"events\": []") => $$"""
        { "schemaVersion": 4, "id": "{{Guid.NewGuid()}}", "name": "{{name}}", "recordMode": "PerWindow",
          "recordedAtUnixMs": 1, "coordSpace": "screen", {{body}} }
        """;

    private static string BundleJson(IEnumerable<string> entries)
        => "{ \"bundleVersion\": 1, \"macros\": [" + string.Join(",", entries) + "] }";

    private static string Repeat(string item, int count) => string.Join(",", Enumerable.Repeat(item, count));

    private string TempFile(string name)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return System.IO.Path.Combine(dir, name);
    }

    [Fact]
    public void FileSizeProblem_NullUpToTheLimit_ThenOneSentence()
    {
        Assert.Null(MacroBundle.FileSizeProblem("share.json", 0));
        Assert.Null(MacroBundle.FileSizeProblem("share.json", MacroBundle.MaxImportFileBytes));

        Assert.Equal("share.json is too large to import (20.1 MB; the limit is 20 MB).",
            MacroBundle.FileSizeProblem("share.json", MacroBundle.MaxImportFileBytes + 1));
        Assert.Equal("share.json is too large to import (150 MB; the limit is 20 MB).",
            MacroBundle.FileSizeProblem("share.json", 150L * 1024 * 1024));
    }

    [Fact]
    public void ReadForImport_FileOverTheSizeLimit_IsRefusedBeforeItIsRead()
    {
        var path = TempFile("huge.json");
        using (var fs = System.IO.File.Create(path)) fs.SetLength(MacroBundle.MaxImportFileBytes + 1);

        var file = MacroBundle.ReadForImport(path);

        Assert.Empty(file.Macros);
        // Not "Not valid JSON": the file was never opened, only measured.
        Assert.Equal("huge.json is too large to import (20.1 MB; the limit is 20 MB).", Assert.Single(file.Log));
    }

    [Fact]
    public void Parse_BundleAtTheMacroLimit_Imports()
    {
        var result = MacroBundle.Parse(BundleJson(Enumerable.Range(0, MacroBundle.MaxMacrosPerBundle).Select(i => EntryJson($"m{i}"))));

        Assert.Empty(result.Failures);
        Assert.Equal(MacroBundle.MaxMacrosPerBundle, result.Macros.Count);
    }

    [Fact]
    public void Parse_BundleOverTheMacroLimit_IsRefusedWhole()
    {
        var result = MacroBundle.Parse(BundleJson(Enumerable.Range(0, MacroBundle.MaxMacrosPerBundle + 1).Select(i => EntryJson($"m{i}"))));

        Assert.Empty(result.Macros);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(MacroBundle.WholeFileLabel, failure.Label);
        Assert.Equal("It holds 501 macros, which is too many to import at once (the limit is 500).", failure.Reason);
    }

    [Fact]
    public void Parse_MacroWithTooManyEvents_FailsAlone_TheRestImport()
    {
        var tooMany = EntryJson("endless", $"\"events\": [{Repeat("{\"kind\":0}", MacroBundle.MaxEventsPerMacro + 1)}]");

        var result = MacroBundle.Parse(BundleJson(new[] { EntryJson("fine"), tooMany, EntryJson("also fine") }));

        Assert.Equal(new[] { "fine", "also fine" }, result.Macros.Select(m => m.Name).ToArray());
        var failure = Assert.Single(result.Failures);
        Assert.Equal("endless", failure.Label);
        Assert.Equal("It has 200,001 events, which is too many to import (the limit is 200,000).", failure.Reason);
    }

    [Fact]
    public void Parse_MacroWithTooManySteps_FailsAlone_TheRestImport()
    {
        var tooMany = EntryJson("endless", $"\"events\": [], \"steps\": [{Repeat("{\"kind\":\"wait\",\"delayMs\":1}", MacroBundle.MaxStepsPerMacro + 1)}]");

        var result = MacroBundle.Parse(BundleJson(new[] { tooMany, EntryJson("fine") }));

        Assert.Equal("fine", Assert.Single(result.Macros).Name);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("endless", failure.Label);
        Assert.Equal("It has 5,001 steps, which is too many to import (the limit is 5,000).", failure.Reason);
    }

    [Fact]
    public void Parse_BareSingleMacroOverALimit_IsRefused()
    {
        var result = MacroBundle.Parse(EntryJson("endless", $"\"events\": [], \"steps\": [{Repeat("{\"kind\":\"wait\",\"delayMs\":1}", MacroBundle.MaxStepsPerMacro + 1)}]"));

        Assert.Empty(result.Macros);
        Assert.Contains("5,001 steps", Assert.Single(result.Failures).Reason);
    }

    [Fact]
    public void SizeProblem_AtTheLimits_IsNull_AndRawStepEventsCountAsEvents()
    {
        var e = new MacroEvent(0, MacroEventKind.KeyDown, 65, 0, 0, 0, 0);
        var atLimit = MakeMacro("x") with
        {
            Events = Enumerable.Repeat(e, MacroBundle.MaxEventsPerMacro).ToList(),
            Steps = Enumerable.Repeat<Labs626.UrTask.Macros.Steps.MacroStep>(
                new Labs626.UrTask.Macros.Steps.WaitStep(1), MacroBundle.MaxStepsPerMacro).ToList(),
        };
        Assert.Null(MacroBundle.SizeProblem(atLimit));

        // A raw step replays an event list of its own; hiding the events there must not get past the limit.
        var hidden = MakeMacro("x") with
        {
            Events = new List<MacroEvent>(),
            Steps = new Labs626.UrTask.Macros.Steps.MacroStep[]
            {
                new Labs626.UrTask.Macros.Steps.RawStep(0, Enumerable.Repeat(e, MacroBundle.MaxEventsPerMacro).ToList(), "a"),
                new Labs626.UrTask.Macros.Steps.RawStep(0, new[] { e }, "b"),
            },
        };
        Assert.Equal("It has 200,001 events, which is too many to import (the limit is 200,000).", MacroBundle.SizeProblem(hidden));
    }

    [Fact]
    public void ReadForImport_ReturnsTheMacrosAndOneLinePerProblem()
    {
        var path = TempFile("share.json");
        var tooMany = EntryJson("endless", $"\"events\": [], \"steps\": [{Repeat("{\"kind\":\"wait\",\"delayMs\":1}", MacroBundle.MaxStepsPerMacro + 1)}]");
        System.IO.File.WriteAllText(path, BundleJson(new[] { EntryJson("fine"), tooMany }));

        var file = MacroBundle.ReadForImport(path);

        Assert.Equal("fine", Assert.Single(file.Macros).Name);
        Assert.Equal("Skipped endless in share.json: It has 5,001 steps, which is too many to import (the limit is 5,000).",
            Assert.Single(file.Log));
    }

    [Fact]
    public void ReadForImport_WholeFileProblems_NameTheFile()
    {
        var tooMany = TempFile("crowd.json");
        System.IO.File.WriteAllText(tooMany, BundleJson(Enumerable.Range(0, MacroBundle.MaxMacrosPerBundle + 1).Select(i => EntryJson($"m{i}"))));
        var notJson = TempFile("notes.json");
        System.IO.File.WriteAllText(notJson, "this is not json");

        Assert.Equal("Nothing was imported from crowd.json. It holds 501 macros, which is too many to import at once (the limit is 500).",
            Assert.Single(MacroBundle.ReadForImport(tooMany).Log));
        Assert.StartsWith("Nothing was imported from notes.json. Not valid JSON:", Assert.Single(MacroBundle.ReadForImport(notJson).Log));
    }

    [Fact]
    public void ReadForImport_FileThatCannotBeRead_IsOneLine_NotAThrow()
    {
        var file = MacroBundle.ReadForImport(TempFile("missing.json"));

        Assert.Empty(file.Macros);
        Assert.StartsWith("Import failed for missing.json:", Assert.Single(file.Log));
    }
}
