using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.UI;

/// <summary>
/// Binds <see cref="PluginRuntime"/> state to the recorder window. Observes
/// runtime events (StateChanged, ConnectionChanged, StatusLogged,
/// MacrosChanged, SequenceProgressed) and surfaces ICommand wrappers plus
/// v0.2 bindable surface: SequenceProgress, IsCompact, IsTopmost, RecordMode,
/// PlayMacroCommand, and derived status properties.
/// </summary>
internal sealed class RecorderViewModel : INotifyPropertyChanged
{
    private const int StatusLogLimit = 100;
    private readonly PluginRuntime _runtime;
    private readonly UserPreferences _prefs = UserPreferences.Load();

    public RecorderViewModel(PluginRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        Macros = new ObservableCollection<Macro>();
        StatusLines = new ObservableCollection<string>();

        RecordCommand = new RelayCommand(_runtime.TriggerRecordToggle);
        StopCommand = new RelayCommand(_runtime.TriggerAbort);
        PlayMacroCommand = new RelayCommand<Macro>(m => { if (m is not null) _runtime.TriggerPlayMacro(m.Id); });

        // Initialize pin state from saved prefs based on current compact mode (default: false / not compact).
        _isTopmost = _isCompact ? _prefs.TopmostInCompactMode : _prefs.TopmostInFullMode;

        _runtime.StateChanged += () =>
        {
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusMeta));
        };
        _runtime.ConnectionChanged += () =>
        {
            OnPropertyChanged(nameof(ConnectionLabel));
            OnPropertyChanged(nameof(IsConnected));
        };
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
            OnPropertyChanged(nameof(HasMacros));
            OnPropertyChanged(nameof(HasNoMacros));
            OnPropertyChanged(nameof(StatusMeta));
        };
        _runtime.SequenceProgressed += p =>
        {
            SequenceProgress = p;

            // Auto-collapse to compact when a multi-alt sequence starts so the
            // Roblox alt windows are visible behind the recorder. Restore prior
            // compact state when the sequence ends. Single-alt plays don't
            // trigger auto-collapse (too brief).
            if (p.Phase == SequencePhase.Focusing && p.Index == 0 && p.Total > 1)
            {
                _wasCompactBeforeSequence = IsCompact;
                IsCompact = true;
            }
            else if (p.Phase == SequencePhase.Done || p.Phase == SequencePhase.Aborted)
            {
                IsCompact = _wasCompactBeforeSequence;
            }
        };
    }

    // ---------- Collections ----------

    public ObservableCollection<Macro> Macros { get; }
    public ObservableCollection<string> StatusLines { get; }

    // ---------- Commands ----------

    public ICommand RecordCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PlayMacroCommand { get; }

    // ---------- Existing state properties ----------

    public string StateLabel => _runtime.State switch
    {
        PluginState.Recording => "RECORDING",
        PluginState.Playing => "PLAYING",
        _ => "IDLE",
    };

    public bool IsRecording => _runtime.State == PluginState.Recording;
    public bool IsPlaying => _runtime.State == PluginState.Playing;

    public string ConnectionLabel => _runtime.IsConnected
        ? $"Connected · RoRoRo {_runtime.HostVersion}"
        : "Not connected to RoRoRo";

    public bool IsConnected => _runtime.IsConnected;

    // ---------- v0.2: SequenceProgress ----------

    private SequenceProgress? _sequenceProgress;
    private bool _wasCompactBeforeSequence;
    public SequenceProgress? SequenceProgress
    {
        get => _sequenceProgress;
        private set
        {
            if (Equals(_sequenceProgress, value)) return;
            _sequenceProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSequencePlaying));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusMeta));
            OnPropertyChanged(nameof(SequenceProgressFraction));
        }
    }

    public bool IsSequencePlaying => _sequenceProgress is { Phase: not SequencePhase.Done and not SequencePhase.Aborted };

    public double SequenceProgressFraction
        => _sequenceProgress is null || _sequenceProgress.Total == 0
            ? 0.0
            : (double)_sequenceProgress.Index / _sequenceProgress.Total;

    // ---------- v0.2: RecordMode ----------

    public RecordMode RecordMode
    {
        get => _runtime.CurrentRecordMode;
        set
        {
            if (_runtime.CurrentRecordMode == value) return;
            _runtime.CurrentRecordMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRecordModeAllWindows));
        }
    }

    public bool IsRecordModeAllWindows
    {
        get => _runtime.CurrentRecordMode == RecordMode.AllWindows;
        set => RecordMode = value ? RecordMode.AllWindows : RecordMode.PerWindow;
    }

    // ---------- v0.2: IsCompact / IsTopmost ----------

    private bool _isCompact;
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (_isCompact == value) return;
            _isCompact = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotCompact));
            // Switch pin state to whichever mode's preference.
            var prefTopmost = value ? _prefs.TopmostInCompactMode : _prefs.TopmostInFullMode;
            if (_isTopmost != prefTopmost)
            {
                _isTopmost = prefTopmost;
                OnPropertyChanged(nameof(IsTopmost));
            }
        }
    }

    public bool IsNotCompact => !_isCompact;

    private bool _isTopmost;
    public bool IsTopmost
    {
        get => _isTopmost;
        set
        {
            if (_isTopmost == value) return;
            _isTopmost = value;
            if (_isCompact) _prefs.TopmostInCompactMode = value;
            else _prefs.TopmostInFullMode = value;
            _prefs.Save();
            OnPropertyChanged();
        }
    }

    // ---------- v0.2: Status pill ----------

    public string StatusLabel => (IsRecording, IsPlaying, IsSequencePlaying) switch
    {
        (true, _, _) => "Recording",
        (_, _, true) => $"Playing {_sequenceProgress!.CurrentAlt?.DisplayName ?? "..."}",
        (_, true, _) => "Playing",
        _ => "Idle",
    };

    public string StatusMeta => (IsRecording, IsSequencePlaying) switch
    {
        (true, _) => $"recording in {_runtime.CurrentRecordMode} mode",
        (_, true) => $"alt {_sequenceProgress!.Index + 1} of {_sequenceProgress.Total} · {_sequenceProgress.Completed} succeeded, {_sequenceProgress.Failed} failed",
        _ => $"{Macros.Count} macros",
    };

    // ---------- v0.2: Empty-state helpers ----------

    public bool HasMacros => Macros.Count > 0;
    public bool HasNoMacros => Macros.Count == 0;

    // ---------- INPC ----------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
