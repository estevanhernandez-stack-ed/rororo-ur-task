using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Turbo keep-alive: one warned sweep across every keep-alive alt due within the
/// batch window, a polled foreground confirm instead of the fixed 1s settle, and a
/// single restore at sweep end. Same fake-clock rig shape as CadenceRunnerTests:
/// the clock JUMPS on Sleep, Space is counted never injected.
/// </summary>
public class TurboKeepAliveTests
{
    private const long Min = 60_000;
    private const long TwelveMin = 12 * Min;
    private const int MaxIterations = 200_000;
    private static readonly IntPtr UserWindow = new(0xBEEF);

    private static AccountRegistry.AccountInfo Alt(int pid) => new(pid, pid, $"alt{pid}", $"acct-{pid}");

    private sealed class FakeClock
    {
        public long NowMs;
        public long Now() => NowMs;
        public Task Sleep(long durationMs, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (durationMs > 0) NowMs += durationMs;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlayer : IMacroPlayer
    {
        public bool IsPlaying => false;
        public event EventHandler<PlaybackStartedArgs>? Started { add { } remove { } }
        public event EventHandler<PlaybackEndedArgs>? Ended { add { } remove { } }
        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
    }

    /// Foreground that lands only once the clock reaches <see cref="LandsAtMs"/> — lets
    /// a test model "the flip takes 45ms" or "the flip never happens".
    private sealed class DelayedForeground : IForegroundWatcher
    {
        private readonly FakeClock _clock;
        public DelayedForeground(FakeClock clock) => _clock = clock;
        public AccountRegistry.AccountInfo? Target;
        public long LandsAtMs = long.MaxValue;
        public AccountRegistry.AccountInfo? ResolveForegroundAccount()
            => _clock.NowMs >= LandsAtMs ? Target : null;
    }

    private sealed class Rig
    {
        public required AssignmentRunner Runner;
        public required FakeClock Clock;
        public required CancellationTokenSource Cts;
        public required DelayedForeground Fg;
        public List<int> Taps { get; } = new();
        public List<long> TapTimes { get; } = new();
        public List<(int pid, long at)> Focused { get; } = new();
        public List<(IntPtr h, long at)> Restored { get; } = new();
        public List<(int s, int n, long at)> Countdown { get; } = new();
        public int Iterations;
    }

    /// <param name="flipDelayMs">ms after Focus before the foreground reads as the alt; null = never.</param>
    /// <param name="intervalFor">per-pid keep-alive interval; default 12 min.</param>
    private static Rig Build(
        IReadOnlyList<Assignment> assignments, long runForMs, long? flipDelayMs = 0,
        Func<int, long>? intervalFor = null, bool turbo = true,
        Action<Rig>? onTap = null)
    {
        var clock = new FakeClock();
        var fg = new DelayedForeground(clock);
        var cts = new CancellationTokenSource();
        Rig rig = null!;
        var currentPid = 0;

        var deps = new CadenceDeps(
            Focus: pid =>
            {
                rig.Focused.Add((pid, clock.NowMs));
                currentPid = pid;
                fg.Target = assignments.First(a => a.Alt.Pid == pid).Alt;
                fg.LandsAtMs = flipDelayMs is null ? long.MaxValue : clock.NowMs + flipDelayMs.Value;
                return (true, null);
            },
            ClockMs: () =>
            {
                if (Interlocked.Increment(ref rig.Iterations) > MaxIterations) cts.Cancel();
                return clock.Now();
            },
            Sleep: (ms, ct) =>
            {
                var t = clock.Sleep(ms, ct);
                if (clock.NowMs >= runForMs) cts.Cancel();
                return t;
            },
            CaptureForeground: () => UserWindow,
            RestoreForeground: h => rig.Restored.Add((h, clock.NowMs)),
            SendKeepAlive: () =>
            {
                rig.Taps.Add(currentPid);
                rig.TapTimes.Add(clock.NowMs);
                onTap?.Invoke(rig);
            },
            KeepAliveIntervalMs: alt => intervalFor?.Invoke(alt.Pid) ?? TwelveMin)
        {
            SweepCountdown = (s, n) => rig.Countdown.Add((s, n, clock.NowMs)),
        };

        rig = new Rig
        {
            Runner = new AssignmentRunner(new FakePlayer(), fg, deps) { TurboKeepAlive = turbo },
            Clock = clock, Cts = cts, Fg = fg,
        };
        return rig;
    }

    private static int SweepsStarted(Rig rig)
        => rig.Countdown.Count(c => c.s == AssignmentRunner.TurboCountdownSeconds);

    /// (a) Two keep-alive alts on staggered intervals (11 and 12 min). After the first
    /// sweep they drift apart by a minute, so without batching they would be serviced
    /// separately. Turbo pulls the not-yet-due one into the due one's sweep: every
    /// sweep covers both, with ONE countdown and ONE restore.
    [Fact]
    public async Task Turbo_BatchesEveryAltDueWithinTheWindow_IntoOneSweepWithOneCountdown()
    {
        var a1 = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var a2 = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { a1, a2 };
        var rig = Build(assignments, runForMs: 60 * Min,
            intervalFor: pid => pid == 1 ? 12 * Min : 11 * Min);

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        var sweeps = SweepsStarted(rig);
        Assert.InRange(sweeps, 4, 7);                                   // ~one per 11 min, not one per alt
        Assert.All(rig.Countdown.Where(c => c.s == 3), c => Assert.Equal(2, c.n));
        Assert.Equal(2 * sweeps, rig.Taps.Count);                       // both alts tapped every sweep
        Assert.Equal(sweeps, rig.Restored.Count);                       // one restore per sweep, not per alt
        Assert.All(rig.Restored, r => Assert.Equal(UserWindow, r.h));

        // Countdown is 3, 2, 1 then 0 (hide), a second apart, BEFORE any focus.
        var first = rig.Countdown.Take(4).ToList();
        Assert.Equal(new[] { 3, 2, 1, 0 }, first.Select(c => c.s));
        Assert.Equal(3_000, first[3].at - first[0].at);
        Assert.True(rig.Focused[0].at >= first[3].at, "a window was jumped before the countdown finished");
    }

    /// (b) The foreground flips 45ms after Focus. The polled confirm sends Space as
    /// soon as it reads the alt: well under the old fixed 1000ms settle.
    [Fact]
    public async Task Turbo_PolledConfirm_SendsAsSoonAsTheForegroundMatches()
    {
        var a1 = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { a1 }, runForMs: 5 * Min, flipDelayMs: 45);

        await rig.Runner.RunAsync(new[] { a1 }, rig.Cts.Token);

        Assert.NotEmpty(rig.Taps);
        var focusToTap = rig.TapTimes[0] - rig.Focused[0].at;
        Assert.InRange(focusToTap, 45 + AssignmentRunner.TurboSettleMs, 45 + AssignmentRunner.TurboPollIntervalMs + AssignmentRunner.TurboSettleMs);
        Assert.True(focusToTap < 1000, $"turbo took {focusToTap}ms from focus to Space; the 1s settle is back");

        // Whole per-alt cost: focus to restore = confirm + post-key wait.
        var focusToRestore = rig.Restored[0].at - rig.Focused[0].at;
        Assert.True(focusToRestore <= 45 + AssignmentRunner.TurboPollIntervalMs + AssignmentRunner.TurboSettleMs + AssignmentRunner.TurboPostKeyMs,
            $"per-alt sweep cost was {focusToRestore}ms");
    }

