using System.Globalization;
using System.IO;

namespace Labs626.UrTask.Hotkeys;

/// <summary>
/// "I have the wheel": Ctrl+Shift+F11 toggles it. While on, every playback is aborted, Ctrl+Shift+P
/// and Ctrl+Shift+L are ignored, and the bridge refuses RunMacro / ClearAt / SweepPath with reason
/// "breakglass", so an agent driving Ur Task gets a clean no it can pause on instead of a mid-step
/// abort it retries. Asked for 2026-10-09 after the user grabbed the PC twice during an agent listing run
/// and each grab read as a bug: the macro aborted on a foreground shift, the agent's wrapper went on to
/// the next account, and keep-alive fought them for focus.
/// <para>The flag is a file (<see cref="FlagPath"/>) so scripts outside Ur Task (night-watch) can see
/// it without the pipe, and so it survives a restart: hands-off stays hands-off until the user releases
/// it. Turning it off restores keep-alive only if it was playing when it went on.</para>
/// </summary>
internal sealed class Breakglass
{
    public const string Chord = "Ctrl+Shift+F11";
    public const string RefusalReason = "breakglass";
    public const string RefusalDetail = "The user has taken the wheel (Ctrl+Shift+F11). Wait for 'Break-glass OFF' in ur-task.log.";

    private readonly object _gate = new();
    private bool _on;
    private bool _restoreKeepAlive;

    public Breakglass(string? flagPath = null)
    {
        FlagPath = flagPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "626Labs", "RoRoRoUrTask", "breakglass");
        // A flag left from a crash or restart means the user never handed back. Stay hands-off.
        _on = File.Exists(FlagPath);
    }

    public string FlagPath { get; }

    public bool IsOn { get { lock (_gate) return _on; } }

    /// <summary>Flip the state. <paramref name="keepAlivePlaying"/> is read only when turning on: it is
    /// what a later release restores. Returns the new state and whether keep-alive should restart.</summary>
    public (bool On, bool RestoreKeepAlive) Toggle(bool keepAlivePlaying, DateTime now)
    {
        lock (_gate)
        {
            if (!_on)
            {
                _on = true;
                _restoreKeepAlive = keepAlivePlaying;
                TryWriteFlag(now);
                return (true, false);
            }
            _on = false;
            var restore = _restoreKeepAlive;
            _restoreKeepAlive = false;
            TryDeleteFlag();
            return (false, restore);
        }
    }

    public static string OnLine(int abortedCount, bool keepAliveWasPlaying)
        => $"Break-glass ON ({Chord}): you have the wheel. Stopped {abortedCount} playback(s)"
           + (keepAliveWasPlaying ? ", keep-alive paused (comes back on release)." : ", keep-alive was off.")
           + " The bridge refuses macros until Ctrl+Shift+F11 again.";

    public static string OffLine(bool restoringKeepAlive)
        => $"Break-glass OFF ({Chord}): hands back." + (restoringKeepAlive ? " Restarting keep-alive." : "");

    public const string StillOnAtStartLine = "Break-glass still ON from before the restart (flag file present). Ctrl+Shift+F11 to hand back.";

    public static string IgnoredLine(string key) => $"{key} ignored: break-glass is on. Ctrl+Shift+F11 to hand back.";

    private void TryWriteFlag(DateTime now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
            File.WriteAllText(FlagPath, now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        }
        catch { /* the in-memory state still holds; night-watch just can't see it */ }
    }

    private void TryDeleteFlag()
    {
        try { File.Delete(FlagPath); } catch { }
    }
}
