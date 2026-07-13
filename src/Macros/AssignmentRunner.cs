using System.Diagnostics;
using System.Runtime.InteropServices;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Everything the cadence loop touches that isn't the player or the foreground watcher.
/// Exists so the scheduler can be driven by a fake clock that JUMPS instead of waiting —
/// a simulated hour runs in milliseconds — and so a unit test never injects a real Space
/// into the developer's desktop or reads the user's prefs file.
/// </summary>
internal sealed record CadenceDeps(
    Func<int, (bool ok, string? error)> Focus,
    Func<long> ClockMs,
    Func<long, CancellationToken, Task> Sleep,   // arg is a DURATION in ms, not a wake time
    Func<IntPtr> CaptureForeground,
    Action<IntPtr> RestoreForeground,
    Action SendKeepAlive,
    Func<AccountRegistry.AccountInfo, long> KeepAliveIntervalMs)
{
    public static CadenceDeps Real
    {
        get
        {
            // Deferred, not eager: `UI.UserPreferences.Load()` only runs the first
            // time KeepAliveIntervalMs is actually INVOKED, not when this property is
            // evaluated. That matters because the 3-arg compat ctor builds off
            // `CadenceDeps.Real with { KeepAliveIntervalMs = ..., ... }` — a `with`
            // expression evaluates the base object first, so an eager disk read here
            // used to fire even though the override immediately discards this
            // delegate and the disk value is never used. Lazy<T> keeps the "once per
            // CadenceDeps.Real access, not once per keep-alive alt" property for real
            // callers (Lazy caches after the first invocation) while making a
            // never-invoked delegate (the compat-ctor case) genuinely free.
            var prefsLazy = new Lazy<UI.UserPreferences>(UI.UserPreferences.Load);
            return new(
                Focus: Win32Focus.AttachAndFocus,
                ClockMs: () => Environment.TickCount64,           // MONOTONIC — never wall-clock
                Sleep: (ms, ct) => ms <= 0 ? Task.CompletedTask : Task.Delay((int)ms, ct),
                CaptureForeground: Win32Focus.CaptureForeground,
                RestoreForeground: h => Win32Focus.RestoreForeground(h),
                SendKeepAlive: () => AssignmentRunner.SendSpaceKeepAlive(),
                KeepAliveIntervalMs: alt => (long)KeepAliveIntervals
                    .For(alt.PlaceId, alt.PlaceName, prefsLazy.Value).TotalMilliseconds);
        }
    }
}

/// <summary>
/// Deadline-driven cadence runner. Takes a list of assignments and services each
/// according to its <see cref="CadenceRole"/>: Active alts farm their macro back-to-back;
/// KeepAlive alts get a single Space only when their idle deadline approaches, and the
/// loop SLEEPS the rest of the time instead of spinning through every alt every ~1.25s.
/// </summary>
internal sealed class AssignmentRunner
{
    private const int DefaultPerAltDelayMs = 1000; // 1s settle before each alt's action
    private const int KeepAliveDelayMs = 200;       // after Space, small wait before moving on
    private const int FocusRetryBackoffMs = 30_000; // spec: bounded 30s retry on a stuck focus

    // The runner is the last line of defense against a hijack loop: a user-editable
    // interval override of 0 (or negative) reads as "always due" and reproduces the
    // original ~1.2s spin; a pathologically huge one overflows Task.Delay's int cast
    // negative and throws ArgumentOutOfRangeException. Clamp to a sane positive range
    // regardless of what KeepAliveIntervalMs hands back.
    private const long MinKeepAliveIntervalMs = 60_000;      // 1 minute floor
    private const long MaxKeepAliveIntervalMs = 60 * 60_000; // 60 minute ceiling — comfortably under int.MaxValue ms

    private readonly IMacroPlayer _player;
    private readonly IForegroundWatcher _foreground;
    private readonly CadenceDeps _deps;
    private CancellationTokenSource? _activeCts;

