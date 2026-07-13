using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class CadenceRunnerTests
{
    private const long Min = 60_000;
    private const long TwelveMin = 12 * Min;

    // AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId,
    //             long PlaceId = 0, string PlaceName = "")
    private static AccountRegistry.AccountInfo Alt(int pid) => new(pid, pid, $"alt{pid}", $"acct-{pid}");

    // Macro.Duration == the LAST event's TimestampMs. That is exactly what the
    // active-pass lookahead reads, so a macro's "length" is set by its last event.
    // MacroEvent(long TimestampMs, MacroEventKind Kind, int VirtualKeyCode,
    //            int X, int Y, int MouseButton, int WheelDelta)
    private static Macro MacroOfLength(long durationMs) => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: $"m{durationMs}",
        RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: 0, RecordedAtUnixMs: 0,
        Events: new List<MacroEvent> { new(durationMs, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0) });

    /// Leaps forward when the runner sleeps — a simulated hour costs no real time.
    private sealed class FakeClock
    {
        public long NowMs;
        public long Now() => NowMs;
        public Task Sleep(long durationMs, CancellationToken ct)
        {
            // Honor cancellation instead of silently completing. Without this, the
            // Important-2 fix (restore-on-cancel around the settle sleep) is
            // untestable under the fake clock: nothing here ever THROWS, so the
            // catch(OperationCanceledException) path this rig exists to exercise
            // never actually runs.
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (durationMs > 0) NowMs += durationMs;   // jump, never actually wait
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlayer : IMacroPlayer
    {
        public List<long> Plays { get; } = new();
        public bool IsPlaying => false;
        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;
        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            Plays.Add(targetUserId);
            return Task.FromResult(PlaybackResult.Completed());
        }
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
    }

    private sealed class FakeForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? Current;
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private sealed record Rig(
        AssignmentRunner Runner, FakeClock Clock, CancellationTokenSource Cts,
        List<int> Taps, List<int> Focused, List<IntPtr> Restored, FakePlayer Player, Func<int> Iterations);

    // Generous relative to any correctly gap-fit scenario in these tests (which need
    // at most low thousands of loop iterations for a simulated hour) — only a
    // busy-spin regression (e.g. a sleep collapsing to <= 0ms and never advancing the
    // clock, or Decide re-entering the same alt synchronously forever) should ever
    // reach it. Minor-1 tripwire: without this, that kind of regression HANGS the
    // test instead of failing it red.
    private const int MaxIterations = 200_000;

    /// A runner whose clock jumps, whose Space is COUNTED not injected, and which
    /// cancels itself once `runForMs` of simulated time has elapsed — or once
    /// <see cref="MaxIterations"/> loop iterations pass, whichever comes first, so a
    /// regression that busy-spins fails the test instead of hanging it.
    private static Rig Build(
        IReadOnlyList<Assignment> assignments, long runForMs, long keepAliveIntervalMs = TwelveMin,
        Func<int, (bool ok, string? error)>? focusOverride = null)
    {
        var clock = new FakeClock();
        var fg = new FakeForeground();
        var player = new FakePlayer();
        var cts = new CancellationTokenSource();
        var taps = new List<int>();
        var focused = new List<int>();
        var restored = new List<IntPtr>();
        var currentPid = 0;
        var iterations = 0;

        var deps = new CadenceDeps(
            Focus: pid =>
            {
                focused.Add(pid);
                currentPid = pid;
                if (focusOverride is not null) return focusOverride(pid);
                fg.Current = assignments.First(a => a.Alt.Pid == pid).Alt;   // so the verify passes
                return (true, null);
            },
            // AssignmentRunner reads the clock exactly once per loop iteration
            // (`var now = _deps.ClockMs();`, unconditionally, before the switch), so
            // this is the one hook that sees every iteration regardless of which
            // branch it takes — including a purely synchronous branch that never
            // calls Sleep at all (the focus-fail spin this tripwire exists to catch).
            ClockMs: () =>
            {
                if (Interlocked.Increment(ref iterations) > MaxIterations) cts.Cancel();
                return clock.Now();
            },
            Sleep: (ms, ct) =>
            {
                var t = clock.Sleep(ms, ct);
                if (clock.NowMs >= runForMs) cts.Cancel();   // end the simulation
                return t;
            },
            CaptureForeground: () => new IntPtr(0xBEEF),     // sentinel: "the user's window"
            RestoreForeground: h => restored.Add(h),
            SendKeepAlive: () => taps.Add(currentPid),       // counted, never injected
            KeepAliveIntervalMs: _ => keepAliveIntervalMs);

        return new Rig(
            new AssignmentRunner(player, fg, deps), clock, cts, taps, focused, restored, player, () => iterations);
    }

    /// THE regression. One keep-alive alt on a 12-minute interval, one simulated hour.
    /// Correct: ~5 taps. The old spin loop: ~2,880 — one every 1.25s, each stealing the
    /// user's foreground. If this count ever climbs back into the hundreds, the
    /// desktop-hijack bug is back and this test is the tripwire.
    [Fact]
    public async Task SingleKeepAliveAlt_OverASimulatedHour_IsTappedAboutFiveTimes_NotThousands()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 60 * Min);

        await rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);

        Assert.InRange(rig.Taps.Count, 4, 6);
        Assert.True(rig.Taps.Count < 20,
            $"keep-alive tapped {rig.Taps.Count}x in a simulated hour — the spin loop is back");
        Assert.True(rig.Iterations() < MaxIterations,
            "hit the busy-spin tripwire instead of completing normally — Minor-1's safety net fired");
    }

    /// Every foreground steal is paired with a restore, so a keep-alive is a ~1s blip
    /// rather than a hijack.
    [Fact]
    public async Task KeepAliveService_RestoresThePriorForeground()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 30 * Min);

        await rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);

        Assert.NotEmpty(rig.Taps);
        Assert.Equal(rig.Taps.Count, rig.Restored.Count);
        Assert.All(rig.Restored, h => Assert.Equal(new IntPtr(0xBEEF), h));
    }

    /// Compat guard: an all-Active squad still round-robins back-to-back, exactly as
    /// v0.6 did. No sleeping, no keep-alive taps.
    [Fact]
    public async Task AllActiveAssignments_RoundRobinBackToBack()
    {
        var a1 = new Assignment(Alt(1), MacroOfLength(1_000), CadenceRole.Active);
        var a2 = new Assignment(Alt(2), MacroOfLength(1_000), CadenceRole.Active);
        var rig = Build(new[] { a1, a2 }, runForMs: 2 * Min);

        await rig.Runner.RunAsync(new[] { a1, a2 }, rig.Cts.Token);

        Assert.Empty(rig.Taps);                       // nothing is on keep-alive
        Assert.Contains(1L, rig.Player.Plays);        // both alts farmed
        Assert.Contains(2L, rig.Player.Plays);
        Assert.True(rig.Player.Plays.Count > 5, "actives must run back-to-back, not sleep");
    }

    /// Gap-fitting end to end: a long Active pass must not starve the keep-alive.
    /// The 5-minute macro means the lookahead sees a keep-alive coming due inside the
    /// next pass and services it FIRST.
    [Fact]
    public async Task LongActivePass_StillLetsTheKeepAliveFire()
    {
        var active = new Assignment(Alt(1), MacroOfLength(5 * Min), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { active, keep }, runForMs: 60 * Min);

        await rig.Runner.RunAsync(new[] { active, keep }, rig.Cts.Token);

        Assert.NotEmpty(rig.Player.Plays);                    // farming still happened
        Assert.NotEmpty(rig.Taps);                            // and the keep-alive still got fed
        Assert.All(rig.Taps, pid => Assert.Equal(2, pid));    // only the keep-alive alt is tapped
    }

    /// CRITICAL regression, trigger 1 (the one that shipped): AttachAndFocus reports
    /// ok=true whenever the target process merely HAS a main window — it does not
    /// confirm the foreground actually flipped — so a keep-alive alongside an Active
    /// macro can reach ServiceKeepAliveAsync's foreground-verify check and fail it on
    /// EVERY attempt (fg.Current never actually becomes the keep-alive's account here).
    /// Before the forward-progress guard, Decide would immediately re-select the same
    /// not-yet-due alt next iteration (the retry backoff is only 30s; the lookahead
    /// horizon covers it) — a foreground steal roughly every ~1.2s, forever, with the
    /// Active alt fully starved (zero farming). The guard must force real progress
    /// (run the Active) between repeat non-due looks at the same stuck keep-alive, so
    /// farming isn't starved even though the keep-alive itself never resolves.
    [Fact]
    public async Task KeepAliveVerifyAlwaysFails_DoesNotStarveActives_AndNeverTapsAGhostForeground()
    {
        var active = new Assignment(Alt(1), MacroOfLength(60 * 1000), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { active, keep };

        // Custom rig (not the shared Build() happy-path Focus): Focus reports ok=true
        // for BOTH alts — matching AttachAndFocus's real contract — but the fake
        // foreground only ever actually flips for the ACTIVE alt. The keep-alive's
        // verify check is therefore permanently, deterministically stuck failing.
        var clock = new FakeClock();
        var fg = new FakeForeground();
        var player = new FakePlayer();
        var cts = new CancellationTokenSource();
        var taps = new List<int>();
        var focused = new List<int>();
        var iterations = 0;
        const long runForMs = 60 * Min;

        var deps = new CadenceDeps(
            Focus: pid =>
            {
                focused.Add(pid);
                if (pid == active.Alt.Pid) fg.Current = active.Alt;   // only the Active alt's focus "really" lands
                return (true, null);
            },
            ClockMs: () =>
            {
                if (Interlocked.Increment(ref iterations) > MaxIterations) cts.Cancel();
                return clock.Now();
            },
            Sleep: (ms, ct) =>
            {
                var t = clock.Sleep(ms, ct);
                if (clock.NowMs >= runForMs) cts.Cancel();
                return t;
            },
            CaptureForeground: () => new IntPtr(0xBEEF),
            RestoreForeground: _ => { },
            SendKeepAlive: () => taps.Add(2),
            KeepAliveIntervalMs: _ => TwelveMin);

        var runner = new AssignmentRunner(player, fg, deps);
        try { await runner.RunAsync(assignments, cts.Token); }
        catch (OperationCanceledException) { }

        Assert.Empty(taps);   // verify never once passes, so Space is never sent
        Assert.True(player.Plays.Count > 5, "the Active alt must keep farming despite the stuck keep-alive");
        Assert.True(iterations < MaxIterations, "hit the busy-spin tripwire — the guard didn't stop the hijack loop");

        // The core forward-progress invariant: the keep-alive may be re-attempted only
        // once per intervening Active pass (plus the one genuinely-due attempt before
        // any Active pass has run yet) — never back-to-back on itself.
        var keepAliveFocusAttempts = focused.Count(p => p == 2);
        Assert.True(keepAliveFocusAttempts <= player.Plays.Count + 1,
            $"keep-alive was focused {keepAliveFocusAttempts}x against only {player.Plays.Count} Active passes — " +
            "it was re-serviced without an intervening RunActive, the spin loop is back");
    }

    /// CRITICAL regression, trigger 2: a crashed/closed alt leaves a stale pid, so
    /// Focus (AttachAndFocus) fails on EVERY attempt. The failure branch returns
    /// without awaiting anything, so — pre-fix — nothing ever yields: Decide keeps
    /// re-selecting the same overdue alt, ServiceKeepAliveAsync keeps failing focus
    /// synchronously, and the loop never calls Sleep at all, meaning even the clock
    /// never advances. That's not just a hijack, it's a 100%-CPU non-yielding hang.
    /// The forward-progress guard's "no Active to fall back on" branch is what
    /// converts this into the intended bounded 30s retry poll instead.
    [Fact]
    public async Task KeepAliveFocusAlwaysFails_LoopYieldsInsteadOfSpinning()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 60 * Min, focusOverride: _ => (false, "window not found"));

        // Wall-clock safety net: a real regression here is a genuine synchronous
        // infinite loop, which no amount of fake-clock jumping will end on its own —
        // this bounds the test's own failure to a few seconds instead of a true hang.
        var run = rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Taps);                 // focus never once succeeds
        Assert.True(rig.Iterations() < MaxIterations,
            "hit the busy-spin tripwire — a stuck focus never yielded to the clock");
        // Bounded 30s retry, not a spin: roughly one attempt per 30s of simulated
        // time, comfortably under a per-1.25s cadence over a simulated hour.
        Assert.True(rig.Focused.Count < 500,
            $"keep-alive focus attempted {rig.Focused.Count}x in a simulated hour — the spin loop is back");
    }

    /// IMPORTANT regression: cancelling mid-settle (the 1s sleep between a successful
    /// Focus and the foreground-verify check) used to hit a bare
    /// `catch (OperationCanceledException) { return; }` that skipped RestoreForeground
    /// entirely — press Stop/Esc/Abort in that ~1s window and the alt window kept the
    /// desktop, the user's prior window was never handed back. The fix wraps the whole
    /// service in try/finally so every exit path restores.
    [Fact]
    public async Task KeepAliveService_CancelledMidSettle_StillRestoresForegroundBeforePropagating()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var clock = new FakeClock();
        var fg = new FakeForeground();
        var restored = new List<IntPtr>();
        var tapped = false;
        var cts = new CancellationTokenSource();
        var sleepCalls = 0;

        var deps = new CadenceDeps(
            Focus: pid => { fg.Current = alt.Alt; return (true, null); },
            ClockMs: clock.Now,
            Sleep: (ms, ct) =>
            {
                // The FIRST sleep of the whole run is exactly the post-focus settle
                // sleep inside ServiceKeepAliveAsync (the alt is due immediately, so
                // the very first loop iteration goes straight to servicing it).
                // Cancel right as that sleep is entered.
                if (Interlocked.Increment(ref sleepCalls) == 1) cts.Cancel();
                return clock.Sleep(ms, ct);
            },
            CaptureForeground: () => new IntPtr(0xBEEF),
            RestoreForeground: h => restored.Add(h),
            SendKeepAlive: () => tapped = true,
            KeepAliveIntervalMs: _ => TwelveMin);

        var runner = new AssignmentRunner(new FakePlayer(), fg, deps);

        try { await runner.RunAsync(new[] { alt }, cts.Token); }
        catch (OperationCanceledException) { /* acceptable unwind path */ }

        Assert.False(tapped, "cancellation landed before the tap — Space must not have been sent");
        Assert.Contains(new IntPtr(0xBEEF), restored);
    }
}
