using Labs626.UrTask.Macros;
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

public class RecipeEditorViewModelTests
{
    private static Macro M(string id, string name)
        => new(Macro.CurrentSchemaVersion, id, name, null, null, null, null, 0, Array.Empty<MacroEvent>());

    [Fact]
    public void CanSave_FalseUntilTerminalSet()
    {
        var vm = new RecipeEditorViewModel(new[] { M("11111111-1111-1111-1111-111111111111", "walk") });
        Assert.False(vm.CanSave);
        vm.AddPositionStep("11111111-1111-1111-1111-111111111111");
        Assert.False(vm.CanSave); // still no terminal
        vm.SetTerminal(StepIteration.KeepAlive, null);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public void Build_ProducesValidRecipe()
    {
        var vm = new RecipeEditorViewModel(new[] {
            M("11111111-1111-1111-1111-111111111111", "walk"),
            M("22222222-2222-2222-2222-222222222222", "mine") });
        vm.AddPositionStep("11111111-1111-1111-1111-111111111111");
        vm.SetTerminal(StepIteration.Loop, "22222222-2222-2222-2222-222222222222");

        var recipe = vm.Build(Guid.NewGuid().ToString(), "walk + mine", nowUnixMs: 123);
        Assert.True(Recipe.ValidateSteps(recipe.Steps).ok);
        Assert.Equal(StepIteration.Loop, recipe.Terminal.Iteration);
        Assert.Equal(123, recipe.RecordedAtUnixMs);
    }
}
