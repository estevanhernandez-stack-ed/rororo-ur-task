namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure game-awareness logic for the macro library (v0.6). Kept static and
/// WPF-free so the view model stays a thin binding layer and the ordering /
/// filtering / mismatch rules are unit-testable.
/// </summary>
public static class MacroGameFilter
{
    /// <summary>
    /// Library ordering: macros scoped to a game someone is currently playing
    /// float first, then everything game-agnostic (all-games override or no
    /// stamp), then game-scoped macros for games nobody is running. Recency
    /// (newest first) orders within each band — the pre-v0.6 behavior.
    /// </summary>
    public static IReadOnlyList<Macro> Sort(IEnumerable<Macro> macros, IReadOnlySet<long> currentPlaceIds)
    {
        return macros
            .OrderBy(m => Band(m, currentPlaceIds))
            .ThenByDescending(m => m.RecordedAtUnixMs)
            .ToList();
    }

    private static int Band(Macro m, IReadOnlySet<long> currentPlaceIds)
    {
        if (!m.IsGameScoped) return 1;
        return currentPlaceIds.Contains(m.RecordedPlaceId!.Value) ? 0 : 2;
    }

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
