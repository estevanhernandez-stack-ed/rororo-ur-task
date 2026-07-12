using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class RecipeTests
{
    private static RecipeStep Pos(string id) => new(id, StepIteration.RunOnce);

    [Fact]
    public void ValidateSteps_TerminalLoop_WithMacro_IsValid()
    {
        var steps = new[] { Pos("a"), new RecipeStep("b", StepIteration.Loop) };
        var (ok, error) = Recipe.ValidateSteps(steps);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateSteps_TerminalKeepAlive_NoMacro_IsValid()
    {
        var steps = new[] { Pos("a"), new RecipeStep(null, StepIteration.KeepAlive) };
        Assert.True(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void ValidateSteps_Empty_IsInvalid()
        => Assert.False(Recipe.ValidateSteps(Array.Empty<RecipeStep>()).ok);

    [Fact]
    public void ValidateSteps_NonTerminalRunOnce_MustHaveMacro()
    {
        var steps = new[] { new RecipeStep(null, StepIteration.RunOnce), new RecipeStep("b", StepIteration.Loop) };
        Assert.False(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void ValidateSteps_TerminalRunOnce_IsInvalid()
    {
        var steps = new[] { new RecipeStep("a", StepIteration.RunOnce) };
        Assert.False(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void ValidateSteps_LoopTerminal_WithoutMacro_IsInvalid()
    {
        var steps = new[] { Pos("a"), new RecipeStep(null, StepIteration.Loop) };
        Assert.False(Recipe.ValidateSteps(steps).ok);
    }

    [Fact]
    public void ValidateSteps_TerminalDone_WithPriorPositionStep_IsValid()
    {
        var steps = new[] { Pos("a"), new RecipeStep(null, StepIteration.Done) };
        var (ok, error) = Recipe.ValidateSteps(steps);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateSteps_TerminalDone_Alone_IsInvalid()
    {
        var steps = new[] { new RecipeStep(null, StepIteration.Done) };
        var (ok, error) = Recipe.ValidateSteps(steps);
        Assert.False(ok);
        Assert.Equal("A loadout needs at least one step to run.", error);
    }

    [Fact]
    public void TerminalAndPositionSteps_Partition()
    {
        var steps = new[] { Pos("a"), Pos("b"), new RecipeStep("c", StepIteration.Loop) };
        var recipe = new Recipe(Recipe.CurrentSchemaVersion, Guid.NewGuid().ToString(), "r", steps, 0);
        Assert.Equal(StepIteration.Loop, recipe.Terminal.Iteration);
        Assert.Equal(new[] { "a", "b" }, recipe.PositionSteps.Select(s => s.MacroId));
    }
}
