using System.Text;
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Macros;

/// <summary>
/// How long each game lets an account sit without input (the LIMIT), and the
/// DEADLINE by which Ur Task must have sent some (limit minus a fixed margin).
///
/// Sourced from rororo-ur-afk/docs/game-idle-timings.md (2026-07-06), refined by
/// measurement:
///  - Roblox disconnects idle players after 20 min. That is a platform FLOOR,
///    games may shorten it, none may extend it. Detection is input-absence; a
///    single Space resets it. Movement does not count.
///  - Games shipping their own anti-AFK (~15 min self-rejoin): limit 15.
///    PS99 measured 2026-10-25: 15 to 16.5 min.
///  - Games with none: limit 20. Unknown games: 18.
///
/// Keyed by normalised game NAME because the research is, and because no verified
/// Roblox PlaceIds are in hand. PlaceId is supported only as an exact user override,
/// read as the DEADLINE. DO NOT populate PlaceIds by guessing them.
/// </summary>
internal static class KeepAliveLimits
{
    public const int DeadlineMarginMinutes = 6;
    public const int MinDeadlineMinutes = 2;
    public const int PlatformIdleKickFloorMinutes = 20;

    private const int OwnAntiAfkLimitMinutes = 15;
    private const int NoAntiAfkLimitMinutes = 20;
    private const int UnknownLimitMinutes = 18;

    // Games that ship NO anti-AFK, Ur Task is the only thing keeping them alive.
    private static readonly string[] NoAntiAfkGames =
    [
        "grow a garden", "adopt me", "brookhaven rp", "bee swarm simulator", "blox fruits",
    ];

    // Games with their own anti-AFK teleport/rejoin.
    private static readonly string[] OwnAntiAfkGames =
    [
        "pet simulator 99", "fisch", "anime vanguards", "blade ball",
    ];

    public static KeepAliveLimit For(long? placeId, string? placeName, UserPreferences prefs)
    {
        // An explicit user override always wins, and is the deadline itself.
        if (placeId is long id && prefs.KeepAliveOverridesByPlaceId.TryGetValue(id, out var mins))
        {
            var clamped = mins < MinDeadlineMinutes;
            var deadline = Math.Max(mins, MinDeadlineMinutes);
            return new KeepAliveLimit(
                TimeSpan.FromMinutes(deadline + DeadlineMarginMinutes),
                TimeSpan.FromMinutes(deadline),
                clamped);
        }

        var key = NormalizeGameName(placeName);
        var limit = UnknownLimitMinutes;
        if (key.Length > 0)
        {
            if (Array.Exists(NoAntiAfkGames, g => g == key)) limit = NoAntiAfkLimitMinutes;
            else if (Array.Exists(OwnAntiAfkGames, g => g == key)) limit = OwnAntiAfkLimitMinutes;
        }
        return new KeepAliveLimit(
            TimeSpan.FromMinutes(limit), TimeSpan.FromMinutes(limit - DeadlineMarginMinutes), false);
    }

    /// <summary>
    /// Lower-case, drop bracketed segments ([...] and (...)), keep only letters,
    /// digits and spaces, collapse runs of spaces, trim. Lets a decorated presence
    /// name match its plain table entry.
    /// </summary>
    internal static string NormalizeGameName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(name.Length);
        int depth = 0;
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch == '[' || ch == '(') { depth++; continue; }
            if (ch == ']' || ch == ')') { if (depth > 0) depth--; continue; }
            if (depth > 0) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
