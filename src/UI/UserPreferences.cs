using System.IO;
using System.Linq;
using System.Text.Json;

namespace Labs626.UrTask.UI;

/// <summary>
/// Persists window-level UI preferences (pin state per mode) to
/// %LOCALAPPDATA%\626Labs\RoRoRoUrTask\ui-prefs.json. Tiny + atomic
/// write. Load failures fall back to defaults silently — UI prefs are
/// best-effort, not load-bearing.
/// </summary>
internal sealed class UserPreferences
{
    private static readonly string PrefsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "RoRoRoUrTask", "ui-prefs.json");

    public bool TopmostInFullMode { get; set; }            // default: false (don't be obnoxious in full mode)
    public bool TopmostInCompactMode { get; set; } = true; // default: true (whole point of compact)
    public bool KeyboardOnlyRecording { get; set; } = true; // default: true (mouse coords are absolute-screen; safer keyboard-only)
    public bool AcceptPluginRunRequests { get; set; } = true; // default: true (sibling plugins like Ur-OCR can fire macros)

    /// <summary>
    /// Per-game keep-alive fire interval overrides, in MINUTES, keyed by Roblox
    /// PlaceId. Beats the shipped table in <see cref="Macros.KeepAliveIntervals"/>.
    /// Empty by default — populated only by the user or from observed presence data,
    /// never by guessing PlaceIds.
    ///
    /// The dictionary itself stays a plain settable property — System.Text.Json
    /// needs that to deserialize into it — but any UI-driven write should go
    /// through <see cref="SetKeepAliveOverrideMinutes"/>, not the indexer directly.
    /// </summary>
    public Dictionary<long, int> KeepAliveOverridesByPlaceId { get; set; } = new();

    // The settings surface must not accept garbage in the first place. A
    // 0/negative override reads as "always due" downstream — AssignmentRunner
    // would fire a keep-alive foreground-steal on every loop iteration, the exact
    // desktop-hijack spin loop this whole feature exists to kill. A pathologically
    // huge one risks the Task.Delay((int)ms) cast overflowing negative.
    // AssignmentRunner ALSO clamps defensively at consumption time (its own
    // Min/MaxKeepAliveIntervalMs, same 1-60 min range) — that is a second line of
    // defense, not a substitute for validating here.
    public const int MinKeepAliveOverrideMinutes = 1;
    public const int MaxKeepAliveOverrideMinutes = 60;

    /// <summary>
    /// Validated setter for a per-game keep-alive override — clamps to
    /// [<see cref="MinKeepAliveOverrideMinutes"/>, <see cref="MaxKeepAliveOverrideMinutes"/>]
    /// so this settings surface can never itself produce a hijack-loop or
    /// overflow-risking value.
    /// </summary>
    public void SetKeepAliveOverrideMinutes(long placeId, int minutes)
        => KeepAliveOverridesByPlaceId[placeId] = Math.Clamp(minutes, MinKeepAliveOverrideMinutes, MaxKeepAliveOverrideMinutes);

    /// <summary>
    /// Clamp every entry already in <see cref="KeepAliveOverridesByPlaceId"/> to the
    /// sane range — defense against a hand-edited or otherwise corrupted on-disk
    /// prefs file, which bypasses <see cref="SetKeepAliveOverrideMinutes"/> entirely
    /// (System.Text.Json deserializes straight into the dictionary). Called by
    /// <see cref="Load"/>; exposed separately so it's testable without disk I/O.
    /// </summary>
    public void SanitizeKeepAliveOverrides()
    {
        if (KeepAliveOverridesByPlaceId.Count == 0) return;
        foreach (var placeId in KeepAliveOverridesByPlaceId.Keys.ToList())
            KeepAliveOverridesByPlaceId[placeId] = Math.Clamp(
                KeepAliveOverridesByPlaceId[placeId], MinKeepAliveOverrideMinutes, MaxKeepAliveOverrideMinutes);
    }

    public static UserPreferences Load()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return new UserPreferences();
            var json = File.ReadAllText(PrefsPath);
            var prefs = JsonSerializer.Deserialize<UserPreferences>(json) ?? new UserPreferences();
            prefs.SanitizeKeepAliveOverrides();
            return prefs;
        }
        catch { return new UserPreferences(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PrefsPath, json);
        }
        catch { /* swallow — prefs are best-effort */ }
    }
}
