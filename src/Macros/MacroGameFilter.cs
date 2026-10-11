namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure game-awareness logic for the macro library (v0.6). Kept static and
/// WPF-free so the view model stays a thin binding layer and the filtering /
/// mismatch rules are unit-testable. Library ORDER no longer lives here: since
/// the grouped library it comes from the names (see <see cref="MacroLibrary"/>),
/// and the game band survives only as the PLAYING NOW filter below.
/// </summary>
public static class MacroGameFilter
{
    /// <summary>
    /// PLAYING NOW filter: keep macros matching a currently-played game plus
    /// everything game-agnostic. With no games running (empty set), the filter
    /// is a no-op — hiding the whole library because no alt is up would read
    /// as data loss.
    /// </summary>
    public static IReadOnlyList<Macro> FilterPlayingNow(IEnumerable<Macro> macros, IReadOnlySet<long> currentPlaceIds)
    {
        if (currentPlaceIds.Count == 0) return macros.ToList();
        return macros
            .Where(m => !m.IsGameScoped || currentPlaceIds.Contains(m.RecordedPlaceId!.Value))
            .ToList();
    }

    /// <summary>
    /// True when a game-scoped macro is paired against an alt known to be in a
    /// DIFFERENT game. Unknown alt game (0) is never a mismatch — soft metadata
    /// stays advisory, matching the RecordedAgainstUserId philosophy.
    /// </summary>
    public static bool IsMismatch(Macro? macro, long altPlaceId)
        => macro is { } m && m.IsGameScoped && altPlaceId > 0 && altPlaceId != m.RecordedPlaceId;
}
