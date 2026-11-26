using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;
using static Labs626.UrTask.Tests.RunnerLedgerTests;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The loop's start-up has two gaps an account can join in: between the single-flight claim and the
/// schedule being published, and while RoRoRo's activity clock is read. A join in either must be kept and
/// served, and one account whose service throws must not stop the rest from being served.
/// </summary>
public class StartupJoinTests
{
    private const long Min = 60_000;

    private static Assignment KeepAlive(int n) => Assignment.WithDerivedRole(Alt(n), null);

    [Fact]
    public async Task An_account_that_joins_while_RoRoRo_activity_is_read_is_served_with_everyone_else()
    {
        Rig rig = null!;
        rig = Build(startMs: 10 * Min, runForMs: 13 * Min, tweak: d => d with
        {
            // Exactly the gap: the schedule is published, the loop is awaiting RoRoRo's clock.
            RefreshHostActivity = _ =>
            {
                rig.Runner.AddAssignment(KeepAlive(3));
                return Task.CompletedTask;
            },
        });
        var progress = new List<AssignmentProgress>();
        rig.Runner.Progress += (_, p) => progress.Add(p);

        await rig.Run(Alt(1), Alt(2));   // must not throw

        Assert.Contains(rig.Focused, f => f.pid == 1);
        Assert.Contains(rig.Focused, f => f.pid == 2);
        Assert.Contains(rig.Focused, f => f.pid == 3);
        Assert.Equal(3, rig.Sent);                                              // one Space each, nobody twice
        Assert.DoesNotContain(rig.Log, l => l.Contains("a service failed"));
        // The late joiner keeps the list position it was given: after the two it joined.
        Assert.Contains(progress, p => p.Current?.Alt.RobloxUserId == 3 && p.IndexInCycle == 2);
    }

    [Fact]
    public async Task An_account_that_joins_before_the_schedule_is_published_is_not_lost()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 11 * Min);
        // Started is emitted after the claim and before the schedule exists: the other gap.
        rig.Runner.Progress += (_, p) =>
        {
            if (p.Phase == AssignmentPhase.Started) rig.Runner.AddAssignment(KeepAlive(3));
        };

        await rig.Run(Alt(1), Alt(2));

        Assert.Contains("keep-alive: alt3 joined the loop", rig.Log);
        Assert.Contains(rig.Focused, f => f.pid == 3);
        Assert.Equal(3, rig.Sent);
    }

    [Fact]
    public async Task A_join_that_arrives_when_no_loop_runs_is_dropped_not_kept_for_the_next_run()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 11 * Min);
        rig.Runner.AddAssignment(KeepAlive(3));   // no loop: AddAssignment does nothing

        await rig.Run(Alt(1), Alt(2));

        Assert.DoesNotContain(rig.Focused, f => f.pid == 3);
        Assert.Equal(2, rig.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_account_whose_service_throws_does_not_stop_the_others_being_served(bool turbo)
    {
        var rig = Build(startMs: 10 * Min, runForMs: 12 * Min,
            focus: pid => pid == 1 ? throw new InvalidOperationException("boom") : new FocusAttempt(true, null, null));
        rig.Runner.TurboKeepAlive = turbo;

        await rig.Run(Alt(1), Alt(2), Alt(3));   // alt1 is the most overdue every time Decide looks

        Assert.Contains(rig.Focused, f => f.pid == 2);
        Assert.Contains(rig.Focused, f => f.pid == 3);
        Assert.Equal(2, rig.Sent);
        // The failing account takes the 30 s back-off on its own due time: retried, never spun.
        var tries = rig.Focused.Where(f => f.pid == 1).Select(f => f.at).ToList();
        Assert.InRange(tries.Count, 3, 6);
        // (A turbo sweep that is going out anyway takes in every account due within two minutes, the
        // backed-off one included, exactly as it does after an ordinary miss: no gap to assert there.)
        if (!turbo)
            Assert.All(tries.Zip(tries.Skip(1), (a, b) => b - a), gap => Assert.True(gap >= 30_000, $"retried after {gap} ms"));
        Assert.Contains("keep-alive: a service failed (boom); retry in 30s", rig.Log);
    }

    private static Macro Farm() => new(
        SchemaVersion: 3, Id: "farm", Name: "farm", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0,
        Events: new List<MacroEvent> { new(100, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0) });

    [Fact]
    public async Task An_Active_account_whose_pass_throws_gives_up_its_turn_to_the_next_Active()
    {
        Rig rig = null!;
        var plays = new List<long>();   // whose window was in front when the player was called
        var player = new FakePlayer { OnPlay = () => plays.Add(rig.Target!.RobloxUserId) };
        rig = Build(startMs: 10 * Min, runForMs: 12 * Min, player: player,
            focus: pid => pid == 1 ? throw new InvalidOperationException("boom") : new FocusAttempt(true, null, null));

        await rig.Runner.RunAsync(
            [new Assignment(Alt(1), Farm(), CadenceRole.Active), new Assignment(Alt(2), Farm(), CadenceRole.Active)],
            rig.Cts.Token);

        Assert.Contains(2L, plays);
        Assert.DoesNotContain(1L, plays);
        // The pass that throws still costs the loop its 30 s back-off each lap: no spin.
        Assert.InRange(rig.Focused.Count(f => f.pid == 1), 2, 6);
    }
}
