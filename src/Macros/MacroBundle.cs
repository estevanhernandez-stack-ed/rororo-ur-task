using System.Text.Json;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Portable macro sharing. Serializes macros into a version-stamped bundle JSON,
/// parses a bundle — or a bare single-macro file shared straight from someone's
/// macros folder — back into v3 macros via <see cref="MacroV1Migrator"/>, and
/// prepares incoming macros for import with a fresh id + deduped name so an
/// import can never overwrite an existing macro.
/// </summary>
public static class MacroBundle
{
    public const int CurrentBundleVersion = 1;

    // ---------- Import limits ----------
    // Import reads a file whole and parses it on the UI thread, and every macro it brings in
    // becomes a file that each later library refresh reads again. Without limits, one shared
    // file could freeze the window and then slow the app for good.

    /// <summary>The largest file import will open. A recorded event is about 200 bytes in a
    /// bundle, so 20 MB is close to 100,000 events: far beyond any bundle Ur Task writes for a
    /// real library, and small enough that reading, parsing and saving it stays in the low
    /// seconds (measured: under 3 s for a file at this limit).</summary>
    public const long MaxImportFileBytes = 20L * 1024 * 1024;

    /// <summary>The most macros one bundle may hold. Each becomes its own file, and the library,
    /// the bridge and the MCP re-read every file on every refresh, so the count is a cost paid
    /// on every load afterwards, not once. 500 is several times a large hand-built library.</summary>
    public const int MaxMacrosPerBundle = 500;

    /// <summary>The most recorded events one macro may hold, counting the events inside raw
    /// steps. The recorder keeps at most about 30 mouse moves a second, so this is close to two
    /// hours of nonstop movement; a keyboard recording never gets near it.</summary>
    public const int MaxEventsPerMacro = 200_000;

    /// <summary>The most steps one macro may hold. Steps are deliberate actions (a click, a key,
    /// a wait), not raw input: the macros Ur Task ships as examples have under ten. 5,000 leaves
    /// room for a long generated routine and stops a file built only to be huge.</summary>
    public const int MaxStepsPerMacro = 5_000;

