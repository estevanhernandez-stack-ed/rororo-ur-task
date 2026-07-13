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

        // Test seam for the pass-cost ratchet: lets a macro's REAL cost diverge from
        // its DECLARED Macro.Duration, mirroring what MacroPlayer.EnsureClientSize
        // actually does in production — a synchronous Win32 window resize that costs
        // real time but never shows up in the macro's recorded event data at all.
        // Defaults to a no-op Sleep, so every test that doesn't set these is unaffected.
        public long RealPlayCostMs { get; set; }
        public Func<long, CancellationToken, Task> AdvanceClockBy { get; set; } = (_, _) => Task.CompletedTask;

        public async Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            Plays.Add(targetUserId);
            if (RealPlayCostMs > 0) await AdvanceClockBy(RealPlayCostMs, external).ConfigureAwait(false);
            return PlaybackResult.Completed();
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
        List<int> Taps, List<long> TapTimes, List<int> Focused, List<IntPtr> Restored,
        FakePlayer Player, Func<int> Iterations);

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
        var player = new FakePlayer { AdvanceClockBy = clock.Sleep };
        var cts = new CancellationTokenSource();
        var taps = new List<int>();
        var tapTimes = new List<long>();
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
            SendKeepAlive: () => { taps.Add(currentPid); tapTimes.Add(clock.NowMs); },  // counted, never injected
            KeepAliveIntervalMs: _ => keepAliveIntervalMs);

        return new Rig(
            new AssignmentRunner(player, fg, deps), clock, cts, taps, tapTimes, focused, restored, player,
            () => iterations);
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
    ///
    /// The Active macro is deliberately 15 minutes — LONGER than the 12-minute
    /// keep-alive interval. That's the actual trigger condition for the livelock this
    /// guard exists to stop: the gap-fit lookahead in <see cref="CadenceScheduler.Decide"/>
    /// only ever re-flags a not-yet-due keep-alive as "urgent" when a single Active
    /// pass could outrun the time remaining until it's genuinely due — which requires
    /// passCost to be comparable to (here, greater than) the interval itself. A short
    /// macro (e.g. 60s against a 12-minute interval) can never manufacture that
    /// condition, so a test built on one is a TAUTOLOGY: it passes whether or not the
    /// guard exists, because the guard's branch is never even reached. Verified by
    /// hand: reverting the guard (always calling ServiceKeepAliveAsync directly,
    /// dropping the gapFittedSinceActivePass forced-progress branch) turns this RED —
    /// the Active alt is fully starved (zero plays) and the keep-alive is
    /// re-attempted thousands of times without ever completing a pass in between.
    [Fact]
    public async Task KeepAliveVerifyAlwaysFails_DoesNotStarveActives_AndNeverTapsAGhostForeground()
    {
        var active = new Assignment(Alt(1), MacroOfLength(15 * Min), CadenceRole.Active);
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
        const long runForMs = 90 * Min;

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
        Assert.True(player.Plays.Count >= 3, "the Active alt must keep farming despite the stuck keep-alive");
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
    ///
    /// Requires an Active alt with a pass cost >= the keep-alive interval, same as the
    /// verify-fail test above and for the same reason: with ZERO Actives present,
    /// NextActivePassCostMs is always 0, so Decide's urgency horizon collapses to
    /// exactly `now` — "urgent via gap-fit" and "genuinely overdue" become the same
    /// condition, and the guard's non-genuinely-overdue branch can never fire. A
    /// keep-alive-only fixture literally cannot distinguish a guarded runner from an
    /// unguarded one (this is the same fact that makes the guard's old
    /// `firstActive is null` fallback dead code — removed as Minor-1). Verified by
    /// hand: reverting the guard turns this RED too — the keep-alive is refocused
    /// thousands of times with the Active fully starved.
    [Fact]
    public async Task KeepAliveFocusAlwaysFails_LoopYieldsInsteadOfSpinning()
    {
        var active = new Assignment(Alt(1), MacroOfLength(15 * Min), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { active, keep };

        // Custom rig, mirroring the verify-fail test above: Focus reports ok=FALSE for
        // the keep-alive on every attempt (a crashed/closed alt's stale pid) but
        // succeeds — and really lands — for the Active alt.
        var clock = new FakeClock();
        var fg = new FakeForeground();
        var player = new FakePlayer();
        var cts = new CancellationTokenSource();
        var taps = new List<int>();
        var focused = new List<int>();
        var restored = new List<IntPtr>();
        var iterations = 0;
        const long runForMs = 90 * Min;

        var deps = new CadenceDeps(
            Focus: pid =>
            {
                focused.Add(pid);
                if (pid == keep.Alt.Pid) return (false, "window not found");
                fg.Current = active.Alt;
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
            RestoreForeground: h => restored.Add(h),
            SendKeepAlive: () => taps.Add(2),
            KeepAliveIntervalMs: _ => TwelveMin);

        var runner = new AssignmentRunner(player, fg, deps);

        // Wall-clock safety net: a real regression here is a genuine synchronous
        // infinite loop, which no amount of fake-clock jumping will end on its own —
        // this bounds the test's own failure to a few seconds instead of a true hang.
        var run = runner.RunAsync(assignments, cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(taps);                     // keep-alive focus never once succeeds
        Assert.Empty(restored);                 // Minor-4: focus never landed, so nothing to restore
        Assert.True(iterations < MaxIterations,
            "hit the busy-spin tripwire — a stuck focus never yielded to the clock");
        Assert.True(player.Plays.Count >= 3, "the Active alt must keep farming despite the stuck keep-alive");

        // Same forward-progress invariant as the verify-fail test: the keep-alive is
        // re-attempted only once per intervening Active pass, never back-to-back.
        var keepAliveFocusAttempts = focused.Count(p => p == keep.Alt.Pid);
        Assert.True(keepAliveFocusAttempts <= player.Plays.Count + 1,
            $"keep-alive was focused {keepAliveFocusAttempts}x against only {player.Plays.Count} Active passes — " +
            "it was re-serviced without an intervening RunActive, the spin loop is back");
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

    /// <summary>
    /// THE highest-value missing test: the pass-cost ratchet. A macro's DECLARED
    /// duration (Macro.Duration, the last event's timestamp) is only an estimate —
    /// MacroPlayer.EnsureClientSize does unmodeled synchronous Win32 window resizing
    /// that never shows up in the macro's recorded events at all, so the REAL cost of
    /// a pass can run far longer than what NextActivePassCostMs's static formula sees.
    /// RunActiveAsync times every pass end to end and ratchets
    /// ScheduledAlt.ObservedPassCostMs up so later lookaheads use the real number —
    /// without that, a keep-alive's deadline can be missed by the full overrun of a
    /// single pass, because Decide is only consulted BETWEEN passes and can't preempt
    /// one already running past its estimate.
    ///
    /// Here the macro DECLARES 10 seconds but really costs 10 minutes (via
    /// FakePlayer's AdvanceClockBy seam) against a 12-minute keep-alive interval.
    /// Verified by hand: reverting the ratchet (NextActivePassCostMs returns just the
    /// static estimate; RunActiveAsync's finally block no longer updates
    /// ObservedPassCostMs) turns this RED — the first pass runs "for free" by the
    /// static estimate's reckoning, so Decide lets it start right as the keep-alive
    /// was approaching due, and the keep-alive doesn't get serviced until that whole
    /// 10-minute pass finishes on top of however much of the interval had already
    /// elapsed — comfortably past Roblox's 20-minute idle kick floor.
    /// </summary>
    [Fact]
    public async Task ActivePassRealCostExceedsDeclaredDuration_RatchetKeepsKeepAliveGapWithinTheKickFloor()
    {
        var declaredMacro = MacroOfLength(10 * 1000);              // DECLARES 10 seconds
        var active = new Assignment(Alt(1), declaredMacro, CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { active, keep };

        var rig = Build(assignments, runForMs: 60 * Min);
        rig.Player.RealPlayCostMs = 10 * Min;                       // REALLY costs 10 minutes
        rig.Player.AdvanceClockBy = rig.Clock.Sleep;

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        Assert.NotEmpty(rig.Player.Plays);                          // farming still happened
        Assert.True(rig.TapTimes.Count >= 2, "need at least 2 taps to measure a gap");

        var maxGapMs = rig.TapTimes.Zip(rig.TapTimes.Skip(1), (a, b) => b - a).Max();
        Assert.True(maxGapMs < 20 * Min,
            $"max keep-alive gap was {maxGapMs / (double)Min:F1} min — past Roblox's 20-minute idle kick floor");
    }

    /// <summary>
    /// Design ruling, pinned rather than left as an accident: when a single Active
    /// pass costs >= the keep-alive interval, CadenceScheduler.Decide's gap-fit
    /// lookahead correctly refuses to ALWAYS preempt for urgency. Honoring urgency
    /// unconditionally would recreate the livelock the forward-progress guard exists
    /// to stop — farming would NEVER run, which is strictly worse than an occasional
    /// stretched (but still safe) keep-alive gap, since the user would get zero
    /// farming value at all. The 12-minute keep-alive interval carries 8 minutes of
    /// margin against Roblox's real 20-minute idle kick floor, so a gap stretched by
    /// one long pass is safe by design.
    /// </summary>
    [Fact]
    public async Task PassCostAtOrAboveInterval_ActivesStillFarm_AndKeepAliveGapStaysUnderTheKickFloor()
    {
        var active = new Assignment(Alt(1), MacroOfLength(15 * Min), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { active, keep };
        var rig = Build(assignments, runForMs: 90 * Min);
        // REAL cost mirrors the declared duration — without this PlayAsync completes
        // near-instantly in simulated time and the rig never actually reproduces the
        // long-blocking-pass dynamic this test's doc-comment narrates.
        rig.Player.RealPlayCostMs = 15 * Min;

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        Assert.NotEmpty(rig.Player.Plays);   // (a) Active still farms — no starvation

        Assert.True(rig.TapTimes.Count >= 2, "need at least 2 taps to measure a gap");
        var maxGapMs = rig.TapTimes.Zip(rig.TapTimes.Skip(1), (a, b) => b - a).Max();
        Assert.True(maxGapMs < 20 * Min,   // (b) the stretched gap stays under the kick floor
            $"max keep-alive gap was {maxGapMs / (double)Min:F1} min — past Roblox's 20-minute idle kick floor");
    }

    /// <summary>
    /// The Active-side mirror of KeepAliveFocusAlwaysFails_LoopYieldsInsteadOfSpinning:
    /// a lone Active alt whose Roblox client crashed (a stale pid) fails Focus on
    /// EVERY attempt. RunActiveAsync's focus-fail branch used to return with no await
    /// at all — Decide keeps handing back the same always-runnable Active (Actives
    /// have no DueAtMs gate the way keep-alives do), the fake clock never advances,
    /// and nothing ever yields: a genuine 100%-CPU non-yielding hang, not just a
    /// hijack. Measured before the fix: 200,002 iterations in ~47ms of wall time with
    /// the simulated clock stuck at 0. The fix mirrors ServiceKeepAliveAsync's bounded
    /// 30s backoff.
    /// </summary>
    [Fact]
    public async Task ActiveFocusAlwaysFails_LoopYieldsInsteadOfSpinning()
    {
        var alt = new Assignment(Alt(1), MacroOfLength(60 * 1000), CadenceRole.Active);
        var rig = Build(new[] { alt }, runForMs: 60 * Min, focusOverride: _ => (false, "window not found"));

        // Wall-clock safety net: a real regression here is a genuine synchronous
        // infinite loop, which no amount of fake-clock jumping will end on its own —
        // this bounds the test's own failure to a few seconds instead of a true hang.
        var run = rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Player.Plays);        // focus never once succeeds, so nothing ever plays
        Assert.True(rig.Iterations() < MaxIterations,
            "hit the busy-spin tripwire — a stuck focus never yielded to the clock");
        // Bounded 30s retry, not a spin: at most ~2 attempts per minute of simulated
        // time, comfortably under a per-iteration cadence over a simulated hour.
        Assert.True(rig.Focused.Count < 500,
            $"Active focus attempted {rig.Focused.Count}x in a simulated hour — the spin loop is back");
    }

    /// <summary>
    /// FALSE-POSITIVE GUARD. Because CadenceScheduler.Decide is re-consulted between
    /// EVERY Active pass (not once per round-robin lap), the realized worst-case gap
    /// between keep-alive taps converges to ≈ the longest Active PASS LENGTH — not
    /// interval + pass, and it does not accumulate. A 16-minute Active pass against a
    /// 12-minute keep-alive interval therefore realizes a ~16-minute gap, which is
    /// comfortably under Roblox's 20-minute idle kick floor (~4 minutes of margin) —
    /// exactly the "gap stretches but stays safe" case the scheduler deliberately
    /// allows. The pre-fix trigger (`alt.IntervalMs < longestActivePassMs`) fired
    /// here anyway (12 &lt; 16) — on essentially any moderately long farming macro —
    /// which trains users to ignore the one warning that actually matters.
    ///
    /// Verified by hand: reverting the fix (comparing IntervalMs to
    /// longestActivePassMs directly instead of the projected-gap-vs-floor formula)
    /// turns the `Assert.Empty(warnings)` below RED — the old trigger emits exactly
    /// one warning for this scenario.
    /// </summary>
    [Fact]
    public async Task SafeButStretchedGap_DoesNotWarn_FalsePositiveGuard()
    {
        // Active alt with a 16-minute macro; keep-alive alt on a 12-minute interval
        // (Build's default).
        var active = new Assignment(Alt(1), MacroOfLength(16 * Min), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { active, keep };
        var rig = Build(assignments, runForMs: 60 * Min);
        // REAL cost mirrors the declared duration — without this PlayAsync completes
        // near-instantly in simulated time and the rig never actually reproduces the
        // long-blocking-pass dynamic this test's premise depends on.
        rig.Player.RealPlayCostMs = 16 * Min;

        var warnings = new List<AssignmentProgress>();
        rig.Runner.Progress += (_, p) =>
        {
            if (p.Phase == AssignmentPhase.Warning) warnings.Add(p);
        };

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        Assert.Empty(warnings);   // safe-but-stretched: no cry-wolf warning

        // Warn-or-not never gates behavior: the run still serviced things normally.
        Assert.NotEmpty(rig.Player.Plays);   // the Active alt still farmed
        Assert.True(rig.TapTimes.Count >= 2, "need at least 2 taps to measure a gap");
        var maxGapMs = rig.TapTimes.Zip(rig.TapTimes.Skip(1), (a, b) => b - a).Max();
        Assert.True(maxGapMs < 20 * Min,
            $"max keep-alive gap was {maxGapMs / (double)Min:F1} min — past Roblox's 20-minute idle kick floor");
    }

    /// <summary>
    /// TRUE-POSITIVE case: an Active pass long enough that the projected keep-alive
    /// gap genuinely nears — here, outright exceeds — Roblox's 20-minute idle kick
    /// floor. The keep-alive alt stays on Build's default 12-minute interval, so the
    /// pass length alone (not the interval) drives the projected gap, isolating this
    /// from the false-negative case below.
    /// </summary>
    [Fact]
    public async Task LongActivePass_ProjectedGapNearsKickFloor_Warns()
    {
        var active = new Assignment(Alt(1), MacroOfLength(22 * Min), CadenceRole.Active);
        var keep = new Assignment(Alt(2), null, CadenceRole.KeepAlive);
        var assignments = new[] { active, keep };
        var rig = Build(assignments, runForMs: 60 * Min);
        rig.Player.RealPlayCostMs = 22 * Min;

        var warnings = new List<AssignmentProgress>();
        rig.Runner.Progress += (_, p) =>
        {
            if (p.Phase == AssignmentPhase.Warning) warnings.Add(p);
        };

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        Assert.Single(warnings);
        Assert.Same(keep, warnings[0].Current);                              // names the alt
        Assert.Contains(keep.Alt.DisplayName, warnings[0].Reason, StringComparison.Ordinal);
        Assert.Contains("20", warnings[0].Reason);                           // states the platform floor
        Assert.Contains("Active", warnings[0].Reason, StringComparison.Ordinal); // gives a remedy
    }

    /// <summary>
    /// FALSE-NEGATIVE GUARD. A per-game keep-alive override of 25 minutes with ZERO
    /// Active alts. There is no Active pass to blame — the interval itself already
    /// exceeds Roblox's 20-minute idle kick floor, so this alt WILL get kicked on its
    /// own. The pre-fix trigger (`alt.IntervalMs < longestActivePassMs`) can never
    /// catch this: with no Actives, longestActivePassMs is 0, so `25min &lt; 0` is
    /// never true — the simplest unschedulable case went completely unreported.
    ///
    /// Verified by hand: reverting the fix turns the `Assert.Single(warnings)` below
    /// RED — the old trigger emits none for this scenario.
    /// </summary>
    [Fact]
    public async Task OverLongIntervalWithNoActives_Warns_FalseNegativeGuard()
    {
        var keep = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var assignments = new[] { keep };
        var rig = Build(assignments, runForMs: 60 * Min, keepAliveIntervalMs: 25 * Min);

        var warnings = new List<AssignmentProgress>();
        rig.Runner.Progress += (_, p) =>
        {
            if (p.Phase == AssignmentPhase.Warning) warnings.Add(p);
        };

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        Assert.Single(warnings);
        Assert.Same(keep, warnings[0].Current);

        // Warn, don't block: the run still serviced it despite the warning.
        Assert.NotEmpty(rig.Taps);
    }

    /// <summary>
    /// Task 6 wired AssignmentPhase.Warning to the activity log AND the themed toast
    /// (RecorderViewModel.AssignmentProgressed). A permanently-unfocusable alt (window
    /// closed/crashed) retries every 30s forever via ServiceKeepAliveAsync's bounded
    /// backoff — before this fix, EmitFocusFailureWarning re-emitted Warning on EVERY
    /// one of those retries once ConsecutiveFocusFailures reached 3, with the
    /// incrementing try-count baked into the text, which defeats ShowError's
    /// exact-text dedup: a fresh toast and log line every 30 seconds, forever, for the
    /// rest of the run. Fixed to fire exactly once, when the streak first CROSSES 3.
    /// </summary>
    [Fact]
    public async Task PermanentlyUnfocusableKeepAlive_WarnsExactlyOnce_NotOnEveryRetry()
    {
        var alt = new Assignment(Alt(1), null, CadenceRole.KeepAlive);
        var rig = Build(new[] { alt }, runForMs: 90 * Min, focusOverride: _ => (false, "window not found"));

        var warnings = new List<AssignmentProgress>();
        rig.Runner.Progress += (_, p) =>
        {
            if (p.Phase == AssignmentPhase.Warning) warnings.Add(p);
        };

        await rig.Runner.RunAsync(new[] { alt }, rig.Cts.Token);

        Assert.Single(warnings);
        Assert.Contains("hasn't been focusable", warnings[0].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(rig.Focused.Count > 4,
            "test premise requires several retries past the 3-failure threshold to actually exercise the spam guard");
    }
}
