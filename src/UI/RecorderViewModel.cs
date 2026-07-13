using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

    // ---------- Task 8: next-due countdown (proof-of-life for a sleeping scheduler) ----------

    // Monotonic (Environment.TickCount64) deadline per KeepAlive row — mirrors
    // AssignmentRunner's own clock choice (never wall-clock; a DST shift or clock
    // adjustment must not make the countdown lie). Only KeepAlive rows are tracked;
    // an Active row is removed the moment it stops being KeepAlive.
    private readonly Dictionary<AssignmentRow, long> _keepAliveDueAtMs = new();
    private readonly DispatcherTimer _keepAliveCountdownTimer;

    public RecorderViewModel(PluginRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        Macros = new ObservableCollection<Macro>();
        StatusLines = new ObservableCollection<string>();
        Assignments = new ObservableCollection<AssignmentRow>();
        SavedRoutines = new ObservableCollection<Recipe>();
        Toasts = new ObservableCollection<ToastItem>();

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

        // Task 8 role presets. Both only ever touch AssignmentRow.Role — never
        // AssignedMacro — so backgrounding an alt is non-destructive: its macro
        // rides along untouched and flipping back to Active resumes farming
        // without re-picking anything. Role changes propagate to PluginRuntime
        // (and reseed the next-due countdown) via OnAssignmentRowPropertyChanged.
        SetAllActiveCommand = new RelayCommand(() =>
        {
            // Critical 2 belt-and-braces: skip rows with no macro. PluginRuntime
            // would coerce them back to KeepAlive at PLAY-time regardless (via
            // Assignment.ResolveRole), so setting Role here would only leave the row
            // DISPLAYING "ACTIVE" next to a "Keep-alive (Space)" macro chip — a lie
            // about what PLAY will actually do.
            foreach (var row in Assignments.Where(r => r.HasMacro)) row.Role = CadenceRole.Active;
        });

        FocusOneCommand = new RelayCommand<AssignmentRow>(focused =>
        {
            // Critical 2: never promote a macro-less row to Active, even though the
            // FOCUS button is also disabled for such rows in XAML (belt-and-braces —
            // this guard holds even if the command is ever invoked another way).
            if (focused is null || !focused.HasMacro) return;
            foreach (var row in Assignments)
                row.Role = row == focused ? CadenceRole.Active : CadenceRole.KeepAlive;
        });

        // Routine (recipe/loadout) run surface — targets exactly the alts checked
        // via AssignmentRow.IsCheckedForRoutine, independent of macro assignment.
        // CanExecute already gates on a selected routine + ≥1 checked alt, but the
        // execute body re-checks (RecipesWindow.OnRunClicked does the same for its
        // own picker) — RunRecipe itself also refuses an empty list, but checking
        // here avoids even attempting the call if Execute is ever invoked directly.
        RunRoutineCommand = new RelayCommand(
            () =>
            {
                if (SelectedRoutine is not { } routine) return;
                var targets = Assignments.Where(r => r.IsCheckedForRoutine).Select(r => r.Alt).ToList();
                if (targets.Count == 0) return;
                _runtime.RunRecipe(routine, targets);
            },
            () => SelectedRoutine is not null && Assignments.Any(r => r.IsCheckedForRoutine) && !IsRunnerActive);

        // Single RUN/STOP toggle for the routine strip button — mirrors
        // TogglePlayStopCommand. Stopping always routes through TriggerAbort
        // (the same Esc/Ctrl+Shift+A abort surface) rather than calling
        // _activeRecipeRunner.Abort() directly, because a routine can be a
        // looping recipe (_runner active) OR a loadout mid-position
        // (_sequence active) — TriggerAbort's Abort case already covers both.
        ToggleRoutineRunCommand = new RelayCommand(
            () =>
            {
                if (IsRoutineRunning)
                {
                    _runtime.TriggerAbort();
                    return;
                }
                if (RunRoutineCommand.CanExecute(null)) RunRoutineCommand.Execute(null);
            },
            () => IsRoutineRunning || (SelectedRoutine is not null && Assignments.Any(r => r.IsCheckedForRoutine)));

        SelectAllRoutineAltsCommand = new RelayCommand(() =>
        {
            foreach (var row in Assignments) row.IsCheckedForRoutine = true;
        });
        SelectNoneRoutineAltsCommand = new RelayCommand(() =>
        {
            foreach (var row in Assignments) row.IsCheckedForRoutine = false;
        });

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

        // Task 8 next-due countdown: low-frequency tick only — this is a
        // minutes-scale countdown (11-17 min fire intervals), not a stopwatch.
        // A per-second timer would be wasted UI-thread churn for no visible gain.
        _keepAliveCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _keepAliveCountdownTimer.Tick += (_, _) => RefreshKeepAliveCountdowns();
        _keepAliveCountdownTimer.Start();

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
        // RecipeRunner's positioning/loop-setup failures (missing macro, all
        // alts failed to position) never reach AssignmentProgressed or
        // SequenceProgressed — RunRecipe only surfaces them via Log()/
        // StatusLogged (see PluginRuntime.RunRecipe's `runner.Progress +=`).
        // Recognize those two failure phases by the "(Phase)." suffix that
        // handler renders and toast them too, so a recipe that can't even
        // start isn't silently buried in the activity log. Log() already
        // dispatches to the UI thread before raising StatusLogged, so no
        // RaiseUI wrap needed here (mirrors LogStatus above).
        _runtime.StatusLogged += line =>
        {
            if (line.Contains($"({nameof(RecipeRunPhase.MacroMissing)}).", StringComparison.Ordinal)
                || line.Contains($"({nameof(RecipeRunPhase.AllAltsFailed)}).", StringComparison.Ordinal))
            {
                ShowError(line);
            }
        };
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
            // Position/one-shot refusal path (e.g. the off-screen-resize refusal
            // from MacroPlayer.EnsureClientSize during a recipe's positioning
            // step) — SequencePlayer emits a Refused-phase progress event with
            // Reason set alongside its per-alt AltOutcome. This event can fire
            // from a background thread (SequencePlayer.PlayAsync runs off the UI
            // thread), so ShowError is explicitly dispatched via RaiseUI.
            if (p.Phase == SequencePhase.Refused && !string.IsNullOrWhiteSpace(p.Reason))
            {
                var reason = p.Reason;
                RaiseUI(() => ShowError(reason));
            }
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
        _runtime.AssignmentProgressed += p => RaiseUI(() =>
        {
            RunnerProgress = p;
            // Round-robin loop / recipe-loop refusal path — same off-screen-resize
            // (and similar preflight) refusals surface here for the terminal
            // Loop/KeepAlive step. Already inside RaiseUI, so ShowError runs on
            // the UI thread.
            // Warning rides the same toast path: the unschedulable-alt notice
            // (emitted once up front when a keep-alive's interval can't outrun the
            // worst-case Active pass) and the 3+-consecutive-focus-failure notice
            // both deserve the same visibility as a refusal — reuse ShowError's
            // themed toast rather than inventing a second notification path.
            if ((p.Phase == AssignmentPhase.Refused || p.Phase == AssignmentPhase.Warning)
                && !string.IsNullOrWhiteSpace(p.Reason))
                ShowError(p.Reason);
            // Task 8 proof-of-life: the Space actually landed for a keep-alive alt —
            // reseed that row's countdown to a fresh full interval. Without this the
            // countdown would only ever count down from the DispatcherTimer's own
            // 30s-granularity view of elapsed time and never resync with what the
            // scheduler actually did (e.g. a focus-retry backoff shortens the real
            // next deadline).
            if (p.Phase == AssignmentPhase.Playing && p.Current?.Role == CadenceRole.KeepAlive)
            {
                var row = Assignments.FirstOrDefault(r => r.Alt.Pid == p.Current.Alt.Pid);
                if (row is not null) SeedKeepAliveDue(row);
            }
        });

        // Ctrl+Shift+L global chord — bridges to the same RunRoutineCommand the
        // RUN button uses, so the chord no-ops exactly when the button would be
        // disabled (no routine selected, no alt checked, or a run already active).
        // The STOP half of the chord never reaches here — PluginRuntime.OnHotkey
        // intercepts it and aborts directly, so this handler only ever starts.
        _runtime.RunRoutineRequested += () => RaiseUI(() =>
        {
            if (RunRoutineCommand.CanExecute(null)) RunRoutineCommand.Execute(null);
        });

        // Mirrors PluginRuntime.IsRecipeRunning into IsRoutineRunning so the
        // routine strip's RUN/STOP toggle (button + Ctrl+Shift+L label) reflects
        // both looping recipes and loadouts — RecipeRunner.RunAsync is "running"
        // for both, a loadout just returns after its position steps instead of
        // looping.
        _runtime.RecipeRunningChanged += () => RaiseUI(() =>
        {
            IsRoutineRunning = _runtime.IsRecipeRunning;
            RaiseRoutineCommandStates();
        });

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

        // Seed the routine picker so it's populated even before the window's
        // first Activated fires (RefreshRoutines is re-called there too, to
        // pick up recipes saved/edited/deleted elsewhere while this window is open).
        RefreshRoutines();
    }

    // ---------- Collections ----------

    public ObservableCollection<Macro> Macros { get; }
    public ObservableCollection<string> StatusLines { get; }
    public ObservableCollection<AssignmentRow> Assignments { get; }
    public ObservableCollection<Recipe> SavedRoutines { get; }
    public ObservableCollection<ToastItem> Toasts { get; }

    // ---------- Commands ----------

    public ICommand RecordCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleCompactCommand { get; }
    public ICommand MarkMacroActiveCommand { get; }
    public ICommand ToggleAltAssignmentCommand { get; }
    public ICommand ResetAssignmentsCommand { get; }
    public ICommand SetAllActiveCommand { get; }
    public ICommand FocusOneCommand { get; }
    public ICommand RunRoutineCommand { get; }
    public ICommand ToggleRoutineRunCommand { get; }
    public ICommand SelectAllRoutineAltsCommand { get; }
    public ICommand SelectNoneRoutineAltsCommand { get; }
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

    // ---------- Routine (recipe/loadout) run surface ----------

    private Recipe? _selectedRoutine;
    /// <summary>The saved recipe/loadout picked in the ASSIGNMENTS pane's routine
    /// strip. RUN targets whichever alts have <see cref="AssignmentRow.IsCheckedForRoutine"/>
    /// checked — independent of the macro-assignment pairing above.</summary>
    public Recipe? SelectedRoutine
    {
        get => _selectedRoutine;
        set
        {
            if (Equals(_selectedRoutine, value)) return;
            _selectedRoutine = value;
            OnPropertyChanged();
            RaiseRoutineCommandStates();
        }
    }

    private bool _isRoutineRunning;
    /// <summary>True while a routine (a looping recipe OR a loadout) is active
    /// via <see cref="PluginRuntime.RunRecipe"/> — mirrors
    /// <see cref="PluginRuntime.IsRecipeRunning"/>, kept in sync through
    /// RecipeRunningChanged. Drives the routine strip's RUN/STOP toggle, same
    /// shape as IsRunnerActive/PlayStopButtonLabel for the plain assignment loop.
    /// A loadout mid-flight (position steps, no loop) still needs a way to stop,
    /// not just a looping recipe — that's why this isn't gated on IsRunnerActive.</summary>
    public bool IsRoutineRunning
    {
        get => _isRoutineRunning;
        private set
        {
            if (_isRoutineRunning == value) return;
            _isRoutineRunning = value;
            OnPropertyChanged();
        }
    }

    /// <summary>"STOP" while a routine (recipe or loadout) is running, else "RUN". Drives the routine strip's toggle button label.</summary>
    public string RoutineRunButtonLabel => IsRoutineRunning ? "STOP" : "RUN";

    /// <summary>Re-query CanExecute on the routine commands and re-notify the
    /// button label. Plain RelayCommand doesn't auto-requery (see
    /// OnAssignmentRowPropertyChanged for the same pattern) — this is the one
    /// place both routine commands' gating conditions (SelectedRoutine,
    /// checked alts, IsRoutineRunning) get re-evaluated from.</summary>
    private void RaiseRoutineCommandStates()
    {
        (RunRoutineCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ToggleRoutineRunCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(RoutineRunButtonLabel));
    }

    /// <summary>Re-read every saved recipe/loadout from disk. Called on window
    /// Activated so a routine saved/edited/deleted elsewhere (Recipes library,
    /// recipe editor) shows up here without a restart. Preserves the current
    /// selection by id across the reload when it still exists.</summary>
    public void RefreshRoutines()
    {
        var previousId = _selectedRoutine?.Id;
        var recipes = _runtime.Recipes.LoadAll().Recipes
            .OrderBy(r => r.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SavedRoutines.Clear();
        foreach (var recipe in recipes) SavedRoutines.Add(recipe);
        SelectedRoutine = previousId is null ? null : SavedRoutines.FirstOrDefault(r => r.Id == previousId);
    }

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
            (RunRoutineCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    /// <summary>Render a single macro as a standalone AutoHotkey (v1 or v2) script and
    /// write it to <paramref name="path"/>. Best-effort port — see
    /// <see cref="AutoHotkeyExporter"/> for the caveats baked into the file header.</summary>
    public void ExportMacroAsAutoHotkey(Macro macro, AhkVersion version, string path)
    {
        if (macro is null) return;
        var script = AutoHotkeyExporter.Export(macro, version);
        File.WriteAllText(path, script); // .NET default is UTF-8 without a BOM, matching ExportMacros above.
        LogStatus($"Exported '{macro.Name ?? "(unnamed)"}' → {Path.GetFileName(path)} (AutoHotkey {(version == AhkVersion.V1 ? "v1" : "v2")})");
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

    // ---------- Toasts: transient, themed surfacing for playback errors/refusals ----------

    private static readonly TimeSpan ToastDedupWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ToastLifetime = TimeSpan.FromSeconds(5);

    private string? _lastToastMessage;
    private DateTimeOffset _lastToastAt;

    /// <summary>
    /// Surface a playback refusal/error as a transient, auto-dismissing toast —
    /// callers are the Refused-phase handlers above plus the recipe-failure
    /// StatusLogged filter. Must run on the UI thread (callers already dispatch
    /// via RaiseUI where the source event isn't already on it) since it mutates
    /// the bound <see cref="Toasts"/> collection and starts a DispatcherTimer.
    /// Dedup: see <see cref="ToastDedup.ShouldSuppress"/> — a loop that refuses
    /// on the same reason every cycle must not spam identical toasts.
    /// </summary>
    public void ShowError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var now = DateTimeOffset.UtcNow;
        if (ToastDedup.ShouldSuppress(_lastToastMessage, _lastToastAt, message, now, ToastDedupWindow))
            return;
        _lastToastMessage = message;
        _lastToastAt = now;

        var toast = new ToastItem(message, ToastSeverity.Error, now);
        Toasts.Add(toast);

        var timer = new DispatcherTimer { Interval = ToastLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toasts.Remove(toast);
        };
        timer.Start();
    }

    // ---------- Assignment helpers ----------

    private void AddAssignmentRow(AccountRegistry.AccountInfo alt)
    {
        if (Assignments.Any(r => r.Alt.Pid == alt.Pid)) return;
        var existing = _runtime.GetAssignment(alt.Pid);
        var row = new AssignmentRow(alt) { AssignedMacro = existing };
        row.PropertyChanged += OnAssignmentRowPropertyChanged;
        Assignments.Add(row);
        // Seed the row's displayed Role so what it SHOWS on first paint agrees with
        // what PLAY ASSIGNMENTS will actually do — CRITICAL 1 fix: this must NOT
        // publish a runtime override. The old code set row.Role directly (after
        // subscribing PropertyChanged above), which synchronously fired
        // SyncRoleToRuntime and froze this alt on a KeepAlive override before the
        // user ever assigned a macro — so assigning one later and pressing PLAY
        // never farmed it. SeedRowRole routes through the same
        // _isRederivingRole-guarded path RefreshAssignmentRow uses below.
        SeedRowRole(row, existing);
    }

    private void RemoveAssignmentRow(int pid)
    {
        var row = Assignments.FirstOrDefault(r => r.Alt.Pid == pid);
        if (row is null) return;
        row.PropertyChanged -= OnAssignmentRowPropertyChanged;
        Assignments.Remove(row);
        _keepAliveDueAtMs.Remove(row);
    }

    /// <summary>Routine-checkbox toggles change RunRoutineCommand's and
    /// ToggleRoutineRunCommand's CanExecute (both require ≥1 checked alt);
    /// plain RelayCommand doesn't auto-requery. Role toggles (Task 8) push the
    /// new role down to PluginRuntime — see <see cref="SyncRoleToRuntime"/> below.</summary>
    private void OnAssignmentRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssignmentRow.IsCheckedForRoutine))
            RaiseRoutineCommandStates();
        else if (e.PropertyName == nameof(AssignmentRow.Role) && sender is AssignmentRow row)
            SyncRoleToRuntime(row);
    }

    private void RefreshAssignmentRow(int pid, Macro? macro)
    {
        var row = Assignments.FirstOrDefault(r => r.Alt.Pid == pid);
        if (row is null) return;
        row.AssignedMacro = macro;
        // CRITICAL 1 fix: re-derive the displayed Role whenever the macro pairing
        // changes. SeedRowRole reads Assignment.ResolveRole with whatever override
        // (if any) is on record — an explicit user choice still wins outright, but a
        // fresh alt with no override flips to Active the instant it gets a macro
        // (so PLAY actually farms it), and flips back to KeepAlive the instant the
        // macro is cleared (Critical 2's display-side mirror).
        SeedRowRole(row, macro);
    }

    // ---------- Task 8: role -> runtime + next-due countdown plumbing ----------

    /// <summary>
    /// True while VM-internal code (<see cref="SeedRowRole"/>) is pushing a
    /// DERIVED role onto a row's <see cref="AssignmentRow.Role"/> — as opposed to a
    /// genuine user gesture (ComboBox pick, FOCUS, the presets). Both paths set the
    /// same property and fire the same PropertyChanged event, so this is the only
    /// way <see cref="SyncRoleToRuntime"/> can tell them apart. CRITICAL 1 fix: an
    /// override must mean "the user explicitly chose this," nothing else — without
    /// this guard, every derived seed/re-derive (row creation, macro assigned or
    /// cleared) would itself publish an override and permanently freeze the row.
    /// </summary>
    private bool _isRederivingRole;

    /// <summary>Push a row's Role into PluginRuntime (so PLAY ASSIGNMENTS actually
    /// honors it) and keep the next-due countdown in sync — seeded when the row
    /// becomes KeepAlive, dropped the moment it stops being one.</summary>
    private void SyncRoleToRuntime(AssignmentRow row)
    {
        // Only a genuine user gesture publishes a runtime override — see
        // _isRederivingRole's doc for why this guard exists and what breaks without
        // it. The countdown bookkeeping below still runs unconditionally: whether
        // this is a derived change or a genuine one, the row's actual role in
        // reality just changed, and the proof-of-life display must track it.
        if (!_isRederivingRole) _runtime.SetRoleOverride(row.Alt.Pid, row.Role);
        if (row.Role == CadenceRole.KeepAlive) SeedKeepAliveDue(row);
        else _keepAliveDueAtMs.Remove(row);
    }

    /// <summary>
    /// Set a row's DISPLAYED Role from <see cref="Assignment.ResolveRole"/> —
    /// exactly the same pure function PluginRuntime consults at PLAY-time — so the
    /// row can never show something PLAY wouldn't actually do. Used at row
    /// creation and whenever a row's macro pairing changes; never publishes a NEW
    /// runtime override (an existing one, if any, is read via
    /// <see cref="PluginRuntime.GetRoleOverride"/> and still wins outright — this
    /// only reflects it, it doesn't create one).
    /// </summary>
    private void SeedRowRole(AssignmentRow row, Macro? macro)
    {
        var role = Assignment.ResolveRole(macro, _runtime.GetRoleOverride(row.Alt.Pid));
        _isRederivingRole = true;
        try { row.Role = role; }
        finally { _isRederivingRole = false; }
    }

    /// <summary>(Re)start a row's countdown at a fresh full interval — called both
    /// when a row first becomes KeepAlive and whenever the scheduler actually taps
    /// it (see the AssignmentProgressed handler above), so the displayed countdown
    /// tracks reality rather than just decaying blindly from the moment of toggle.</summary>
    private void SeedKeepAliveDue(AssignmentRow row)
    {
        var intervalMs = (long)KeepAliveIntervals.For(row.Alt.PlaceId, row.Alt.PlaceName, _prefs).TotalMilliseconds;
        _keepAliveDueAtMs[row] = Environment.TickCount64 + intervalMs;
        row.SetNextDue(TimeSpan.FromMilliseconds(intervalMs));
    }

    /// <summary>DispatcherTimer tick (30s — this is a minutes-scale countdown, not
    /// a stopwatch): recompute every tracked row's remaining time from its stored
    /// deadline. A row that stopped being KeepAlive (flipped back to Active) is
    /// dropped here too, in case something mutated Role without going through
    /// <see cref="SyncRoleToRuntime"/> (defensive; today nothing does).</summary>
    private void RefreshKeepAliveCountdowns()
    {
        if (_keepAliveDueAtMs.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var row in _keepAliveDueAtMs.Keys.ToList())
        {
            if (row.Role != CadenceRole.KeepAlive) { _keepAliveDueAtMs.Remove(row); continue; }
            row.SetNextDue(TimeSpan.FromMilliseconds(Math.Max(0, _keepAliveDueAtMs[row] - now)));
        }
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
