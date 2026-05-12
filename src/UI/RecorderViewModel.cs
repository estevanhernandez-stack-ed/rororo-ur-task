using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.UI;

/// <summary>
/// Binds <see cref="PluginRuntime"/> state to the recorder window. Observes
/// runtime events and surfaces ICommand wrappers plus v0.2 bindable surface:
/// SequenceProgress, IsCompact, IsTopmost, RecordMode, assignment table commands,
/// and derived status properties.
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
        Assignments = new ObservableCollection<AssignmentRow>();

        RecordCommand = new RelayCommand(_runtime.TriggerRecordToggle);
        StopCommand = new RelayCommand(_runtime.TriggerAbort);
        ToggleCompactCommand = new RelayCommand(() => IsCompact = !IsCompact);

        // Assignment commands
        MarkMacroActiveCommand = new RelayCommand<Macro>(m => { if (m is not null) ActiveAssignmentMacro = m; });

        ToggleAltAssignmentCommand = new RelayCommand<AssignmentRow>(row =>
        {
            if (row is null) return;
            // Toggle: if this row already has the active macro assigned, clear it.
            // Else, assign the active macro (which may be null = keep-alive).
            if (row.AssignedMacro is not null && _activeAssignmentMacro is not null
                && row.AssignedMacro.Id == _activeAssignmentMacro.Id)
            {
                _runtime.AssignMacroToAlt(row.Alt.Pid, null);
            }
            else
            {
                _runtime.AssignMacroToAlt(row.Alt.Pid, _activeAssignmentMacro);
            }
        });

        ResetAssignmentsCommand = new RelayCommand(() => _runtime.ResetAssignments());

        PlayAssignmentsCommand = new RelayCommand(
            () => _runtime.TriggerPlayAssignments(),
            () => Assignments.Count > 0 && !IsRunnerActive);

        StopAssignmentsCommand = new RelayCommand(
            () => _runtime.TriggerAbort(),
            () => IsRunnerActive);

        // Initialize pin state from saved prefs based on current compact mode.
        _isTopmost = _isCompact ? _prefs.TopmostInCompactMode : _prefs.TopmostInFullMode;

        _runtime.StateChanged += () =>
        {
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsAnyPlaybackActive));
            OnPropertyChanged(nameof(IsNotPlaybackActive));
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
        _runtime.CurrentlyPlayingMacroChanged += _ =>
        {
            // No longer drives card state but keep for potential future use.
        };
        _runtime.LastMacroChanged += _ =>
        {
            // LastMacro is kept on runtime but no longer drives a card chip.
        };

        _runtime.SequenceProgressed += p =>
        {
            SequenceProgress = p;
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

        // Assignment event handlers
        _runtime.AssignmentChanged += (pid, macro) => RaiseUI(() =>
        {
            RefreshAssignmentRow(pid, macro);
            RecomputePairings();
        });
        _runtime.AssignmentsReset += () => RaiseUI(() =>
        {
            foreach (var r in Assignments) r.AssignedMacro = null;
            RecomputePairings();
        });
        _runtime.AssignmentProgressed += p => RaiseUI(() => RunnerProgress = p);

        // Account add/remove updates rows live
        _runtime.Accounts.AccountAdded += (_, info) => RaiseUI(() => AddAssignmentRow(info));
        _runtime.Accounts.AccountRemoved += (_, info) => RaiseUI(() => RemoveAssignmentRow(info.Pid));

        // Seed current alts on construction
        foreach (var alt in _runtime.Accounts.Snapshot()) AddAssignmentRow(alt);
    }

    // ---------- Collections ----------

    public ObservableCollection<Macro> Macros { get; }
    public ObservableCollection<string> StatusLines { get; }
    public ObservableCollection<AssignmentRow> Assignments { get; }

    // ---------- Commands ----------

    public ICommand RecordCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleCompactCommand { get; }
    public ICommand MarkMacroActiveCommand { get; }
    public ICommand ToggleAltAssignmentCommand { get; }
    public ICommand ResetAssignmentsCommand { get; }
    public ICommand PlayAssignmentsCommand { get; }
    public ICommand StopAssignmentsCommand { get; }

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
            OnPropertyChanged(nameof(IsAnyPlaybackActive));
            OnPropertyChanged(nameof(IsNotPlaybackActive));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusMeta));
            OnPropertyChanged(nameof(SequenceProgressFraction));
        }
    }

    public bool IsSequencePlaying => _sequenceProgress is { Phase: not SequencePhase.Done and not SequencePhase.Aborted };

    /// <summary>True when any single-macro, multi-alt sequence, or assignment-loop playback is active.</summary>
    public bool IsAnyPlaybackActive => IsRecording || IsPlaying || IsSequencePlaying || IsRunnerActive;

    /// <summary>Inverse of <see cref="IsAnyPlaybackActive"/> — drives IsEnabled bindings.</summary>
    public bool IsNotPlaybackActive => !IsAnyPlaybackActive;

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

    // ---------- Assignment: ActiveAssignmentMacro ----------

    private Macro? _activeAssignmentMacro;
    public Macro? ActiveAssignmentMacro
    {
        get => _activeAssignmentMacro;
        private set
        {
            if (Equals(_activeAssignmentMacro, value)) return;
            _activeAssignmentMacro = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveAssignmentMacroId));
            OnPropertyChanged(nameof(HasActiveAssignment));
            OnPropertyChanged(nameof(ActiveAssignmentName));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusMeta));
        }
    }

    public string? ActiveAssignmentMacroId => _activeAssignmentMacro?.Id;
    public bool HasActiveAssignment => _activeAssignmentMacro is not null;
    public string ActiveAssignmentName => _activeAssignmentMacro?.Name ?? "(none)";

    // ---------- Assignment: paired-alt visibility (1:1 multi-pair display) ----------

    // Map of MacroId → paired alt DisplayName. Re-issued as a new instance whenever
    // assignments change so MultiBindings on PairedAltByMacroId re-evaluate.
    private IReadOnlyDictionary<string, string> _pairedAltByMacroId =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> PairedAltByMacroId
    {
        get => _pairedAltByMacroId;
        private set
        {
            _pairedAltByMacroId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PairingCount));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusMeta));
        }
    }

    /// <summary>Count of macros currently paired with an alt (1:1 enforced upstream).</summary>
    public int PairingCount => _pairedAltByMacroId.Count;

    private void RecomputePairings()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _runtime.AllAssignments)
        {
            if (kv.Value is null) continue;
            // Look up the alt's display name by PID.
            var row = Assignments.FirstOrDefault(r => r.Alt.Pid == kv.Key);
            if (row is null) continue;
            map[kv.Value.Id] = row.Alt.DisplayName;
        }
        PairedAltByMacroId = map;
    }

    // ---------- Assignment: RunnerProgress ----------

    private AssignmentProgress? _runnerProgress;
    public AssignmentProgress? RunnerProgress
    {
        get => _runnerProgress;
        private set
        {
            if (Equals(_runnerProgress, value)) return;
            _runnerProgress = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunnerActive));
            OnPropertyChanged(nameof(IsAnyPlaybackActive));
            OnPropertyChanged(nameof(IsNotPlaybackActive));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusMeta));
            // Refresh command CanExecute
            (PlayAssignmentsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopAssignmentsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsRunnerActive => _runnerProgress is { Phase: not AssignmentPhase.Stopped };

    // ---------- v0.2: Status pill ----------

    public string StatusLabel => (IsRecording, IsRunnerActive, IsSequencePlaying, IsPlaying) switch
    {
        (true, _, _, _) => "Recording",
        (_, true, _, _) => $"Running · {_runnerProgress!.Current?.Alt.DisplayName ?? "..."}",
        (_, _, true, _) => $"Playing {_sequenceProgress!.CurrentAlt?.DisplayName ?? "..."}",
        (_, _, _, true) => "Playing",
        _ => PairingCount > 0
            ? $"Ready · {PairingCount} paired"
            : (HasActiveAssignment ? $"Ready · {ActiveAssignmentName} selected" : "Idle"),
    };

    public string StatusMeta => (IsRecording, IsRunnerActive, IsSequencePlaying) switch
    {
        (true, _, _) => $"recording in {_runtime.CurrentRecordMode} mode",
        (_, true, _) => $"cycle {_runnerProgress!.Cycle} · alt {_runnerProgress.IndexInCycle + 1}/{_runnerProgress.TotalInCycle} · "
                        + (_runnerProgress.Current?.Macro?.Name ?? "keep-alive"),
        (_, _, true) => $"alt {_sequenceProgress!.Index + 1} of {_sequenceProgress.Total} · {_sequenceProgress.Completed} succeeded, {_sequenceProgress.Failed} failed",
        _ => PairingCount > 0
            ? "Ctrl+Shift+P starts the loop · Esc stops"
            : HasActiveAssignment
                ? "Click an alt row to pair it · unpaired alts get keep-alive"
                : $"{Macros.Count} macros · pick one to assign",
    };

    // ---------- v0.2: Empty-state helpers ----------

    public bool HasMacros => Macros.Count > 0;
    public bool HasNoMacros => Macros.Count == 0;

    // ---------- Macro mutations ----------

    public void RenameMacro(Macro macro, string newName)
    {
        if (macro is null || string.IsNullOrWhiteSpace(newName)) return;
        var renamed = macro with { Name = newName };
        _runtime.Store.Save(renamed);
        _runtime.RaiseMacrosChanged();
    }

    public void DeleteMacro(Macro macro)
    {
        if (macro is null) return;
        _runtime.Store.Delete(macro.Id);
        _runtime.RaiseMacrosChanged();
    }

    // ---------- Assignment helpers ----------

    private void AddAssignmentRow(AccountRegistry.AccountInfo alt)
    {
        if (Assignments.Any(r => r.Alt.Pid == alt.Pid)) return;
        var existing = _runtime.GetAssignment(alt.Pid);
        Assignments.Add(new AssignmentRow(alt) { AssignedMacro = existing });
    }

    private void RemoveAssignmentRow(int pid)
    {
        var row = Assignments.FirstOrDefault(r => r.Alt.Pid == pid);
        if (row is not null) Assignments.Remove(row);
    }

    private void RefreshAssignmentRow(int pid, Macro? macro)
    {
        var row = Assignments.FirstOrDefault(r => r.Alt.Pid == pid);
        if (row is not null) row.AssignedMacro = macro;
    }

    // ---------- Dispatcher helper ----------

    private static void RaiseUI(Action callback)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) callback();
        else disp.BeginInvoke(callback);
    }

    // ---------- INPC ----------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
