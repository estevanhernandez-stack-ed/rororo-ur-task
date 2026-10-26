using System.ComponentModel;
using System.Runtime.CompilerServices;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

internal sealed class AssignmentRow : INotifyPropertyChanged
{
    private readonly KeepAliveView? _keepAlive;

    public AssignmentRow(AccountRegistry.AccountInfo alt, KeepAliveView? keepAlive = null)
    {
        Alt = alt;
        _keepAlive = keepAlive;
    }

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

    /// <summary>Proof-of-life for a sleeping scheduler, read from the keep-alive ledger: when the account
    /// next needs input, or why it will not get any. Empty for Active rows and before the loop has seen the account.</summary>
    public string NextDueText
    {
        get
        {
            if (Role != CadenceRole.KeepAlive || _keepAlive is null) return string.Empty;
            var e = _keepAlive.Ledger.Get(Alt.RobloxUserId);
            if (e is null) return string.Empty;
            if (e.Off) return "off";
            if (e.State == AccountKeepAliveState.Held) return "held";
            if (e.State == AccountKeepAliveState.PausedForMacro)
                return _keepAlive.Gate.Status.For is { Length: > 0 } name ? $"paused for {name}" : "paused";
            var untilMs = Math.Max(0, _keepAlive.Ledger.DueAtMs(Alt.RobloxUserId) - _keepAlive.NowMs());
            return $"next: {(int)Math.Ceiling(untilMs / 60_000.0)}m";
        }
    }

    /// <summary>Re-reads the ledger (the UI's refresh tick calls this).</summary>
    public void RefreshKeepAlive()
    {
        OnPropertyChanged(nameof(NextDueText));
        OnPropertyChanged(nameof(KeepAliveOff));
    }

    /// <summary>Keep-alive off for this account: until the user turns it back on or Ur Task restarts.
    /// Macros still run on an off account; off is about keep-alive only.</summary>
    public bool KeepAliveOff
    {
        get => _keepAlive?.Ledger.Get(Alt.RobloxUserId)?.Off ?? false;
        set
        {
            if (_keepAlive is null || KeepAliveOff == value) return;
            // Tracked first: the flag lives on the ledger entry, and the loop may not have started yet.
            _keepAlive.Ledger.Track(Alt, _keepAlive.LimitFor(Alt));
            _keepAlive.Ledger.SetOff(Alt.RobloxUserId, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(NextDueText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
