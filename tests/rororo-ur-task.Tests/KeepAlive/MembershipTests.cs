using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>Per-account off, accounts joining the running loop, and what break-glass says while it holds.</summary>
public class MembershipTests
{
    private const long Min = 60_000;
    private static readonly DateTimeOffset WallBase = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);

    private static AccountRegistry.AccountInfo Alt(int n) => RunnerLedgerTests.Alt(n);
    private static Assignment KeepAlive(int n) => Assignment.WithDerivedRole(Alt(n), null);

    [Fact]
    public async Task An_off_account_is_never_focused_and_its_clock_runs()
    {
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 40 * Min);
        rig.StampAt(Alt(1), 10 * Min);
        rig.Ledger.SetOff(1, true);

        await rig.Run(Alt(1), Alt(2));

        Assert.DoesNotContain(rig.Focused, f => f.pid == 1);
        Assert.Contains(rig.Focused, f => f.pid == 2);
        var e = rig.Ledger.Get(1)!;
        Assert.True(e.Off);
        Assert.Equal(AccountKeepAliveState.Off, e.State);
        Assert.True(rig.NowMs - e.LastInputMs >= 30 * Min);   // nothing stamped it: its idle kept growing
        Assert.Contains(rig.Ledger.Snapshot(), x => x.UserId == 1 && x.Off);
    }

    [Fact]
    public async Task An_account_launched_mid_loop_joins_as_keep_alive()
    {
        var added = false;
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 40 * Min, onSleep: (r, _) =>
        {
            if (added || r.NowMs < 20 * Min) return;
            added = true;
            r.Runner.AddAssignment(KeepAlive(2));
        });

        await rig.Run(Alt(1));

        Assert.True(added);
        Assert.NotNull(rig.Ledger.Get(2));
        Assert.Contains(rig.Focused, f => f.pid == 2 && f.at >= 20 * Min);
    }

    [Fact]
    public async Task An_account_launched_mid_loop_stays_out_if_it_was_toggled_off_before()
    {
        var added = false;
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 40 * Min, onSleep: (r, _) =>
        {
            if (added || r.NowMs < 20 * Min) return;
            added = true;
            r.Runner.AddAssignment(KeepAlive(2));
        });
        rig.Ledger.Track(Alt(2), RunnerLedgerTests.Fifteen);
        rig.Ledger.SetOff(2, true);

        await rig.Run(Alt(1));

        Assert.True(added);
        Assert.DoesNotContain(rig.Focused, f => f.pid == 2);
        Assert.True(rig.Ledger.Get(2)!.Off);
    }

    [Fact]
    public async Task A_removed_account_stops_being_served_but_keeps_its_ledger_history()
    {
        var removed = false;
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 50 * Min, onSleep: (r, _) =>
        {
            if (removed || r.NowMs < 25 * Min) return;
            removed = true;
            r.Runner.RemoveAssignment(2, 2);
        });

        await rig.Run(Alt(1), Alt(2));

        Assert.True(removed);
        Assert.DoesNotContain(rig.Focused, f => f.pid == 2 && f.at > 26 * Min);
        Assert.Contains(rig.Focused, f => f.pid == 1 && f.at > 40 * Min);
        Assert.NotNull(rig.Ledger.Get(2));
    }

    [Fact]
    public async Task An_account_toggled_off_stays_out_after_breakglass_off_and_a_restart()
    {
        CancellationTokenSource? second = null;
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 20 * Min,
            onSleep: (r, _) => { if (second is not null && r.NowMs >= 50 * Min) second.Cancel(); });
        rig.Ledger.Track(Alt(1), RunnerLedgerTests.Fifteen);
        rig.Ledger.SetOff(1, true);

        await rig.Run(Alt(1), Alt(2));            // the first loop
        Assert.DoesNotContain(rig.Focused, f => f.pid == 1);

        // Break-glass OFF (or Ctrl+Shift+P stop/start) builds the list from every running account again.
        second = new CancellationTokenSource();
        rig.NowMs = 30 * Min;
        await rig.Runner.RunAsync(new[] { KeepAlive(1), KeepAlive(2) }, second.Token);

        Assert.DoesNotContain(rig.Focused, f => f.pid == 1);
        Assert.Contains(rig.Focused, f => f.pid == 2 && f.at >= 30 * Min);
        Assert.True(rig.Ledger.Get(1)!.Off);
    }

    [Fact]
    public void Off_resets_on_a_new_ledger()
    {
        var first = new KeepAliveLedger(() => 0, () => WallBase);
        first.Track(Alt(1), RunnerLedgerTests.Fifteen);
        first.SetOff(1, true);

        var restarted = new KeepAliveLedger(() => 0, () => WallBase);
        restarted.Track(Alt(1), RunnerLedgerTests.Fifteen);

        Assert.False(restarted.Get(1)!.Off);
    }

    [Fact]
    public void Breakglass_holds_every_account_and_says_so()
    {
        long now = 0;
        Func<DateTimeOffset> wall = () => WallBase + TimeSpan.FromMilliseconds(now);
        var ledger = new KeepAliveLedger(() => now, wall);
        var log = new List<string>();
        var hold = new BreakglassHold(ledger, new MissReporter(log.Add, wall), () => now);
        ledger.Track(Alt(1), RunnerLedgerTests.Fifteen);
        ledger.Track(Alt(2), RunnerLedgerTests.Fifteen);
        now = 1 * Min;
        ledger.StampLanded(1, InputSource.KeepAlive);
        ledger.StampLanded(2, InputSource.KeepAlive);

        hold.Begin();
        Assert.All(ledger.Snapshot(), e => Assert.Equal(AccountKeepAliveState.Held, e.State));

        now = 5 * Min; hold.Tick();                        // 4 min idle: not at the 9 min deadline yet
        Assert.Null(ledger.Get(1)!.LastMiss);
        Assert.Empty(log);

        now = 10 * Min + 1; hold.Tick();                   // 9 min 1 s idle: deadline reached
        Assert.Equal(new Miss(MissCause.HeldOff, "break-glass"), ledger.Get(1)!.LastMiss);
        Assert.Equal(1, ledger.Get(1)!.ConsecutiveMisses);
        Assert.Equal(2, log.Count(l => l.Contains("a reset or kick")));   // one line per account

        now = 12 * Min; hold.Tick();                       // same idle stretch: nothing more
        Assert.Equal(1, ledger.Get(1)!.ConsecutiveMisses);
        Assert.Equal(2, log.Count);

        hold.End();
        Assert.All(ledger.Snapshot(), e => Assert.Equal(AccountKeepAliveState.Waiting, e.State));
    }

    [Fact]
    public void Breakglass_hold_leaves_an_off_account_shown_as_off()
    {
        long now = 0;
        Func<DateTimeOffset> wall = () => WallBase + TimeSpan.FromMilliseconds(now);
        var ledger = new KeepAliveLedger(() => now, wall);
        var hold = new BreakglassHold(ledger, new MissReporter(_ => { }, wall), () => now);
        ledger.Track(Alt(1), RunnerLedgerTests.Fifteen);
        ledger.SetOff(1, true);

        hold.Begin();
        Assert.Equal(AccountKeepAliveState.Off, ledger.Get(1)!.State);
        hold.End();
        Assert.Equal(AccountKeepAliveState.Off, ledger.Get(1)!.State);
    }

    [Fact]
    public async Task A_relaunch_announced_before_the_old_exit_keeps_the_account_scheduled()
    {
        var done = false;
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 60 * Min, onSleep: (r, _) =>
        {
            if (done || r.NowMs < 25 * Min) return;
            done = true;
            var relaunched = new AccountRegistry.AccountInfo(22, 2, "alt2", "acct-2");   // same user, new pid
            r.Runner.AddAssignment(Assignment.WithDerivedRole(relaunched, null));
            r.Runner.RemoveAssignment(2, 2);                                              // the old pid exits afterwards
        });

        await rig.Run(Alt(1), Alt(2));

        Assert.True(done);
        Assert.Contains(rig.Focused, f => f.pid == 22);
        Assert.DoesNotContain(rig.Focused, f => f.pid == 2 && f.at > 26 * Min);
    }

    [Fact]
    public async Task Relaunching_an_active_account_leaves_it_active_and_farming()
    {
        var done = false;
        var plays = 0;
        var playsAtRelaunch = 0;
        var macro = new Macro(
            SchemaVersion: 3, Id: "farm", Name: "farm", RecordMode: "PerWindow", RecordedAgainstUserId: null,
            RecordedAgainstDisplayName: null, InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: new List<MacroEvent>());
        var player = new RunnerLedgerTests.FakePlayer { OnPlay = () => plays++ };
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 12 * Min, onSleep: (r, _) =>
        {
            if (done || r.NowMs < 11 * Min) return;
            done = true;
            playsAtRelaunch = plays;
            r.Runner.AddAssignment(new Assignment(new AccountRegistry.AccountInfo(22, 2, "alt2", "acct-2"), null, CadenceRole.KeepAlive));
        }, player: player);

        await rig.Runner.RunAsync(new[] { new Assignment(Alt(2), macro, CadenceRole.Active) }, rig.Cts.Token);

        Assert.True(done);
        Assert.True(playsAtRelaunch > 0);
        Assert.True(plays > playsAtRelaunch);        // still farming after the relaunch
        Assert.Null(rig.Ledger.Get(2));              // never became a keep-alive account
    }

    [Fact]
    public void A_tick_queued_before_End_does_nothing_after_it()
    {
        long now = 0;
        Func<DateTimeOffset> wall = () => WallBase + TimeSpan.FromMilliseconds(now);
        var ledger = new KeepAliveLedger(() => now, wall);
        var hold = new BreakglassHold(ledger, new MissReporter(_ => { }, wall), () => now);
        ledger.Track(Alt(1), RunnerLedgerTests.Fifteen);
        now = 1 * Min; ledger.StampLanded(1, InputSource.KeepAlive);
        hold.Begin();
        hold.End();

        now = 30 * Min; hold.Tick();

        Assert.Equal(AccountKeepAliveState.Waiting, ledger.Get(1)!.State);
        Assert.Null(ledger.Get(1)!.LastMiss);
    }
}
