using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Disk-backed macro library at
/// <c>%LOCALAPPDATA%\626Labs\RoRoRoUrTask\macros\&lt;id&gt;.json</c>. One file per
/// macro keeps load / save / delete operations independent of each other and the
/// directory diffable for power-user inspection. A save replaces its file in one
/// rename (see <see cref="Save"/>). Invalid files surface as load failures rather than
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
    //
    // Every *.json is read whatever it is called, but save, delete, archive and restore all act
    // on <id>.json (PathFor). So the id is checked here, where a problem is one line in the log:
    // a macro whose id PathFor would refuse never reaches the list (rename or delete on it used
    // to throw), and two files with one id load as one macro (the rest of the app looks macros up
    // by id and assumes there is one).
    private static LoadResult LoadFrom(string directory)
    {
        var read = new List<(string Path, Macro Macro, Guid Id)>();
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
                if (string.IsNullOrWhiteSpace(macro.Id))
                {
                    failures.Add(new LoadFailure(path, NoIdReason));
                    continue;
                }
                if (!Guid.TryParse(macro.Id, out var id))
                {
                    failures.Add(new LoadFailure(path, BadIdReason(macro.Id)));
                    continue;
                }
                read.Add((path, macro, id));
            }
            catch (Exception ex)
            {
                failures.Add(new LoadFailure(path, ex.Message));
            }
        }

        // One id, several files (a backup copy left in the folder, a file written by another
        // tool). Keep the file named after the id, because that is the one a save or delete
        // touches; with none, the first by file name, so the pick is the same on every load.
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sameId in read.GroupBy(r => r.Id).Where(g => g.Count() > 1))
        {
            var ordered = sameId
                .OrderBy(r => IsNamedAfterItsId(r.Path, r.Macro.Id) ? 0 : 1)
                .ThenBy(r => Path.GetFileName(r.Path), StringComparer.Ordinal)
                .ToList();
            var kept = Path.GetFileName(ordered[0].Path);
            foreach (var other in ordered.Skip(1))
            {
                skipped.Add(other.Path);
                failures.Add(new LoadFailure(other.Path, DuplicateIdReason(kept)));
            }
        }

        var loaded = read.Where(r => !skipped.Contains(r.Path)).Select(r => r.Macro).ToList();
        return new LoadResult(loaded, failures);
    }

    private static bool IsNamedAfterItsId(string path, string id)
        => string.Equals(Path.GetFileNameWithoutExtension(path), id, StringComparison.OrdinalIgnoreCase);

    // Load-failure reasons. They are read after "load failed: <file name> — " in the activity
    // log, and an agent gets the same sentence back when it asks to run that file.
    internal const string NoIdReason =
        "This macro file has no id, so Ur Task can't use it. Use IMPORT to bring it in with a new id, then remove this file from the macros folder.";

    internal static string BadIdReason(string id)
        => $"Its id '{(id.Length > 40 ? id[..40] + "..." : id)}' is not a GUID, so Ur Task can't use it. "
         + "Use IMPORT to bring it in with a new id, then remove this file from the macros folder.";

    internal static string DuplicateIdReason(string keptFileName)
        => $"It has the same id as {keptFileName}, so Ur Task uses {keptFileName} and skips this file. "
         + "If this is a backup copy, move it out of the macros folder. To keep both, use IMPORT on this file first.";

    /// <summary>
    /// Save a macro. Writes to a .tmp sibling, then replaces the real file with it in one
    /// rename, so a crash mid-write can't leave a half-written file the next load picks up and
    /// a failed save leaves the previous file exactly as it was. It used to delete the old file
    /// first and then move: when that move failed, the only copy left was a .tmp nothing loads.
    /// Throws when the file can't be replaced (something else has it open); callers report it.
    /// </summary>
    public void Save(Macro macro)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));
        var target = PathFor(macro.Id);
        var tmp = target + ".tmp";
        var json = JsonSerializer.Serialize(macro, JsonOptions);
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            // Best effort: the save failed, so don't leave its half behind in the folder.
            try { File.Delete(tmp); } catch { }
            throw;
        }
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

    /// <summary>
    /// A lookup by id that cannot throw: the first macro with an id wins and a macro with no id
    /// is left out. <see cref="LoadAll"/> no longer returns either, but a ToDictionary over a
    /// macro list ran on the UI thread and took the app down on a duplicate, so the lookups do
    /// not depend on that.
    /// </summary>
    public static Dictionary<string, Macro> IndexById(IEnumerable<Macro> macros, IEqualityComparer<string>? comparer = null)
    {
        var index = new Dictionary<string, Macro>(comparer ?? StringComparer.OrdinalIgnoreCase);
        foreach (var macro in macros)
        {
            if (macro is null || string.IsNullOrEmpty(macro.Id)) continue;
            index.TryAdd(macro.Id, macro);
        }
        return index;
    }

    public sealed record LoadResult(IReadOnlyList<Macro> Macros, IReadOnlyList<LoadFailure> Failures);
    public sealed record LoadFailure(string Path, string Reason);
}