    /// (c) The foreground never reads as the alt. The cap expires, NO Space is ever
    /// sent, and the alt backs off ~30s instead of spinning.
    [Fact]
    public async Task Turbo_CapExpiryWithoutMatch_SendsNoInput_AndBacksOff()
    {
        var a1 = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { a1 }, runForMs: 5 * Min, flipDelayMs: null);

        await rig.Runner.RunAsync(new[] { a1 }, rig.Cts.Token);

        Assert.Empty(rig.Taps);
        Assert.True(rig.Focused.Count >= 2, "need two attempts to measure the backoff");
        Assert.True(rig.Focused.Count < 20, $"focused {rig.Focused.Count}x in 5 min; the retry is spinning");
        var gaps = rig.Focused.Zip(rig.Focused.Skip(1), (x, y) => y.at - x.at).ToList();
        Assert.All(gaps, g => Assert.True(g >= 30_000, $"retried after {g}ms; the 30s backoff is gone"));

        // The confirm gave up at the cap, not at a full second: focus -> restore is
        // bounded by the cap plus one poll step.
        var first = rig.Restored[0].at - rig.Focused[0].at;
        Assert.InRange(first, AssignmentRunner.TurboConfirmCapMs,
            AssignmentRunner.TurboConfirmCapMs + AssignmentRunner.TurboPollIntervalMs);
        Assert.True(rig.Iterations < MaxIterations, "hit the busy-spin tripwire");
    }

