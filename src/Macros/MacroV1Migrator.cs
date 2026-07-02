using System.Text.Json;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure migration function: reads any-version macro JSON and returns a v3 Macro.
/// v1 macros (SchemaVersion = 1) get their Bound* fields mapped to
/// RecordedAgainst* metadata and RecordMode defaulted to "PerWindow".
/// v2 macros are upgraded to v3.
/// </summary>
public static class MacroV1Migrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parse macro JSON and return it as v3. The migration is sticky — re-save
    /// the returned Macro and the on-disk file persists in v3 shape.
    /// </summary>
    public static Macro LoadAndMigrate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var schemaVersion = root.TryGetProperty("schemaVersion", out var sv) ? sv.GetInt32() : 1;

        if (schemaVersion >= 2)
        {
            var m = JsonSerializer.Deserialize<Macro>(json, JsonOptions)
                ?? throw new InvalidOperationException("Macro deserialized as null.");
            // v2 → v3 (and defensive default for v3 files missing coordSpace):
            // pre-v3 recordings are absolute screen pixels.
            return m with
            {
                SchemaVersion = Macro.CurrentSchemaVersion,
                CoordSpace = m.CoordSpace ?? Macro.CoordSpaceScreen,
            };
        }

        // v1 → v3 mapping (Bound* fields → RecordedAgainst* metadata).
        var id = root.GetProperty("id").GetString()!;
        var name = root.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() : null;
        var recordedAgainstUserId = root.TryGetProperty("boundUserId", out var bu) ? bu.GetInt64() : (long?)null;
        var recordedAgainstDisplayName = root.TryGetProperty("boundDisplayName", out var bd) && bd.ValueKind != JsonValueKind.Null ? bd.GetString() : null;
        var recordedAtUnixMs = root.GetProperty("recordedAtUnixMs").GetInt64();

        var events = new List<MacroEvent>();
        if (root.TryGetProperty("events", out var evs))
        {
            foreach (var ev in evs.EnumerateArray())
            {
                events.Add(JsonSerializer.Deserialize<MacroEvent>(ev.GetRawText(), JsonOptions)!);
            }
        }

        return new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: id,
            Name: name,
            RecordMode: "PerWindow",
            RecordedAgainstUserId: recordedAgainstUserId,
            RecordedAgainstDisplayName: recordedAgainstDisplayName,
            InterAltDelayMs: null,
            RecordedAtUnixMs: recordedAtUnixMs,
            Events: events,
            CoordSpace: Macro.CoordSpaceScreen);
    }
}
