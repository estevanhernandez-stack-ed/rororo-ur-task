using Labs626.UrTask.PluginHost;
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

public class PlaybackTargetPickerViewModelTests
{
    private static AccountRegistry.AccountInfo Alt(int pid, long userId, string name)
        => new(pid, userId, name, $"acct-{pid}");

    private static IReadOnlyList<AccountRegistry.AccountInfo> ThreeAlts() => new[]
    {
        Alt(1001, 47821334, "Goldnail8"),
        Alt(1002, 47821335, "ScrambledTen"),
        Alt(1003, 47821336, "PinkPotatoChip"),
    };

    [Fact]
    public void SingleSelect_Toggle_ReplacesSelection()
    {
        var vm = new PlaybackTargetPickerViewModel(ThreeAlts(), preferredUserId: null, multiSelect: false);
        vm.Toggle(vm.Alts[0]);
        Assert.Single(vm.SelectedTargets);
        Assert.Equal(47821334, vm.SelectedTargets[0].RobloxUserId);

        vm.Toggle(vm.Alts[2]);
        // Single-select replaces the previous selection.
        Assert.Single(vm.SelectedTargets);
        Assert.Equal(47821336, vm.SelectedTargets[0].RobloxUserId);
    }

    [Fact]
    public void MultiSelect_TogglesAccumulate_OrderPreserved()
    {
        var vm = new PlaybackTargetPickerViewModel(ThreeAlts(), preferredUserId: null, multiSelect: true);
        vm.Toggle(vm.Alts[2]); // pick PinkPotatoChip first → order 1
        vm.Toggle(vm.Alts[0]); // pick Goldnail8 second → order 2

        Assert.Equal(2, vm.SelectedTargets.Count);
        Assert.Equal(47821336, vm.SelectedTargets[0].RobloxUserId);
        Assert.Equal(47821334, vm.SelectedTargets[1].RobloxUserId);
        Assert.Equal(1, vm.OrderTagFor(vm.Alts[2]));
        Assert.Equal(2, vm.OrderTagFor(vm.Alts[0]));
        Assert.Null(vm.OrderTagFor(vm.Alts[1])); // not selected
    }

    [Fact]
    public void MultiSelect_DeselectMiddle_OrderRenumbers()
    {
        var vm = new PlaybackTargetPickerViewModel(ThreeAlts(), preferredUserId: null, multiSelect: true);
        vm.Toggle(vm.Alts[0]); // order 1
        vm.Toggle(vm.Alts[1]); // order 2
        vm.Toggle(vm.Alts[2]); // order 3
        vm.Toggle(vm.Alts[1]); // deselect middle

        Assert.Equal(2, vm.SelectedTargets.Count);
        Assert.Equal(1, vm.OrderTagFor(vm.Alts[0]));
        Assert.Equal(2, vm.OrderTagFor(vm.Alts[2]));
        Assert.Null(vm.OrderTagFor(vm.Alts[1]));
    }

    [Fact]
    public void PreferredUserId_PreSelectsThatAlt()
    {
        var vm = new PlaybackTargetPickerViewModel(ThreeAlts(), preferredUserId: 47821335, multiSelect: false);
        Assert.Single(vm.SelectedTargets);
        Assert.Equal(47821335, vm.SelectedTargets[0].RobloxUserId);
    }

    [Fact]
    public void CanPlay_FalseWhenNothingSelected_TrueOtherwise()
    {
        var vm = new PlaybackTargetPickerViewModel(ThreeAlts(), preferredUserId: null, multiSelect: true);
        Assert.False(vm.CanPlay);
        vm.Toggle(vm.Alts[0]);
        Assert.True(vm.CanPlay);
        vm.Toggle(vm.Alts[0]);
        Assert.False(vm.CanPlay);
    }
}
