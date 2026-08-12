using System.ComponentModel;
using System.Runtime.CompilerServices;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

internal sealed class AssignmentRow : INotifyPropertyChanged
{
    public AssignmentRow(AccountRegistry.AccountInfo alt) { Alt = alt; }

    public AccountRegistry.AccountInfo Alt { get; }

    private Macro? _assigned;
    public Macro? AssignedMacro
    {
        get => _assigned;
        set
        {
            if (Equals(_assigned, value)) return;
            _assigned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayMacroName));
            OnPropertyChanged(nameof(HasMacro));
            OnPropertyChanged(nameof(HasGameMismatch));
            OnPropertyChanged(nameof(GameMismatchHint));
        }
    }

    public string DisplayMacroName => _assigned?.Name ?? "Keep-alive (Space)";
    public bool HasMacro => _assigned is not null;

    private bool _isCheckedForRoutine;
    /// <summary>Per-alt selection for routine (recipe/loadout) runs — independent
    /// of <see cref="AssignedMacro"/>; the two coexist on the same row.</summary>
    public bool IsCheckedForRoutine
    {
        get => _isCheckedForRoutine;
        set
        {
            if (_isCheckedForRoutine == value) return;
            _isCheckedForRoutine = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Soft warning: game-scoped macro paired with an alt known to be in a different game.</summary>
    public bool HasGameMismatch => MacroGameFilter.IsMismatch(_assigned, Alt.PlaceId);

    public string GameMismatchHint => HasGameMismatch
        ? $"Recorded in {_assigned!.RecordedGameName ?? "another game"}; {Alt.DisplayName} is in {(string.IsNullOrEmpty(Alt.PlaceName) ? "a different game" : Alt.PlaceName)}. Plays anyway — this is just a heads-up."
        : string.Empty;

    // ---------- Cadence role toggle + next-due countdown (Task 8) ----------

    private CadenceRole _role = CadenceRole.Active;

    /// <summary>
    /// UI-controlled cadence role for this row — independent of <see cref="AssignedMacro"/>.
    /// Backgrounding (flip to KeepAlive) is non-destructive: the macro is untouched, merely
    /// paused, so flipping back to Active resumes farming without re-picking anything.
    /// </summary>
    public CadenceRole Role
    {
        get => _role;
        set { if (_role == value) return; _role = value; OnPropertyChanged(); OnPropertyChanged(nameof(NextDueText)); }
    }

    private TimeSpan? _nextDue;

    /// <summary>Proof-of-life for a sleeping scheduler. Empty for Active rows.</summary>
    public string NextDueText => Role == CadenceRole.KeepAlive && _nextDue is TimeSpan t
        ? $"next: {Math.Max(0, (int)Math.Round(t.TotalMinutes))}m"
        : string.Empty;

    public void SetNextDue(TimeSpan? due)
    {
        _nextDue = due;
        OnPropertyChanged(nameof(NextDueText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
