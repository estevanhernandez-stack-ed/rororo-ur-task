using Labs626.UrTask.Macros;
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

    // ---------- Task 8: Role toggle + next-due countdown ----------

    // AssignmentRow's real shape: ctor takes the alt; the macro is the settable
    // `AssignedMacro` property (Macro?), NOT an id.
    private static AssignmentRow Row(int pid = 1)
        => new(new AccountRegistry.AccountInfo(pid, pid, $"alt{pid}", $"acct-{pid}"));

    private static Macro NewMacro() => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: "farm",
        RecordMode: "PerWindow", RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    [Fact]
    public void Role_RaisesPropertyChanged()
    {
        var row = Row();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Role = CadenceRole.KeepAlive;

        Assert.Contains(nameof(AssignmentRow.Role), raised);
    }

    /// Backgrounding must NOT be destructive — the macro survives, merely paused.
    /// Flip the row back to Active and it farms again without re-picking anything.
    [Fact]
    public void SettingKeepAlive_DoesNotClearTheAssignedMacro()
    {
        var row = Row();
        var macro = NewMacro();
        row.AssignedMacro = macro;

        row.Role = CadenceRole.KeepAlive;

        Assert.Same(macro, row.AssignedMacro);
        Assert.True(row.HasMacro);
    }

    /// Proof-of-life: a keep-alive row shows when it next fires. Without this the
    /// scheduler is invisible — a quiet screen reads as "broken."
    [Fact]
    public void KeepAliveRow_ShowsNextDueCountdown()
    {
        var row = Row();
        row.Role = CadenceRole.KeepAlive;

        row.SetNextDue(TimeSpan.FromMinutes(8));

        Assert.Equal("next: 8m", row.NextDueText);
    }

    [Fact]
    public void ActiveRow_ShowsNoCountdown()
    {
        var row = Row();
        row.AssignedMacro = NewMacro();
        row.Role = CadenceRole.Active;

        row.SetNextDue(TimeSpan.FromMinutes(8));   // even if set, an Active row shows nothing

        Assert.Equal(string.Empty, row.NextDueText);
    }
}
