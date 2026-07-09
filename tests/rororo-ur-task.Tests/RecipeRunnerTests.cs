using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace RoRoRo.UrTask.Tests;

public class RecipeRunnerTests
{
    private static AccountRegistry.AccountInfo Alt(int pid, long uid, string name)
        => new(pid, uid, name, Guid.NewGuid().ToString());

    private static Macro MacroWithId(string id)
        => new(Macro.CurrentSchemaVersion, id, id, null, null, null, null, 0, Array.Empty<MacroEvent>());

    // runOnce fake: report every alt Completed unless its uid is in `failUids`.
    private static RecipeRunner.RunOnceDelegate FakeRunOnce(
        List<string> log, HashSet<long>? failUids = null)
        => (macro, alts, ct) =>
        {
            log.Add($"runOnce:{macro.Id}:[{string.Join(",", alts.Select(a => a.RobloxUserId))}]");
            var per = alts.Select(a => new AltOutcome(a,
                failUids != null && failUids.Contains(a.RobloxUserId)
                    ? PlaybackOutcome.Refused : PlaybackOutcome.Completed, null)).ToList();
            int done = per.Count(p => p.Outcome == PlaybackOutcome.Completed);
            return Task.FromResult(new SequenceResult(per, done, per.Count - done, 0, TimeSpan.Zero));
        };

    private static RecipeRunner.RunLoopDelegate FakeRunLoop(List<string> log)
        => (assignments, ct) =>
        {
            log.Add($"loop:[{string.Join(",", assignments.Select(a => $"{a.Alt.RobloxUserId}:{a.Macro?.Id ?? "keepalive"}"))}]");
            return Task.CompletedTask;
        };

    [Fact]
    public async Task Position_Barriers_Then_Loop_Starts()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a"), Alt(2, 20, "b") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Equal(2, log.Count);
        Assert.Equal("runOnce:pos:[10,20]", log[0]);           // position first, all alts
        Assert.Equal("loop:[10:loop,20:loop]", log[1]);        // then loop, all alts, loop macro
    }

    [Fact]
    public async Task TerminalKeepAlive_PassesNullMacro()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep(null, StepIteration.KeepAlive) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Equal("loop:[10:keepalive]", log[1]);
    }

    [Fact]
    public async Task PositionFailure_ProceedsWithSuccessesOnly()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a"), Alt(2, 20, "b") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log, failUids: new HashSet<long> { 20 }), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Equal("loop:[10:loop]", log[1]);  // alt 20 dropped, squad not blocked
    }

    [Fact]
    public async Task AllPositionsFail_LoopNeverStarts()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        var runner = new RecipeRunner(FakeRunOnce(log, failUids: new HashSet<long> { 10 }), FakeRunLoop(log), MacroWithId);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.Single(log);                      // only the runOnce; no loop
        Assert.StartsWith("runOnce", log[0]);
    }

    [Fact]
    public async Task UnresolvedPositionMacro_EmitsMacroMissing_AndLoopNeverStarts()
    {
        var log = new List<string>();
        var phases = new List<RecipeRunPhase>();
        var alts = new[] { Alt(1, 10, "a") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("missing", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        Macro? Resolve(string id) => id == "missing" ? null : MacroWithId(id);

        var runner = new RecipeRunner(FakeRunOnce(log), FakeRunLoop(log), Resolve);
        runner.Progress += (_, e) => phases.Add(e.Phase);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.DoesNotContain(log, l => l.StartsWith("loop:"));
        Assert.Contains(RecipeRunPhase.MacroMissing, phases);
    }

    [Fact]
    public async Task UnresolvedTerminalLoopMacro_EmitsMacroMissing_AndLoopNeverStarts()
    {
        var log = new List<string>();
        var phases = new List<RecipeRunPhase>();
        var alts = new[] { Alt(1, 10, "a") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("missing", StepIteration.Loop) }, 0);

        Macro? Resolve(string id) => id == "missing" ? null : MacroWithId(id);

        var runner = new RecipeRunner(FakeRunOnce(log), FakeRunLoop(log), Resolve);
        runner.Progress += (_, e) => phases.Add(e.Phase);
        await runner.RunAsync(recipe, alts, CancellationToken.None);

        Assert.DoesNotContain(log, l => l.StartsWith("loop:"));
        Assert.Contains(RecipeRunPhase.MacroMissing, phases);
    }

    // Covers Fix 1 (host-loss safety): OnHostLost calls _activeRecipeRunner.Abort(),
    // which cancels the recipe's OWN token (not a sub-runner's internal token). This
    // proves that once that token is cancelled — even mid-position-step, before the
    // terminal step is reached — RunAsync's pre-terminal ct.ThrowIfCancellationRequested()
    // fires and the terminal loop never starts. Without this, a stale RecipeRunner
    // would start a fresh terminal input-loop against orphaned PIDs after host loss.
    [Fact]
    public async Task TokenCancelledDuringPosition_TerminalLoopNeverStarts()
    {
        var log = new List<string>();
        var alts = new[] { Alt(1, 10, "a") };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r",
            new[] { new RecipeStep("pos", StepIteration.RunOnce), new RecipeStep("loop", StepIteration.Loop) }, 0);

        using var cts = new CancellationTokenSource();

        // Fake runOnce that cancels the CTS (simulating OnHostLost calling
        // _activeRecipeRunner.Abort() while positioning is in flight) and then
        // returns a normal all-Completed result, same as a real position step that
        // finishes just as the abort lands.
        RecipeRunner.RunOnceDelegate cancellingRunOnce = (macro, altsArg, ct) =>
        {
            log.Add($"runOnce:{macro.Id}:[{string.Join(",", altsArg.Select(a => a.RobloxUserId))}]");
            cts.Cancel();
            var per = altsArg.Select(a => new AltOutcome(a, PlaybackOutcome.Completed, null)).ToList();
            return Task.FromResult(new SequenceResult(per, per.Count, 0, 0, TimeSpan.Zero));
        };

        var runner = new RecipeRunner(cancellingRunOnce, FakeRunLoop(log), MacroWithId);

        // RecipeRunner swallows OperationCanceledException internally — this must
        // complete without throwing to the caller.
        await runner.RunAsync(recipe, alts, cts.Token);

        Assert.DoesNotContain(log, l => l.StartsWith("loop:"));
    }
}
