using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>Owner ruling 1 (0.13.1): while a recording pauses keep-alive, warn before an account is lost.</summary>
public class RecordingHoldTests
{
    private const long Min = 60_000;
    // 20:00 local on the rig's clock, so the reset times read like the ruling's example.
    private static readonly DateTimeOffset WallBase = new(2026, 11, 25, 20, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public long Now;
        public readonly List<string> Log = new();
        public readonly HashSet<long> Open = new() { 1, 2, 3 };
        public readonly KeepAliveLedger Ledger;
        public readonly RecordingHold Hold;

        public Rig()
        {
            Func<DateTimeOffset> wall = () => WallBase + TimeSpan.FromMilliseconds(Now);
            Ledger = new KeepAliveLedger(() => Now, wall);
            Hold = new RecordingHold(Ledger, Log.Add, () => Now, id => Open.Contains(id));
            for (var n = 1; n <= 3; n++) Ledger.Track(RunnerLedgerTests.Alt(n), RunnerLedgerTests.Fifteen);
        }

        public void Stamp(long userId, long atMs)
        {
            var was = Now;
            Now = atMs;
            Ledger.StampLanded(userId, InputSource.KeepAlive);
            Now = was;
        }
    }

    [Fact]
    public void Warns_once_when_a_paused_account_reaches_its_deadline_naming_it_and_the_time()
    {
        // Fifteen: the game resets at 15 min idle, keep-alive would have acted by 9.
        var rig = new Rig();
        rig.Stamp(1, 2 * Min);
        rig.Now = 3 * Min;
        rig.Hold.Begin([1]);

        rig.Now = 10 * Min; rig.Hold.Tick();          // 8 min idle: not yet
        Assert.Empty(rig.Log);

        rig.Now = 11 * Min + 1; rig.Hold.Tick();      // 9 min idle: say so
        Assert.Equal(
            "alt1 has had no input for 9 min while you record. The game may reset it around 20:17. Stop recording to let keep-alive run.",
            Assert.Single(rig.Log));

        rig.Now = 13 * Min; rig.Hold.Tick();          // same idle stretch: said once
        Assert.Single(rig.Log);
    }

    [Fact]
    public void Says_nothing_about_accounts_the_paused_loop_was_not_serving()
    {
        // The break-glass hold warns about every account the ledger has ever seen. This one must not.
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Stamp(2, 0);
        rig.Stamp(3, 0);
        rig.Hold.Begin([1]);

        rig.Now = 14 * Min; rig.Hold.Tick();

        Assert.Contains("alt1", Assert.Single(rig.Log));
    }

    [Fact]
    public void Writes_nothing_to_the_ledger()
    {
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Hold.Begin([1]);

        rig.Now = 14 * Min; rig.Hold.Tick();
        rig.Hold.End();

        var e = rig.Ledger.Get(1)!;
        Assert.Equal(AccountKeepAliveState.Waiting, e.State);
        Assert.Equal(0, e.ConsecutiveMisses);
        Assert.Null(e.LastMiss);
    }

    [Fact]
    public void Skips_an_account_that_is_switched_off()
    {
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Ledger.SetOff(1, true);
        rig.Hold.Begin([1]);

        rig.Now = 14 * Min; rig.Hold.Tick();

        Assert.Empty(rig.Log);
    }

    [Fact]
    public void Skips_an_account_that_closed_during_the_recording()
    {
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Hold.Begin([1]);
        rig.Open.Remove(1);

        rig.Now = 14 * Min; rig.Hold.Tick();

        Assert.Empty(rig.Log);
    }

    [Fact]
    public void New_input_re_arms_the_warning_for_the_next_idle_stretch()
    {
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Hold.Begin([1]);
        rig.Now = 9 * Min; rig.Hold.Tick();
        Assert.Single(rig.Log);

        rig.Stamp(1, 10 * Min);                       // the user played it, RoRoRo's clock said so
        rig.Now = 12 * Min; rig.Hold.Tick();
        Assert.Single(rig.Log);

        rig.Now = 19 * Min; rig.Hold.Tick();          // 9 min idle again
        Assert.Equal(2, rig.Log.Count);
        Assert.Contains("around 20:25", rig.Log[1]);
    }

    [Fact]
    public void An_account_with_no_input_history_counts_its_idle_from_the_pause()
    {
        var rig = new Rig();
        rig.Now = 5 * Min;
        rig.Hold.Begin([2]);

        rig.Now = 13 * Min; rig.Hold.Tick();
        Assert.Empty(rig.Log);

        rig.Now = 14 * Min; rig.Hold.Tick();
        Assert.Equal(
            "alt2 has had no input for 9 min while you record. The game may reset it around 20:20. Stop recording to let keep-alive run.",
            Assert.Single(rig.Log));
    }

    [Fact]
    public void An_account_already_past_its_limit_is_told_in_the_past_tense()
    {
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Now = 16 * Min;
        rig.Hold.Begin([1]);

        Assert.Equal(
            "alt1 has had no input for 16 min while you record. The game may have reset it around 20:15. Stop recording to let keep-alive run.",
            Assert.Single(rig.Log));
    }

    [Fact]
    public void Says_nothing_before_it_begins_or_after_it_ends()
    {
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Now = 14 * Min;

        rig.Hold.Tick();
        Assert.Empty(rig.Log);

        rig.Hold.Begin([]);
        rig.Hold.Tick();
        Assert.Empty(rig.Log);

        rig.Hold.Begin([1]);
        rig.Hold.End();
        rig.Log.Clear();
        rig.Now = 20 * Min; rig.Hold.Tick();          // a tick already queued on the timer
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void A_later_recording_warns_again_about_the_same_idle_stretch()
    {
        var rig = new Rig();
        rig.Stamp(1, 0);
        rig.Hold.Begin([1]);
        rig.Now = 9 * Min; rig.Hold.Tick();
        rig.Hold.End();

        rig.Hold.Begin([1]);

        Assert.Equal(2, rig.Log.Count);
    }
}

/// <summary>What the recording pause asks the runner: which accounts was the loop serving as keep-alive accounts.</summary>
public class RunnerKeepAliveUserIdsTests
{
    private const long Min = 60_000;

    [Fact]
    public async Task Names_the_keep_alive_accounts_of_the_running_loop_and_nothing_once_it_stops()
    {
        IReadOnlySet<long>? during = null;
        var rig = RunnerLedgerTests.Build(startMs: 10 * Min, runForMs: 12 * Min,
            onSleep: (r, _) => during ??= r.Runner.KeepAliveUserIds);
        var macro = new Macro(Macro.CurrentSchemaVersion, "m", "farm", null, null, null, null, 0,
            new[] { new MacroEvent(0, MacroEventKind.KeyDown, 0x45, 0, 0, 0, 0) });

        await rig.Runner.RunAsync(
        [
            Assignment.WithDerivedRole(RunnerLedgerTests.Alt(1), null),
            Assignment.WithDerivedRole(RunnerLedgerTests.Alt(2), null),
            Assignment.WithDerivedRole(RunnerLedgerTests.Alt(3), macro),   // Active: it plays a macro
        ], rig.Cts.Token);

        Assert.NotNull(during);
        Assert.Equal(new HashSet<long> { 1, 2 }, during);
        Assert.Empty(rig.Runner.KeepAliveUserIds);
    }
}
