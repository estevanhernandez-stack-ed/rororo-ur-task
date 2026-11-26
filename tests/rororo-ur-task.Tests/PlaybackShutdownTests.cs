using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Quitting mid-playback (L2-02). The players release held keys and mouse buttons in their finally
/// blocks, which run only if the playback unwinds before the process exits. PluginRuntime's
/// DisposeAsync never aborted them, so tray Quit during a held W left the key down in the game.
/// PluginRuntime cannot be built in a unit test, so its abort-and-wait lives in this helper.
/// </summary>
public class PlaybackShutdownTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task Aborts_everything_then_waits_for_playback_to_unwind()
    {
        var aborted = new List<string>();
        bool running = true;
        var released = new TaskCompletionSource();
        var aborts = new (string, Func<bool>)[]
        {
            ("the routine", () => { aborted.Add("routine"); return false; }),
            ("the keep-alive loop", () => { aborted.Add("runner"); return true; }),
            ("the sequence", () => { aborted.Add("sequence"); return true; }),
            // The player's finally (which lets go of held keys) runs a moment after the abort.
            ("the player", () =>
            {
                aborted.Add("player");
                _ = Task.Run(async () => { await Task.Delay(60); running = false; released.TrySetResult(); });
                return true;
            }),
        };

        var stopped = await PlaybackShutdown.AbortAndWaitAsync(aborts, () => running, _ => { }, TimeSpan.FromSeconds(5), Poll);

        Assert.True(stopped);
        Assert.Equal(new[] { "routine", "runner", "sequence", "player" }, aborted);
        Assert.True(released.Task.IsCompleted, "returned before the playback had unwound");
    }

    [Fact]
    public async Task One_abort_that_throws_does_not_skip_the_others_and_leaves_a_line()
    {
        var aborted = new List<string>();
        var log = new List<string>();
        var aborts = new (string, Func<bool>)[]
        {
            ("the keep-alive loop", () => throw new ObjectDisposedException("cts")),
            ("the sequence", () => { aborted.Add("sequence"); return true; }),
            ("the player", () => { aborted.Add("player"); return true; }),
        };

        var stopped = await PlaybackShutdown.AbortAndWaitAsync(aborts, () => false, log.Add, TimeSpan.FromSeconds(1), Poll);

        Assert.True(stopped);
        Assert.Equal(new[] { "sequence", "player" }, aborted);
        var line = Assert.Single(log);
        Assert.StartsWith("shutdown: couldn't stop the keep-alive loop:", line);
    }

    [Fact]
    public async Task Gives_up_after_the_wait_and_says_so_when_playback_never_unwinds()
    {
        var log = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var stopped = await PlaybackShutdown.AbortAndWaitAsync(
            new (string, Func<bool>)[] { ("the player", () => true) },
            isRunning: () => true, log.Add, wait: TimeSpan.FromMilliseconds(150), Poll);

        Assert.False(stopped);
        Assert.InRange(clock.ElapsedMilliseconds, 100, 3000); // bounded: quitting never hangs on it
        Assert.Equal("shutdown: playback was still running after 0.15 s; a held key or button may not have been released", Assert.Single(log));
    }

    [Fact]
    public async Task Does_not_wait_at_all_when_nothing_is_running()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var stopped = await PlaybackShutdown.AbortAndWaitAsync(
            new (string, Func<bool>)[] { ("the player", () => false) }, () => false, _ => { }, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        Assert.True(stopped);
        Assert.True(clock.ElapsedMilliseconds < 2000);
    }

    [Fact]
    public async Task A_running_probe_that_throws_ends_the_wait_instead_of_the_shutdown()
    {
        var log = new List<string>();
        var stopped = await PlaybackShutdown.AbortAndWaitAsync(
            new (string, Func<bool>)[] { ("the player", () => true) },
            isRunning: () => throw new InvalidOperationException("probe broke"), log.Add, TimeSpan.FromSeconds(1), Poll);
        Assert.False(stopped);
        Assert.StartsWith("shutdown: couldn't tell whether playback had stopped:", Assert.Single(log));
    }

    [Fact]
    public void The_default_wait_is_about_a_second()
        => Assert.Equal(TimeSpan.FromSeconds(1), PlaybackShutdown.DefaultWait);
}