    /// <summary>The <see cref="EntryFailure.Label"/> of a failure that is about the whole file,
    /// not one macro in it.</summary>
    public const string WholeFileLabel = "(file)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        AllowOutOfOrderMetadataProperties = true,
    };

    public static string Serialize(IReadOnlyList<Macro> macros, long exportedAtUnixMs)
    {
        if (macros is null) throw new ArgumentNullException(nameof(macros));
        var envelope = new BundleEnvelope(CurrentBundleVersion, "rororo-ur-task", exportedAtUnixMs, macros);
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    /// <summary>
    /// Parse bundle-or-single-macro JSON. Per-entry failures are isolated — one
    /// broken macro in a shared bundle never sinks the rest, mirroring
    /// <see cref="MacroStore.LoadAll"/>'s successes-and-failures posture.
    /// </summary>
    public static ParseResult Parse(string json)
    {
        var macros = new List<Macro>();
        var failures = new List<EntryFailure>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            failures.Add(new EntryFailure(WholeFileLabel, $"Not valid JSON: {ex.Message}"));
            return new ParseResult(macros, failures);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("macros", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                // Counted before any entry is read: a bundle over the limit costs nothing more.
                var count = arr.GetArrayLength();
                if (count > MaxMacrosPerBundle)
                {
                    failures.Add(new EntryFailure(WholeFileLabel, string.Create(Invariant,
                        $"It holds {count:N0} macros, which is too many to import at once (the limit is {MaxMacrosPerBundle:N0}).")));
                    return new ParseResult(macros, failures);
                }

                var index = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    index++;
                    var label = el.ValueKind == JsonValueKind.Object
                                && el.TryGetProperty("name", out var n)
                                && n.ValueKind == JsonValueKind.String
                        ? n.GetString()!
                        : $"entry {index}";
                    try
                    {
                        var macro = MacroV1Migrator.LoadAndMigrate(el.GetRawText());
                        if (SizeProblem(macro) is { } tooBig) failures.Add(new EntryFailure(label, tooBig));
                        else macros.Add(macro);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new EntryFailure(label, ex.Message));
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    var macro = MacroV1Migrator.LoadAndMigrate(json);
                    if (SizeProblem(macro) is { } tooBig) failures.Add(new EntryFailure(WholeFileLabel, tooBig));
                    else macros.Add(macro);
                }
                catch (Exception ex)
                {
                    failures.Add(new EntryFailure(WholeFileLabel, ex.Message));
                }
            }
            else
            {
                failures.Add(new EntryFailure(WholeFileLabel, "Expected a macro bundle or a single macro object."));
            }
        }

        return new ParseResult(macros, failures);
    }

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Null when the file is small enough to import; otherwise the sentence for the
    /// activity log. Checked on the file's length, before it is opened.</summary>
    public static string? FileSizeProblem(string fileName, long bytes)
    {
        if (bytes <= MaxImportFileBytes) return null;
        const double Mb = 1024 * 1024;
        // Rounded up to a tenth, so a file just over the limit never reads "20 MB; the limit is 20 MB".
        var size = Math.Ceiling(bytes / Mb * 10) / 10;
        return string.Create(Invariant,
            $"{fileName} is too large to import ({size:0.#} MB; the limit is {MaxImportFileBytes / Mb:0.#} MB).");
    }

    /// <summary>Null when the macro is within the per-macro limits; otherwise the sentence for
    /// the activity log. A raw step replays an event list of its own, so those count as events.</summary>
    public static string? SizeProblem(Macro macro)
    {
        if (macro is null) throw new ArgumentNullException(nameof(macro));
        long events = macro.Events?.Count ?? 0;
        var steps = macro.Steps?.Count ?? 0;
        if (macro.Steps is not null)
            foreach (var step in macro.Steps)
                if (step is Steps.RawStep { Events: { } raw }) events += raw.Count;

        if (events > MaxEventsPerMacro)
            return string.Create(Invariant,
                $"It has {events:N0} events, which is too many to import (the limit is {MaxEventsPerMacro:N0}).");
        if (steps > MaxStepsPerMacro)
            return string.Create(Invariant,
                $"It has {steps:N0} steps, which is too many to import (the limit is {MaxStepsPerMacro:N0}).");
        return null;
    }

    /// <summary>
    /// Everything import needs from one file: the macros to bring in, and the activity-log line
    /// for each thing that was left out. Never throws: a file that can't be read is a line too.
    /// </summary>
    public static FileImport ReadForImport(string path)
    {
        var fileName = System.IO.Path.GetFileName(path);
        ParseResult result;
        try
        {
            if (FileSizeProblem(fileName, new System.IO.FileInfo(path).Length) is { } tooLarge)
                return new FileImport(Array.Empty<Macro>(), new[] { tooLarge });
            result = Parse(System.IO.File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new FileImport(Array.Empty<Macro>(), new[] { $"Import failed for {fileName}: {ex.Message}" });
        }

        var log = new List<string>();
        foreach (var failure in result.Failures)
            log.Add(failure.Label == WholeFileLabel
                ? $"Nothing was imported from {fileName}. {failure.Reason}"
                : $"Skipped {failure.Label} in {fileName}: {failure.Reason}");
        return new FileImport(result.Macros, log);
    }

    /// <summary>
    /// Mint a fresh Guid id (imports are additive, never destructive — a shared
    /// macro whose id collides with a local one must not overwrite it) and
    /// dedupe the name with a " (2)" style suffix. <paramref name="takenNames"/>
    /// should be built with <see cref="StringComparer.OrdinalIgnoreCase"/>; the
    /// chosen name is added to it so batch imports keep walking the suffix.
    /// </summary>
    public static Macro PrepareForImport(Macro incoming, ISet<string> takenNames)
    {
        if (incoming is null) throw new ArgumentNullException(nameof(incoming));
        if (takenNames is null) throw new ArgumentNullException(nameof(takenNames));

        var name = incoming.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var candidate = name;
            var suffix = 2;
            while (takenNames.Contains(candidate))
            {
                candidate = $"{name} ({suffix++})";
            }
            takenNames.Add(candidate);
            name = candidate;
        }

        return incoming with
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            SchemaVersion = Macro.CurrentSchemaVersion,
        };
    }

    public sealed record ParseResult(IReadOnlyList<Macro> Macros, IReadOnlyList<EntryFailure> Failures);
    public sealed record EntryFailure(string Label, string Reason);
    public sealed record FileImport(IReadOnlyList<Macro> Macros, IReadOnlyList<string> Log);

    private sealed record BundleEnvelope(int BundleVersion, string App, long ExportedAtUnixMs, IReadOnlyList<Macro> Macros);
}
