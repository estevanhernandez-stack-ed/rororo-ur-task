using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Verifies the atomic single-flight claim on SequencePlayer.PlayAsync:
/// a second concurrent call must refuse (return an empty result) rather than
/// clobber the in-flight sequence's CancellationTokenSource.
/// </summary>
public class SequencePlayerReentryGuardTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static Macro NewMacro() => new(
        SchemaVersion: 2,
        Id: Guid.NewGuid().ToString(),
        Name: "guard-test",
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: 0,
        Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(int pid, long userId, string name)
        => new(pid, userId, name, $"acct-{pid}");

    /// <summary>
    /// Fake player that signals when it has been entered and then holds until
    /// the test releases it. This lets the test confirm the first PlayAsync
    /// call is mid-flight while the second is attempted.
    /// </summary>
    private sealed class BlockingFakePlayer : IMacroPlayer
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;

        public BlockingFakePlayer(
            TaskCompletionSource entered,
            TaskCompletionSource release)
        {
            _entered = entered;
            _release = release;
        }

        public bool IsPlaying => false;
        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;

        public async Task<PlaybackResult> PlayAsync(
            Macro macro,
            long targetUserId,
            CancellationToken external = default)
        {
            // Signal: we're inside PlayAsync
            _entered.TrySetResult();
            // Hold until the test releases us
            await _release.Task.ConfigureAwait(false);
            return PlaybackResult.Completed();
        }

        public Task<PlaybackResult> PlayAllWindowsRawAsync(
            Macro macro,
            CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());

        public bool Abort() => false;
    }

    private sealed class FakeForeground : IForegroundWatcher
    {
        public Func<AccountRegistry.AccountInfo?>? Resolver { get; set; }
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Resolver?.Invoke();
    }

    // ── the test ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayAsync_ConcurrentEntry_SecondCallRefusesWithEmptyResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingPlayer = new BlockingFakePlayer(entered, release);
        var fg = new FakeForeground();

        var target = Alt(1001, 47821334, "Goldnail8");

        // Foreground always reports our single target (so the focus-flip check passes)
        fg.Resolver = () => target;

        // focus func: always ok
        var sequence = new SequencePlayer(blockingPlayer, fg, _ => (true, null));

        // ── FIRST call: start but don't await yet ─────────────────────────
        var firstTask = sequence.PlayAsync(NewMacro(), new[] { target }, interAltDelayMs: 0);

        // Wait until BlockingFakePlayer.PlayAsync signals it's in-flight
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // At this point IsRunning must be true (guard is armed)
        Assert.True(sequence.IsRunning, "Expected sequence to be mid-flight.");

        // ── SECOND call: must be refused atomically ───────────────────────
        var secondResult = await sequence.PlayAsync(NewMacro(), new[] { target }, interAltDelayMs: 0);

        // The second call should return immediately with an empty/zero result
        Assert.Empty(secondResult.PerAlt);
        Assert.Equal(0, secondResult.Completed);
        Assert.Equal(0, secondResult.Failed);
        Assert.Equal(0, secondResult.Skipped);

        // ── Release the first call and verify it completed normally ───────
        release.TrySetResult();
        var firstResult = await firstTask;

        Assert.Equal(1, firstResult.Completed);
        Assert.Equal(0, firstResult.Failed);
        Assert.Equal(0, firstResult.Skipped);
    }
}
