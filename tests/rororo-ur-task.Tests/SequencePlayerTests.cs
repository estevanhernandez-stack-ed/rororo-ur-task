using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class SequencePlayerTests
{
    private static Macro NewMacro() => new(
        SchemaVersion: 2,
        Id: Guid.NewGuid().ToString(),
        Name: "test",
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: 0,
        Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(int pid, long userId, string name)
        => new(pid, userId, name, $"acct-{pid}");

    private sealed class FakePlayer : IMacroPlayer
    {
        public Queue<PlaybackResult> Results { get; } = new();
        public List<long> CalledWithTargets { get; } = new();
        public bool IsPlaying => false;

        public event EventHandler<PlaybackStartedArgs>? Started;
        public event EventHandler<PlaybackEndedArgs>? Ended;

        public Task<PlaybackResult> PlayAsync(Macro macro, long targetUserId, CancellationToken external = default)
        {
            CalledWithTargets.Add(targetUserId);
            var r = Results.Count > 0 ? Results.Dequeue() : PlaybackResult.Completed();
            return Task.FromResult(r);
        }

        public bool Abort() => false;
    }

    private sealed class FakeForeground : IForegroundWatcher
    {
        public Func<AccountRegistry.AccountInfo?>? Resolver { get; set; }
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Resolver?.Invoke();
    }

    [Fact]
    public async Task PlayAsync_AllSucceed_ReturnsCompletedForEachAlt()
    {
        var player = new FakePlayer();
        var fg = new FakeForeground();
        // Injectable focus: always succeeds (no real window needed)
        var sequence = new SequencePlayer(player, fg, _ => (true, null));

        var targets = new[]
        {
            Alt(1001, 47821334, "Goldnail8"),
            Alt(1002, 47821335, "ScrambledTen"),
            Alt(1003, 47821336, "PinkPotatoChip"),
        };

        // FakeForeground reports foreground as whichever target was last focused.
        // Updated in the Progress handler so the foreground-flip check passes.
        long lastFocused = 0;
        fg.Resolver = () => targets.FirstOrDefault(a => a.RobloxUserId == lastFocused);

        sequence.Progress += (_, prog) =>
        {
            if (prog.Phase == SequencePhase.Focusing && prog.CurrentAlt is not null)
                lastFocused = prog.CurrentAlt.RobloxUserId;
        };

        var result = await sequence.PlayAsync(NewMacro(), targets, interAltDelayMs: 0);

        Assert.Equal(3, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(3, result.PerAlt.Count);
        Assert.All(result.PerAlt, ao => Assert.Equal(PlaybackOutcome.Completed, ao.Outcome));
        Assert.Equal(new long[] { 47821334, 47821335, 47821336 }, player.CalledWithTargets);
    }

    [Fact]
    public async Task PlayAsync_OneAltFails_SkipsAndContinues()
    {
        var player = new FakePlayer();
        player.Results.Enqueue(PlaybackResult.Completed());
        player.Results.Enqueue(PlaybackResult.Refused("ScrambledTen window closed at event 8."));
        player.Results.Enqueue(PlaybackResult.Completed());

        var fg = new FakeForeground();
        // Hint to subagent: SequencePlayer's test constructor takes the focus
        // injection arg. Mirror the happy-path test's no-op focus.
        var sequence = new SequencePlayer(player, fg, _ => (true, null));

        var targets = new[]
        {
            Alt(1001, 47821334, "Goldnail8"),
            Alt(1002, 47821335, "ScrambledTen"),
            Alt(1003, 47821336, "PinkPotatoChip"),
        };
        long lastFocused = 0;
        fg.Resolver = () => targets.FirstOrDefault(a => a.RobloxUserId == lastFocused);
        sequence.Progress += (_, prog) =>
        {
            if (prog.Phase == SequencePhase.Focusing && prog.CurrentAlt is not null)
                lastFocused = prog.CurrentAlt.RobloxUserId;
        };

        var result = await sequence.PlayAsync(NewMacro(), targets, interAltDelayMs: 0);

        Assert.Equal(2, result.Completed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(PlaybackOutcome.Completed, result.PerAlt[0].Outcome);
        Assert.Equal(PlaybackOutcome.Refused, result.PerAlt[1].Outcome);
        Assert.Equal(PlaybackOutcome.Completed, result.PerAlt[2].Outcome);
        Assert.Equal(3, player.CalledWithTargets.Count); // all three were called — fail didn't stop the loop
    }

    [Fact]
    public async Task PlayAsync_AbortMidSequence_RemainingMarkedSkipped()
    {
        var player = new FakePlayer();
        // First alt completes; abort fires before the second.
        player.Results.Enqueue(PlaybackResult.Completed());

        var fg = new FakeForeground();
        var sequence = new SequencePlayer(player, fg, _ => (true, null));

        var targets = new[]
        {
            Alt(1001, 47821334, "Goldnail8"),
            Alt(1002, 47821335, "ScrambledTen"),
            Alt(1003, 47821336, "PinkPotatoChip"),
        };
        long lastFocused = 0;
        fg.Resolver = () => targets.FirstOrDefault(a => a.RobloxUserId == lastFocused);

        sequence.Progress += (_, prog) =>
        {
            if (prog.Phase == SequencePhase.Focusing && prog.CurrentAlt is not null)
                lastFocused = prog.CurrentAlt.RobloxUserId;

            // Trigger abort just as the loop starts focusing alt 2.
            if (prog.Completed == 1 && prog.Phase == SequencePhase.Focusing && prog.CurrentAlt?.RobloxUserId == 47821335)
                sequence.Abort();
        };

        var result = await sequence.PlayAsync(NewMacro(), targets, interAltDelayMs: 0);

        Assert.Equal(1, result.Completed);
        Assert.Equal(2, result.Skipped); // alts 2 and 3 marked Skipped
        Assert.Equal(PlaybackOutcome.Completed, result.PerAlt[0].Outcome);
        Assert.Equal(PlaybackOutcome.Skipped, result.PerAlt[1].Outcome);
        Assert.Equal(PlaybackOutcome.Skipped, result.PerAlt[2].Outcome);
        Assert.Equal("Sequence aborted.", result.PerAlt[1].Reason);
    }

    [Fact]
    public async Task PlayAsync_ForegroundFlipFails_RecordsAltFailedAndContinues()
    {
        var player = new FakePlayer();
        // Two completions enqueued — for alt 1 and alt 3. Alt 2 fails before PlayAsync is called.
        player.Results.Enqueue(PlaybackResult.Completed());
        player.Results.Enqueue(PlaybackResult.Completed());

        var fg = new FakeForeground();
        var sequence = new SequencePlayer(player, fg, _ => (true, null));

        var targets = new[]
        {
            Alt(1001, 47821334, "Goldnail8"),
            Alt(99999, 47821335, "PhantomAlt"), // foreground never reports this one
            Alt(1003, 47821336, "PinkPotatoChip"),
        };

        // Foreground reports the first or third alt but never the phantom middle one.
        long lastFocused = 0;
        fg.Resolver = () =>
        {
            if (lastFocused == targets[0].RobloxUserId) return targets[0];
            if (lastFocused == targets[2].RobloxUserId) return targets[2];
            return null; // phantom alt never becomes foreground
        };
        sequence.Progress += (_, prog) =>
        {
            if (prog.Phase == SequencePhase.Focusing && prog.CurrentAlt is not null)
                lastFocused = prog.CurrentAlt.RobloxUserId;
        };

        var result = await sequence.PlayAsync(NewMacro(), targets, interAltDelayMs: 0);

        Assert.Equal(2, result.Completed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(PlaybackOutcome.Completed, result.PerAlt[0].Outcome);
        Assert.Equal(PlaybackOutcome.Refused, result.PerAlt[1].Outcome);
        Assert.Equal(PlaybackOutcome.Completed, result.PerAlt[2].Outcome);
        Assert.NotNull(result.PerAlt[1].Reason);
        // The failed alt should NOT have triggered MacroPlayer.PlayAsync — only alts 1 and 3.
        Assert.Equal(2, player.CalledWithTargets.Count);
        Assert.DoesNotContain(targets[1].RobloxUserId, player.CalledWithTargets);
    }
}
