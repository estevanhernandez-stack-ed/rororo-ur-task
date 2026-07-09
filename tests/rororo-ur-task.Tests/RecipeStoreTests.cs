using System.IO;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class RecipeStoreTests
{
    private static Recipe Sample(string id) => new(
        Recipe.CurrentSchemaVersion, id, "walk + mine",
        new[] { new RecipeStep("11111111-1111-1111-1111-111111111111", StepIteration.RunOnce),
                new RecipeStep("22222222-2222-2222-2222-222222222222", StepIteration.Loop) },
        RecordedAtUnixMs: 1000);

    [Fact]
    public void Save_Then_LoadAll_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-recipes-" + Guid.NewGuid());
        var store = new RecipeStore(dir);
        var id = Guid.NewGuid().ToString();

        store.Save(Sample(id));
        var loaded = store.LoadAll();

        Assert.Empty(loaded.Failures);
        var back = Assert.Single(loaded.Recipes);
        Assert.Equal("walk + mine", back.Name);
        Assert.Equal(2, back.Steps.Count);
        Assert.Equal(StepIteration.Loop, back.Terminal.Iteration);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-recipes-" + Guid.NewGuid());
        var store = new RecipeStore(dir);
        var id = Guid.NewGuid().ToString();
        store.Save(Sample(id));
        store.Delete(id);
        Assert.Empty(store.LoadAll().Recipes);
    }

    [Fact]
    public void LoadAll_MalformedFile_SurfacesAsFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-recipes-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");
        var loaded = new RecipeStore(dir).LoadAll();
        Assert.Empty(loaded.Recipes);
        Assert.Single(loaded.Failures);
    }
}
