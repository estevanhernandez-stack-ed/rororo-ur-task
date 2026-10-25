using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Keep-alive takes its turn through the gate (spec "Pause and pick up"): a due account is served
/// at a step boundary of whatever playback is in, never mid-step, and the foreground goes back to
/// the paused macro's window. The fake clock moves in small chunks with a real yield between them,
/// so a playback's steps, the main loop and the watchdog interleave the way they do live.
/// </summary>
public class TakeoverTests
{
    private const long Sec = 1_000;
    private const long Min = 60_000;
    private static readonly KeepAliveLimit Fifteen = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), false);
    private static readonly DateTimeOffset WallBase = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
    private static readonly Macro LongMacro = new(SchemaVersion: 3, Id: "m", Name: "long", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 1, Events: Array.Empty<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(int n) => new(n, n, $"alt{n}", $"acct-{n}");

    private sealed class Rig
    {
        private long _now;
        private readonly object _eventsLock = new();
        private readonly List<string> _events = new();
        public long RunForMs;
        public required CancellationTokenSource Cts;
        public required KeepAliveLedger Ledger;
        public required ForegroundGate Gate;
        public AssignmentRunner Runner = null!;
        public volatile AccountRegistry.AccountInfo? Front;
        public readonly HashSet<int> Closed = new();
        public readonly List<FakePlayback> Playbacks = new();
        public Action<int>? OnFocus;
        public int SendThrows;                                   // the next N Spaces throw instead of landing
        public readonly List<(int pid, long at)> Taps = new();
        public readonly List<string> GateLog = new();
        public readonly List<string> SweepLog = new();

        public long NowMs => Interlocked.Read(ref _now);
        public void SetNow(long ms) => Interlocked.Exchange(ref _now, ms);
        public void Advance(long ms)
        {
            if (Interlocked.Add(ref _now, ms) >= RunForMs) Cts.Cancel();
        }

        public void Add(string e) { lock (_eventsLock) _events.Add(e); }
        public string[] Events { get { lock (_eventsLock) return _events.ToArray(); } }

        public bool AnyInStep { get { lock (Playbacks) return Playbacks.Any(p => p.InStep); } }

        public void StampAt(AccountRegistry.AccountInfo a, long atMs)
        {
            var back = NowMs;
            Ledger.Track(a, Fifteen);
            SetNow(atMs);
            Ledger.StampLanded(a.RobloxUserId, InputSource.KeepAlive);
            SetNow(back);
        }
    }

    /// <summary>A step-based playback on one account: enters the gate, parks at each boundary,
    /// and on resume checks its own window is in front, as MacroPlayer's step runner does.</summary>
    private sealed class FakePlayback(Rig rig, AccountRegistry.AccountInfo target, int steps, long stepMs,
        bool keyCredit = false, int ticksPerStep = 1)
    {
        public volatile bool InStep;

        public async Task<PlaybackResult> RunAsync(CancellationToken ct)
        {
            lock (rig.Playbacks) rig.Playbacks.Add(this);
            try
            {
                using var _ = rig.Gate.EnterPlayback(target.RobloxUserId, $"macro on {target.DisplayName}");
                rig.Front = target;
                for (int i = 0; i < steps; i++)
                {
                    var boundary = rig.Gate.AtBoundaryAsync(i, steps, ct);
                    if (!boundary.IsCompleted)
                    {
                        rig.Add($"paused at {i}");
                        try { await boundary; }
                        catch (OperationCanceledException) { return PlaybackResult.Aborted("cancelled"); }
                        rig.Add($"resumed at {i}");
                        if (rig.Closed.Contains(target.Pid) || rig.Front?.Pid != target.Pid)
                        {
                            rig.Add("aborted");
                            return PlaybackResult.Aborted("window gone");
                        }
                    }
                    if (ct.IsCancellationRequested) return PlaybackResult.Aborted("cancelled");
                    InStep = true;
                    for (int t = 0; t < ticksPerStep; t++)
                    {
                        rig.Advance(stepMs / ticksPerStep);
                        await Task.Delay(1, CancellationToken.None);
                    }
                    if (keyCredit) rig.Ledger.StampLanded(target.RobloxUserId, InputSource.Macro);
                    InStep = false;
                }
                rig.Add($"pass done on {target.Pid}");
                return PlaybackResult.Completed();
            }
            finally
            {
                InStep = false;
                lock (rig.Playbacks) rig.Playbacks.Remove(this);
            }
        }
    }

    private sealed class GatedPlayer(Rig rig, int steps, long stepMs) : IMacroPlayer
    {
        public bool IsPlaying => false;
        public event EventHandler<PlaybackStartedArgs>? Started { add { } remove { } }
        public event EventHandler<PlaybackEndedArgs>? Ended { add { } remove { } }
        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
            => new FakePlayback(rig, Alt((int)targetUserId), steps, stepMs).RunAsync(external);
        public Task<PlaybackResult> PlayAllWindowsRawAsync(Macro macro, CancellationToken external = default)
            => Task.FromResult(PlaybackResult.Completed());
        public bool Abort() => false;
    }

    private sealed class Fg(Rig rig) : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? ResolveForegroundAccount()
            => rig.Front is { } f && !rig.Closed.Contains(f.Pid) ? f : null;
    }

    private static Rig Build(long startMs, long runForMs, int activeSteps = 30, long activeStepMs = 10 * Sec)
    {
        Rig rig = null!;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // a hang fails, never wedges the suite
        var ledger = new KeepAliveLedger(() => rig.NowMs, () => WallBase + TimeSpan.FromMilliseconds(rig.NowMs));
        var deps = new CadenceDeps(
            Focus: pid =>
            {
                if (rig.AnyInStep) rig.Add("VIOLATION focus mid-step");
                rig.Add($"focus {pid}");
                rig.OnFocus?.Invoke(pid);
                if (rig.Closed.Contains(pid)) return new FocusAttempt(false, MissCause.WindowGone, null);
                rig.Front = Alt(pid);
                return new FocusAttempt(true, null, null);
            },
            ClockMs: () => rig.NowMs,
            Sleep: async (ms, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                // Chunks with a real yield between them: a long sleep lets other actors run meanwhile.
                do
                {
                    var chunk = Math.Min(Math.Max(ms, 0), 10 * Sec);
                    rig.Advance(chunk);
                    ms -= chunk;
                    await Task.Delay(1, CancellationToken.None);
                    ct.ThrowIfCancellationRequested();
                } while (ms > 0);
            },
            CaptureForeground: () => (IntPtr)(rig.Front?.Pid ?? 0),
            RestoreForeground: h =>
            {
                rig.Add($"restore {(int)h}");
                if ((int)h != 0 && !rig.Closed.Contains((int)h)) rig.Front = Alt((int)h);
            },
            SendKeepAlive: () =>
            {
                if (Interlocked.Decrement(ref rig.SendThrows) >= 0)
                {
                    rig.Add("send threw");
                    throw new InvalidOperationException("send blew up");
                }
                rig.Add($"tap {rig.Front?.Pid}");
                lock (rig.Taps) rig.Taps.Add((rig.Front?.Pid ?? 0, rig.NowMs));
                return true;
            },
            KeepAliveIntervalMs: _ => (long)Fifteen.Deadline.TotalMilliseconds)
        {
            SweepLog = line => { lock (rig.SweepLog) rig.SweepLog.Add(line); },
            KeepAliveLimitFor = _ => Fifteen,
        };
        rig = new Rig
        {
            Cts = cts,
            RunForMs = runForMs,
            Ledger = ledger,
            Gate = new ForegroundGate(() => rig.NowMs, line => { lock (rig.GateLog) rig.GateLog.Add(line); }),
        };
        rig.SetNow(startMs);
        rig.Runner = new AssignmentRunner(new GatedPlayer(rig, activeSteps, activeStepMs), new Fg(rig), deps, ledger, rig.Gate);
        return rig;
    }

    private static void AssertInOrder(string[] events, params string[] expected)
    {
        var at = 0;
        foreach (var e in expected)
        {
            var i = Array.FindIndex(events, at, x => x == e || (e.EndsWith(" at") && x.StartsWith(e + " ")));
            Assert.True(i >= 0, $"'{e}' not found in order after index {at}. Events: {string.Join(" | ", events)}");
            at = i + 1;
        }
    }

    /// <summary>Starts a bridge playback and returns once it is in the gate, so the runner never races its entry.</summary>
    private static async Task<Task<PlaybackResult>> StartPlayback(Rig rig, FakePlayback playback, CancellationToken ct)
    {
        var task = Task.Run(() => playback.RunAsync(ct));
        while (rig.Gate.Status.PlaybackTarget is null && !task.IsCompleted) await Task.Delay(1);
        return task;
    }

    private static Task RunKeepAlive(Rig rig, params AccountRegistry.AccountInfo[] alts)
        => rig.Runner.RunAsync(alts.Select(a => Assignment.WithDerivedRole(a, null)).ToList(), rig.Cts.Token);

    [Fact]
    public async Task A_bridge_playback_on_another_account_pauses_for_a_due_alt()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 25 * Min);
        var bridge = Alt(1); var keep = Alt(2);
        rig.StampAt(keep, 20 * Min - 9 * Min + 30 * Sec);   // due 30 s after the start
        using var pbCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        rig.Ledger.Track(bridge, Fifteen);   // the paused account shows as paused for the length of the break
        rig.OnFocus = pid => rig.Add($"states {rig.Ledger.Get(1)!.State} {rig.Ledger.Get(2)!.State}");
        var playback = await StartPlayback(rig, new FakePlayback(rig, bridge, steps: 20, stepMs: 10 * Sec), pbCts.Token);

        await RunKeepAlive(rig, keep);
        var result = await playback;

        AssertInOrder(rig.Events, "paused at", "focus 2", "states PausedForMacro Serving", "tap 2", "restore 1", "resumed at");
        Assert.DoesNotContain(rig.Events, e => e.StartsWith("VIOLATION"));
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(AccountKeepAliveState.Waiting, rig.Ledger.Get(1)!.State);
        Assert.Equal(AccountKeepAliveState.Waiting, rig.Ledger.Get(2)!.State);
    }

    [Fact]
    public async Task An_alt_playing_its_own_macro_is_never_paused_for_itself()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 28 * Min);
        var keep = Alt(2);
        rig.StampAt(keep, 20 * Min - 9 * Min + 3 * Min);   // would be due 3 min in, but its own keys credit it
        using var pbCts = new CancellationTokenSource();
        var playback = await StartPlayback(rig, new FakePlayback(rig, keep, steps: 200, stepMs: 10 * Sec, keyCredit: true),
            pbCts.Token);

        await RunKeepAlive(rig, keep);
        pbCts.Cancel();
        await playback;

        Assert.DoesNotContain(rig.Events, e => e.StartsWith("paused") || e.StartsWith("focus") || e.StartsWith("tap"));
        lock (rig.GateLog) Assert.Empty(rig.GateLog);
    }

    [Fact]
    public async Task Keep_alive_never_takes_the_foreground_mid_step()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 25 * Min);
        var bridge = Alt(1); var keep = Alt(2);
        rig.StampAt(keep, 20 * Min - 9 * Min + 5 * Sec);    // due just inside the first long step
        using var pbCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var playback = await StartPlayback(rig, new FakePlayback(rig, bridge, steps: 4, stepMs: 60 * Sec, ticksPerStep: 6),
            pbCts.Token);

        await RunKeepAlive(rig, keep);
        await playback;

        Assert.Contains("tap 2", rig.Events);
        Assert.DoesNotContain(rig.Events, e => e.StartsWith("VIOLATION"));
        AssertInOrder(rig.Events, "paused at", "focus 2", "tap 2", "resumed at");
    }

    [Fact]
    public async Task Paused_macro_window_closes_during_the_break()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 40 * Min);
        var bridge = Alt(1); var keep = Alt(2);
        rig.StampAt(keep, 20 * Min - 9 * Min + 30 * Sec);
        rig.OnFocus = pid => { if (pid == 2) rig.Closed.Add(1); };   // the macro's window dies mid-break
        using var pbCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var playback = await StartPlayback(rig, new FakePlayback(rig, bridge, steps: 20, stepMs: 10 * Sec), pbCts.Token);

        var run = RunKeepAlive(rig, keep);
        var result = await playback;
        rig.OnFocus = null;
        await run;

        Assert.Equal(PlaybackOutcome.Aborted, result.Outcome);
        Assert.False(rig.Gate.Status.Paused);
        Assert.Null(rig.Gate.Status.PlaybackTarget);
        AssertInOrder(rig.Events, "paused at", "focus 2", "tap 2", "restore 1", "resumed at", "aborted", "focus 2", "tap 2");
        lock (rig.SweepLog) Assert.Contains("keep-alive: Space to alt2 (1000 ms to front)", rig.SweepLog); // the next one: no playback, the old path
    }

    [Fact]
    public async Task Active_pass_is_paused_by_the_watchdog()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 20 * Min + 330 * Sec, activeSteps: 30, activeStepMs: 10 * Sec);
        var active = Alt(1); var keep = Alt(2);
        rig.StampAt(keep, 20 * Min - 9 * Min + 60 * Sec);   // due a minute into a 5-minute pass
        var assignments = new List<Assignment>
        {
            new(active, LongMacro, CadenceRole.Active),
            Assignment.WithDerivedRole(keep, null),
        };

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        AssertInOrder(rig.Events, "focus 1", "paused at", "focus 2", "tap 2", "restore 1", "resumed at", "pass done on 1");
        Assert.DoesNotContain(rig.Events, e => e.StartsWith("VIOLATION"));
        var paused = rig.Events.First(e => e.StartsWith("paused at"));
        Assert.True(int.Parse(paused["paused at ".Length..]) < 29, paused);   // mid-pass, not at its end
    }

    [Fact]
    public async Task A_mouse_only_macro_on_its_own_account_is_paused_only_once_critical()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 29 * Min);
        var keep = Alt(2);
        var stampedAt = 20 * Min - 8 * Min;                       // deadline in 1 min, critical (limit - 1) in 6
        rig.StampAt(keep, stampedAt);
        using var pbCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var playback = await StartPlayback(rig, new FakePlayback(rig, keep, steps: 400, stepMs: 10 * Sec), pbCts.Token);

        await RunKeepAlive(rig, keep);
        pbCts.Cancel();
        await playback;

        (int pid, long at)[] taps;
        lock (rig.Taps) taps = rig.Taps.ToArray();
        var first = Assert.Single(taps.Take(1));
        Assert.Equal(2, first.pid);
        Assert.True(first.at >= stampedAt + 14 * Min, $"Space at idle {(first.at - stampedAt) / 1000} s, before limit - 1 min");
        AssertInOrder(rig.Events, "paused at", "focus 2", "tap 2", "resumed at");
        Assert.DoesNotContain(rig.Events, e => e.StartsWith("VIOLATION"));
    }

    [Fact]
    public async Task An_account_with_no_input_history_counts_its_idle_from_tracking()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 36 * Min);
        var keep = Alt(2);                                        // no input on either clock
        using var pbCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var playback = await StartPlayback(rig, new FakePlayback(rig, keep, steps: 1000, stepMs: 10 * Sec), pbCts.Token);

        await RunKeepAlive(rig, keep);
        pbCts.Cancel();
        await playback;

        (int pid, long at)[] taps;
        lock (rig.Taps) taps = rig.Taps.ToArray();
        Assert.NotEmpty(taps);
        Assert.True(taps[0].at >= 20 * Min + 14 * Min, $"Space {(taps[0].at - 20 * Min) / 1000} s after tracking");
    }

    [Fact]
    public async Task A_service_that_throws_does_not_end_the_watchdog()
    {
        var rig = Build(startMs: 20 * Min, runForMs: 20 * Min + 330 * Sec, activeSteps: 30, activeStepMs: 10 * Sec);
        rig.SendThrows = 1;
        var active = Alt(1); var keep = Alt(2);
        rig.StampAt(keep, 20 * Min - 9 * Min + 60 * Sec);
        var assignments = new List<Assignment>
        {
            new(active, LongMacro, CadenceRole.Active),
            Assignment.WithDerivedRole(keep, null),
        };

        await rig.Runner.RunAsync(assignments, rig.Cts.Token);

        AssertInOrder(rig.Events, "send threw", "resumed at", "paused at", "focus 2", "tap 2", "resumed at", "pass done on 1");
        lock (rig.SweepLog)
            Assert.Contains("keep-alive watchdog: a service failed (send blew up); still watching", rig.SweepLog);
    }
}
