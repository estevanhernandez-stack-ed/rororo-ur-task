using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>Archive = move the file into <c>macros\archive\</c>; Restore moves it back.</summary>
public class MacroArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static Macro M(string name) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(),
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: 1,
        Events: new List<MacroEvent>(),
        CoordSpace: Macro.CoordSpaceScreen);

    [Fact]
    public void Archive_MovesTheFileIntoTheArchiveFolder()
    {
        var store = new MacroStore(_dir);
        var keep = M("keep");
        var old = M("old");
        store.Save(keep);
        store.Save(old);

        Assert.True(store.Archive(old.Id));

        Assert.False(File.Exists(Path.Combine(_dir, $"{old.Id}.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "archive", $"{old.Id}.json")));
        Assert.Equal(Path.Combine(_dir, "archive"), store.ArchiveDirectory);
    }

    [Fact]
    public void LoadAll_SkipsArchived_AndLoadArchived_ReadsOnlyThem()
    {
        var store = new MacroStore(_dir);
        var keep = M("keep");
        var old = M("old");
        store.Save(keep);
        store.Save(old);
        store.Archive(old.Id);

        var live = store.LoadAll();
        Assert.Empty(live.Failures);
        Assert.Equal("keep", Assert.Single(live.Macros).Name);

        var archived = store.LoadArchived();
        Assert.Empty(archived.Failures);
        Assert.Equal("old", Assert.Single(archived.Macros).Name);
    }

    [Fact]
    public void LoadArchived_WithNoArchiveFolder_IsEmpty()
    {
        var store = new MacroStore(_dir);
        store.Save(M("keep"));

        var archived = store.LoadArchived();

        Assert.Empty(archived.Macros);
        Assert.Empty(archived.Failures);
    }

    [Fact]
    public void Restore_MovesItBack()
    {
        var store = new MacroStore(_dir);
        var old = M("old");
        store.Save(old);
        store.Archive(old.Id);

        Assert.True(store.Restore(old.Id));

        Assert.Equal("old", Assert.Single(store.LoadAll().Macros).Name);
        Assert.Empty(store.LoadArchived().Macros);
    }

    [Fact]
    public void Archive_UnknownId_ReturnsFalse()
    {
        var store = new MacroStore(_dir);

        Assert.False(store.Archive(Guid.NewGuid().ToString()));
        Assert.False(store.Restore(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Restore_NeverOverwritesALiveMacro()
    {
        var store = new MacroStore(_dir);
        var old = M("archived copy");
        store.Save(old);
        store.Archive(old.Id);
        store.Save(old with { Name = "live copy" });

        Assert.False(store.Restore(old.Id));

        Assert.Equal("live copy", Assert.Single(store.LoadAll().Macros).Name);
        Assert.Equal("archived copy", Assert.Single(store.LoadArchived().Macros).Name);
    }

    [Fact]
    public void Archive_ReplacesAStaleArchivedCopy()
    {
        var store = new MacroStore(_dir);
        var m = M("v1");
        store.Save(m);
        store.Archive(m.Id);
        store.Save(m with { Name = "v2" });

        Assert.True(store.Archive(m.Id));

        Assert.Empty(store.LoadAll().Macros);
        Assert.Equal("v2", Assert.Single(store.LoadArchived().Macros).Name);
    }

    [Fact]
    public void Archive_RejectsANonGuidId()
    {
        var store = new MacroStore(_dir);

        Assert.Throws<ArgumentException>(() => store.Archive("..\\escape"));
    }

    [Fact]
    public void BridgeListMacros_DropsArchivedMacros()
    {
        // The production invoker reads store.LoadAll() — same delegate here.
        var store = new MacroStore(_dir);
        var keep = M("keep");
        var old = M("old");
        store.Save(keep);
        store.Save(old);
        store.Archive(old.Id);

        var invoker = new MacroRunInvoker(
            loadMacros: () => store.LoadAll().Macros,
            snapshot: () => Array.Empty<AccountRegistry.AccountInfo>(),
            resolveForegroundUserId: () => null,
            isBusy: () => false,
            play: (_, _, _, _) => System.Threading.Tasks.Task.CompletedTask);

        var list = invoker.ListMacros();

        Assert.Equal(keep.Id, Assert.Single(list).Id);
    }

    // ---------- usage warning ----------

    [Fact]
    public void Usage_NamesPairedAltsAndRecipes()
    {
        var target = M("target");
        var other = M("other");
        var recipes = new[]
        {
            new Recipe(1, "r1", "Morning farm", new[] { new RecipeStep(target.Id, StepIteration.RunOnce), new RecipeStep(other.Id, StepIteration.Loop) }, 1),
            new Recipe(1, "r2", "Unrelated", new[] { new RecipeStep(other.Id, StepIteration.Loop) }, 1),
            new Recipe(1, "r3", null, new[] { new RecipeStep(target.Id, StepIteration.Loop) }, 1),
        };
        var pairings = new[] { ("zed", (string?)target.Id), ("amy", target.Id), ("bob", other.Id), ("cal", null) };

        var usage = MacroUsage.Find(target.Id, pairings, recipes);

        Assert.True(usage.IsUsed);
        Assert.Equal(new[] { "amy", "zed" }, usage.PairedAlts);
        Assert.Equal(new[] { "(unnamed)", "Morning farm" }, usage.Recipes);
    }

    [Fact]
    public void Usage_UnusedMacro_IsNotUsed()
    {
        var usage = MacroUsage.Find(Guid.NewGuid().ToString(),
            Array.Empty<(string, string?)>(), Array.Empty<Recipe>());

        Assert.False(usage.IsUsed);
    }
}
