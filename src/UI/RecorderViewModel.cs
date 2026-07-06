using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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

        StackWindowsCommand = new RelayCommand(() => _runtime.ArrangeStack(), CanArrange);
        GridWindowsCommand = new RelayCommand(() => _runtime.ArrangeGrid(), CanArrange);
        RestoreWindowsCommand = new RelayCommand(() => _runtime.ArrangeRestore(), CanArrange);

        PlayAssignmentsCommand = new RelayCommand(
            () => _runtime.TriggerPlayAssignments(),
            () => Assignments.Count > 0 && !IsRunnerActive);

        StopAssignmentsCommand = new RelayCommand(
            () => _runtime.TriggerAbort(),
            () => IsRunnerActive);

        // Single PLAY/STOP toggle — bound to the unified button. When runner is
        // active, this calls abort (same as Esc); otherwise starts the loop.
        // Prevents the rc11 trap where hitting PLAY again started a NEW round
        // instead of stopping the current one.
        TogglePlayStopCommand = new RelayCommand(
            () =>
            {
                if (IsRunnerActive)
                {
                    _runtime.TriggerAbort();
                    return;
                }
                var mismatched = Assignments.Where(r => r.HasGameMismatch).ToList();
                if (mismatched.Count > 0)
                {
                    LogStatus($"Note: {mismatched.Count} pairing{(mismatched.Count == 1 ? "" : "s")} "
                        + $"target a different game than the macro was recorded in "
                        + $"({string.Join(", ", mismatched.Select(r => r.Alt.DisplayName))}). Playing anyway.");
                }
                _runtime.TriggerPlayAssignments();
            },
            () => IsRunnerActive || Assignments.Count > 0);

        // Initialize pin state from saved prefs based on current compact mode.
        _isTopmost = _isCompact ? _prefs.TopmostInCompactMode : _prefs.TopmostInFullMode;

        // Hydrate keyboard-only toggle from prefs onto the runtime so the first
        // recording obeys the saved preference.
        _runtime.RecordKeyboardOnly = _prefs.KeyboardOnlyRecording;

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
        _runtime.StatusLogged += LogStatus;
        _runtime.MacrosChanged += () =>
        {
            _allMacros = _runtime.Store.LoadAll().Macros;
            RefreshMacroList();
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

        // Account add/remove updates rows live. Also refresh Stack/Grid CanExecute
        // here — this (not RunnerProgress) is the actual trigger for "no alts
        // running" per spec; RelayCommand doesn't auto-requery like RelayCommand<T>.
        _runtime.Accounts.AccountAdded += (_, info) => RaiseUI(() =>
        {
            AddAssignmentRow(info);
            RefreshMacroList(); // current-games set changed — re-band the library
            (StackWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GridWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestoreWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        });
        _runtime.Accounts.AccountRemoved += (_, info) => RaiseUI(() =>
        {
            RemoveAssignmentRow(info.Pid);
            RefreshMacroList();
            (StackWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GridWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestoreWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        });

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
    public ICommand TogglePlayStopCommand { get; }
    public ICommand StackWindowsCommand { get; }
    public ICommand GridWindowsCommand { get; }
    public ICommand RestoreWindowsCommand { get; }

    private bool CanArrange() => _runtime.Accounts.Snapshot().Count > 0;

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

    // ---------- v0.2: Keyboard-only recording toggle ----------

    /// <summary>
    /// When true (default), mouse events are dropped during recording.
    /// Persisted across sessions via <see cref="UserPreferences"/>. Inverse
    /// (RecordMouseToo) exposed for the warning binding.
    /// </summary>
    public bool IsKeyboardOnlyRecording
    {
        get => _runtime.RecordKeyboardOnly;
        set
        {
            if (_runtime.RecordKeyboardOnly == value) return;
            _runtime.RecordKeyboardOnly = value;
            _prefs.KeyboardOnlyRecording = value;
            _prefs.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowMouseRecordingWarning));
        }
    }

    /// <summary>True when mouse recording is enabled (inverse of IsKeyboardOnlyRecording). Drives the yellow stacking-warning visibility.</summary>
    public bool ShowMouseRecordingWarning => !_runtime.RecordKeyboardOnly;

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
        // Build MacroId → ordered list of paired alt names. Multiple alts can share
        // the same macro under the one-to-many pairing model; collapse the list to
        // a single comma-joined string so the per-card chip can display it as
        // "→ alt1, alt2, alt3" without needing nested ItemsControls.
        var altsByMacroId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in _runtime.AllAssignments)
        {
            if (kv.Value is null) continue;
            var row = Assignments.FirstOrDefault(r => r.Alt.Pid == kv.Key);
            if (row is null) continue;
            if (!altsByMacroId.TryGetValue(kv.Value.Id, out var list))
            {
                list = new List<string>();
                altsByMacroId[kv.Value.Id] = list;
            }
            list.Add(row.Alt.DisplayName);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (macroId, names) in altsByMacroId)
        {
            // Stable display order — sort alphabetically so the chip doesn't reorder
            // on every reassignment.
            names.Sort(StringComparer.OrdinalIgnoreCase);
            map[macroId] = string.Join(", ", names);
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
            OnPropertyChanged(nameof(CurrentRunnerAltPid));
            // Refresh command CanExecute
            (PlayAssignmentsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopAssignmentsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (TogglePlayStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StackWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GridWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestoreWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(PlayStopButtonLabel));
        }
    }

    /// <summary>
    /// PID of the alt currently being visited by the runner (-1 when no runner active).
    /// AssignmentRow XAML binds against this for "currently playing" row highlight.
    /// </summary>
    public int CurrentRunnerAltPid => _runnerProgress?.Current?.Alt.Pid ?? -1;

    public bool IsRunnerActive => _runnerProgress is { Phase: not AssignmentPhase.Stopped };

    /// <summary>"STOP" when a round-robin is running, otherwise "PLAY ASSIGNMENTS". Drives the single toggle button label.</summary>
    public string PlayStopButtonLabel => IsRunnerActive ? "STOP" : "PLAY ASSIGNMENTS";

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

    // ---------- v0.6: game-aware library ----------

    private IReadOnlyList<Macro> _allMacros = Array.Empty<Macro>();

    private bool _isPlayingNowFilter;
    /// <summary>PLAYING NOW chip: hard-hide game-scoped macros for games no alt is running.</summary>
    public bool IsPlayingNowFilter
    {
        get => _isPlayingNowFilter;
        set
        {
            if (_isPlayingNowFilter == value) return;
            _isPlayingNowFilter = value;
            OnPropertyChanged();
            RefreshMacroList();
        }
    }

    /// <summary>Distinct place ids across running alts (0 = unknown, excluded).</summary>
    private IReadOnlySet<long> CurrentPlaceIds()
        => Assignments.Select(r => r.Alt.PlaceId).Where(id => id > 0).ToHashSet();

    /// <summary>Rebuild the visible macro list: playing-now filter, then game-band + recency sort.</summary>
    private void RefreshMacroList()
    {
        var current = CurrentPlaceIds();
        var visible = _isPlayingNowFilter
            ? MacroGameFilter.FilterPlayingNow(_allMacros, current)
            : _allMacros;

        Macros.Clear();
        foreach (var m in MacroGameFilter.Sort(visible, current))
            Macros.Add(m);
        OnPropertyChanged(nameof(HasMacros));
        OnPropertyChanged(nameof(HasNoMacros));
        OnPropertyChanged(nameof(StatusMeta));
    }

    /// <summary>Flip the per-macro "usable in all games" override and persist it.</summary>
    public void ToggleAllGames(Macro macro)
    {
        if (macro is null) return;
        _runtime.Store.Save(macro with { AllGames = !macro.AllGames });
        _runtime.RaiseMacrosChanged();
    }

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

    // ---------- v0.5: macro import / export ----------

    /// <summary>Write the given macros to a portable bundle file at <paramref name="path"/>.</summary>
    public void ExportMacros(IReadOnlyList<Macro> macros, string path)
    {
        if (macros is null || macros.Count == 0) return;
        var json = MacroBundle.Serialize(macros, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        File.WriteAllText(path, json);
        LogStatus(macros.Count == 1
            ? $"Exported '{macros[0].Name ?? "(unnamed)"}' → {Path.GetFileName(path)}"
            : $"Exported {macros.Count} macros → {Path.GetFileName(path)}");
    }

    /// <summary>
    /// Import macros from bundle (or bare single-macro) files. Imports are
    /// additive: every imported macro gets a fresh id and a deduped name, so an
    /// import can never overwrite an existing macro. Per-entry failures land in
    /// the activity log without sinking the rest of the batch.
    /// </summary>
    public void ImportMacros(IEnumerable<string> filePaths)
    {
        var takenNames = new HashSet<string>(
            Macros.Select(m => m.Name).Where(n => !string.IsNullOrWhiteSpace(n))!,
            StringComparer.OrdinalIgnoreCase);
        var imported = 0;

        foreach (var path in filePaths)
        {
            MacroBundle.ParseResult result;
            try
            {
                result = MacroBundle.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                LogStatus($"Import failed for {Path.GetFileName(path)}: {ex.Message}");
                continue;
            }

            foreach (var failure in result.Failures)
                LogStatus($"Skipped {failure.Label} in {Path.GetFileName(path)}: {failure.Reason}");

            foreach (var macro in result.Macros)
            {
                _runtime.Store.Save(MacroBundle.PrepareForImport(macro, takenNames));
                imported++;
            }
        }

        if (imported > 0) _runtime.RaiseMacrosChanged();
        LogStatus($"Imported {imported} macro{(imported == 1 ? "" : "s")}.");
    }

    private void LogStatus(string line)
    {
        StatusLines.Insert(0, line);
        while (StatusLines.Count > StatusLogLimit) StatusLines.RemoveAt(StatusLines.Count - 1);
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
