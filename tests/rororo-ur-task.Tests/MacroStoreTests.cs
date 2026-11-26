using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The macro folder is shared ground: Ur Task writes it, the user copies files in and out of it,
/// and an agent writes macro files straight into it. These tests are about the store staying
/// whole when the folder is not in the shape Ur Task alone would leave it in.
/// </summary>
public class MacroStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Macro M(string name, string? id = null) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: id ?? Guid.NewGuid().ToString(),
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: 1,
        Events: new List<MacroEvent>(),
        CoordSpace: Macro.CoordSpaceScreen);

    private string PathOf(Macro m) => Path.Combine(_dir, $"{m.Id}.json");

    // ---------- L5-02: a save replaces the file in one step ----------

    [Fact]
    public void Save_OverAnExistingFile_LeavesTheNewFileAndNoTmp()
    {
        var store = new MacroStore(_dir);
        var macro = M("first");
        store.Save(macro);

        store.Save(macro with { Name = "second" });

        var loaded = store.LoadAll();
        Assert.Empty(loaded.Failures);
        Assert.Equal("second", Assert.Single(loaded.Macros).Name);
        Assert.Equal(new[] { $"{macro.Id}.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    /// <summary>
    /// The failure the old delete-then-move had: the old file was already gone when the move
    /// failed, so the only copy left was a .tmp nothing loads. Here something else (a virus
    /// scanner on the freshly written file, say) holds the .tmp without sharing delete, which
    /// makes the rename fail. The macro on disk must still be the one from before the save.
    /// </summary>
    [Fact]
    public void Save_WhenTheNewFileCannotBeMovedIntoPlace_KeepsTheOldMacro()
    {
        var store = new MacroStore(_dir);
        var macro = M("first");
        store.Save(macro);
        var tmp = PathOf(macro) + ".tmp";
        File.WriteAllText(tmp, string.Empty);

        using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<Exception>(() => store.Save(macro with { Name = "second" }));
        }

        var loaded = store.LoadAll();
        Assert.Empty(loaded.Failures);
        Assert.Equal("first", Assert.Single(loaded.Macros).Name);
    }

    [Fact]
    public void Save_WhenTheMacroFileIsBeingRead_ThrowsAndKeepsTheOldMacro()
    {
        var store = new MacroStore(_dir);
        var macro = M("first");
        store.Save(macro);

        // What the bridge thread's File.ReadAllText holds while it reads a macro.
        using (new FileStream(PathOf(macro), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => store.Save(macro with { Name = "second" }));
        }

        Assert.Equal("first", Assert.Single(store.LoadAll().Macros).Name);
        Assert.False(File.Exists(PathOf(macro) + ".tmp"));
    }

    /// <summary>
    /// Replacing a file in one rename fails while anything has the old file open, even a reader
    /// that shares delete (the delete-then-move it replaced got past those). Such handles last
    /// milliseconds, so the replace tries again a few times. The wait is passed in here, so the
    /// test decides when the other handle lets go instead of racing a timer.
    /// </summary>
    [Fact]
    public void ReplaceFile_WhenTheFileIsBusyForAMoment_TriesAgain_AndSucceeds()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "macro.json");
        var tmp = target + ".tmp";
        File.WriteAllText(target, "old");
        File.WriteAllText(tmp, "new");
        var scanner = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var waits = new List<int>();

        try
        {
            MacroStore.ReplaceFile(tmp, target, wait: ms => { waits.Add(ms); scanner.Dispose(); });
        }
        finally { scanner.Dispose(); }

        Assert.Equal(new[] { MacroStore.ReplaceRetryMs }, waits);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void ReplaceFile_WhenTheFileStaysBusy_GivesUpAfterAFewTries_AndTheOldFileIsIntact()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "macro.json");
        var tmp = target + ".tmp";
        File.WriteAllText(target, "old");
        File.WriteAllText(tmp, "new");
        var waits = 0;

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => MacroStore.ReplaceFile(tmp, target, wait: _ => waits++));
        }

        Assert.Equal(MacroStore.ReplaceAttempts - 1, waits);
        Assert.Equal("old", File.ReadAllText(target));
    }

    // ---------- L5-04: the id decides the file, so the store checks it at load ----------

    private static string FileJson(string? idJson, string name) => $$"""
    {
      "schemaVersion": 4,
      {{(idJson is null ? "" : $"\"id\": {idJson},")}}
      "name": "{{name}}",
      "recordMode": "PerWindow",
      "recordedAgainstUserId": null,
      "recordedAgainstDisplayName": null,
      "interAltDelayMs": null,
      "recordedAtUnixMs": 1,
      "coordSpace": "screen",
      "events": []
    }
    """;

    private void Write(string fileName, string? idJson, string name)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, fileName), FileJson(idJson, name));
    }

    private static string Quoted(string id) => $"\"{id}\"";

    [Theory]
    [InlineData("\"not-a-guid\"")]
    [InlineData("\"\"")]
    [InlineData("\"../escape\"")]
    public void Load_IdThatIsNotAGuid_IsALoadFailure_NotAMacro(string idJson)
    {
        Write("handmade.json", idJson, "handmade");

        var loaded = new MacroStore(_dir).LoadAll();

        Assert.Empty(loaded.Macros);
        var failure = Assert.Single(loaded.Failures);
        Assert.EndsWith("handmade.json", failure.Path);
        Assert.Contains("id", failure.Reason);
    }

    [Theory]
    [InlineData(null)]     // no id property at all
    [InlineData("null")]   // "id": null
    public void Load_MissingId_IsALoadFailure_NotAMacro(string? idJson)
    {
        Write("handmade.json", idJson, "handmade");

        var loaded = new MacroStore(_dir).LoadAll();

        Assert.Empty(loaded.Macros);
        var failure = Assert.Single(loaded.Failures);
        Assert.EndsWith("handmade.json", failure.Path);
        Assert.Contains("no id", failure.Reason);
    }

    [Fact]
    public void Load_BadId_DoesNotStopTheRestLoading()
    {
        var good = M("good");
        var store = new MacroStore(_dir);
        store.Save(good);
        Write("handmade.json", Quoted("not-a-guid"), "handmade");

        var loaded = store.LoadAll();

        Assert.Equal("good", Assert.Single(loaded.Macros).Name);
        Assert.Single(loaded.Failures);
    }

    /// <summary>Everything the library lists can be renamed and deleted: an id the store would
    /// refuse to build a path from never reaches the list in the first place.</summary>
    [Fact]
    public void EveryLoadedMacro_CanBeSavedAndDeleted()
    {
        var store = new MacroStore(_dir);
        store.Save(M("good"));
        Write("handmade.json", Quoted("not-a-guid"), "handmade");
        Write("noid.json", null, "noid");

        foreach (var m in store.LoadAll().Macros)
        {
            store.Save(m with { Name = "renamed" });
            store.Delete(m.Id);
        }

        Assert.Empty(store.LoadAll().Macros);
    }

    [Fact]
    public void Load_DuplicateId_PrefersTheFileNamedAfterTheId()
    {
        const string id = "ffffffff-0000-4000-8000-000000000001";
        // The copy sorts first by name, so only the "named after the id" rule can pick the other.
        Write("aaa backup.json", Quoted(id), "the copy");
        Write($"{id}.json", Quoted(id), "the real one");

        var loaded = new MacroStore(_dir).LoadAll();

        Assert.Equal("the real one", Assert.Single(loaded.Macros).Name);
        var failure = Assert.Single(loaded.Failures);
        Assert.EndsWith("aaa backup.json", failure.Path);
        Assert.Contains($"{id}.json", failure.Reason);
    }

    [Fact]
    public void Load_DuplicateId_WithNoFileNamedAfterTheId_TakesTheFirstByFileName()
    {
        const string id = "ffffffff-0000-4000-8000-000000000002";
        Write("b.json", Quoted(id), "from b");
        Write("a.json", Quoted(id), "from a");
        Write("c.json", Quoted(id), "from c");

        var loaded = new MacroStore(_dir).LoadAll();

        Assert.Equal("from a", Assert.Single(loaded.Macros).Name);
        Assert.Equal(new[] { "b.json", "c.json" },
            loaded.Failures.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(loaded.Failures, f => Assert.Contains("a.json", f.Reason));
    }

    [Fact]
    public void Load_SameIdInDifferentCase_IsADuplicate()
    {
        const string id = "abcdef00-0000-4000-8000-000000000003";
        Write($"{id}.json", Quoted(id), "lower");
        Write("upper.json", Quoted(id.ToUpperInvariant()), "upper");

        var loaded = new MacroStore(_dir).LoadAll();

        Assert.Equal("lower", Assert.Single(loaded.Macros).Name);
        Assert.Single(loaded.Failures);
    }

    [Fact]
    public void Load_Archived_AppliesTheSameIdRules()
    {
        var archive = Path.Combine(_dir, "archive");
        Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(archive, "handmade.json"), FileJson(Quoted("not-a-guid"), "handmade"));

        var loaded = new MacroStore(_dir).LoadArchived();

        Assert.Empty(loaded.Macros);
        Assert.Single(loaded.Failures);
    }

    /// <summary>The load-failure sentences tell the user to IMPORT the file. That advice has to
    /// be true: import mints a fresh id, so a file with no id or a bad one comes in whole.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("\"not-a-guid\"")]
    public void FileWithABadId_CanBeImported_AndThenLoads(string? idJson)
    {
        var parsed = MacroBundle.Parse(FileJson(idJson, "handmade"));
        Assert.Empty(parsed.Failures);
        var store = new MacroStore(_dir);

        store.Save(MacroBundle.PrepareForImport(Assert.Single(parsed.Macros), new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        var loaded = store.LoadAll();
        Assert.Empty(loaded.Failures);
        Assert.Equal("handmade", Assert.Single(loaded.Macros).Name);
    }

    // ---------- L5-05: a one-field change is applied to what is on disk now ----------

    private static readonly MacroEvent SpaceDown = new(0, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0);
    private static readonly MacroEvent SpaceUp = new(80, MacroEventKind.KeyUp, 0x20, 0, 0, 0, 0);

    /// <summary>
    /// The library holds the copy it loaded at its last refresh. An agent rewrites the same
    /// file with new moves, then the user renames the macro in Ur Task. Saving the library's
    /// copy with a new name (what Rename and All games did) writes the old moves back.
    /// </summary>
    [Fact]
    public void SavingTheListsCopy_WritesStaleContentBack_WhichIsWhyUpdateExists()
    {
        var store = new MacroStore(_dir);
        var listed = M("old name");
        store.Save(listed);
        store.Save(listed with { Events = new[] { SpaceDown, SpaceUp } }); // the other tool's rewrite

        store.Save(listed with { Name = "new name" });

        Assert.Empty(Assert.Single(store.LoadAll().Macros).Events); // the rewrite is gone
    }

    [Fact]
    public void Update_AppliesTheChangeToTheFileAsItIsNow_NotToAnOlderCopy()
    {
        var store = new MacroStore(_dir);
        var listed = M("old name");
        store.Save(listed);
        store.Save(listed with { Events = new[] { SpaceDown, SpaceUp }, RecordedGameName = "Pet Simulator 99" });

        Assert.True(store.Update(listed.Id, fresh => fresh with { Name = "new name" }));

        var onDisk = Assert.Single(store.LoadAll().Macros);
        Assert.Equal("new name", onDisk.Name);
        Assert.Equal(2, onDisk.Events.Count);
        Assert.Equal("Pet Simulator 99", onDisk.RecordedGameName);
        Assert.Equal(new[] { $"{listed.Id}.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Update_WhenTheFileIsGone_ReturnsFalse_AndDoesNotBringItBack()
    {
        var store = new MacroStore(_dir);
        var listed = M("deleted elsewhere");
        store.Save(listed);
        File.Delete(PathOf(listed));

        Assert.False(store.Update(listed.Id, fresh => fresh with { Name = "new name" }));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    /// <summary>A macro in a file that is not named after its id (written by another tool) is
    /// changed in that file. Writing <c>&lt;id&gt;.json</c> beside it would leave two files with
    /// one id, and the other tool's next rewrite would go to the one Ur Task no longer reads.</summary>
    [Fact]
    public void Update_AMacroInAFileNotNamedAfterItsId_ChangesThatFile()
    {
        const string id = "ffffffff-0000-4000-8000-000000000005";
        Write("agent-made.json", Quoted(id), "old name");
        var store = new MacroStore(_dir);

        Assert.True(store.Update(id, fresh => fresh with { AllGames = true }));

        var loaded = store.LoadAll();
        Assert.Empty(loaded.Failures);
        Assert.True(Assert.Single(loaded.Macros).AllGames);
        Assert.Equal(new[] { "agent-made.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Update_WhenTheFileCannotBeRead_Throws_AndLeavesItAlone()
    {
        var store = new MacroStore(_dir);
        var listed = M("half written");
        store.Save(listed);
        File.WriteAllText(PathOf(listed), "{ \"schemaVersion\": 4, \"id\": "); // caught mid-rewrite

        Assert.ThrowsAny<Exception>(() => store.Update(listed.Id, fresh => fresh with { Name = "new name" }));

        Assert.Equal("{ \"schemaVersion\": 4, \"id\": ", File.ReadAllText(PathOf(listed)));
    }

    [Fact]
    public void Update_MayNotChangeTheId()
    {
        var store = new MacroStore(_dir);
        var listed = M("keeps its id");
        store.Save(listed);

        Assert.Throws<ArgumentException>(() => store.Update(listed.Id, fresh => fresh with { Id = Guid.NewGuid().ToString() }));

        Assert.Equal(listed.Id, Assert.Single(store.LoadAll().Macros).Id);
    }

    [Fact]
    public void Load_ReadsOneMacroFreshFromDisk_AndNullWhenItIsGone()
    {
        var store = new MacroStore(_dir);
        var listed = M("old name");
        store.Save(listed);
        store.Save(listed with { Name = "rewritten" });

        Assert.Equal("rewritten", store.Load(listed.Id)?.Name);
        Assert.Null(store.Load(Guid.NewGuid().ToString()));
        Assert.Null(store.Load("not-a-guid"));
    }

    [Fact]
    public void IndexById_FirstWins_AndSkipsMissingIds()
    {
        var first = M("first", "ffffffff-0000-4000-8000-000000000004");
        var second = M("second", "FFFFFFFF-0000-4000-8000-000000000004");
        var noId = M("no id") with { Id = null! };

        var index = MacroStore.IndexById(new[] { noId, first, second });

        Assert.Equal("first", Assert.Single(index).Value.Name);
        Assert.True(index.ContainsKey("FFFFFFFF-0000-4000-8000-000000000004"));
    }
}
