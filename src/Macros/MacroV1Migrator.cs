using System.Text.Json;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure migration function: reads any-version macro JSON and returns a v2 Macro.
/// v1 macros (SchemaVersion = 1) get their Bound* fields mapped to
/// RecordedAgainst* metadata and RecordMode defaulted to "PerWindow".
/// v2 macros pass through unchanged.
/// </summary>
public static class MacroV1Migrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Parse macro JSON and return it as v2. The migration is sticky — re-save
    /// the returned Macro and the on-disk file persists in v2 shape.
    /// </summary>
    public static Macro LoadAndMigrate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var schemaVersion = root.TryGetProperty("schemaVersion", out var sv) ? sv.GetInt32() : 1;

        if (schemaVersion >= 2)
        {
            return JsonSerializer.Deserialize<Macro>(json, JsonOptions)
                ?? throw new InvalidOperationException("v2 macro deserialized as null.");
        }

        // v1 → v2 mapping.
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
            SchemaVersion: 2,
            Id: id,
            Name: name,
            RecordMode: "PerWindow",
            RecordedAgainstUserId: recordedAgainstUserId,
            RecordedAgainstDisplayName: recordedAgainstDisplayName,
            InterAltDelayMs: null,
            RecordedAtUnixMs: recordedAtUnixMs,
            Events: events);
    }
}
