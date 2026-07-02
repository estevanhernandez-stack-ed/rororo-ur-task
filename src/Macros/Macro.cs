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
    int? RecordedClientH = null)
{
    /// <summary>Current schema version. Bump on shape changes.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Absolute screen pixels — all pre-v3 recordings + AllWindows mode.</summary>
    public const string CoordSpaceScreen = "screen";

    /// <summary>Window-client-relative pixels — v3 per-window recordings.</summary>
    public const string CoordSpaceClient = "client";

    public bool IsClientSpace =>
        string.Equals(CoordSpace, CoordSpaceClient, StringComparison.OrdinalIgnoreCase);

    public TimeSpan Duration => Events.Count == 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Events[^1].TimestampMs);
}
