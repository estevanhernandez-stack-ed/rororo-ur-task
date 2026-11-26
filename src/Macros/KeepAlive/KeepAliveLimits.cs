using System.Text;
using Labs626.UrTask.PluginHost;
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

    /// <summary>True when the announce says which game the account is in. RoRoRo fills that in from
    /// presence, which arrives after the launch event: the first announce of a fresh launch carries none.</summary>
    public static bool HasGame(AccountRegistry.AccountInfo alt)
        => alt.PlaceId != 0 || !string.IsNullOrWhiteSpace(alt.PlaceName);

    /// <summary>True while any running account still has no game name: the cue to ask RoRoRo for its
    /// accounts again, because nothing re-sends an account when presence fills its game in.</summary>
    public static bool AnyGameMissing(IEnumerable<AccountRegistry.AccountInfo> running)
        => running.Any(a => string.IsNullOrWhiteSpace(a.PlaceName));

    /// <summary>
    /// The limit an account carries after it is announced again. An announce with a game is computed
    /// fresh, so a game that fills in late (or changes) moves the limit. An announce with no game is not
    /// news: the limit already held stays, so a blank never undoes what an earlier announce learned.
    /// </summary>
    public static KeepAliveLimit OnAnnounce(
        AccountRegistry.AccountInfo alt, KeepAliveLimit? held, Func<AccountRegistry.AccountInfo, KeepAliveLimit> compute)
        => held is not null && !HasGame(alt) ? held : compute(alt);

    /// <summary>
    /// Tracks an announced account in the ledger on the limit <see cref="OnAnnounce"/> gives it. Re-tracking
    /// keeps the account's input history and changes only its identity and limit. Returns the activity
    /// line to show when the limit of an already-tracked account changed, else null.
    /// </summary>
    public static string? TrackAnnounced(
        KeepAliveLedger ledger, AccountRegistry.AccountInfo alt, Func<AccountRegistry.AccountInfo, KeepAliveLimit> compute)
    {
        var held = ledger.Get(alt.RobloxUserId)?.Limit;
        var limit = OnAnnounce(alt, held, compute);
        ledger.Track(alt, limit);
        return held is not null && held != limit ? LimitChangedLine(alt, limit) : null;
    }

    /// <summary>What the activity log says when an account's limit changes because its game became known.</summary>
    public static string LimitChangedLine(AccountRegistry.AccountInfo alt, KeepAliveLimit limit)
    {
        var minutes = (int)limit.Limit.TotalMinutes;
        return string.IsNullOrWhiteSpace(alt.PlaceName)
            ? $"{alt.DisplayName} changed game: keep-alive now uses the {minutes}-minute limit for that game."
            : $"{alt.DisplayName} is in {alt.PlaceName.Trim()}: keep-alive now uses its {minutes}-minute limit.";
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