    public AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground)
        : this(player, foreground, CadenceDeps.Real) { }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, Func<int, (bool, string?)> focus)
        : this(player, foreground, CadenceDeps.Real with
        {
            Focus = focus,
            // This compat ctor exists for call sites/tests that only want to fake
            // Focus. Left alone it silently inherits the REST of CadenceDeps.Real too
            // — a real Win32Focus.CaptureForeground/RestoreForeground pair (which
            // zeroes and restores the SYSTEM-WIDE SPI_SETFOREGROUNDLOCKTIMEOUT) and a
            // real UserPreferences disk read — exactly the side effects the CadenceDeps
            // seam exists to keep out of the unit suite. No-op them here; a test that
            // needs to assert on capture/restore or interval math should use the
            // CadenceDeps ctor directly instead.
            CaptureForeground = () => IntPtr.Zero,
            RestoreForeground = _ => { },
            KeepAliveIntervalMs = _ => (long)TimeSpan.FromMinutes(KeepAliveIntervals.UnknownGameMinutes).TotalMilliseconds,
            // Left alone this also inherits the REAL SendKeepAlive (a genuine
            // SendInput Space keystroke into the developer's desktop) — the same
            // side-effect class this whole seam exists to keep out of the unit
            // suite. No-op it here; nothing in AssignmentRunnerTests asserts on the
            // keystroke itself (KeepAliveInputStructSize_MatchesCanonicalWin32InputSize
            // tests SendSpaceKeepAlive's cbSize directly, without going through a
            // runner instance at all).
            SendKeepAlive = () => { },
        }) { }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, CadenceDeps deps)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _deps = deps ?? throw new ArgumentNullException(nameof(deps));
    }

    public event EventHandler<AssignmentProgress>? Progress;

    public bool IsRunning => _activeCts is not null;

    /// <summary>
    /// Run the deadline-scheduled cadence loop forever (until cancellation). Each
    /// iteration asks <see cref="CadenceScheduler.Decide"/> for exactly one next
    /// action — service the most urgent keep-alive, run the next Active pass, or
    /// sleep until something is due. No fixed "pass" through the list anymore: a
    /// KeepAlive alt is serviced only when its deadline approaches, so the loop
    /// spends most of its time asleep instead of stealing foreground every ~1.25s.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<Assignment> assignments,
        CancellationToken external = default)
    {
        if (assignments is null) throw new ArgumentNullException(nameof(assignments));
        if (assignments.Count == 0) return;

        // Atomic single-flight claim. Only one cadence loop may run at a time. A
        // second concurrent entry must NOT clobber the in-flight loop's token — it
        // refuses (returns immediately) and runs nothing.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        if (Interlocked.CompareExchange(ref _activeCts, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }
        var ct = cts.Token;

        // Emitted exactly once, only for the call that actually won the
        // single-flight guard above — a losing call returns before this line, so it
        // emits nothing. This is the symmetric counterpart to the Stopped phase
        // emitted in the finally below: callers (PluginRuntime) that need to know
        // "the loop is REALLY about to start servicing these alts" — e.g. to publish
        // the ur-afk claim file — must hang off this signal rather than the caller's
        // own optimism about whether its RunAsync call will win. Carries the full
        // assignment set via AllAssignments so a subscriber doesn't need any
        // additional shared/racy state to know which alts this run covers.
        EmitProgress(new AssignmentProgress(1, -1, assignments.Count, null, AssignmentPhase.Started, AllAssignments: assignments));

        // Runtime safety net (CRITICAL 2's belt-and-braces half): coerce every
        // assignment through Assignment.ResolveRole before scheduling, the same way
        // IntervalMs below is clamped to a sane range regardless of what the caller
        // handed in. A null-macro Active assignment must never reach the scheduler —
        // ScheduledAlt.IsKeepAlive would read false, Decide has no deadline gate for
        // Active, and RunActiveAsync would focus/settle(1s)/verify/find-nothing-to-play
        // on every single loop iteration forever: a continuous ~1s foreground steal
        // with zero farming value AND zero keep-alive protection. This is the LAST
        // line of defense — it holds even if a future call site (or a bug upstream in
        // the UI's override plumbing) ever hands this runner that exact invariant
        // violation. Only replaces the record (a `with` always allocates a new
        // instance, even when the value is unchanged) when coercion actually flips
        // something — the overwhelmingly common case hands the original Assignment
        // straight through unmodified, preserving reference identity for callers
        // (e.g. AssignmentProgress.Current) that key off it.
        var scheduled = assignments.Select(a =>
        {
            var resolvedRole = Assignment.ResolveRole(a.Macro, a.Role);
            var coerced = resolvedRole == a.Role ? a : a with { Role = resolvedRole };
            return new ScheduledAlt
            {
                Assignment = coerced,
                IntervalMs = coerced.Role == CadenceRole.KeepAlive
                    ? Math.Clamp(_deps.KeepAliveIntervalMs(coerced.Alt), MinKeepAliveIntervalMs, MaxKeepAliveIntervalMs)
                    : 0,
                DueAtMs = _deps.ClockMs(),   // every keep-alive is due immediately on start:
                                             // tap once up front, THEN settle into its interval.
            };
        }).ToList();

        // Unschedulable check. Because Decide is re-consulted between EVERY Active
        // pass (not once per round-robin lap), the realized worst-case gap between
        // keep-alive taps converges to ≈ the longest Active PASS LENGTH — not
        // interval + pass, and it does not accumulate. With no Active alts at all,
        // the gap is simply the interval itself. So the projected worst-case gap for
        // a keep-alive alt is Math.Max(longestActivePassMs, alt.IntervalMs) — warn
        // when THAT approaches the platform's kick floor, independent of how the
        // interval and the pass length compare to each other. Comparing the interval
        // to the pass length directly (the pre-fix trigger) is wrong in both
        // directions: a 16-min pass against a 12-min interval reads as "shorter" and
        // cries wolf even though the realized ~16-min gap is comfortably safe; a
        // 25-min per-game override with zero Active alts reads as "not shorter than
        // 0" and silently misses a kick that is guaranteed to happen.
        var longestActivePassMs = scheduled
            .Where(a => !a.IsKeepAlive)
            .Select(StaticPassCostMs)
            .DefaultIfEmpty(0L)
            .Max();
        var warnThresholdMs = (long)TimeSpan.FromMinutes(KeepAliveIntervals.WarnThresholdMinutes).TotalMilliseconds;

        foreach (var alt in scheduled.Where(a => a.IsKeepAlive))
        {
            var projectedGapMs = Math.Max(longestActivePassMs, alt.IntervalMs);
            if (projectedGapMs < warnThresholdMs) continue;

            var gapMins = projectedGapMs / 60_000.0;
            EmitProgress(new AssignmentProgress(
                0, -1, assignments.Count, alt.Assignment, AssignmentPhase.Warning,
                $"{alt.Assignment.Alt.DisplayName} may get kicked — its projected keep-alive gap is " +
                $"~{gapMins:F0} min, at or near Roblox's {KeepAliveIntervals.PlatformIdleKickFloorMinutes}-minute " +
                $"idle floor (games may shorten this, none may extend it). " +
                $"Shorten the macro, split it, or set this alt to Active."));
        }

        // Original list position of each alt — this is what IndexInCycle/TotalInCycle
        // report. The scheduler services one alt at a time (not a fixed sweep through the
        // list), so "cycle" below tracks logical PASSES: it advances the first time an
        // alt is serviced a second time since the last pass boundary, mirroring what the
        // old strict round-robin meant by "cycle" — one full lap — without requiring every
        // alt to actually be revisited in list order (a keep-alive miles from due just
        // rides along without incrementing anything).
        var indexOf = new Dictionary<ScheduledAlt, int>(scheduled.Count);
        for (int i = 0; i < scheduled.Count; i++) indexOf[scheduled[i]] = i;
        var servicedThisPass = new HashSet<ScheduledAlt>();
        var cycle = 1;

        int AdvancePass(ScheduledAlt alt)
        {
            if (!servicedThisPass.Add(alt))
            {
                cycle++;
                servicedThisPass.Clear();
                servicedThisPass.Add(alt);
            }
            return cycle;
        }

        var activeCursor = 0;
        var activeCount = scheduled.Count(a => !a.IsKeepAlive);   // fixed for the run — the
                                                                   // role mix doesn't change mid-loop

        // Forward-progress guard (the CRITICAL fix): Decide is a stateless lookahead —
        // re-entered immediately after a service with ~zero elapsed time — and nothing
        // in it stops it from handing back the SAME not-yet-due alt forever if
        // servicing it didn't clear the condition that made it look urgent (e.g. a
        // foreground-verify check that keeps failing, or a focus that keeps failing).
        // Re-entering it right back is exactly the desktop-hijack spin loop this
        // feature exists to kill. This set tracks which keep-alive alts have already
        // been gap-fitted (gotten a look) since the last time real progress happened —
        // an Active pass. An alt only earns a second look after that progress, or
        // after it becomes GENUINELY overdue (never starve a real deadline).
        var gapFittedSinceActivePass = new HashSet<ScheduledAlt>();

        async Task RunNextActiveAsync(ScheduledAlt alt, CancellationToken token)
        {
            await RunActiveAsync(alt, AdvancePass(alt), indexOf[alt], assignments.Count, token).ConfigureAwait(false);
            activeCursor = activeCount == 0 ? 0 : (activeCursor + 1) % activeCount;   // bounded — never overflows
            gapFittedSinceActivePass.Clear();   // real progress happened; every alt earns another look
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = _deps.ClockMs();
                var rotated = Rotated(scheduled, activeCursor);
                var decision = CadenceScheduler.Decide(rotated, now, NextActivePassCostMs(scheduled, activeCursor));

                switch (decision)
                {
                    case CadenceDecision.SleepUntil sleep:
                        // The whole point: nothing to do, so do NOTHING. No focus steal.
                        try { await _deps.Sleep(sleep.WakeAtMs - now, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        break;

                    case CadenceDecision.ServiceKeepAlive svc:
                    {
                        var genuinelyOverdue = svc.Alt.DueAtMs <= now;
                        // Add() also records this attempt for next time; its return
                        // tells us whether it was ALREADY there from a prior look.
                        var alreadyGapFitted = !gapFittedSinceActivePass.Add(svc.Alt);

                        if (!genuinelyOverdue && alreadyGapFitted)
                        {
                            // Decide handed back an alt we already gap-fitted, and it's
                            // still not actually due — servicing it again right now
                            // would be the spin loop. Force real progress instead: run
                            // the next Active pass.
                            //
                            // There is always one to run here. With zero Actives,
                            // nextActivePassCostMs is 0 (see NextActivePassCostMs), so
                            // Decide's urgency horizon collapses to exactly `now` — the
                            // only way an alt reads as urgent is if it's genuinely
                            // overdue too, which this branch has already excluded. So
                            // reaching here is proof at least one Active exists; a
                            // `firstActive is null` fallback would be unreachable dead
                            // code (an earlier version of this guard had exactly that,
                            // credited in a since-rewritten test's docstring).
                            var firstActive = rotated.First(a => !a.IsKeepAlive);
                            await RunNextActiveAsync(firstActive, ct).ConfigureAwait(false);
                            break;
                        }

                        // Genuinely overdue (never starved), or this is its first look
                        // since the last Active pass — service it for real.
                        await ServiceKeepAliveAsync(
                            svc.Alt, AdvancePass(svc.Alt), indexOf[svc.Alt], assignments.Count, ct).ConfigureAwait(false);
                        break;
                    }

                    case CadenceDecision.RunActive run:
                        await RunNextActiveAsync(run.Alt, ct).ConfigureAwait(false);
                        break;
                }
            }
            EmitProgress(new AssignmentProgress(cycle, -1, assignments.Count, null, AssignmentPhase.Stopped));
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }

    /// <summary>
    /// Same alts, with the Active entries rotated so the cursor's alt comes first.
    /// KeepAlives are order-independent (Decide picks by deadline), so they ride along.
    /// This is what makes Active alts round-robin — <see cref="CadenceScheduler.Decide"/>
    /// is stateless and always returns the FIRST Active it sees.
    /// </summary>
    private static IReadOnlyList<ScheduledAlt> Rotated(IReadOnlyList<ScheduledAlt> alts, int cursor)
    {
        var actives = alts.Where(a => !a.IsKeepAlive).ToList();
        if (actives.Count <= 1) return alts;
        var start = cursor % actives.Count;
        var rotated = actives.Skip(start).Concat(actives.Take(start));
        return alts.Where(a => a.IsKeepAlive).Concat(rotated).ToList();
    }

    /// <summary>
    /// What one more Active pass will cost, so Decide knows whether a keep-alive can
    /// wait for it. Macro.Duration is already known (last-event timestamp), but that
    /// alone is only an ESTIMATE — it doesn't model AttachAndFocus or MacroPlayer's
    /// preflight (EnsureClientSize does synchronous Win32 window resizing whose cost
    /// is bounded only by the target's message pump).
    ///
    /// <b>This does NOT actually satisfy <see cref="Decide"/>'s "conservative UPPER
    /// BOUND" contract</b>, despite the goal. It is a LEARNED, REACTIVE high-water
    /// mark, not a true bound: <see cref="RunActiveAsync"/> times every pass end to
    /// end and ratchets <see cref="ScheduledAlt.ObservedPassCostMs"/> up (never down),
    /// and this returns whichever of the static estimate or that high-water mark is
    /// larger. Two honest consequences follow from "learned after the fact" rather
    /// than "known in advance": the FIRST pass that overruns the static estimate can
    /// still miss a deadline — the ratchet only updates once that pass has already
    /// finished, too late to protect the keep-alive it ran ahead of. And the ratchet
    /// has no decay: one outlier pass (e.g. a one-time slow resize) permanently
    /// inflates this alt's horizon for the rest of the run, even once every later
    /// pass goes back to being fast.
    /// </summary>
    private static long NextActivePassCostMs(IReadOnlyList<ScheduledAlt> alts, int cursor)
    {
        var actives = alts.Where(a => !a.IsKeepAlive).ToList();
        if (actives.Count == 0) return 0;
        var next = actives[cursor % actives.Count];
        return Math.Max(StaticPassCostMs(next), next.ObservedPassCostMs);
    }

    /// <summary>
    /// The static, Macro.Duration-derived estimate of one Active pass's cost —
    /// shared by <see cref="NextActivePassCostMs"/> (which additionally folds in
    /// the per-alt observed high-water mark) and the up-front unschedulable-alt
    /// warning in <see cref="RunAsync"/> (which runs before any pass has been
    /// observed, so the static estimate is all it has).
    /// </summary>
    private static long StaticPassCostMs(ScheduledAlt alt)
    {
        var macro = alt.Assignment.Macro;
        var playMs = macro is null ? 0L : (long)macro.Duration.TotalMilliseconds;
        return playMs + DefaultPerAltDelayMs + (macro?.InterAltDelayMs ?? 500);
    }

    /// <summary>
    /// Service one overdue-or-soon-due keep-alive: capture the user's current
    /// foreground, focus the alt, verify the flip, tap Space, then hand the
    /// desktop back. A keep-alive is a ~1s blip, not a hijack.
    ///
    /// Owns <see cref="ScheduledAlt.DueAtMs"/> and <see cref="ScheduledAlt.ConsecutiveFocusFailures"/>
    /// end to end on EVERY exit path — the caller no longer touches either after this
    /// returns. (It used to unconditionally stamp DueAtMs = now + IntervalMs after every
    /// call, which silently clobbered the shorter 30s retry backoff this method sets on
    /// a focus/verify failure — that stomp is what let a stuck verify look "due again
    /// soon" instead of "due again in ~30s," feeding the desktop-hijack spin loop.)
    /// </summary>
    private async Task ServiceKeepAliveAsync(ScheduledAlt alt, int cycle, int index, int total, CancellationToken ct)
    {
        var asn = alt.Assignment;
        EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Focusing));

        var prior = _deps.CaptureForeground();     // whatever the USER was doing
        var focusSucceeded = false;                 // gates the restore below — see finally
        try
        {
            if (!_deps.Focus(asn.Alt.Pid).ok)
            {
                // Bounded retry — do NOT hammer a window that won't focus. After three
                // straight misses the window is almost certainly gone (alt closed/crashed),
                // so say so loudly instead of silently retrying it forever.
                alt.DueAtMs = _deps.ClockMs() + FocusRetryBackoffMs;
                alt.ConsecutiveFocusFailures++;
                EmitFocusFailureWarning(alt, cycle, index, total, asn);
                return;   // nothing was stolen — Focus never actually flipped anything
            }
            focusSucceeded = true;

            try { await _deps.Sleep(DefaultPerAltDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Safety invariant (unchanged from v0.6): never synthesize input unless the
            // foreground really is the alt we aimed at. AttachAndFocus reports ok=true
            // whenever the process merely HAS a main window — it does not confirm the
            // foreground actually flipped — so this is the check that actually catches
            // that gap, and it is the MOST COMMON way a real service attempt fails.
            var fg = _foreground.ResolveForegroundAccount();
            if (fg is null || fg.RobloxUserId != asn.Alt.RobloxUserId)
            {
                alt.DueAtMs = _deps.ClockMs() + FocusRetryBackoffMs;
                alt.ConsecutiveFocusFailures++;
                EmitFocusFailureWarning(alt, cycle, index, total, asn);
                return;
            }

            EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Playing));
            _deps.SendKeepAlive();
            alt.ConsecutiveFocusFailures = 0;                  // a GENUINELY successful service clears the streak
            alt.DueAtMs = _deps.ClockMs() + alt.IntervalMs;    // settle back into the normal interval
            try { await _deps.Sleep(KeepAliveDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* still restore below, via finally */ }
        }
        finally
        {
            // Hand the desktop back on every exit path where something was actually
            // taken — including a cancellation thrown mid-settle (press Stop/Esc/Abort
            // during that window used to return without restoring, leaving the alt
            // holding the desktop) and a failed foreground-verify (Focus reported
            // ok=true, so a real attempt was made even if it didn't land).
            //
            // But NOT the focus-FAIL path: there, Focus itself never returned ok, so
            // nothing was stolen — restoring anyway used to poke the system-wide
            // SPI_SETFOREGROUNDLOCKTIMEOUT every single attempt, including the 30s
            // retry loop against a dead alt (crashed/closed window) for as long as the
            // run keeps going.
            if (focusSucceeded)
            {
                _deps.RestoreForeground(prior);
            }
        }
    }

    /// <summary>
    /// Shared Skipped/Warning emission for a failed focus OR a failed foreground-verify
    /// — both mean "this attempt didn't land," and both count toward the same
    /// ConsecutiveFocusFailures streak so "hasn't been focusable for N tries" actually
    /// fires for whichever failure mode is happening.
    ///
    /// Fires the Warning exactly ONCE per failure streak — the instant the counter
    /// CROSSES the 3-failure threshold, not on every retry once it's at-or-above 3.
    /// Task 6 wired Warning to the activity log AND the themed toast (see
    /// RecorderViewModel.AssignmentProgressed); a crashed/closed alt retries every
    /// 30s forever, and the old `>= 3` condition re-emitted Warning on every one of
    /// those retries with the incrementing try-count baked into the text, which
    /// defeats ShowError's exact-text dedup — a fresh toast and log line every 30s,
    /// forever. Resetting ConsecutiveFocusFailures to 0 on a successful focus (see
    /// ServiceKeepAliveAsync) already re-arms this, so a genuinely flapping alt still
    /// warns again after it recovers and re-fails. Retries beyond the first crossing
    /// still emit Skipped (silent, no Reason) so the activity log/UI still reflects
    /// that an attempt happened — they just don't re-toast.
    /// </summary>
    private void EmitFocusFailureWarning(ScheduledAlt alt, int cycle, int index, int total, Assignment asn)
    {
        if (alt.ConsecutiveFocusFailures == 3)
        {
            EmitProgress(new AssignmentProgress(
                cycle, index, total, asn, AssignmentPhase.Warning,
                $"{asn.Alt.DisplayName} hasn't been focusable for 3 tries — its window may be gone. Still retrying every 30s."));
            return;
        }
        EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Skipped));
    }

    /// <summary>
    /// Run one Active pass: focus, settle, verify foreground, play. Today's
    /// behavior, unchanged — and deliberately no restore-foreground afterward, so
    /// an Active alt holds focus between its own back-to-back passes (farming).
    ///
    /// Timed end to end (see <see cref="ScheduledAlt.ObservedPassCostMs"/>): this is
    /// the caller-side half of the self-correcting <see cref="NextActivePassCostMs"/>
    /// upper bound that <see cref="CadenceScheduler.Decide"/>'s hard-deadline guarantee
    /// depends on.
    /// </summary>
    private async Task RunActiveAsync(ScheduledAlt alt, int cycle, int index, int total, CancellationToken ct)
    {
        var asn = alt.Assignment;
        var startMs = _deps.ClockMs();
        try
        {
            // Phase: Focusing
            EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Focusing));
            if (!_deps.Focus(asn.Alt.Pid).ok)
            {
                EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Skipped));
                // Bounded backoff — mirrors ServiceKeepAliveAsync's FocusRetryBackoffMs.
                // Without an await here, an Active alt whose Roblox client crashed
                // (stale pid) spins this method synchronously forever: Focus keeps
                // failing, this returns immediately with zero elapsed time, the outer
                // loop re-enters Decide, and Decide keeps handing back the same
                // always-runnable Active (Actives have no DueAtMs gate the way
                // keep-alives do) — the clock never advances and nothing ever
                // un-sticks it. In production this also floods the Progress handler
                // (and the WPF UI it drives) at spin rate instead of a sane 30s cadence.
                try { await _deps.Sleep(FocusRetryBackoffMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return;
            }
            try { await _deps.Sleep(DefaultPerAltDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Verify foreground actually flipped
            var fg = _foreground.ResolveForegroundAccount();
            if (fg is null || fg.RobloxUserId != asn.Alt.RobloxUserId)
            {
                EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Skipped));
                return;
            }

            // Phase: Playing
            EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Playing));
            if (asn.Macro is not null)
            {
                try
                {
                    var playResult = await _player.PlayAsync(asn.Macro, asn.Alt.RobloxUserId, ct).ConfigureAwait(false);
                    // Preflight refusals (e.g. client-space resize failed) and
                    // mid-playback aborts (e.g. foreground shifted) return before the
                    // player's Started/Ended events fire — without this, they vanish
                    // silently on the cadence path. Skip emit on a user-initiated
                    // cancellation, not a genuine refusal/abort.
                    if (playResult.Outcome is PlaybackOutcome.Refused or PlaybackOutcome.Aborted)
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            EmitProgress(new AssignmentProgress(
                                cycle, index, total, asn, AssignmentPhase.Refused, playResult.Reason));
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            // Ratchet up, never down — a self-correcting UPPER BOUND only holds if it
            // never forgets the worst pass it has actually seen.
            var elapsed = _deps.ClockMs() - startMs;
            if (elapsed > alt.ObservedPassCostMs) alt.ObservedPassCostMs = elapsed;
        }
    }

    public bool Abort()
    {
        var cts = _activeCts;
        if (cts is null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void EmitProgress(AssignmentProgress p)
    {
        try { Progress?.Invoke(this, p); } catch { /* swallow */ }
    }

    internal static bool SendSpaceKeepAlive()
    {
        const ushort VK_SPACE = 0x20;
        var down = SendKeyEvent(VK_SPACE, keyUp: false);
        Thread.Sleep(50); // briefly held
        var up = SendKeyEvent(VK_SPACE, keyUp: true);

        // SendInput returns the number of events inserted; 0 means Windows
        // rejected the call (e.g. cbSize mismatch). Surface it instead of
        // swallowing — a silent 0 here is exactly the bug that made keep-alive
        // a no-op for every release through v0.2.2.
        var ok = down == 1 && up == 1;
        if (!ok)
            Debug.WriteLine($"[AssignmentRunner] keep-alive Space rejected by SendInput (down={down}, up={up}).");
        return ok;
    }

    private static uint SendKeyEvent(ushort vk, bool keyUp)
    {
        var scanCode = (ushort)MapVirtualKey(vk, 0);
        var flags = keyUp ? 0x0002u : 0u; // KEYEVENTF_KEYUP

        var input = new INPUT { type = 1 };
        input.union.keyboard = new KEYBDINPUT { wVk = vk, wScan = scanCode, dwFlags = flags };
        return SendOne(ref input);
    }

    private static unsafe uint SendOne(ref INPUT input)
    {
        fixed (INPUT* p = &input) { return SendInput(1, p, Marshal.SizeOf<INPUT>()); }
    }

    /// <summary>
    /// Test seam: the cbSize this runner passes to SendInput. MUST equal the
    /// canonical Win32 INPUT size, or SendInput rejects every keep-alive event.
    /// </summary>
    internal static int KeepAliveInputStructSize => Marshal.SizeOf<INPUT>();

    // ---------- Win32 (minimal — mirrors MacroPlayer's interop for self-containment) ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion union; }

    // The union MUST be sized to its largest member (MOUSEINPUT). The keep-alive
    // only ever writes the keyboard field, but Win32 SendInput validates cbSize
    // against the full INPUT size — drop MOUSEINPUT and cbSize comes up 8 bytes
    // short (32 vs 40 on x64), SendInput fails, and the Space is never injected.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint SendInput(uint cInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
}

/// <summary>
/// How often an alt gets serviced. Active = run its macro back-to-back (farming).
/// KeepAlive = fire a single Space only when its idle deadline approaches, so the
/// scheduler can sleep instead of stealing foreground every ~1.25s.
/// </summary>
public enum CadenceRole { Active, KeepAlive }

public sealed record Assignment(
    AccountRegistry.AccountInfo Alt,
    Macro? Macro,
    CadenceRole Role)   // NO default value — see WithDerivedRole.
{
    /// <summary>
    /// The legacy/derived rule: a macro means you meant to farm; no macro means you
    /// meant to stay alive. Deliberately a factory rather than a C# default value —
    /// a `= CadenceRole.Active` default would silently make no-macro assignments
    /// Active and spin them back-to-back, recreating the bug the scheduler fixes.
    /// </summary>
    public static Assignment WithDerivedRole(AccountRegistry.AccountInfo alt, Macro? macro)
        => new(alt, macro, macro is null ? CadenceRole.KeepAlive : CadenceRole.Active);

    /// <summary>
    /// The single source of truth for "what role does this alt actually get",
    /// covering both the UI's explicit-override path and the legacy derived rule in
    /// one pure, testable function. Encoding: an explicit <paramref name="userOverride"/>
    /// wins outright — EXCEPT a null <paramref name="macro"/> can never resolve to
    /// Active (that is the exact foreground-hijack loop the cadence scheduler exists
    /// to kill: <see cref="CadenceScheduler.Decide"/> has no deadline gate for
    /// Active, so RunActiveAsync would focus/settle/verify/find-nothing-to-play on
    /// every single loop iteration, forever). Absent an override, the role derives
    /// from macro presence, same as <see cref="WithDerivedRole"/>.
    /// </summary>
    public static CadenceRole ResolveRole(Macro? macro, CadenceRole? userOverride)
        => macro is null ? CadenceRole.KeepAlive : userOverride ?? CadenceRole.Active;
}

// Refused covers both PlaybackOutcome.Refused (preflight declined, e.g. client-space
// resize failed) and PlaybackOutcome.Aborted (mid-playback foreground shift) from the
// player — from the round-robin's perspective both mean "this alt's macro didn't play
// to completion," surfaced with Reason so it isn't silently swallowed.
// Warning covers two distinct "this needs a human's attention" cases, both surfaced
// louder than a routine Skipped so they don't get lost in the noise:
//   - a keep-alive alt that's missed 3+ consecutive focus attempts (its window is
//     almost certainly gone — alt closed/crashed), emitted mid-run from
//     ServiceKeepAliveAsync; and
//   - a keep-alive alt whose interval is shorter than the worst-case Active pass it
//     could be waiting behind — genuinely unschedulable, not just occasionally late —
//     emitted once up front at the top of RunAsync, before anything gets kicked.
// Started fires exactly once, only for the RunAsync call that actually wins the
// single-flight CompareExchange guard (see RunAsync) — the symmetric counterpart to
// Stopped. A losing concurrent call emits nothing, so anything hanging off Started
// (e.g. PluginRuntime's ur-afk claim publish) never fires for a call that never
// actually started servicing anyone.
public enum AssignmentPhase { Focusing, Playing, Skipped, Refused, Stopped, Warning, Started }

public sealed record AssignmentProgress(
    int Cycle,
    int IndexInCycle,       // -1 = no current alt (Stopped/Started state)
    int TotalInCycle,
    Assignment? Current,
    AssignmentPhase Phase,
    string? Reason = null,             // set on Phase == Refused; carries PlaybackResult.Reason
    IReadOnlyList<Assignment>? AllAssignments = null); // set on Phase == Started; the exact set this run is about to service
