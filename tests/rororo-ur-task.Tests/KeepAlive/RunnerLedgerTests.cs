using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The runner schedules from the ledger and explains itself: real idle decides who is due,
/// every Space and every miss is logged with its cause. Fake clock that jumps on Sleep.
/// </summary>
public class RunnerLedgerTests
{
    private const long Min = 60_000;
    private static readonly KeepAliveLimit Fifteen = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), false);
    private static readonly DateTimeOffset WallBase = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);

    private static AccountRegistry.AccountInfo Alt(int n) => new(n, n, $"alt{n}", $"acct-{n}");

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

    private sealed class Fg(Func<AccountRegistry.AccountInfo?> resolve) : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => resolve();
    }

    private sealed class Rig
    {
        public required AssignmentRunner Runner;
        public required KeepAliveLedger Ledger;
        public required CancellationTokenSource Cts;
        public long NowMs;
        public int Sent;
        public List<(int pid, long at)> Focused { get; } = new();
        public List<string> Log { get; } = new();
        public AccountRegistry.AccountInfo? Target;
        public Task Run(params AccountRegistry.AccountInfo[] alts)
            => Runner.RunAsync(alts.Select(a => Assignment.WithDerivedRole(a, null)).ToList(), Cts.Token);

        /// An account last got input at the given mono time; the clock is put back to where it was.
        public void StampAt(AccountRegistry.AccountInfo a, long atMs)
        {
            var back = NowMs;
            Ledger.Track(a, Fifteen);
            NowMs = atMs;
            Ledger.StampLanded(a.RobloxUserId, InputSource.KeepAlive);
            NowMs = back;
        }
    }

    /// <param name="inFront">who reads as the foreground; default is whoever was just focused.</param>
    /// <param name="focus">the Focus seam; default is a clean success that brings the window forward.</param>
    private static Rig Build(long startMs, long runForMs, Func<Rig, AccountRegistry.AccountInfo?>? inFront = null,
        string holder = "unknown", bool sendOk = true, Func<int, FocusAttempt>? focus = null,
        Action<Rig, long>? onSleep = null, Action<string>? markActive = null)
    {
        Rig rig = null!;
        var cts = new CancellationTokenSource();
        var calls = 0;
        var ledger = new KeepAliveLedger(() => rig.NowMs, () => WallBase + TimeSpan.FromMilliseconds(rig.NowMs));
        var deps = new CadenceDeps(
            Focus: pid =>
            {
                rig.Focused.Add((pid, rig.NowMs));
                rig.Target = Enumerable.Range(1, 20).Select(Alt).First(a => a.Pid == pid);
                return focus?.Invoke(pid) ?? new FocusAttempt(true, null, null);
            },
            ClockMs: () => { if (++calls > 200_000) cts.Cancel(); return rig.NowMs; },
            Sleep: (ms, ct) =>
            {
                if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
                onSleep?.Invoke(rig, ms);
                if (ms > 0) rig.NowMs += ms;
                if (rig.NowMs >= runForMs) cts.Cancel();
                return Task.CompletedTask;
            },
            CaptureForeground: () => IntPtr.Zero,
            RestoreForeground: _ => { },
            SendKeepAlive: () => { rig.Sent++; return sendOk; },
            KeepAliveIntervalMs: _ => (long)Fifteen.Deadline.TotalMilliseconds)
        {
            SweepLog = line => rig.Log.Add(line),
            DescribeForeground = () => holder,
            KeepAliveLimitFor = _ => Fifteen,
            MarkActive = markActive ?? (_ => { }),
        };
        rig = new Rig
        {
            Cts = cts, Ledger = ledger, NowMs = startMs,
            Runner = new AssignmentRunner(new FakePlayer(), new Fg(() => inFront is null ? rig.Target : inFront(rig)), deps, ledger),
        };
        return rig;
    }

    [Fact]
    public async Task Start_does_not_make_everyone_due_now()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 12 * Min);
        var a = Alt(1); var b = Alt(2);
        rig.StampAt(a, 2 * Min);        // A last got input 8 minutes before the start

        await rig.Run(a, b);

        Assert.Equal((2, 10 * Min), rig.Focused[0]);        // never touched: due now
        Assert.Equal((1, 11 * Min), rig.Focused[1]);        // deadline 9 min after its Space
    }

    [Fact]
    public async Task Unknown_accounts_start_two_seconds_apart()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 8_000);
        await rig.Run(Alt(1), Alt(2), Alt(3));

        Assert.Equal(new[] { 10 * Min, 10 * Min + 2_000, 10 * Min + 4_000 }, rig.Focused.Take(3).Select(f => f.at));
        Assert.Equal(new[] { 1, 2, 3 }, rig.Focused.Take(3).Select(f => f.pid));
    }

    [Fact]
    public async Task A_landed_space_marks_the_account_active()
    {
        var marked = new List<string>();
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000, markActive: marked.Add);
        await rig.Run(Alt(1));
        Assert.Equal(new[] { "acct-1" }, marked);
    }

    [Fact]
    public async Task A_missed_space_does_not_mark_the_account_active()
    {
        var marked = new List<string>();
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000, sendOk: false, markActive: marked.Add);
        await rig.Run(Alt(1));
        Assert.Empty(marked);
    }

    [Fact]
    public async Task A_throwing_mark_never_breaks_the_runner()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000, markActive: _ => throw new InvalidOperationException());
        await rig.Run(Alt(1));
        Assert.Equal(1, rig.Sent);
    }

    [Fact]
    public async Task A_landed_space_stamps_the_ledger_and_logs_a_line()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000);
        await rig.Run(Alt(1));

        var e = rig.Ledger.Get(1)!;
        Assert.Equal(InputSource.KeepAlive, e.LastSource);
        Assert.Equal(10 * Min + 1_000, e.LastInputMs);
        Assert.Contains("keep-alive: Space to alt1 (1000 ms to front)", rig.Log);
    }

    [Fact]
    public async Task A_rejected_space_does_not_stamp()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000, sendOk: false);
        await rig.Run(Alt(1));

        Assert.Null(rig.Ledger.Get(1)!.LastInputMs);
        Assert.Equal(MissCause.SendRejected, rig.Ledger.Get(1)!.LastMiss!.Cause);
    }

    [Fact]
    public async Task A_verify_miss_names_whats_in_front()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 5_000, inFront: _ => null,
            holder: "'Visual Studio Code' (Code)");
        await rig.Run(Alt(1));

        Assert.Contains("keep-alive: missed alt1, 'Visual Studio Code' (Code) was in front; retry in 30s", rig.Log);
        Assert.Equal(MissCause.OtherInFront, rig.Ledger.Get(1)!.LastMiss!.Cause);
        Assert.Equal(1, rig.Ledger.Get(1)!.ConsecutiveMisses);
    }

    [Fact]
    public async Task Seven_alts_missing_for_one_cause_log_one_line()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 20_000, inFront: _ => null,
            holder: "'Notepad' (notepad)");
        await rig.Run(Enumerable.Range(1, 7).Select(Alt).ToArray());

        var line = Assert.Single(rig.Log, l => l.Contains("every due account missed"));
        Assert.Equal("keep-alive: every due account missed: 'Notepad' (notepad) was in front", line);
        Assert.DoesNotContain(rig.Log, l => l.StartsWith("keep-alive: missed alt"));
        Assert.Equal(7, Enumerable.Range(1, 7).Sum(i => rig.Ledger.Get(i)!.ConsecutiveMisses));
    }

    [Fact]
    public async Task Recovery_line_after_misses()
    {
        var a = Alt(1);
        var rig = Build(startMs: 10 * Min, runForMs: 14 * Min,
            inFront: r => r.NowMs >= 12 * Min + 2_000 ? r.Target : null);
        rig.StampAt(a, 2 * Min);

        await rig.Run(a);

        Assert.Contains("keep-alive: alt1 landed again after 2 misses (10 min since its last Space)", rig.Log);
        Assert.Equal(0, rig.Ledger.Get(1)!.ConsecutiveMisses);
    }

    [Fact]
    public async Task Expect_reset_line_when_retry_cannot_land_before_the_limit()
    {
        var a = Alt(1);
        var rig = Build(startMs: 16 * Min + 40_000, runForMs: 18 * Min, inFront: _ => null);
        rig.StampAt(a, 2 * Min);        // limit 15 min: the game resets at 17:00

        await rig.Run(a);

        var line = Assert.Single(rig.Log, l => l.Contains("expect a reset or kick"));
        Assert.Equal("keep-alive: alt1 has had no input for 14 min; expect a reset or kick around 14:17", line);
    }

    [Fact]
    public async Task A_refused_focus_still_verifies_and_sends_when_the_window_came_forward()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000,
            focus: _ => new FocusAttempt(false, MissCause.FocusRefused, "refused"));
        await rig.Run(Alt(1));

        Assert.Equal(1, rig.Sent);
        Assert.Equal(InputSource.KeepAlive, rig.Ledger.Get(1)!.LastSource);
    }

    [Fact]
    public async Task A_refused_focus_that_does_not_verify_is_a_FocusRefused_miss()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000, inFront: _ => null,
            focus: _ => new FocusAttempt(false, MissCause.FocusRefused, "refused"));
        await rig.Run(Alt(1));

        Assert.Equal(0, rig.Sent);
        Assert.Equal(MissCause.FocusRefused, rig.Ledger.Get(1)!.LastMiss!.Cause);
        Assert.Contains("keep-alive: missed alt1, Windows refused focus; retry in 30s", rig.Log);
    }

    [Fact]
    public async Task A_gone_window_is_a_hard_miss_with_no_verify()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000,
            focus: _ => new FocusAttempt(false, MissCause.WindowGone, "no window"));
        await rig.Run(Alt(1));

        Assert.Equal(0, rig.Sent);
        Assert.Equal(MissCause.WindowGone, rig.Ledger.Get(1)!.LastMiss!.Cause);
        Assert.Contains("keep-alive: missed alt1, window gone; retry in 30s", rig.Log);
    }

    [Fact]
    public async Task Turbo_treats_a_refused_focus_as_soft_and_keeps_its_old_lines()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 6_000,
            focus: _ => new FocusAttempt(false, MissCause.FocusRefused, "refused"));
        rig.Runner.TurboKeepAlive = true;
        await rig.Run(Alt(1));

        Assert.Equal(1, rig.Sent);
        Assert.Contains(rig.Log, l => l.StartsWith("turbo keep-alive: Space to alt1 ("));
        Assert.Contains("turbo keep-alive: sweep done, 1 of 1 Space sent", rig.Log);
    }

    [Fact]
    public async Task Turbo_refused_focus_that_never_comes_forward_is_a_FocusRefused_miss_in_the_old_words()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 6_000, inFront: _ => null,
            focus: _ => new FocusAttempt(false, MissCause.FocusRefused, "refused"));
        rig.Runner.TurboKeepAlive = true;
        await rig.Run(Alt(1));

        Assert.Equal(0, rig.Sent);
        Assert.Equal(MissCause.FocusRefused, rig.Ledger.Get(1)!.LastMiss!.Cause);
        Assert.Contains("turbo keep-alive: skipped alt1, focus failed; retry in 30s", rig.Log);
    }

    [Fact]
    public async Task Input_stamped_on_the_ledger_from_outside_defers_the_keep_alive()
    {
        var a = Alt(1);
        var fired = false;
        var rig = Build(startMs: 10 * Min, runForMs: 20 * Min, onSleep: (r, ms) =>
        {
            if (fired || ms < Min) return;
            fired = true;
            r.Ledger.StampLanded(1, InputSource.Macro);   // e.g. a macro's keys, at 10:00
        });
        rig.StampAt(a, 2 * Min);                            // would be due at 11:00

        await rig.Run(a);

        Assert.DoesNotContain(rig.Focused, f => f.at < 19 * Min);
        Assert.Contains(rig.Focused, f => f.at == 19 * Min);
    }
}
