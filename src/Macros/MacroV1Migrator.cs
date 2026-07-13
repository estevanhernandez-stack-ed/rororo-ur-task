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
                // System.Text.Json leaves the positional `Events` record param null
                // for JSON with a missing or explicit `"events": null` property — the
                // record's own `IReadOnlyList<MacroEvent>` type isn't null-checked at
                // deserialize time. Without the `?? []`, SanitizeTimestamps' foreach
                // NREs on that null instead of degrading to an empty (harmless,
                // zero-event) macro.
                Events = SanitizeTimestamps(m.Events ?? []),
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
            Events: SanitizeTimestamps(events),
            CoordSpace: Macro.CoordSpaceScreen);
    }

    /// <summary>
    /// CRITICAL 2 (defense in depth): <see cref="MacroEvent.TimestampMs"/> is a bare
    /// <c>long</c> read straight off user-editable (or corrupted) on-disk JSON, with
    /// no schema-level bound. MacroPlayer's playback loop computes
    /// <c>evt.TimestampMs - clock.ElapsedMilliseconds</c> and casts the result to
    /// <c>int</c> for <c>Task.Delay</c> — a value above <c>int.MaxValue</c> ms
    /// (~24.8 days) truncates to a NEGATIVE int on that cast, and <c>Task.Delay</c>
    /// throws <c>ArgumentOutOfRangeException</c> for anything less than -1. That
    /// exception used to escape <c>AssignmentRunner.RunActiveAsync</c> uncaught,
    /// which escaped <c>RunAsync</c> entirely and left the ur-afk claim file
    /// heartbeating forever with nothing servicing the alts it claimed to own.
    /// Clamping here — the one place every macro file passes through on its way
    /// into memory, for every schema version — means a hand-edited or corrupted
    /// macro can never reach ANY playback path (round-robin, sequence, AllWindows)
    /// with an out-of-range timestamp, on top of (not instead of) MacroPlayer's own
    /// clamp on the cast itself.
    /// </summary>
    private static IReadOnlyList<MacroEvent> SanitizeTimestamps(IReadOnlyList<MacroEvent> events)
    {
        var sanitized = new List<MacroEvent>(events.Count);
        foreach (var e in events)
        {
            var clamped = Math.Clamp(e.TimestampMs, 0L, (long)int.MaxValue);
            sanitized.Add(clamped == e.TimestampMs ? e : e with { TimestampMs = clamped });
        }
        return sanitized;
    }
}
