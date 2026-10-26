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


    private static readonly KeepAliveLimit Fifteen = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), false);

    private sealed class Clock { public long Now = 10 * 60_000; }

    private static (AssignmentRow row, KeepAliveLedger ledger, Clock clock) KeepAliveRow()
    {
        var clock = new Clock();
        var ledger = new KeepAliveLedger(() => clock.Now, () => DateTimeOffset.UnixEpoch);
        var gate = new ForegroundGate(() => clock.Now, _ => { });
        var view = new KeepAliveView(ledger, gate, () => clock.Now, _ => Fifteen);
        var row = new AssignmentRow(Alt(1), view) { Role = CadenceRole.KeepAlive };
        return (row, ledger, clock);
    }

    private static void StampAgo(KeepAliveLedger ledger, Clock clock, long userId, long agoMs)
    {
        var now = clock.Now;
        clock.Now = now - agoMs;
        ledger.StampLanded(userId, InputSource.KeepAlive);
        clock.Now = now;
    }

    /// Proof-of-life: a keep-alive row shows when it next fires, read from the ledger.
    [Fact]
    public void Next_due_text_comes_from_the_ledger()
    {
        var (row, ledger, clock) = KeepAliveRow();
        ledger.Track(row.Alt, Fifteen);
        StampAgo(ledger, clock, row.Alt.RobloxUserId, 5 * 60_000);   // deadline 9 min: 4 min left

        Assert.Equal("next: 4m", row.NextDueText);
    }

    [Fact]
    public void Next_due_text_rounds_up_and_never_goes_negative()
    {
        var (row, ledger, clock) = KeepAliveRow();
        ledger.Track(row.Alt, Fifteen);
        StampAgo(ledger, clock, row.Alt.RobloxUserId, 5 * 60_000 + 30_000);   // 3 min 30 s left
        Assert.Equal("next: 4m", row.NextDueText);

        clock.Now += 20 * 60_000;
        Assert.Equal("next: 0m", row.NextDueText);   // overdue
    }

    [Fact]
    public void Next_due_text_is_empty_before_the_loop_has_seen_the_account()
    {
        var (row, _, _) = KeepAliveRow();
        Assert.Equal(string.Empty, row.NextDueText);
    }

    [Fact]
    public void ActiveRow_ShowsNoCountdown()
    {
        var (row, ledger, _) = KeepAliveRow();
        ledger.Track(row.Alt, Fifteen);
        row.AssignedMacro = NewMacro();
        row.Role = CadenceRole.Active;

        Assert.Equal(string.Empty, row.NextDueText);
    }

    [Fact]
    public void Keep_alive_off_sets_the_ledger_and_shows_off()
    {
        var (row, ledger, _) = KeepAliveRow();
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.KeepAliveOff = true;

        Assert.True(ledger.Get(row.Alt.RobloxUserId)!.Off);   // tracked on demand: the loop may not have started
        Assert.True(row.KeepAliveOff);
        Assert.Equal("off", row.NextDueText);
        Assert.Contains(nameof(AssignmentRow.KeepAliveOff), raised);

        row.KeepAliveOff = false;
        Assert.False(ledger.Get(row.Alt.RobloxUserId)!.Off);
        Assert.StartsWith("next:", row.NextDueText);
    }

    [Fact]
    public void Held_under_breakglass_and_paused_with_nobody_to_name()
    {
        var (row, ledger, _) = KeepAliveRow();
        ledger.Track(row.Alt, Fifteen);

        ledger.SetState(row.Alt.RobloxUserId, AccountKeepAliveState.Held);
        Assert.Equal("held", row.NextDueText);

        ledger.SetState(row.Alt.RobloxUserId, AccountKeepAliveState.PausedForMacro);
        Assert.Equal("paused", row.NextDueText);   // no break pending: nobody to name
    }

    [Fact]
    public void A_row_without_a_keep_alive_view_shows_nothing_and_ignores_the_toggle()
    {
        var row = new AssignmentRow(Alt(1)) { Role = CadenceRole.KeepAlive };
        row.KeepAliveOff = true;
        Assert.False(row.KeepAliveOff);
        Assert.Equal(string.Empty, row.NextDueText);
    }
}
