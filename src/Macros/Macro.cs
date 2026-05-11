namespace Labs626.UrTask.Macros;

/// <summary>
/// Top-level macro envelope. <see cref="BoundUserId"/> is the load-bearing
/// field — captured at record start from the foreground RoRoRo-managed
/// account; playback (task 6) refuses to fire unless the active foreground
/// window resolves to the same user id.
/// </summary>
public sealed record Macro(
    int SchemaVersion,
    string Id,
    string? Name,
    long BoundUserId,
    string BoundAccountId,
    string BoundDisplayName,
    long RecordedAtUnixMs,
    IReadOnlyList<MacroEvent> Events)
{
    /// <summary>Current schema version. Bump on shape changes.</summary>
    public const int CurrentSchemaVersion = 1;

    public TimeSpan Duration => Events.Count == 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Events[^1].TimestampMs);
}
