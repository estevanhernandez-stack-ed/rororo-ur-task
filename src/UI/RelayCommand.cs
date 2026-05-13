using System.Windows.Input;

namespace Labs626.UrTask.UI;

/// <summary>Minimal ICommand for VM-bound buttons. v0.1 ships zero deps; tracking
/// CommunityToolkit.Mvvm is overkill for ~3 commands.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Generic ICommand for VM-bound buttons that carry a typed parameter
/// (e.g. per-card play buttons binding to a <see cref="Macros.Macro"/>).</summary>
internal sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Predicate<T>? _canExecute;

    public RelayCommand(Action<T> execute, Predicate<T>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T)parameter!) ?? true;
    public void Execute(object? parameter) => _execute((T)parameter!);
}
