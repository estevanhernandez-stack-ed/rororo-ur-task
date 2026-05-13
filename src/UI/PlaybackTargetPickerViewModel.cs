using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

/// <summary>
/// Selection state for the target picker. Single-select replaces selection
/// on each toggle; multi-select accumulates with click-order tracking
/// (the order tag drives the SequencePlayer iteration order).
/// </summary>
internal sealed class PlaybackTargetPickerViewModel : INotifyPropertyChanged
{
    private readonly List<AccountRegistry.AccountInfo> _selection = new();

    public PlaybackTargetPickerViewModel(
        IReadOnlyList<AccountRegistry.AccountInfo> alts,
        long? preferredUserId,
        bool multiSelect)
    {
        Alts = new ObservableCollection<AccountRegistry.AccountInfo>(alts);
        MultiSelect = multiSelect;

        if (preferredUserId is not null)
        {
            var preferred = alts.FirstOrDefault(a => a.RobloxUserId == preferredUserId.Value);
            if (preferred is not null) _selection.Add(preferred);
        }
    }

    public ObservableCollection<AccountRegistry.AccountInfo> Alts { get; }
    public bool MultiSelect { get; }

    public IReadOnlyList<AccountRegistry.AccountInfo> SelectedTargets => _selection;

    public bool CanPlay => _selection.Count > 0;

    public string PlayButtonLabel => MultiSelect
        ? (_selection.Count switch
            {
                0 => "Select alts to play",
                1 => "PLAY ON 1 ALT  ↵",
                int n => $"PLAY ON {n} ALTS  ↵",
            })
        : "PLAY  ↵";

    public void Toggle(AccountRegistry.AccountInfo alt)
    {
        if (alt is null) return;
        var existing = _selection.FindIndex(a => a.RobloxUserId == alt.RobloxUserId);
        if (existing >= 0)
        {
            _selection.RemoveAt(existing);
        }
        else
        {
            if (!MultiSelect) _selection.Clear();
            _selection.Add(alt);
        }
        OnPropertyChanged(nameof(SelectedTargets));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayButtonLabel));
    }

    /// <summary>1-based click order for multi-select; null when not selected or in single-select mode.</summary>
    public int? OrderTagFor(AccountRegistry.AccountInfo alt)
    {
        if (!MultiSelect) return null;
        var idx = _selection.FindIndex(a => a.RobloxUserId == alt.RobloxUserId);
        return idx < 0 ? null : idx + 1;
    }

    public bool IsSelected(AccountRegistry.AccountInfo alt) => _selection.Any(a => a.RobloxUserId == alt.RobloxUserId);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
