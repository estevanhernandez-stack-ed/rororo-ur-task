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

    [Fact]
    public void Build_PlaceStamp_SkipsAllGamesMacro()
    {
        var macroA = new Macro(Macro.CurrentSchemaVersion, "11111111-1111-1111-1111-111111111111", "A", null, null, null, null, 0,
            Array.Empty<MacroEvent>(), RecordedPlaceId: 111, RecordedGameName: "AllGamesLeftover", AllGames: true);
        var macroB = new Macro(Macro.CurrentSchemaVersion, "22222222-2222-2222-2222-222222222222", "B", null, null, null, null, 0,
            Array.Empty<MacroEvent>(), RecordedPlaceId: 222, RecordedGameName: "RealGame", AllGames: false);

        var vm = new RecipeEditorViewModel(new[] { macroA, macroB });
        vm.AddPositionStep(macroA.Id);
        vm.SetTerminal(StepIteration.Loop, macroB.Id);

        var recipe = vm.Build(Guid.NewGuid().ToString(), "walk + mine", nowUnixMs: 123);
        Assert.Equal(222, recipe.RecordedPlaceId);
        Assert.Equal("RealGame", recipe.RecordedGameName);
    }
}
