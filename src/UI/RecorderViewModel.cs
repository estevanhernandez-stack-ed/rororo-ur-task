using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.UI;

/// <summary>
/// Binds <see cref="PluginRuntime"/> state to the recorder window. Observes
/// runtime events (StateChanged, ConnectionChanged, StatusLogged,
/// MacrosChanged), polls the foreground watcher on a 250ms dispatcher timer,
/// and surfaces simple ICommand wrappers for Record/Play/Stop buttons.
/// </summary>
internal sealed class RecorderViewModel : INotifyPropertyChanged
{
    private const int StatusLogLimit = 100;
    private readonly PluginRuntime _runtime;
    private readonly DispatcherTimer _foregroundTimer;

    public RecorderViewModel(PluginRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        Macros = new ObservableCollection<Macro>();
        StatusLines = new ObservableCollection<string>();

        RecordCommand = new RelayCommand(_runtime.TriggerRecordToggle);
        PlayCommand = new RelayCommand(_runtime.TriggerPlay);
        StopCommand = new RelayCommand(_runtime.TriggerAbort);

        _runtime.StateChanged += () => { OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(IsRecording)); OnPropertyChanged(nameof(IsPlaying)); };
        _runtime.ConnectionChanged += () => { OnPropertyChanged(nameof(ConnectionLabel)); OnPropertyChanged(nameof(IsConnected)); };
        _runtime.StatusLogged += line =>
        {
            StatusLines.Insert(0, line);
            while (StatusLines.Count > StatusLogLimit) StatusLines.RemoveAt(StatusLines.Count - 1);
        };
        _runtime.MacrosChanged += () =>
        {
            Macros.Clear();
            var loaded = _runtime.Store.LoadAll();
            foreach (var m in loaded.Macros.OrderByDescending(m => m.RecordedAtUnixMs))
                Macros.Add(m);
            OnPropertyChanged(nameof(LastMacroLabel));
        };

        _foregroundTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Normal, (_, _) => OnPropertyChanged(nameof(BoundForegroundLabel)),
            Dispatcher.CurrentDispatcher);
        _foregroundTimer.Start();
    }

    public ObservableCollection<Macro> Macros { get; }
    public ObservableCollection<string> StatusLines { get; }

    public ICommand RecordCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }

    public string StateLabel => _runtime.State switch
    {
        PluginState.Idle => "IDLE",
        PluginState.Recording => "RECORDING",
        PluginState.Playing => "PLAYING",
        _ => "—",
    };

    public bool IsRecording => _runtime.State == PluginState.Recording;
    public bool IsPlaying => _runtime.State == PluginState.Playing;

    public string ConnectionLabel => _runtime.IsConnected
        ? $"Connected · RoRoRo {_runtime.HostVersion}"
        : "Not connected to RoRoRo";

    public bool IsConnected => _runtime.IsConnected;

    public string BoundForegroundLabel
    {
        get
        {
            var fg = _runtime.ResolveForegroundNow();
            return fg is null
                ? "No RoRoRo-managed window in foreground"
                : $"{fg.DisplayName}  ·  user {fg.RobloxUserId}";
        }
    }

    public string LastMacroLabel
    {
        get
        {
            var m = _runtime.LastMacro;
            return m is null
                ? "—"
                : $"{m.Events.Count} events · {m.Duration.TotalSeconds:F1}s · bound {m.BoundDisplayName}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
