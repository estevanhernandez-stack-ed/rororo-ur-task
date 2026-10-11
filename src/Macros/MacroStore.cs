using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Disk-backed macro library at
/// <c>%LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros\&lt;id&gt;.json</c>. One file per
/// macro keeps load / save / delete operations atomic and the directory diffable
/// for power-user inspection. Invalid files surface as load failures rather than
/// throwing — the UI surfaces them so the user can act, the engine never crashes
/// on a malformed file.
/// </summary>
public sealed class MacroStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // String-form enums match MacroV1Migrator's read-side options for symmetry —
        // direct-deserialize code paths (not going through the migrator) don't have
        // to repeat the converter setup. The migrator's converter accepts both string
        // and integer forms, so v0.1 macros (integer enums) still load fine.
        Converters = { new JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
    };

    private readonly string _directory;

    public MacroStore() : this(DefaultDirectory()) { }

    public MacroStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        System.IO.Directory.CreateDirectory(_directory);
    }

    /// <summary>Default storage path under LocalAppData.</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "RoRoRoUrTask", "macros");

    public string Directory => _directory;

    /// <summary>Archived macros live one level down, in <c>macros\archive\</c>.</summary>
    public string ArchiveDirectory => Path.Combine(_directory, "archive");

    /// <summary>
    /// Load every macro file in the directory. Returns successes and failures
    /// separately so the UI can surface invalid files without breaking the load.
    /// v0.1 macros are auto-migrated to v2 via <see cref="MacroV1Migrator"/>;
    /// the migration is sticky — the next save persists in v2 shape.
    /// </summary>
    public LoadResult LoadAll() => LoadFrom(_directory);

    /// <summary>
    /// Load the macros in <see cref="ArchiveDirectory"/>. Empty when nothing has been
    /// archived yet; the folder is created on the first archive, not here.
    /// </summary>
    public LoadResult LoadArchived()
        => System.IO.Directory.Exists(ArchiveDirectory)
            ? LoadFrom(ArchiveDirectory)
            : new LoadResult(Array.Empty<Macro>(), Array.Empty<LoadFailure>());

    // Top level only (EnumerateFiles' default). LoadAll must never see the archive
    // subfolder: that is what keeps archived macros out of the library, the action
    // bridge's ListMacros and the MCP's list_macros.
    private static LoadResult LoadFrom(string directory)
    {
        var loaded = new List<Macro>();
        var failures = new List<LoadFailure>();

        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(path);
                var macro = MacroV1Migrator.LoadAndMigrate(json);
                if (macro is null)
                {
                    failures.Add(new LoadFailure(path, "Migration returned null."));
                    continue;
                }
                loaded.Add(macro);
            }
            catch (Exception ex)
            {
                failures.Add(new LoadFailure(path, ex.Message));
            }
        }

        return new LoadResult(loaded, failures);
    }

    /// <summary>
    /// Save a macro. Writes to a .tmp sibling then renames atomically so a
    /// crash mid-write can't leave a half-written file the next load picks up.
    /// </summary>
    public void Save(Macro macro)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));
        var target = PathFor(macro.Id);
        var tmp = target + ".tmp";
        var json = JsonSerializer.Serialize(macro, JsonOptions);
        File.WriteAllText(tmp, json);
        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target);
    }

    public void Delete(string macroId)
    {
        var target = PathFor(macroId);
        if (File.Exists(target)) File.Delete(target);
    }

    /// <summary>
    /// Move <c>&lt;id&gt;.json</c> into <see cref="ArchiveDirectory"/>. Archiving is a file
    /// move, not a flag, so every reader of the top level (library, bridge, MCP) drops
    /// the macro without learning anything new. A stale archived copy with the same id
    /// is replaced. Returns false when there is no live macro with that id.
    /// </summary>
    public bool Archive(string macroId)
    {
        var source = PathFor(macroId);
        if (!File.Exists(source)) return false;
        System.IO.Directory.CreateDirectory(ArchiveDirectory);
        File.Move(source, ArchivedPathFor(macroId), overwrite: true);
        return true;
    }

    /// <summary>
    /// Move an archived macro back to the top level. Never overwrites a live macro
    /// with the same id: returns false then, and when nothing is archived under that id.
    /// </summary>
    public bool Restore(string macroId)
    {
        var source = ArchivedPathFor(macroId);
        var target = PathFor(macroId);
        if (!File.Exists(source) || File.Exists(target)) return false;
        File.Move(source, target);
        return true;
    }

    private string ArchivedPathFor(string macroId)
        => Path.Combine(ArchiveDirectory, Path.GetFileName(PathFor(macroId)));

    private string PathFor(string macroId)
    {
        // Macro ids are Guids; safe as filenames. Defensive guard rejects anything
        // exotic so a malformed id can't escape the macros directory.
        if (!Guid.TryParse(macroId, out _))
            throw new ArgumentException("Macro id must be a Guid.", nameof(macroId));
        return Path.Combine(_directory, $"{macroId}.json");
    }

    public sealed record LoadResult(IReadOnlyList<Macro> Macros, IReadOnlyList<LoadFailure> Failures);
    public sealed record LoadFailure(string Path, string Reason);
}
