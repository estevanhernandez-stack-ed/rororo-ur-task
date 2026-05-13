namespace Labs626.UrTask.Macros;

/// <summary>
/// Top-level macro envelope (v2). v2 macros are portable — playback target
/// is picked at play time, not bound at record time. The
/// <see cref="RecordedAgainstUserId"/> / <see cref="RecordedAgainstDisplayName"/>
/// fields are informational metadata ("recorded against Goldnail8") and are
/// never enforced at playback. v1 macros loaded from disk get migrated to v2
/// at load time by <see cref="MacroV1Migrator"/>.
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
    IReadOnlyList<MacroEvent> Events)
{
    /// <summary>Current schema version. Bump on shape changes.</summary>
    public const int CurrentSchemaVersion = 2;

    public TimeSpan Duration => Events.Count == 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Events[^1].TimestampMs);
}
