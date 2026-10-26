using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;
using static Labs626.UrTask.Tests.RunnerLedgerTests;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The whole-branch review's fixes for 0.13.0: a refused focus is soft on the macro paths too, refused
/// misses name the holder (and the turbo sweep collapses them), the first loop start reads RoRoRo's clock
/// before anyone is due, Active accounts are tracked, and a service that throws never ends the loop.
/// </summary>
public class FinalFixTests
{
    private const long Min = 60_000;

    private static Macro Farm() => new(
        SchemaVersion: 2, Id: "farm", Name: "farm", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0,
        Events: new List<MacroEvent> { new(100, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0) });

    [Fact]
    public async Task An_Active_pass_with_a_refused_focus_still_verifies_and_plays_when_the_window_came_forward()
    {
        var plays = 0;
        var player = new FakePlayer { OnPlay = () => plays++ };
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 5_000, player: player,
            focus: _ => new FocusAttempt(false, MissCause.FocusRefused, "refused"));

        await rig.Runner.RunAsync([new Assignment(Alt(1), Farm(), CadenceRole.Active)], rig.Cts.Token);

        Assert.True(plays >= 1, $"expected the pass to play, played {plays}");
    }

    [Fact]
    public async Task An_Active_pass_with_a_gone_window_still_backs_off_without_playing()
    {
        var plays = 0;
        var player = new FakePlayer { OnPlay = () => plays++ };
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 5_000, player: player,
            focus: _ => new FocusAttempt(false, MissCause.WindowGone, "no window"));

        await rig.Runner.RunAsync([new Assignment(Alt(1), Farm(), CadenceRole.Active)], rig.Cts.Token);

        Assert.Equal(0, plays);
        Assert.Single(rig.Focused);   // one attempt, then the 30 s backoff outlasts the run
    }

    [Fact]
    public async Task A_turbo_sweep_refused_by_one_holder_keeps_its_old_lines_and_adds_one_collapse_line()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 10_000, inFront: _ => null,
            holder: "'Notepad' (notepad)",
            focus: _ => new FocusAttempt(false, MissCause.FocusRefused, "refused"));
        rig.Runner.TurboKeepAlive = true;

        await rig.Run(Enumerable.Range(1, 5).Select(Alt).ToArray());

        for (var i = 1; i <= 5; i++)
            Assert.Single(rig.Log, l => l == $"turbo keep-alive: skipped alt{i}, focus failed; retry in 30s");
        var collapse = Assert.Single(rig.Log, l => l.Contains("every due account missed"));
        Assert.Equal("keep-alive: every due account missed: Windows refused focus while 'Notepad' (notepad) was in front", collapse);
        Assert.DoesNotContain(rig.Log, l => l.StartsWith("keep-alive: missed alt"));
    }

    [Fact]
    public async Task The_first_loop_start_reads_RoRoRo_activity_before_anyone_is_due()
    {
        Rig rig = null!;
        rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 30_000, tweak: d => d with
        {
            RefreshHostActivity = _ =>
            {
                // RoRoRo saw input on alt1 three minutes ago; alt2 has no history anywhere.
                rig.Ledger.MergeHostActivity("acct-1", rig.Ledger.ToWall(rig.NowMs) - TimeSpan.FromMinutes(3));
                return Task.CompletedTask;
            },
        });

        await rig.Run(Alt(1), Alt(2));

        Assert.Equal((2, 10 * Min), rig.Focused[0]);
        Assert.DoesNotContain(rig.Focused, f => f.pid == 1);   // due at its deadline, 16:00
        Assert.Equal(16 * Min, rig.Ledger.DueAtMs(1));
    }

    [Fact]
    public async Task A_host_refresh_that_hangs_is_bounded_and_the_loop_still_starts()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 3_000, tweak: d => d with
        {
            RefreshHostActivity = _ => Task.Delay(Timeout.Infinite, CancellationToken.None),
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await rig.Run(Alt(1));

        Assert.Equal(1, rig.Sent);
        Assert.InRange(clock.ElapsedMilliseconds, 0, 10_000);
    }

    [Fact]
    public async Task GetStatus_lists_an_Active_account_as_active_and_keep_alive_never_serves_it()
    {
        StatusProvider status = null!;
        GetStatusResponse? seen = null;
        var player = new FakePlayer { OnPlay = () => seen ??= status.GetStatus() };
        var rig = Build(startMs: 10 * Min, runForMs: 30 * Min, player: player);
        rig.Runner.TurboKeepAlive = true;
        status = new StatusProvider(rig.Ledger, new ForegroundGate(() => rig.NowMs, _ => { }), new PlaybackRegistry(),
            () => false, () => rig.Runner.IsRunning, () => rig.Runner.TurboKeepAlive, () => rig.Runner.ActiveUserIds);

        await rig.Runner.RunAsync(
            [new Assignment(Alt(1), Farm(), CadenceRole.Active), Assignment.WithDerivedRole(Alt(2), null)], rig.Cts.Token);

        var active = Assert.Single(seen!.Accounts!, a => a.UserId == "1");
        Assert.Equal("active", active.Role);
        Assert.Equal(15, active.LimitMinutes);
        Assert.Contains(rig.Log, l => l.Contains("Space to alt2"));
        Assert.DoesNotContain(rig.Log, l => l.Contains("Space to alt1"));
    }

    [Fact]
    public async Task A_service_that_throws_is_logged_and_the_loop_retries_in_30s()
    {
        var calls = 0;
        var rig = Build(startMs: 10 * Min, runForMs: 10 * Min + 40_000, focus: _ =>
        {
            if (calls++ == 0) throw new InvalidOperationException("boom");
            return new FocusAttempt(true, null, null);
        });

        await rig.Run(Alt(1));   // must not throw

        Assert.Contains("keep-alive: a service failed (boom); retry in 30s", rig.Log);
        Assert.Equal(1, rig.Sent);
        Assert.Equal(10 * Min + 30_000, rig.Focused[1].at);
    }

    [Fact]
    public async Task A_clamped_place_override_is_logged_once_per_account()
    {
        var clamped = new KeepAliveLimit(TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(2), true);
        var rig = Build(startMs: 10 * Min, runForMs: 20 * Min, tweak: d => d with { KeepAliveLimitFor = _ => clamped });

        await rig.Run(Alt(1));

        Assert.Single(rig.Log, l => l == "keep-alive: alt1's place override was under 2 min; using 2 min");
        Assert.True(rig.Sent >= 3);   // served several times, said once
    }
    [Fact]
    public async Task A_setup_exception_still_emits_Stopped()
    {
        var rig = Build(startMs: 10 * Min, runForMs: 11 * Min,
            tweak: d => d with { KeepAliveLimitFor = _ => throw new InvalidOperationException("bad prefs") });
        var phases = new List<AssignmentPhase>();
        rig.Runner.Progress += (_, p) => phases.Add(p.Phase);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Run(Alt(1)));

        Assert.Contains(AssignmentPhase.Stopped, phases);
        Assert.False(rig.Runner.IsRunning);
    }
}
