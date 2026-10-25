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
    Func<int, FocusAttempt> Focus,
    Func<long> ClockMs,
    Func<long, CancellationToken, Task> Sleep,   // arg is a DURATION in ms, not a wake time
    Func<IntPtr> CaptureForeground,
    Action<IntPtr> RestoreForeground,
    Func<bool> SendKeepAlive,
    Func<AccountRegistry.AccountInfo, long> KeepAliveIntervalMs)
{
    /// <summary>
    /// Turbo keep-alive's pre-sweep warning: (secondsLeft, windowCount). Called with
    /// 3, 2, 1 one second apart, then with 0 to hide, on every exit path including a
    /// cancelled countdown. Init-only with a no-op default so every existing
    /// positional construction (and every test that never turns turbo on) is unchanged.
    /// The real one drives <see cref="UI.TurboSweepToast"/>, a topmost window that
    /// never takes focus.
    /// </summary>
    public Action<int, int> SweepCountdown { get; init; } = (_, _) => { };

    /// <summary>One line per alt a turbo sweep reaches (sent, or skipped and why) and one when it
    /// ends, so a live check can read every window's Space off ur-task.log. No-op by default.</summary>
    public Action<string> SweepLog { get; init; } = _ => { };

    /// <summary>The game's limit and deadline for an alt. Null derives them from <see cref="KeepAliveIntervalMs"/>
    /// (interval = deadline, limit = the platform floor).</summary>
    public Func<AccountRegistry.AccountInfo, KeepAliveLimit>? KeepAliveLimitFor { get; init; }

    /// <summary>Who is in front right now, short, for the line that says why a keep-alive missed.</summary>
    public Func<string> DescribeForeground { get; init; } = () => "unknown";

    /// <summary>Held from the end of the countdown until the foreground is back: while it is held,
    /// the user's own key and click presses are dropped instead of landing in whichever account's
    /// window is in front (Hotkeys.SweepInputGuard). No-op by default.</summary>
    public Func<IDisposable> GuardUserInput { get; init; } = () => NoGuard.Instance;

    /// <summary>Called with the account id each time a keep-alive Space lands, so RoRoRo can credit its own
    /// activity clock. Fire-and-forget; must not throw. No-op by default.</summary>
    public Action<string> MarkActive { get; init; } = _ => { };

    private sealed class NoGuard : IDisposable
    {
        public static readonly NoGuard Instance = new();
        public void Dispose() { }
    }

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
                KeepAliveIntervalMs: alt => (long)KeepAliveLimits
                    .For(alt.PlaceId, alt.PlaceName, prefsLazy.Value).Deadline.TotalMilliseconds)
            {
                KeepAliveLimitFor = alt => KeepAliveLimits.For(alt.PlaceId, alt.PlaceName, prefsLazy.Value),
                SweepCountdown = (secondsLeft, windowCount) =>
                {
                    if (secondsLeft == AssignmentRunner.TurboCountdownSeconds)
                        Diagnostics.DiagLog.Write(
                            $"turbo keep-alive: sweeping {windowCount} window(s) in {secondsLeft}s");
                    UI.TurboSweepToast.Update(secondsLeft, windowCount);
                },
                SweepLog = Diagnostics.DiagLog.Write,
                DescribeForeground = Win32Focus.DescribeForegroundShort,
                GuardUserInput = Hotkeys.SweepInputGuard.Begin,
            };
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
    internal const long StartSpreadMs = 2_000;      // accounts with no input history on either clock start this far apart
    private const int UnschedulableRunwayMinutes = 2; // warn when the projected gap comes within this of the limit
    internal const long PreemptMarginMs = 60_000;   // a playback that reaches no boundary is cut at limit - 1 min
    internal const int WatchdogTickMs = 1_000;      // how often the watchdog looks while a playback is in

    // Turbo keep-alive. One warned sweep services every keep-alive alt due within
    // the batch window, and each alt costs a polled foreground confirm instead of a
    // fixed 1s settle. The safety invariant does not move: no input until the
    // foreground is verified to be the target alt; a cap expiry takes the same
    // 30s backoff a failed verify always has.
    internal const int TurboCountdownSeconds = 3;      // warning before every sweep
    internal const long TurboBatchWindowMs = 120_000;  // pull in alts due within 2 min
    internal const int TurboPollIntervalMs = 15;       // foreground re-check cadence
    internal const int TurboConfirmCapMs = 300;        // give up (no input) after this
    internal const int TurboPostKeyMs = 40;            // let the Space register before moving on
    internal const int TurboSettleMs = 40;             // after the confirm, before the key: Roblox can drop a key
                                                       // that lands the same frame its window comes forward (Este 22:08)

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
    private readonly KeepAliveLedger _ledger;
    private readonly MissReporter _reporter;
    private readonly ForegroundGate _gate;
    /// <summary>One keep-alive service at a time: the main loop and the watchdog both take it.</summary>
    private readonly SemaphoreSlim _turn = new(1, 1);
    /// <summary>When the runner first tracked each account: an account with no input on either clock counts
    /// its idle from here, so it can still reach critical. Kept across runs: a restarted loop served nobody.</summary>
    private readonly Dictionary<long, long> _trackedAtMs = new();
    private CancellationTokenSource? _activeCts;
    private volatile bool _turboKeepAlive;

    // The running schedule. Replaced whole (copy on write) under _turn by AddAssignment/RemoveAssignment, so
    // a reader that took the reference never sees a list change under it. Null while no loop runs.
    private volatile IReadOnlyList<ScheduledAlt>? _scheduled;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ScheduledAlt, int> _indexOf = new();
    private int _nextIndex;
    /// <summary>Membership changes waiting for the turn: AddAssignment/RemoveAssignment must not block the
    /// caller behind a service, so they queue and whoever next holds _turn applies them.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action<IReadOnlyList<ScheduledAlt>>> _membership = new();

    private IReadOnlyList<ScheduledAlt> Schedule => _scheduled ?? [];

    /// <summary>
    /// Turbo keep-alive (user preference). Read at every keep-alive decision, so
    /// flipping it mid-run takes effect at the next sweep without a restart. Off is
    /// exactly the pre-turbo path: one alt at a time, 1s settle, restore per alt.
    /// </summary>
    public bool TurboKeepAlive
    {
        get => _turboKeepAlive;
        set => _turboKeepAlive = value;
    }

    public AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground)
        : this(player, foreground, CadenceDeps.Real) { }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, Func<int, (bool, string?)> focus)
        : this(player, foreground, CadenceDeps.Real with
        {
            Focus = pid => focus(pid),
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
            KeepAliveIntervalMs = _ => (long)KeepAliveLimits.For(null, null, new UI.UserPreferences()).Deadline.TotalMilliseconds,
            // Left alone this also inherits the REAL SendKeepAlive (a genuine
            // SendInput Space keystroke into the developer's desktop) — the same
            // side-effect class this whole seam exists to keep out of the unit
            // suite. No-op it here; nothing in AssignmentRunnerTests asserts on the
            // keystroke itself (KeepAliveInputStructSize_MatchesCanonicalWin32InputSize
            // tests SendSpaceKeepAlive's cbSize directly, without going through a
            // runner instance at all).
            SendKeepAlive = () => true,
        }) { }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, CadenceDeps deps)
        : this(player, foreground, deps, PrivateLedger(deps)) { }

    private static KeepAliveLedger PrivateLedger(CadenceDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        return new KeepAliveLedger(deps.ClockMs, () => DateTimeOffset.Now);
    }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, CadenceDeps deps, KeepAliveLedger ledger)
        : this(player, foreground, deps, ledger, PrivateGate(deps)) { }

    /// <summary>A gate no playback ever enters, so every break is granted at once and the runner behaves
    /// as it did before the gate. Its lines go nowhere: the sweep log stays exactly the 0.12.1 lines.</summary>
    private static ForegroundGate PrivateGate(CadenceDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        return new ForegroundGate(deps.ClockMs, _ => { });
    }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, CadenceDeps deps, KeepAliveLedger ledger,
        ForegroundGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _deps = deps ?? throw new ArgumentNullException(nameof(deps));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _reporter = new MissReporter(_deps.SweepLog, () => _ledger.ToWall(_deps.ClockMs()));
    }

    public event EventHandler<AssignmentProgress>? Progress;

    public bool IsRunning => _activeCts is not null;

    /// <summary>Accounts the running loop plays a macro on (Active). Empty when no loop runs. A snapshot read of
    /// the schedule, so it never waits on a pass.</summary>
    public IReadOnlySet<long> ActiveUserIds
        => (_scheduled ?? []).Where(a => !a.IsKeepAlive).Select(a => a.Assignment.Alt.RobloxUserId).ToHashSet();

    /// <summary>
    /// An account that appeared while the loop runs joins it as keep-alive (Active needs a macro the
    /// running loop never had a chance to pair). Already scheduled: refreshes its window identity. Does
    /// nothing when no loop runs. Never blocks behind a service: applied under the turn as soon as it frees.
    /// </summary>
    public void AddAssignment(Assignment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        _membership.Enqueue(cur => AddNow(cur, a));
        TryApplyMembership();
    }

    /// <summary>An account left. Its ledger history stays for GetStatus until the plugin restarts.</summary>
    public void RemoveAssignment(long robloxUserId, int pid)
    {
        // Only the exiting window's own entry: a relaunch can announce the new pid before the old one exits.
        _membership.Enqueue(cur => _scheduled = cur.Where(x =>
            x.Assignment.Alt.RobloxUserId != robloxUserId || x.Assignment.Alt.Pid != pid).ToList());
        TryApplyMembership();
    }

    private void TryApplyMembership()
    {
        if (!_turn.Wait(0)) return;   // the holder applies it on the way out
        ReleaseTurn();
    }

    /// <summary>Applies queued membership changes while holding _turn, then lets go of it. A change queued
    /// after the drain but before the release is picked up by the re-check.</summary>
    private void ReleaseTurn()
    {
        try
        {
            while (_membership.TryDequeue(out var change))
                if (_scheduled is { } cur) change(cur);
        }
        finally { _turn.Release(); }
        if (!_membership.IsEmpty) TryApplyMembership();
    }

    private void AddNow(IReadOnlyList<ScheduledAlt> cur, Assignment a)
    {
        var alt = a.Alt;
        var coerced = a.Role == CadenceRole.KeepAlive ? a : a with { Role = CadenceRole.KeepAlive };
        var existing = cur.FirstOrDefault(x => x.Assignment.Alt.RobloxUserId == alt.RobloxUserId);
        if (existing is not null)
        {
            // The same account relaunched: new window identity only. Role, macro, interval and limit stay.
            existing.Assignment = existing.Assignment with { Alt = alt };
            if (existing.IsKeepAlive) _ledger.Track(alt, existing.Limit!);
            return;
        }
        var intervalMs = Math.Clamp(_deps.KeepAliveIntervalMs(alt), MinKeepAliveIntervalMs, MaxKeepAliveIntervalMs);
        var limit = LimitFor(alt, intervalMs);
        var now = _deps.ClockMs();
        _ledger.Track(alt, limit);   // keeps an off flag set earlier this session
        lock (_trackedAtMs) _trackedAtMs.TryAdd(alt.RobloxUserId, now);
        var due = _ledger.DueAtMs(alt.RobloxUserId);
        var scheduled = new ScheduledAlt
        {
            Assignment = coerced,
            IntervalMs = intervalMs,
            Limit = limit,
            DueAtMs = due <= now ? now : due,
            ConsecutiveFocusFailures = _ledger.Get(alt.RobloxUserId)?.ConsecutiveMisses ?? 0,
        };
        _indexOf[scheduled] = Interlocked.Increment(ref _nextIndex) - 1;
        _scheduled = cur.Append(scheduled).ToList();
        try { _deps.SweepLog($"keep-alive: {alt.DisplayName} joined the loop"); } catch { /* a log line never blocks */ }
    }

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

        // Declared here, ahead of the try below, purely for scope: the finally
        // block's Stopped emit (a SIBLING of try, not nested inside it) needs to
        // read the current cycle count on every exit path, including one where an
        // exception unwound before the loop ever assigned a "real" value below.
        int cycle = 1;
        Task? watchdog = null;
        CancellationTokenSource? watchCts = null;

        // Moved up from just before the while loop below: the single-flight
        // win above, the Started emit (which publishes the ur-afk claim), and
        // all the scheduling setup between here and the loop now live INSIDE
        // this try, so the finally at the bottom — which emits Stopped and
        // releases _activeCts — covers this window too. A throw anywhere in
        // setup used to leave the claim published with Stopped never firing
        // (its 20s heartbeat republishing forever) and _activeCts permanently
        // non-null (IsRunning stuck true, every future RunAsync losing the
        // single-flight guard) — PLAY would silently do nothing until the app
        // restarted, while ur-afk stayed off alts nobody was servicing. The
        // losing single-flight caller above still returns BEFORE this try — it
        // never ran, so it must never emit Stopped or release the winner's claim.
        try
        {
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
            var initialScheduled = assignments.Select(a =>
            {
                var resolvedRole = Assignment.ResolveRole(a.Macro, a.Role);
                var coerced = resolvedRole == a.Role ? a : a with { Role = resolvedRole };
                var intervalMs = coerced.Role == CadenceRole.KeepAlive
                    ? Math.Clamp(_deps.KeepAliveIntervalMs(coerced.Alt), MinKeepAliveIntervalMs, MaxKeepAliveIntervalMs)
                    : 0;
                return new ScheduledAlt
                {
                    Assignment = coerced,
                    IntervalMs = intervalMs,
                    Limit = coerced.Role == CadenceRole.KeepAlive ? LimitFor(coerced.Alt, intervalMs) : null,
                };
            }).ToList();
            _scheduled = initialScheduled;   // from here the schedule is replaced, never edited in place (see AddAssignment)

            // The ledger decides who is due: each keep-alive alt is due at its real last input plus its
            // deadline, so starting the loop resets nobody. An alt with no history is due now, and the
            // ones due now start StartSpreadMs apart in assignment order, never all in the same second.
            var startMs = _deps.ClockMs();
            var spreadIndex = 0;
            foreach (var alt in initialScheduled)
            {
                if (!alt.IsKeepAlive) { alt.DueAtMs = startMs; continue; }
                var userId = alt.Assignment.Alt.RobloxUserId;
                _ledger.Track(alt.Assignment.Alt, alt.Limit!);
                lock (_trackedAtMs) _trackedAtMs.TryAdd(userId, startMs);
                var due = _ledger.DueAtMs(userId);
                alt.DueAtMs = due <= startMs ? startMs + spreadIndex++ * StartSpreadMs : due;
                alt.ConsecutiveFocusFailures = _ledger.Get(userId)?.ConsecutiveMisses ?? 0;
            }

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
            // Each alt is held to its OWN game's limit (capped at the platform floor: games may shorten
            // it, none may extend it), less a fixed runway.
            long WarnThresholdMs(ScheduledAlt a) =>
                (long)(Min(a.Limit!.Limit, TimeSpan.FromMinutes(KeepAliveLimits.PlatformIdleKickFloorMinutes))
                       - TimeSpan.FromMinutes(UnschedulableRunwayMinutes)).TotalMilliseconds;

            // IMPORTANT 1 fix: fired once up front from the STATIC estimate here, and
            // re-fired (see CheckUnschedulableWarnings, called after every Active pass
            // below) as ObservedPassCostMs ratchets upward. The static estimate alone
            // under-counts real Win32 costs (AttachAndFocus, MacroPlayer's preflight
            // resize) exactly the way NextActivePassCostMs's own lookahead does — a
            // macro that DECLARES 2 minutes but really takes 25 reads as safe here
            // (max(2min, interval) never crosses the warn threshold) and would
            // otherwise never warn at all, even once the ratchet learns the real
            // 25-minute cost and the realized keep-alive gap sails past Roblox's
            // 20-minute kick floor. alreadyWarnedUnschedulable makes this fire-once-
            // ever per alt: the ratchet only ever climbs, so once an alt has crossed
            // the threshold it stays crossed — no need to track a re-arming edge the
            // way EmitFocusFailureWarning does for a streak that can recover.
            var alreadyWarnedUnschedulable = new HashSet<ScheduledAlt>();

            void CheckUnschedulableWarnings()
            {
                var longestActivePassMs = Schedule
                    .Where(a => !a.IsKeepAlive)
                    .Select(a => Math.Max(StaticPassCostMs(a), a.ObservedPassCostMs))
                    .DefaultIfEmpty(0L)
                    .Max();

                foreach (var alt in Schedule.Where(a => a.IsKeepAlive))
                {
                    if (alreadyWarnedUnschedulable.Contains(alt)) continue;
                    var projectedGapMs = Math.Max(longestActivePassMs, alt.IntervalMs);
                    if (projectedGapMs < WarnThresholdMs(alt)) continue;

                    alreadyWarnedUnschedulable.Add(alt);
                    var gapMins = projectedGapMs / 60_000.0;
                    EmitProgress(new AssignmentProgress(
                        0, -1, assignments.Count, alt.Assignment, AssignmentPhase.Warning,
                        $"{alt.Assignment.Alt.DisplayName} may get kicked — its projected keep-alive gap is " +
                        $"~{gapMins:F0} min, at or near Roblox's {KeepAliveLimits.PlatformIdleKickFloorMinutes}-minute " +
                        $"idle floor (games may shorten this, none may extend it). " +
                        $"Shorten the macro, split it, or set this alt to Active."));
                }
            }

            CheckUnschedulableWarnings();

            // Original list position of each alt — this is what IndexInCycle/TotalInCycle
            // report. The scheduler services one alt at a time (not a fixed sweep through the
            // list), so "cycle" below tracks logical PASSES: it advances the first time an
            // alt is serviced a second time since the last pass boundary, mirroring what the
            // old strict round-robin meant by "cycle" — one full lap — without requiring every
            // alt to actually be revisited in list order (a keep-alive miles from due just
            // rides along without incrementing anything).
            var indexOf = _indexOf;
            indexOf.Clear();
            for (int i = 0; i < initialScheduled.Count; i++) indexOf[initialScheduled[i]] = i;
            _nextIndex = initialScheduled.Count;
            var servicedThisPass = new HashSet<ScheduledAlt>();
            cycle = 1;   // declared ahead of the try (see above) so finally can read it too

            // Locked: the watchdog's services advance the pass too, from another thread.
            int AdvancePass(ScheduledAlt alt)
            {
                lock (servicedThisPass)
                {
                    if (!servicedThisPass.Add(alt))
                    {
                        cycle++;
                        servicedThisPass.Clear();
                        servicedThisPass.Add(alt);
                    }
                    return cycle;
                }
            }

            var lastSeenInput = new Dictionary<long, long?>();
            var activeCursor = 0;
            int ActiveCount() => _scheduled!.Count(a => !a.IsKeepAlive);   // accounts that join mid-run are keep-alive,
                                                                           // but one may leave, so it is read each time

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
                var activeCount = ActiveCount();
                activeCursor = activeCount == 0 ? 0 : (activeCursor + 1) % activeCount;   // bounded — never overflows
                gapFittedSinceActivePass.Clear();   // real progress happened; every alt earns another look
                // IMPORTANT 1 fix: a completed pass is exactly when ObservedPassCostMs
                // may have just ratcheted up (see RunActiveAsync's finally) — re-check
                // every keep-alive against the now-current longest observed cost.
                CheckUnschedulableWarnings();
            }

            // Keep-alive takes its turn through the gate (spec "Pause and pick up"). Every service, from
            // the main loop or the watchdog, comes through here: one at a time (_turn), and only once the
            // gate has granted a break. With no playback in the break is granted at once, so nothing
            // changes. With one in, it parks at its next step boundary first, and the break serves every
            // keep-alive due within the batch window through the sweep (countdown and input guard), whether
            // or not turbo is ticked; the sweep's captured foreground is the paused macro's window, so its
            // restore hands the foreground back to the macro. chosen is the main loop's pick, null for the
            // watchdog; dueSeen is chosen's due time when Decide picked it (both read under _turn).
            // A playback is never paused for the account it plays on until that account is critical (idle at
            // its limit less a minute): returns true when it deferred for that reason, and the main loop
            // waits a watchdog tick before looking again.
            async Task<bool> TakeTurnAsync(ScheduledAlt? chosen, long dueSeen, CancellationToken token)
            {
                try { await _turn.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
                try
                {
                    var now = _deps.ClockMs();
                    List<ScheduledAlt> urgent;
                    if (chosen is not null)
                    {
                        if (chosen.DueAtMs != dueSeen) return false;   // the watchdog served it while we waited for the turn
                        if (IsOff(chosen)) return false;                  // switched off since Decide picked it
                        if (OwnPlaybackCanWait(chosen, _gate.Status.PlaybackTarget, now)) return true;
                        urgent = [chosen];
                    }
                    else
                    {
                        if (_gate.Status.PlaybackTarget is not { } target) return false;   // ended: the main loop has it
                        urgent = WatchdogDue(Schedule, target, now);
                        if (urgent.Count == 0) return false;
                    }

                    IDisposable lease;
                    try
                    {
                        lease = await _gate.RequestBreakAsync(
                            urgent[0].Assignment.Alt.DisplayName, PreemptAfter(urgent, now), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return false; }
                    using (lease)
                    {
                        var paused = _gate.Status.PlaybackTarget;   // non-null: a playback is parked for this break
                        // A playback may have come in while we waited for the grant: the same rule again.
                        var grantedAt = _deps.ClockMs();
                        urgent = urgent.Where(a => !IsOff(a) && !OwnPlaybackCanWait(a, paused, grantedAt)).ToList();
                        if (urgent.Count == 0) return chosen is not null;
                        var first = urgent[0];
                        if (paused is { } p) _ledger.SetState(p, AccountKeepAliveState.PausedForMacro);
                        try
                        {
                            if (paused is not null || TurboKeepAlive)
                            {
                                // Pulling an alt forward only ever taps it earlier, the safe direction. Every
                                // swept alt counts as gap-fitted, so the forward-progress guard holds for them.
                                var batch = BreakBatch(Schedule, urgent, paused, _deps.ClockMs());
                                if (chosen is not null) foreach (var a in batch) gapFittedSinceActivePass.Add(a);
                                await ServiceKeepAliveSweepAsync(
                                    batch, AdvancePass, indexOf, assignments.Count, token).ConfigureAwait(false);
                            }
                            else
                            {
                                await ServiceKeepAliveAsync(
                                    first, AdvancePass(first), indexOf[first], assignments.Count, token).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            if (paused is { } p2) _ledger.SetState(p2, AccountKeepAliveState.Waiting);
                        }
                    }
                    return false;
                }
                finally { ReleaseTurn(); }
            }

            // The watchdog covers what the main loop cannot: while the runner's own Active pass plays, the
            // loop is inside it and cannot notice a keep-alive deadline. Each second a playback is in, it
            // looks for a due keep-alive and takes the turn for it. It never ticks while nothing plays.
            // A failed service is logged and the watch goes on (the tick rate-limits it); only the run's
            // cancellation ends it.
            async Task WatchdogAsync(CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await _gate.WhenPlaybackInAsync(token).ConfigureAwait(false);
                        await _deps.Sleep(WatchdogTickMs, token).ConfigureAwait(false);
                        if (_gate.Status.PlaybackTarget is not { } target) continue;
                        if (WatchdogDue(Schedule, target, _deps.ClockMs()).Count == 0) continue;   // Schedule is a whole snapshot
                        await TakeTurnAsync(null, 0, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        try { _deps.SweepLog($"keep-alive watchdog: a service failed ({ex.Message}); still watching"); }
                        catch { /* a log line must never end the watch */ }
                    }
                }
            }

            watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watchToken = watchCts.Token;
            watchdog = Task.Run(() => WatchdogAsync(watchToken));

            while (!ct.IsCancellationRequested)
            {
                _reporter.FlushMinute();
                // Under the turn: the watchdog's services write the same due times, so the refresh's
                // read-modify-write and Decide never see one half-done, and the due Decide acted on is the
                // one TakeTurnAsync checks for a service that landed in between.
                try { await _turn.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                long now;
                IReadOnlyList<ScheduledAlt> rotated;
                CadenceDecision decision;
                long decidedDue;
                try
                {
                    RefreshDueFromLedger(Schedule, lastSeenInput);
                    now = _deps.ClockMs();
                    // An off account is never picked: its entry stays in the schedule and the ledger, so its
                    // clock keeps running and it shows as off, but the scheduler does not see it.
                    rotated = Rotated(Schedule, activeCursor).Where(a => !a.IsKeepAlive || !IsOff(a)).ToList();
                    decision = CadenceScheduler.Decide(rotated, now, NextActivePassCostMs(Schedule, activeCursor));
                    decidedDue = decision is CadenceDecision.ServiceKeepAlive picked ? picked.Alt.DueAtMs : 0;
                }
                finally { ReleaseTurn(); }

                switch (decision)
                {
                    case CadenceDecision.SleepUntil sleep:
                        // The whole point: nothing to do, so do NOTHING. No focus steal.
                        // Never sleep past the moment the open miss group is due to be written.
                        var sleepMs = sleep.WakeAtMs - now;
                        if (_reporter.UntilFlush is { } until)
                            sleepMs = Math.Min(sleepMs, Math.Max(1_000, (long)until.TotalMilliseconds));
                        try { await _deps.Sleep(sleepMs, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        break;

                    case CadenceDecision.ServiceKeepAlive svc:
                    {
                        var genuinelyOverdue = decidedDue <= now;
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

                        // Genuinely overdue (never starved), or this is its first look since the last
                        // Active pass: service it for real, through the gate. Turbo (or a playback that
                        // parks for the break) sweeps every keep-alive due within the batch window along
                        // with it, so the user is interrupted once, not once per alt.
                        if (await TakeTurnAsync(svc.Alt, decidedDue, ct).ConfigureAwait(false))
                        {
                            // Its own macro is playing and it can still wait: look again in a tick. The watchdog
                            // serves it at its limit less a minute if the playback is still in by then.
                            try { await _deps.Sleep(WatchdogTickMs, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                        }
                        break;
                    }

                    case CadenceDecision.RunActive run:
                        _reporter.FlushMinute(force: true);   // an Active pass can be long; say what happened first
                        await RunNextActiveAsync(run.Alt, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            // CRITICAL 2 fix: Stopped must fire on EVERY exit from the loop above,
            // including an exception escaping the loop body (e.g.
            // MacroPlayer.PlayAsync's Task.Delay throwing ArgumentOutOfRangeException
            // off a pathological on-disk MacroEvent.TimestampMs) — not just the
            // graceful cancellation-observed exit. Stopped used to be emitted as the
            // last line INSIDE the try, so an exception jumped straight past it to
            // this finally, skipping it entirely. Stopped is the ONLY thing that
            // releases the ur-afk claim file (see PluginRuntime's Progress
            // subscription) — skipping it left the claim's 20s heartbeat rewriting a
            // fresh heartbeatUtc for the rest of the app's life while nothing was
            // actually servicing those alts: ur-afk reads a live in-TTL claim and
            // stays off them, so every alt in the run gets silently kicked. It also
            // wedged the UI (RecorderViewModel.IsRunnerActive derives from the last
            // progress Phase, so it never dropped off "running") and made Abort()
            // return false once _activeCts below went null, with no way to recover
            // short of restarting the app.
            if (watchCts is not null)
            {
                // Stopped before Stopped is said: no keep-alive service runs after it.
                try { watchCts.Cancel(); } catch (ObjectDisposedException) { }
                try { await watchdog!.ConfigureAwait(false); } catch { /* it logs its own failure */ }
                watchCts.Dispose();
            }
            try { _reporter.FlushMinute(force: true); } catch { /* a log line must never block the shutdown */ }
            EmitProgress(new AssignmentProgress(cycle, -1, assignments.Count, null, AssignmentPhase.Stopped));
            _scheduled = null;
            _membership.Clear();
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
    /// The ledger is the source of truth: input stamped by anyone else (host activity, a macro's keys)
    /// defers a keep-alive. Only ever moves a due time later, so a 30s miss backoff is not pulled earlier.
    /// New input also re-arms the once-per-stretch reset warning.
    /// </summary>
    private void RefreshDueFromLedger(IReadOnlyList<ScheduledAlt> scheduled, Dictionary<long, long?> lastSeen)
    {
        foreach (var alt in scheduled)
        {
            if (!alt.IsKeepAlive) continue;
            var userId = alt.Assignment.Alt.RobloxUserId;
            if (_ledger.Get(userId) is not { LastInputMs: { } last }) continue;
            alt.DueAtMs = Math.Max(alt.DueAtMs, _ledger.DueAtMs(userId));
            if (lastSeen.TryGetValue(userId, out var seen) && seen is { } s && last > s)
                _reporter.Rearm(alt.Assignment.Alt.DisplayName);
            lastSeen[userId] = last;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>The later of the alt's own due time (which may carry a miss backoff) and the ledger's, which
    /// moves with input stamped by anyone.</summary>
    private long EffectiveDueMs(ScheduledAlt alt)
    {
        var userId = alt.Assignment.Alt.RobloxUserId;
        return _ledger.Get(userId) is { LastInputMs: not null }
            ? Math.Max(alt.DueAtMs, _ledger.DueAtMs(userId))
            : alt.DueAtMs;
    }

    private bool IsOff(ScheduledAlt alt) => _ledger.Get(alt.Assignment.Alt.RobloxUserId)?.Off == true;

    /// <summary>Idle has reached the limit less the preempt margin: past the point where the account can wait.
    /// An account with no input on either clock counts its idle from when the runner first tracked it.</summary>
    private bool IsCritical(ScheduledAlt alt, long now)
    {
        var userId = alt.Assignment.Alt.RobloxUserId;
        var e = _ledger.Get(userId);
        long since;
        if (e?.LastInputMs is { } last) since = last;
        else
        {
            lock (_trackedAtMs) { if (!_trackedAtMs.TryGetValue(userId, out since)) return false; }
        }
        var limit = e?.Limit ?? alt.Limit!;
        return now - since >= (long)limit.Limit.TotalMilliseconds - PreemptMarginMs;
    }

    /// <summary>The playback in plays on this account and the account is not critical yet: its own macro is
    /// the only thing it would be paused for, so it waits (spec: a playback is never paused for itself).</summary>
    private bool OwnPlaybackCanWait(ScheduledAlt alt, long? playbackTarget, long now)
        => playbackTarget == alt.Assignment.Alt.RobloxUserId && !IsCritical(alt, now);

    /// <summary>Keep-alive alts the watchdog serves now: due, not off, and not the playback's own account,
    /// whose own keys keep it credited, unless it has idled to the limit less a minute (then a Space at
    /// a boundary on its own window is harmless). Most urgent first.</summary>
    private List<ScheduledAlt> WatchdogDue(IReadOnlyList<ScheduledAlt> scheduled, long playbackTarget, long now)
        => scheduled
            .Where(a => a.IsKeepAlive && !IsOff(a) && EffectiveDueMs(a) <= now
                        && (a.Assignment.Alt.RobloxUserId != playbackTarget || IsCritical(a, now)))
            .OrderBy(EffectiveDueMs)
            .ToList();

    /// <summary>Everyone one break serves: the alts it was asked for, plus every keep-alive due within the
    /// batch window, leaving out the paused playback's own account unless it is critical.</summary>
    private List<ScheduledAlt> BreakBatch(
        IReadOnlyList<ScheduledAlt> scheduled, IReadOnlyList<ScheduledAlt> asked, long? paused, long now)
    {
        var horizon = now + TurboBatchWindowMs;
        return scheduled
            .Where(a => a.IsKeepAlive && !IsOff(a) && (asked.Contains(a)
                || (EffectiveDueMs(a) <= horizon
                    && (a.Assignment.Alt.RobloxUserId != paused || IsCritical(a, now)))))
            .OrderBy(EffectiveDueMs)
            .ToList();
    }

    /// <summary>How long a playback gets to reach a step boundary before keep-alive cuts it: the most urgent
    /// alt's limit less a minute, less its idle so far, never below zero. Unknown idle reads as the deadline.</summary>
    private TimeSpan PreemptAfter(IEnumerable<ScheduledAlt> alts, long now)
    {
        var runwayMs = long.MaxValue;
        foreach (var a in alts)
        {
            var e = _ledger.Get(a.Assignment.Alt.RobloxUserId);
            var limit = e?.Limit ?? a.Limit!;
            var idleMs = e?.LastInputMs is { } last ? now - last : (long)limit.Deadline.TotalMilliseconds;
            runwayMs = Math.Min(runwayMs, (long)limit.Limit.TotalMilliseconds - PreemptMarginMs - idleMs);
        }
        return TimeSpan.FromMilliseconds(runwayMs == long.MaxValue ? 0 : Math.Max(0, runwayMs));
    }

    /// <summary>The account shows as Serving until the scope ends, then Waiting.</summary>
    private IDisposable Serving(long userId)
    {
        _ledger.SetState(userId, AccountKeepAliveState.Serving);
        return new StateScope(() => _ledger.SetState(userId, AccountKeepAliveState.Waiting));
    }

    private sealed class StateScope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }

    /// <summary>The alt's limit from the deps, or derived from its (clamped) interval when the deps give none.</summary>
    private KeepAliveLimit LimitFor(AccountRegistry.AccountInfo alt, long intervalMs)
    {
        if (_deps.KeepAliveLimitFor is { } f) return f(alt);
        var deadline = TimeSpan.FromMilliseconds(intervalMs);
        // No game knowledge here, so the only limit that holds is the platform floor.
        var floor = TimeSpan.FromMinutes(KeepAliveLimits.PlatformIdleKickFloorMinutes);
        return new KeepAliveLimit(deadline > floor ? deadline : floor, deadline, false);
    }

    /// <summary>
    /// A keep-alive attempt did not land: record it in the ledger (the source of truth for the streak),
    /// back off 30s, say once if a reset is now unavoidable, and surface the 3-miss warning with its cause.
    /// <paramref name="log"/> is false for the turbo sweep, which writes its own 0.12.1 line.
    /// </summary>
    private void RecordKeepAliveMiss(
        ScheduledAlt alt, Miss miss, int cycle, int index, int total, bool log)
    {
        var asn = alt.Assignment;
        var userId = asn.Alt.RobloxUserId;
        var now = _deps.ClockMs();
        var count = _ledger.RecordMiss(userId, miss);
        alt.ConsecutiveFocusFailures = count > 0 ? count : alt.ConsecutiveFocusFailures + 1;
        alt.DueAtMs = now + FocusRetryBackoffMs;
        if (log) _reporter.Missed(asn.Alt.DisplayName, miss, alt.ConsecutiveFocusFailures);

        // Idle has reached the deadline and the next retry cannot land before the game's limit.
        if (_ledger.Get(userId) is { LastInputMs: { } last } e)
        {
            var idleMs = now - last;
            var resetAt = last + (long)e.Limit.Limit.TotalMilliseconds;
            if (idleMs >= (long)e.Limit.Deadline.TotalMilliseconds && alt.DueAtMs > resetAt)
                _reporter.ExpectReset(asn.Alt.DisplayName, _ledger.ToWall(resetAt), (int)(idleMs / 60_000));
        }
        EmitFocusFailureWarning(alt, cycle, index, total, asn, MissReporter.CauseText(miss));
    }

    /// <summary>A Space landed: stamp the ledger, take the next due time from it, clear the streak, report recovery.</summary>
    private void RecordKeepAliveLanded(ScheduledAlt alt)
    {
        var asn = alt.Assignment;
        var userId = asn.Alt.RobloxUserId;
        var before = _ledger.Get(userId);
        var now = _deps.ClockMs();
        _ledger.StampLanded(userId, InputSource.KeepAlive);
        try { _deps.MarkActive(asn.Alt.AccountId); } catch { /* a courtesy call never breaks keep-alive */ }
        alt.ConsecutiveFocusFailures = 0;
        alt.DueAtMs = before is null ? now + alt.IntervalMs : _ledger.DueAtMs(userId);
        long? idleMs = before?.LastInputMs is { } last ? now - last : null;
        _reporter.Landed(before, asn.Alt.DisplayName, idleMs);
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
        using var serving = Serving(asn.Alt.RobloxUserId);
        EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Focusing));

        var prior = _deps.CaptureForeground();     // whatever the USER was doing
        var restoreNeeded = false;                  // gates the restore below — see finally
        try
        {
            var focusAt = _deps.ClockMs();
            var attempt = _deps.Focus(asn.Alt.Pid);
            // A gone window is final: no verify, nothing to wait for. A refusal is soft, Windows
            // sometimes brings the window forward anyway, so it still gets the verify below.
            if (!attempt.Ok && attempt.Cause == MissCause.WindowGone)
            {
                RecordKeepAliveMiss(alt, new Miss(MissCause.WindowGone, null), cycle, index, total, log: true);
                return;   // nothing was stolen — Focus never actually flipped anything
            }
            var refused = !attempt.Ok;
            restoreNeeded = !refused;

            try { await _deps.Sleep(DefaultPerAltDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                restoreNeeded = true;   // a refusal may still have brought the window forward: hand it back
                return;
            }

            // Safety invariant (unchanged from v0.6): never synthesize input unless the
            // foreground really is the alt we aimed at. AttachAndFocus reports ok=true
            // whenever the process merely HAS a main window — it does not confirm the
            // foreground actually flipped — so this is the check that actually catches
            // that gap, and it is the MOST COMMON way a real service attempt fails.
            var fg = _foreground.ResolveForegroundAccount();
            if (fg is null || fg.RobloxUserId != asn.Alt.RobloxUserId)
            {
                var miss = refused
                    ? new Miss(MissCause.FocusRefused, null)
                    : new Miss(MissCause.OtherInFront, _deps.DescribeForeground());
                RecordKeepAliveMiss(alt, miss, cycle, index, total, log: true);
                return;
            }
            restoreNeeded = true;
            var toFrontMs = _deps.ClockMs() - focusAt;

            EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Playing));
            if (!_deps.SendKeepAlive())
            {
                // Windows rejected the input: nothing landed, so this is a miss like a failed verify.
                RecordKeepAliveMiss(alt, new Miss(MissCause.SendRejected, null), cycle, index, total, log: true);
                return;   // finally still restores: the focus was taken
            }
            RecordKeepAliveLanded(alt);                        // a GENUINELY successful service clears the streak
            _deps.SweepLog($"keep-alive: Space to {asn.Alt.DisplayName} ({toFrontMs} ms to front)");
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
            // run keeps going. A refused focus that then verified is the one exception:
            // the window did come forward, so it is restored like any other.
            if (restoreNeeded)
            {
                _deps.RestoreForeground(prior);
            }
        }
    }

    /// <summary>
    /// Turbo keep-alive: one warned sweep across <paramref name="batch"/>.
    ///
    /// 1. Countdown. <see cref="CadenceDeps.SweepCountdown"/> gets 3, 2, 1 a second
    ///    apart, then 0 (hide) on every exit, cancellation included. Nothing has been
    ///    taken yet, so a cancel here restores nothing.
    /// 2. Capture the user's foreground ONCE, after the countdown — whatever they
    ///    are on at the moment the sweep actually starts.
    /// 3. Per alt: Focus, then poll <see cref="IForegroundWatcher.ResolveForegroundAccount"/>
    ///    every <see cref="TurboPollIntervalMs"/> up to <see cref="TurboConfirmCapMs"/>.
    ///    The moment it reads the target alt, send Space and wait
    ///    <see cref="TurboPostKeyMs"/>. Cap expiry = the same 30s backoff and
    ///    streak a failed verify takes on the normal path, and NO input.
    /// 4. Restore the captured foreground ONCE, in a finally, if any focus was
    ///    actually taken — the same gate <see cref="ServiceKeepAliveAsync"/> uses.
    ///
    /// Owns DueAtMs and ConsecutiveFocusFailures for every alt it reaches, exactly
    /// like <see cref="ServiceKeepAliveAsync"/>. An alt the sweep never reaches
    /// (cancelled first) keeps its old DueAtMs; the loop is exiting anyway.
    /// </summary>
    private async Task ServiceKeepAliveSweepAsync(
        IReadOnlyList<ScheduledAlt> batch,
        Func<ScheduledAlt, int> advancePass,
        IReadOnlyDictionary<ScheduledAlt, int> indexOf,
        int total,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;

        try
        {
            for (int s = TurboCountdownSeconds; s > 0; s--)
            {
                _deps.SweepCountdown(s, batch.Count);
                await _deps.Sleep(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return; }   // finally hides; nothing was taken
        finally
        {
            try { _deps.SweepCountdown(0, batch.Count); } catch { /* a toast must never break the sweep */ }
        }
        if (ct.IsCancellationRequested) return;

        // Disposed after the finally below has restored the foreground.
        using var guard = _deps.GuardUserInput();
        var prior = _deps.CaptureForeground();
        var anyFocusTaken = false;
        int sent = 0, reached = 0;
        try
        {
            foreach (var alt in batch)
            {
                if (ct.IsCancellationRequested) return;   // Abort hotkey: stop between alts

                var asn = alt.Assignment;
                using var serving = Serving(asn.Alt.RobloxUserId);   // ends with this alt's iteration
                var cycle = advancePass(alt);
                var index = indexOf[alt];
                var name = asn.Alt.DisplayName;
                reached++;
                EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Focusing));

                var focusAt = _deps.ClockMs();
                var attempt = _deps.Focus(asn.Alt.Pid);
                if (!attempt.Ok && attempt.Cause == MissCause.WindowGone)
                {
                    RecordKeepAliveMiss(alt, new Miss(MissCause.WindowGone, null), cycle, index, total, log: false);
                    _deps.SweepLog($"turbo keep-alive: skipped {name}, focus failed; retry in {FocusRetryBackoffMs / 1000}s");
                    continue;   // nothing stolen by THIS alt; the sweep moves on
                }
                // A refusal is soft: the window sometimes comes forward anyway, so it still gets the polled confirm.
                var refused = !attempt.Ok;
                if (!refused) anyFocusTaken = true;

                bool confirmed;
                try { confirmed = await PollForegroundIsAsync(asn.Alt.RobloxUserId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { anyFocusTaken = true; return; }   // a refusal may still have moved it

                if (!confirmed)
                {
                    // Safety invariant: the foreground never read as this alt within
                    // the cap — send nothing, back off, warn on the streak crossing.
                    var miss = refused
                        ? new Miss(MissCause.FocusRefused, null)
                        : new Miss(MissCause.OtherInFront, _deps.DescribeForeground());
                    RecordKeepAliveMiss(alt, miss, cycle, index, total, log: false);
                    _deps.SweepLog(refused
                        ? $"turbo keep-alive: skipped {name}, focus failed; retry in {FocusRetryBackoffMs / 1000}s"
                        : $"turbo keep-alive: skipped {name}, not in front within {TurboConfirmCapMs} ms; retry in {FocusRetryBackoffMs / 1000}s");
                    continue;
                }
                anyFocusTaken = true;
                var confirmMs = _deps.ClockMs() - focusAt;
                try { await _deps.Sleep(TurboSettleMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                // Re-check after the settle: the user (or another window) can take the foreground in those ms.
                var settled = _foreground.ResolveForegroundAccount();
                if (settled is null || settled.RobloxUserId != asn.Alt.RobloxUserId)
                {
                    RecordKeepAliveMiss(alt, new Miss(MissCause.OtherInFront, _deps.DescribeForeground()),
                        cycle, index, total, log: false);
                    _deps.SweepLog($"turbo keep-alive: skipped {name}, lost the foreground in the settle; retry in {FocusRetryBackoffMs / 1000}s");
                    continue;
                }
                if (ct.IsCancellationRequested) return;   // last check before input

                EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Playing));
                if (!_deps.SendKeepAlive())
                {
                    RecordKeepAliveMiss(alt, new Miss(MissCause.SendRejected, null), cycle, index, total, log: false);
                    _deps.SweepLog($"turbo keep-alive: skipped {name}, Windows rejected the input; retry in {FocusRetryBackoffMs / 1000}s");
                    continue;
                }
                RecordKeepAliveLanded(alt);
                sent++;
                _deps.SweepLog($"turbo keep-alive: Space to {name} ({confirmMs} ms to front)");

                try { await _deps.Sleep(TurboPostKeyMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            if (anyFocusTaken) _deps.RestoreForeground(prior);
            _deps.SweepLog(ct.IsCancellationRequested
                ? $"turbo keep-alive: sweep stopped after {reached} of {batch.Count}, {sent} Space sent"
                : $"turbo keep-alive: sweep done, {sent} of {batch.Count} Space sent");
        }
    }

    /// <summary>
    /// Polled foreground confirm for turbo: true the moment the foreground resolves
    /// to <paramref name="robloxUserId"/>, false once <see cref="TurboConfirmCapMs"/>
    /// has passed without a match. Checks immediately, then every
    /// <see cref="TurboPollIntervalMs"/>. Throws on cancellation.
    /// </summary>
    private async Task<bool> PollForegroundIsAsync(long robloxUserId, CancellationToken ct)
    {
        var startMs = _deps.ClockMs();
        while (true)
        {
            var fg = _foreground.ResolveForegroundAccount();
            if (fg is not null && fg.RobloxUserId == robloxUserId) return true;
            if (_deps.ClockMs() - startMs >= TurboConfirmCapMs) return false;
            await _deps.Sleep(TurboPollIntervalMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Shared Skipped/Warning emission for a failed focus OR a failed foreground-verify
    /// — both mean "this attempt didn't land," and both count toward the same
    /// ConsecutiveFocusFailures streak.
    ///
    /// On the Active side that streak is shared with a THIRD, unrelated failure mode
    /// too — <see cref="RunActiveAsync"/>'s own Refused/Aborted branch increments the
    /// same counter (with its own distinct warning text) — so by the time this method
    /// sees the streak cross 3, those 3 consecutive attempts are not guaranteed to all
    /// have been focus/verify failures: e.g. two Refused plays followed by one real
    /// focus failure also reads as ConsecutiveFocusFailures == 3 here. The message
    /// below is worded to stay true under that mix (it does NOT claim "3 tries have
    /// all failed to focus"), rather than tracking a separate counter per failure mode
    /// just to make the wording marginally more specific.
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
    private void EmitFocusFailureWarning(
        ScheduledAlt alt, int cycle, int index, int total, Assignment asn, string? lastCause = null)
    {
        if (alt.ConsecutiveFocusFailures == 3)
        {
            EmitProgress(new AssignmentProgress(
                cycle, index, total, asn, AssignmentPhase.Warning,
                $"{asn.Alt.DisplayName} hasn't reliably reached the foreground in 3 tries — its window " +
                $"may be gone, or focus isn't sticking. Still retrying every 30s." +
                (lastCause is null ? "" : $" Last cause: {lastCause}.")));
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
    ///
    /// CRITICAL 1 fix: all three ways this method can end an attempt without a
    /// completed pass — Focus failing, the foreground-verify failing, and
    /// PlayAsync coming back Refused/Aborted — now take the SAME bounded 30s
    /// backoff and count against the SAME <see cref="ScheduledAlt.ConsecutiveFocusFailures"/>
    /// streak <see cref="ServiceKeepAliveAsync"/> already uses. <c>Decide</c> has no
    /// <c>DueAtMs</c> gate for Active — nothing else stops the outer loop from
    /// re-entering <c>Decide</c> immediately and re-picking this exact alt — so an
    /// unguarded exit here is a foreground steal roughly once a second, forever:
    /// the same hijack loop this whole feature exists to kill. This is a live field
    /// path, not hypothetical: <see cref="MacroPlayer.PlayAsync"/>'s client-space
    /// preflight (<c>EnsureClientSize</c>) returns <c>Refused</c> synchronously, with
    /// no waits, whenever a macro was recorded on a bigger monitor than it's
    /// replaying on. The streak resets only on a genuinely successful pass
    /// (<see cref="PlaybackOutcome.Completed"/>) — a transient blip must not linger
    /// into a stale warning later.
    /// </summary>
    private async Task RunActiveAsync(ScheduledAlt alt, int cycle, int index, int total, CancellationToken ct)
    {
        var asn = alt.Assignment;
        var startMs = _deps.ClockMs();
        try
        {
            // Phase: Focusing
            EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Focusing));
            if (!_deps.Focus(asn.Alt.Pid).Ok)
            {
                // Bounded retry — same shape as ServiceKeepAliveAsync's focus-fail
                // exit: count it against the shared streak and reuse the same
                // once-per-crossing Warning idiom (IMPORTANT 2), so a dead/crashed
                // Active alt gets the same "hasn't reliably reached the foreground"
                // surfacing a KeepAlive alt already gets, instead of vanishing into
                // a silent Skipped forever.
                alt.ConsecutiveFocusFailures++;
                EmitFocusFailureWarning(alt, cycle, index, total, asn);
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
                // CRITICAL 1 fix: this used to be a bare return with NO backoff — the
                // "MOST COMMON way a real service attempt fails" per
                // ServiceKeepAliveAsync's own doc, left completely uncovered on the
                // Active side. Same counted streak + bounded backoff as the
                // focus-fail branch just above.
                alt.ConsecutiveFocusFailures++;
                EmitFocusFailureWarning(alt, cycle, index, total, asn);
                try { await _deps.Sleep(FocusRetryBackoffMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
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
                    //
                    // Closed-by-default: treat only PlaybackOutcome.Completed as a
                    // successful pass, not "everything that isn't Refused/Aborted".
                    // PlaybackOutcome.Skipped exists (MacroPlayer.cs has a public
                    // factory for it) and IMacroPlayer.PlayAsync never returns it
                    // today — so the old open-by-default `is Refused or Aborted`
                    // check was only safe by accident. If any implementation ever
                    // DOES return Skipped, that used to fall into the `else` branch
                    // below, reset the streak, and take zero backoff after only the
                    // 1s settle — the exact ~1-second foreground-hijack spin this
                    // whole feature exists to eliminate.
                    if (playResult.Outcome != PlaybackOutcome.Completed)
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            EmitProgress(new AssignmentProgress(
                                cycle, index, total, asn, AssignmentPhase.Refused, playResult.Reason));
                        }
                        // CRITICAL 1 fix: a Refused/Aborted PlayAsync result used to
                        // fall straight through with NO backoff — e.g.
                        // MacroPlayer.EnsureClientSize returning Refused
                        // SYNCHRONOUSLY (recorded on a bigger monitor than this one
                        // is replaying on) reproduces the exact same ~1s
                        // foreground-steal spin as the other two exits above. Same
                        // counted streak + bounded backoff; its own once-per-crossing
                        // warning uses distinct wording (this alt WAS focusable —
                        // it's the macro itself getting refused/aborted right now —
                        // so it isn't routed through EmitFocusFailureWarning's
                        // "hasn't reliably reached the foreground" text). Worded as
                        // "keeps failing to complete" rather than "N times in a row":
                        // ConsecutiveFocusFailures is shared with the focus-fail and
                        // verify-fail branches above, so a streak that crosses 3 here
                        // is not guaranteed to have been 3 refusals back to back — a
                        // focus or verify blip could have contributed to the count
                        // too. Only the CURRENT attempt (the one that just crossed
                        // the threshold) is genuinely known to be a refusal/abort.
                        alt.ConsecutiveFocusFailures++;
                        if (alt.ConsecutiveFocusFailures == 3)
                        {
                            EmitProgress(new AssignmentProgress(
                                cycle, index, total, asn, AssignmentPhase.Warning,
                                $"{asn.Alt.DisplayName}'s macro keeps failing to complete (latest: " +
                                $"refused/aborted — {playResult.Reason}). Still retrying every 30s."));
                        }
                        try { await _deps.Sleep(FocusRetryBackoffMs, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                    else
                    {
                        // A genuinely successful pass — clear the streak so a
                        // transient blip doesn't linger into a stale warning later.
                        alt.ConsecutiveFocusFailures = 0;
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
            Diagnostics.DiagLog.Write($"keep-alive Space rejected by SendInput (down={down}, up={up}).");
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
