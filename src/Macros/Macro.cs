using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Top-level macro envelope (v3). v3 adds a coordinate space: "client" macros
/// store mouse coords relative to the recorded window's client area (plus the
/// recorded client size) and replay against the target window wherever it sits;
/// "screen" macros (all pre-v3 recordings) keep absolute screen pixels and play
/// exactly as before. v1/v2 files migrate at load via <see cref="MacroV1Migrator"/>.
/// </summary>
public sealed record Macro(
    int SchemaVersion,
    string Id,
    string? Name,
    string? RecordMode,                 // "PerWindow" | "AllWindows"; null = PerWindow (legacy)
    long? RecordedAgainstUserId,        // soft metadata, not enforced
    string? RecordedAgainstDisplayName,
    int? InterAltDelayMs,               // per-macro override for SequencePlayer; null = default 500ms
    long RecordedAtUnixMs,
    IReadOnlyList<MacroEvent> Events,
    string? CoordSpace = null,          // "screen" | "client"; null treated as screen (legacy)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RecordedClientW = null,        // physical px; set only when CoordSpace == "client"
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RecordedClientH = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? RecordedMaximized = null,     // true when the anchor window was maximized at record-start;
                                         // null = unknown (pre-existing macros) — EnsureClientSize tries
                                         // a windowed fit first and only falls back to maximize-and-leave
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? RecordedPlaceId = null,       // game identity at record time — soft metadata (v0.6, still schema v3
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RecordedGameName = null,    //   so v0.5 readers still open shared bundles; nullable = "any game")
    bool AllGames = false)              // user override: usable everywhere regardless of the stamp
{
    /// <summary>Current schema version. Bump on shape changes.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Absolute screen pixels — all pre-v3 recordings + AllWindows mode.</summary>
    public const string CoordSpaceScreen = "screen";

    /// <summary>Window-client-relative pixels — v3 per-window recordings.</summary>
    public const string CoordSpaceClient = "client";

    public bool IsClientSpace =>
        string.Equals(CoordSpace, CoordSpaceClient, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this macro is tied to a specific game: it carries a
    /// place stamp AND the user hasn't flipped the "All games" override.</summary>
    public bool IsGameScoped => !AllGames && RecordedPlaceId is > 0;

    public TimeSpan Duration => Events.Count == 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Events[^1].TimestampMs);
}
