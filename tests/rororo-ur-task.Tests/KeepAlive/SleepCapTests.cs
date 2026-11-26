using Labs626.UrTask.Macros;
using static Labs626.UrTask.Tests.RunnerLedgerTests;

namespace Labs626.UrTask.Tests;

/// <summary>
/// A sleeping loop used to sleep until the earliest due time among the accounts it knew when it went to
/// sleep: up to a full deadline. An account that joined, or was switched back on, in the meantime waited
/// that long whatever its own idle time was. No single sleep is longer than five seconds now, and a wake
/// that finds nothing due says and shows nothing.
/// </summary>
public class SleepCapTests
{
    private const long Min = 60_000;
    private const long Cap = 5_000;

    [Fact]
    public void The_cap_is_five_seconds()
        => Assert.Equal(Cap, AssignmentRunner.MaxSleepMs);

    [Fact]
    public async Task An_account_that_joins_mid_sleep_already_due_is_served_within_the_cap()
    {
        var added = false;
        var rig = Build(startMs: 10 * Min, runForMs: 20 * Min, onSleep: (r, _) =>
        {
            if (added) return;
            added = true;                                                            // the loop has just gone to sleep
            r.Runner.AddAssignment(Assignment.WithDerivedRole(Alt(2), null));        // no input history: due now
        });
        rig.StampAt(Alt(1), 10 * Min);        // just served: nothing is due until 19:00

        await rig.Run(Alt(1));

        Assert.True(added);
        var first = rig.Focused.First(f => f.pid == 2);
        Assert.True(first.at <= 10 * Min + Cap, $"alt2 waited until {first.at / 1000 - 600} s after it joined");
    }

    [Fact]
    public async Task An_account_switched_back_on_mid_sleep_already_due_is_served_within_the_cap()
    {
        var switchedOn = false;
        var rig = Build(startMs: 10 * Min, runForMs: 20 * Min, onSleep: (r, _) =>
        {
            if (switchedOn) return;
            switchedOn = true;
            r.Ledger.SetOff(2, false);        // what the row's keep-alive switch does
        });
        rig.StampAt(Alt(1), 10 * Min);        // just served: nothing is due until 19:00
        rig.StampAt(Alt(2), 30_000);          // idle 9.5 min at the start, and switched off
        rig.Ledger.SetOff(2, true);

        await rig.Run(Alt(1), Alt(2));

        Assert.True(switchedOn);
        var first = rig.Focused.First(f => f.pid == 2);
        Assert.True(first.at <= 10 * Min + Cap, $"alt2 waited until {first.at / 1000 - 600} s after it was switched on");
    }

    [Fact]
    public async Task An_idle_loop_says_and_shows_nothing_across_many_capped_wakes()
    {
        var sleeps = 0;
        var rig = Build(startMs: 10 * Min, runForMs: 18 * Min, onSleep: (_, _) => sleeps++);
        rig.StampAt(Alt(1), 10 * Min);        // nothing is due for the whole eight minutes
        rig.StampAt(Alt(2), 10 * Min);
        var progress = new List<AssignmentPhase>();
        rig.Runner.Progress += (_, p) => progress.Add(p.Phase);

        await rig.Run(Alt(1), Alt(2));

        Assert.True(sleeps >= 90, $"expected a wake every five seconds for eight minutes, saw {sleeps} sleeps");
        Assert.Equal(new[] { AssignmentPhase.Started, AssignmentPhase.Stopped }, progress);
        Assert.Empty(rig.Log);
        Assert.Empty(rig.Focused);
        Assert.Equal(0, rig.Sent);
    }
}
