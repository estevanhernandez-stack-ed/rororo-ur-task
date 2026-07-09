using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Disk-backed recipe library at
/// <c>%LOCALAPPDATA%\626Labs\RoRoRoUrTask\recipes\&lt;id&gt;.json</c>. One file per
/// recipe; atomic tmp-then-rename write. Mirrors <see cref="MacroStore"/>. Recipes
/// reference macros by id — resolved against MacroStore at run time.
/// </summary>
public sealed class RecipeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public RecipeStore() : this(DefaultDirectory()) { }

    public RecipeStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        System.IO.Directory.CreateDirectory(_directory);
    }

    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "RoRoRoUrTask", "recipes");

    public string Directory => _directory;

    public LoadResult LoadAll()
    {
        var loaded = new List<Recipe>();
        var failures = new List<LoadFailure>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), JsonOptions);
                if (recipe is null) { failures.Add(new LoadFailure(path, "Deserialize returned null.")); continue; }
                loaded.Add(recipe);
            }
            catch (Exception ex) { failures.Add(new LoadFailure(path, ex.Message)); }
        }
        return new LoadResult(loaded, failures);
    }

    public void Save(Recipe recipe)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        var target = PathFor(recipe.Id);
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(recipe, JsonOptions));
        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target);
    }

    public void Delete(string recipeId)
    {
        var target = PathFor(recipeId);
        if (File.Exists(target)) File.Delete(target);
    }

    private string PathFor(string recipeId)
    {
        if (!Guid.TryParse(recipeId, out _))
            throw new ArgumentException("Recipe id must be a Guid.", nameof(recipeId));
        return Path.Combine(_directory, $"{recipeId}.json");
    }

    public sealed record LoadResult(IReadOnlyList<Recipe> Recipes, IReadOnlyList<LoadFailure> Failures);
    public sealed record LoadFailure(string Path, string Reason);
}
