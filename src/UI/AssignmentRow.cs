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

    /// <summary>Soft warning: game-scoped macro paired with an alt known to be in a different game.</summary>
    public bool HasGameMismatch => MacroGameFilter.IsMismatch(_assigned, Alt.PlaceId);

    public string GameMismatchHint => HasGameMismatch
        ? $"Recorded in {_assigned!.RecordedGameName ?? "another game"}; {Alt.DisplayName} is in {(string.IsNullOrEmpty(Alt.PlaceName) ? "a different game" : Alt.PlaceName)}. Plays anyway — this is just a heads-up."
        : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
