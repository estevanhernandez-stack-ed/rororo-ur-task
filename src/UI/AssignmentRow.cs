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
        }
    }

    public string DisplayMacroName => _assigned?.Name ?? "Keep-alive (Space)";
    public bool HasMacro => _assigned is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
