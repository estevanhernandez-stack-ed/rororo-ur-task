using System.IO;
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

    public static UserPreferences Load()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return new UserPreferences();
            var json = File.ReadAllText(PrefsPath);
            return JsonSerializer.Deserialize<UserPreferences>(json) ?? new UserPreferences();
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
