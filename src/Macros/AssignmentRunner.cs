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
    public static CadenceDeps Real => new(
        Focus: Win32Focus.AttachAndFocus,
        ClockMs: () => Environment.TickCount64,           // MONOTONIC — never wall-clock
        Sleep: (ms, ct) => ms <= 0 ? Task.CompletedTask : Task.Delay((int)ms, ct),
        CaptureForeground: Win32Focus.CaptureForeground,
        RestoreForeground: h => Win32Focus.RestoreForeground(h),
        SendKeepAlive: () => AssignmentRunner.SendSpaceKeepAlive(),
        KeepAliveIntervalMs: alt => (long)KeepAliveIntervals
            .For(alt.PlaceId, alt.PlaceName, UI.UserPreferences.Load()).TotalMilliseconds);
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

    private readonly IMacroPlayer _player;
    private readonly IForegroundWatcher _foreground;
    private readonly CadenceDeps _deps;
    private CancellationTokenSource? _activeCts;

    public AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground)
        : this(player, foreground, CadenceDeps.Real) { }

    internal AssignmentRunner(IMacroPlayer player, IForegroundWatcher foreground, Func<int, (bool, string?)> focus)
        : this(player, foreground, CadenceDeps.Real with { Focus = focus }) { }

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

        var scheduled = assignments.Select(a => new ScheduledAlt
        {
            Assignment = a,
            IntervalMs = a.Role == CadenceRole.KeepAlive ? _deps.KeepAliveIntervalMs(a.Alt) : 0,
            DueAtMs = _deps.ClockMs(),   // every keep-alive is due immediately on start:
                                         // tap once up front, THEN settle into its interval.
        }).ToList();

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

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = _deps.ClockMs();
                var decision = CadenceScheduler.Decide(
                    Rotated(scheduled, activeCursor), now, NextActivePassCostMs(scheduled, activeCursor));

                switch (decision)
                {
                    case CadenceDecision.SleepUntil sleep:
                        // The whole point: nothing to do, so do NOTHING. No focus steal.
                        try { await _deps.Sleep(sleep.WakeAtMs - now, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                        break;

                    case CadenceDecision.ServiceKeepAlive svc:
                        await ServiceKeepAliveAsync(
                            svc.Alt, AdvancePass(svc.Alt), indexOf[svc.Alt], assignments.Count, ct).ConfigureAwait(false);
                        // Re-read the clock: the service itself consumed real time.
                        svc.Alt.DueAtMs = _deps.ClockMs() + svc.Alt.IntervalMs;
                        break;

                    case CadenceDecision.RunActive run:
                        await RunActiveAsync(
                            run.Alt, AdvancePass(run.Alt), indexOf[run.Alt], assignments.Count, ct).ConfigureAwait(false);
                        activeCursor++;   // advance the round-robin among the actives
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
    /// wait for it. Macro.Duration is already known (last-event timestamp) — this is
    /// what turns cadence from a guess into a computed timeline.
    /// </summary>
    private static long NextActivePassCostMs(IReadOnlyList<ScheduledAlt> alts, int cursor)
    {
        var actives = alts.Where(a => !a.IsKeepAlive).ToList();
        if (actives.Count == 0) return 0;
        var next = actives[cursor % actives.Count];
        var macro = next.Assignment.Macro;
        var playMs = macro is null ? 0 : (long)macro.Duration.TotalMilliseconds;
        return playMs + DefaultPerAltDelayMs + (macro?.InterAltDelayMs ?? 500);
    }

    /// <summary>
    /// Service one overdue-or-soon-due keep-alive: capture the user's current
    /// foreground, focus the alt, verify the flip, tap Space, then hand the
    /// desktop back. A keep-alive is a ~1s blip, not a hijack.
    /// </summary>
    private async Task ServiceKeepAliveAsync(ScheduledAlt alt, int cycle, int index, int total, CancellationToken ct)
    {
        var asn = alt.Assignment;
        EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Focusing));

        var prior = _deps.CaptureForeground();     // whatever the USER was doing

        if (!_deps.Focus(asn.Alt.Pid).ok)
        {
            // Bounded retry — do NOT hammer a window that won't focus. After three
            // straight misses the window is almost certainly gone (alt closed/crashed),
            // so say so loudly instead of silently retrying it forever.
            alt.DueAtMs = _deps.ClockMs() + FocusRetryBackoffMs;
            alt.ConsecutiveFocusFailures++;
            EmitProgress(new AssignmentProgress(
                cycle, index, total, asn,
                alt.ConsecutiveFocusFailures >= 3 ? AssignmentPhase.Warning : AssignmentPhase.Skipped,
                alt.ConsecutiveFocusFailures >= 3
                    ? $"{asn.Alt.DisplayName} hasn't been focusable for {alt.ConsecutiveFocusFailures} tries — its window may be gone. Still retrying every 30s."
                    : null));
            return;
        }
        alt.ConsecutiveFocusFailures = 0;   // a good focus clears the streak
        try { await _deps.Sleep(DefaultPerAltDelayMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Safety invariant (unchanged from v0.6): never synthesize input unless the
        // foreground really is the alt we aimed at.
        var fg = _foreground.ResolveForegroundAccount();
        if (fg is null || fg.RobloxUserId != asn.Alt.RobloxUserId)
        {
            alt.DueAtMs = _deps.ClockMs() + FocusRetryBackoffMs;
            EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Skipped));
            _deps.RestoreForeground(prior);
            return;
        }

        EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Playing));
        _deps.SendKeepAlive();
        try { await _deps.Sleep(KeepAliveDelayMs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* still restore below */ }

        // Hand the desktop back. A keep-alive is a ~1s blip, not a hijack. When an
        // Active alt was farming, this also returns focus to it so farming resumes.
        _deps.RestoreForeground(prior);
    }

    /// <summary>
    /// Run one Active pass: focus, settle, verify foreground, play. Today's
    /// behavior, unchanged — and deliberately no restore-foreground afterward, so
    /// an Active alt holds focus between its own back-to-back passes (farming).
    /// </summary>
    private async Task RunActiveAsync(ScheduledAlt alt, int cycle, int index, int total, CancellationToken ct)
    {
        var asn = alt.Assignment;

        // Phase: Focusing
        EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Focusing));
        if (!_deps.Focus(asn.Alt.Pid).ok)
        {
            EmitProgress(new AssignmentProgress(cycle, index, total, asn, AssignmentPhase.Skipped));
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
}

// Refused covers both PlaybackOutcome.Refused (preflight declined, e.g. client-space
// resize failed) and PlaybackOutcome.Aborted (mid-playback foreground shift) from the
// player — from the round-robin's perspective both mean "this alt's macro didn't play
// to completion," surfaced with Reason so it isn't silently swallowed.
// Warning covers a keep-alive alt that's missed 3+ consecutive focus attempts —
// its window is almost certainly gone (alt closed/crashed) — surfaced louder than
// a routine Skipped so it doesn't get lost in the noise of transient focus blips.
public enum AssignmentPhase { Focusing, Playing, Skipped, Refused, Stopped, Warning }

public sealed record AssignmentProgress(
    int Cycle,
    int IndexInCycle,       // -1 = no current alt (Stopped state)
    int TotalInCycle,
    Assignment? Current,
    AssignmentPhase Phase,
    string? Reason = null); // set on Phase == Refused; carries PlaybackResult.Reason
