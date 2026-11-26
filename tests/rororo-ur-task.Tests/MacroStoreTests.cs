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
}
