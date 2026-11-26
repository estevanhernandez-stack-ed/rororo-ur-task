using System.Diagnostics;
using System.Globalization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Stops every playback on the way out and gives it a moment to unwind. The players release held
/// keys and mouse buttons in their finally blocks, and those run only if the playback unwinds
/// before the process exits: quitting from the tray during a held W left the key down in the game.
/// Lives outside <c>PluginRuntime</c>, which cannot be built in a unit test.
/// </summary>
internal static class PlaybackShutdown
{
    /// <summary>How long to wait for playback to unwind. Short, because quitting must stay quick
    /// and the app's own exit only waits a few seconds for the whole dispose.</summary>
    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Calls every abort, each in its own try so one that throws cannot skip the rest, then polls
    /// <paramref name="isRunning"/> until it reports false or <paramref name="wait"/> runs out.
    /// Never throws. True when playback had stopped by the time it returned.
    /// </summary>
    /// <param name="aborts">What to stop, with a name for the log line if stopping it fails.</param>
    public static async Task<bool> AbortAndWaitAsync(
        IReadOnlyList<(string Name, Func<bool> Abort)> aborts, Func<bool> isRunning, Action<string> log,
        TimeSpan? wait = null, TimeSpan? poll = null)
    {
        foreach (var (name, abort) in aborts)
        {
            try { abort(); }
            catch (Exception ex) { Say(log, $"shutdown: couldn't stop {name}: {ex.Message}"); }
        }

        var limit = wait ?? DefaultWait;
        var step = poll ?? DefaultPoll;
        var clock = Stopwatch.StartNew();
        try
        {
            while (isRunning())
            {
                if (clock.Elapsed >= limit)
                {
                    Say(log, string.Create(CultureInfo.InvariantCulture,
                        $"shutdown: playback was still running after {limit.TotalSeconds:0.##} s; a held key or button may not have been released"));
                    return false;
                }
                await Task.Delay(step).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Say(log, $"shutdown: couldn't tell whether playback had stopped: {ex.Message}");
            return false;
        }
    }

    private static void Say(Action<string> log, string line)
    {
        try { log(line); } catch { /* the log is the last thing allowed to stop a shutdown */ }
    }
}
