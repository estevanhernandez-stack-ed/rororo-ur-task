using Labs626.UrTask.PluginHost;
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Covers <see cref="AssignmentRow.IsCheckedForRoutine"/> — the per-alt selector
/// the routine (recipe/loadout) run surface uses to pick which alts RUN targets.
/// RecorderViewModel.RunRoutineCommand's CanExecute reads this directly
/// (Assignments.Any(r => r.IsCheckedForRoutine)); RecorderViewModel itself isn't
/// unit-testable here since it requires a live PluginRuntime (global hotkeys,
/// gRPC client) — same reason no existing test constructs one.
/// </summary>
public class AssignmentRowTests
{
    private static AccountRegistry.AccountInfo Alt(int pid) => new(pid, pid, $"alt{pid}", $"acct-{pid}");

    [Fact]
    public void IsCheckedForRoutine_DefaultsFalse()
    {
        var row = new AssignmentRow(Alt(1));
        Assert.False(row.IsCheckedForRoutine);
    }

    [Fact]
    public void IsCheckedForRoutine_SetTrue_RaisesPropertyChanged()
    {
        var row = new AssignmentRow(Alt(1));
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.IsCheckedForRoutine = true;

        Assert.True(row.IsCheckedForRoutine);
        Assert.Contains(nameof(AssignmentRow.IsCheckedForRoutine), raised);
    }

    [Fact]
    public void IsCheckedForRoutine_SetSameValue_DoesNotRaisePropertyChanged()
    {
        var row = new AssignmentRow(Alt(1));
        var raiseCount = 0;
        row.PropertyChanged += (_, _) => raiseCount++;

        row.IsCheckedForRoutine = false; // already false by default — no-op

        Assert.Equal(0, raiseCount);
    }

    [Fact]
    public void IsCheckedForRoutine_IndependentOfAssignedMacro()
    {
        var row = new AssignmentRow(Alt(1));

        row.IsCheckedForRoutine = true;

        // Checking the routine-target box doesn't touch the (separate) macro
        // assignment, and vice versa — the two selectors coexist on the same row.
        Assert.True(row.IsCheckedForRoutine);
        Assert.Null(row.AssignedMacro);
    }
}
