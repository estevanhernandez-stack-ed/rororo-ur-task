using Labs626.UrTask.Macros;
using static Labs626.UrTask.Tests.RunnerLedgerTests;

namespace Labs626.UrTask.Tests;

/// <summary>
/// An Active account's macro that throws (not a refusal, not a cancellation) used to skip the failure
/// streak, the Refused event and the user's warning, and surface only as a "keep-alive: a service failed"
/// line in the file log. It is a macro that did not play, so it takes the branch a refusal takes.
/// </summary>
public class ActiveMacroThrowTests
{
    private const long Min = 60_000;

    private static Macro Farm() => new(
        SchemaVersion: 3, Id: "farm", Name: "farm", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0,
        Events: new List<MacroEvent> { new(100, MacroEventKind.KeyDown, 0x20, 0, 0, 0, 0) });

    /// alt1's macro throws on every pass; alt2's plays. Whose pass it is = whose window was just focused.
    private static (Rig rig, List<long> played, List<AssignmentProgress> progress) ThrowingRig(long runForMs)
    {
        Rig rig = null!;
        var played = new List<long>();
        var player = new FakePlayer
        {
            OnPlay = () =>
            {
                if (rig.Target!.RobloxUserId == 1) throw new InvalidOperationException("macro blew up");
                played.Add(rig.Target.RobloxUserId);
            },
        };
        rig = Build(startMs: 10 * Min, runForMs: runForMs, player: player);
        var progress = new List<AssignmentProgress>();
        rig.Runner.Progress += (_, p) => progress.Add(p);
        return (rig, played, progress);
    }

    private static Assignment[] TwoActives() =>
    [
        new Assignment(Alt(1), Farm(), CadenceRole.Active),
        new Assignment(Alt(2), Farm(), CadenceRole.Active),
    ];

    [Fact]
    public async Task A_macro_that_throws_is_reported_as_refused_with_the_message_and_the_other_Active_still_plays()
    {
        var (rig, played, progress) = ThrowingRig(runForMs: 13 * Min);

        await rig.Runner.RunAsync(TwoActives(), rig.Cts.Token);

        Assert.True(played.Count(u => u == 2) >= 2, $"alt2 played {played.Count} time(s): starved by alt1's failing macro");
        var refused = progress.Where(p => p.Phase == AssignmentPhase.Refused).ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, p =>
        {
            Assert.Equal(1, p.Current!.Alt.RobloxUserId);
            Assert.Equal("The macro hit an error and stopped (macro blew up).", p.Reason);
        });
        // The wrong subsystem's line, in the file log only, is what this used to produce.
        Assert.DoesNotContain(rig.Log, l => l.Contains("a service failed"));
    }

    [Fact]
    public async Task A_macro_that_keeps_throwing_raises_the_usual_warning_once_and_retries_every_30s()
    {
        var (rig, _, progress) = ThrowingRig(runForMs: 14 * Min);

        await rig.Runner.RunAsync(TwoActives(), rig.Cts.Token);

        var warning = Assert.Single(progress, p => p.Phase == AssignmentPhase.Warning);   // once, at the third failure
        Assert.Equal(1, warning.Current!.Alt.RobloxUserId);
        Assert.StartsWith("alt1's macro keeps failing to complete", warning.Reason);
        Assert.Contains("macro blew up", warning.Reason);
        // Each failure costs the loop the 30 s back-off a refusal takes: never a spin on alt1's window.
        var tries = rig.Focused.Where(f => f.pid == 1).Select(f => f.at).ToList();
        Assert.InRange(tries.Count, 4, 9);
        Assert.All(tries.Zip(tries.Skip(1), (a, b) => b - a), gap => Assert.True(gap >= 30_000, $"retried after {gap} ms"));
    }

    [Fact]
    public async Task A_lone_Active_whose_macro_throws_backs_off_and_never_spins()
    {
        var (rig, _, progress) = ThrowingRig(runForMs: 12 * Min);

        await rig.Runner.RunAsync([new Assignment(Alt(1), Farm(), CadenceRole.Active)], rig.Cts.Token);

        Assert.InRange(rig.Focused.Count, 3, 5);   // about one try every 31 s for two minutes
        Assert.Equal(rig.Focused.Count, progress.Count(p => p.Phase == AssignmentPhase.Refused));
    }
}