    /// (d) Restore happens once at sweep end, not between alts — and on cancellation
    /// mid-sweep it still happens, exactly once, and no later alt is touched.
    [Fact]
    public async Task Turbo_RestoresPriorForegroundOnce_AtSweepEnd()
    {
        var alts = new[] { 1, 2, 3 }.Select(p => new Assignment(Alt(p), null, CadenceRole.KeepAlive)).ToArray();
        var rig = Build(alts, runForMs: 1 * Min);

        await rig.Runner.RunAsync(alts, rig.Cts.Token);

        Assert.Equal(new[] { 1, 2, 3 }, rig.Taps);
        var restore = Assert.Single(rig.Restored);
        Assert.Equal(UserWindow, restore.h);
        Assert.True(restore.at >= rig.TapTimes.Last(), "restored before the last alt was serviced");
    }

    [Fact]
    public async Task Turbo_CancelledMidSweep_RestoresOnce_AndStopsTouchingAlts()
    {
        var alts = new[] { 1, 2, 3 }.Select(p => new Assignment(Alt(p), null, CadenceRole.KeepAlive)).ToArray();
        // Abort hotkey fires the instant the first alt gets its Space.
        var rig = Build(alts, runForMs: 30 * Min, onTap: r => r.Cts.Cancel());

        await rig.Runner.RunAsync(alts, rig.Cts.Token);

        Assert.Equal(new[] { 1 }, rig.Taps);                          // alts 2 and 3 never got input
        Assert.Equal(new[] { 1 }, rig.Focused.Select(f => f.pid));    // ...nor even a focus
        var restore = Assert.Single(rig.Restored);
        Assert.Equal(UserWindow, restore.h);
    }

    [Fact]
    public async Task Turbo_CancelledDuringCountdown_TakesNothing_AndHidesTheWarning()
    {
        var alts = new[] { 1, 2 }.Select(p => new Assignment(Alt(p), null, CadenceRole.KeepAlive)).ToArray();

        // Abort hotkey fires as the "2" of the countdown is shown.
        var clock = new FakeClock();
        var fg = new DelayedForeground(clock);
        var cts2 = new CancellationTokenSource();
        var focused = new List<int>();
        var restored = new List<IntPtr>();
        var countdown = new List<int>();
        var taps = 0;
        var deps = new CadenceDeps(
            Focus: pid => { focused.Add(pid); return (true, null); },
            ClockMs: clock.Now,
            Sleep: clock.Sleep,
            CaptureForeground: () => UserWindow,
            RestoreForeground: restored.Add,
            SendKeepAlive: () => taps++,
            KeepAliveIntervalMs: _ => TwelveMin)
        {
            SweepCountdown = (s, n) => { countdown.Add(s); if (s == 2) cts2.Cancel(); },
        };
        var r2 = new AssignmentRunner(new FakePlayer(), fg, deps) { TurboKeepAlive = true };

        await r2.RunAsync(alts, cts2.Token);

        Assert.Equal(new[] { 3, 2, 0 }, countdown);   // hidden on the way out
        Assert.Empty(focused);
        Assert.Empty(restored);
        Assert.Equal(0, taps);
    }

    /// (e) Turbo off: no countdown, ever, and the old one-alt-at-a-time shape holds
    /// (one restore per tap, 1s+ from focus to Space).
    [Fact]
    public async Task TurboOff_IsTheOldPath_NoCountdown_RestorePerAlt()
    {
        var a1 = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var a2 = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { a1, a2 };
        var rig = Build(assignments, runForMs: 30 * Min, turbo: false);

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        Assert.Empty(rig.Countdown);
        Assert.NotEmpty(rig.Taps);
        Assert.Equal(rig.Taps.Count, rig.Restored.Count);
        Assert.True(rig.TapTimes[0] - rig.Focused[0].at >= 1000, "turbo-off lost its 1s settle");
    }

    /// Opt-in: off on a fresh runner and a fresh prefs file.
    [Fact]
    public void TurboKeepAlive_DefaultsOff()
    {
        var runner = new AssignmentRunner(new FakePlayer(), new DelayedForeground(new FakeClock()), _ => (true, null));
        Assert.False(runner.TurboKeepAlive);
        Assert.False(new Labs626.UrTask.UI.UserPreferences().TurboKeepAlive);
    }
}
