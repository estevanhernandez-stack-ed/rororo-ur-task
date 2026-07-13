using Labs626.UrTask.UI;

namespace Labs626.UrTask.Macros;

/// <summary>
/// How often to fire a keep-alive Space for an alt, by the game it's in.
///
/// NAMED "Intervals", NOT "Thresholds", on purpose: these are FIRE intervals —
/// when we act — with headroom under Roblox's 20-minute idle floor ALREADY baked
/// in. Calling them thresholds invites a caller to helpfully apply a safety margin
/// to a number that already has one. Never multiply these down.
///
/// Sourced from rororo-ur-afk/docs/game-idle-timings.md (2026-07-06):
///  - Roblox disconnects idle players after 20 min. That is a platform FLOOR —
///    games may shorten it, none may extend it. Detection is input-absence; a
///    single Space resets it. Movement does not count.
///  - Games shipping their own anti-AFK (~15 min self-rejoin) only need us as a
///    BACKSTOP -> 17 min. Games with none need us as PRIMARY keeper -> 11 min.
///  - Unknown games assume no help -> 12 min.
///
/// Keyed by game NAME because the research is, and because no verified Roblox
/// PlaceIds are in hand. PlaceId is supported only as an exact user override.
/// DO NOT populate PlaceIds by guessing them.
/// </summary>
internal static class KeepAliveIntervals
{
    public const int UnknownGameMinutes = 12;
    private const int PrimaryKeeperMinutes = 11;
    private const int BackstopMinutes = 17;

    // Games that ship NO anti-AFK — Ur Task is the only thing keeping them alive.
    private static readonly string[] PrimaryKeeperGames =
    [
        "grow a garden", "adopt me", "brookhaven rp", "bee swarm simulator", "blox fruits",
    ];

    // Games with their own anti-AFK teleport/rejoin — we're insurance, not the keeper.
    private static readonly string[] BackstopGames =
    [
        "pet simulator 99", "fisch", "anime vanguards", "blade ball",
    ];

    public static TimeSpan For(long? placeId, string? placeName, UserPreferences prefs)
    {
        // An explicit user override always wins — our table is [community] confidence.
        if (placeId is long id && prefs.KeepAliveOverridesByPlaceId.TryGetValue(id, out var mins))
            return TimeSpan.FromMinutes(mins);

        var key = Normalize(placeName);
        if (key.Length > 0)
        {
            if (Array.Exists(PrimaryKeeperGames, g => g == key)) return TimeSpan.FromMinutes(PrimaryKeeperMinutes);
            if (Array.Exists(BackstopGames, g => g == key)) return TimeSpan.FromMinutes(BackstopMinutes);
        }
        return TimeSpan.FromMinutes(UnknownGameMinutes);
    }

    private static string Normalize(string? name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();
}
