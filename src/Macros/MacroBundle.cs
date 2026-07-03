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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
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
            failures.Add(new EntryFailure("(file)", $"Not valid JSON: {ex.Message}"));
            return new ParseResult(macros, failures);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("macros", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    index++;
                    try
                    {
                        macros.Add(MacroV1Migrator.LoadAndMigrate(el.GetRawText()));
                    }
                    catch (Exception ex)
                    {
                        var label = el.ValueKind == JsonValueKind.Object
                                    && el.TryGetProperty("name", out var n)
                                    && n.ValueKind == JsonValueKind.String
                            ? n.GetString()!
                            : $"entry {index}";
                        failures.Add(new EntryFailure(label, ex.Message));
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                try
                {
                    macros.Add(MacroV1Migrator.LoadAndMigrate(json));
                }
                catch (Exception ex)
                {
                    failures.Add(new EntryFailure("(file)", ex.Message));
                }
            }
            else
            {
                failures.Add(new EntryFailure("(file)", "Expected a macro bundle or a single macro object."));
            }
        }

        return new ParseResult(macros, failures);
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

    private sealed record BundleEnvelope(int BundleVersion, string App, long ExportedAtUnixMs, IReadOnlyList<Macro> Macros);
}
