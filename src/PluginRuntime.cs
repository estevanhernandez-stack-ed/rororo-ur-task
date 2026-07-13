using System.IO;
using System.Windows;
using Labs626.UrTask.Hotkeys;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask;

/// <summary>
/// Owns all the moving parts (gRPC client, account registry, foreground
/// watcher, macro recorder + player + store, auto-stop coordinator,
/// hotkeys) and wires the Ctrl+Shift+R / Ctrl+Shift+P / Ctrl+Shift+A (+ bare Esc
/// while playing) hotkey handlers. The ViewModel
/// observes this runtime — runtime knows nothing about UI.
///
/// Public events surface state changes for the VM to bind against. All
/// events fire on the hotkey thread (or wherever the underlying
/// component raises them); the VM marshals to the UI dispatcher.
/// </summary>
internal sealed class PluginRuntime : IAsyncDisposable
{
    public const string PluginId = "626labs.ur-task";

    public AccountRegistry Accounts { get; }
    public MacroStore Store { get; }
    public RecipeStore Recipes { get; }

    private readonly ForegroundWatcher _foreground;
    private readonly MacroRecorder _recorder;
    private readonly MacroPlayer _player;
    private readonly SequencePlayer _sequence;
    private readonly AssignmentRunner _runner;
    private readonly ClaimFile _claim = new();
    private readonly HotkeyService _hotkeys;
    private readonly PluginClient _client;
    private readonly PluginHost.IWindowMetrics _metrics = new PluginHost.WindowMetrics();
    private readonly PluginHost.WindowArrangeService _arranger;

    private readonly CancellationTokenSource _bridgeCts = new();
    private Ipc.MacroRunnerServer? _bridgeServer;

    private AccountRegistry.AccountInfo? _recordingBoundAccount;
    private IntPtr _recordingAnchorHwnd = IntPtr.Zero;
    private (int W, int H)? _recordingClientSize;
    private bool? _recordingMaximized;
    private Macro? _lastMacro;
    private bool _sequenceActive;
    private volatile bool _playerActive;
    private RecipeRunner? _activeRecipeRunner;

    // ---------- Assignment state ----------
    private readonly Dictionary<int, Macro?> _assignments = new(); // key: alt.Pid; value: assigned Macro or null for keep-alive

    // Task 8: UI-driven cadence-role override, keyed by alt.Pid — independent of
    // _assignments above (macro). Absent an entry, role-building falls back to
    // Assignment.WithDerivedRole (macro present -> Active, none -> KeepAlive), the
    // pre-Task-8 rule. Present, it wins outright — this is what makes backgrounding
    // an alt (Active -> KeepAlive) non-destructive: the macro in _assignments is
    // never touched, so flipping the override back to Active resumes farming with
    // nothing re-picked.
    //
    // ConcurrentDictionary, not plain Dictionary: SetRoleOverride is written from
    // the UI thread (ComboBox/preset commands) and GetRoleOverride is read from the
    // hotkey thread (OnHotkey building the PLAY assignment list) — an unsynchronized
    // Dictionary racing a concurrent read/write is undefined behavior. (The same
    // shape of race pre-dates this branch on _assignments below; that one is
    // untouched here — this fixes only the NEW race this branch introduced.)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, CadenceRole> _roleOverrides = new();

    public RecordMode CurrentRecordMode { get; set; } = RecordMode.PerWindow;

    /// <summary>
    /// When true (default), all mouse events are dropped during recording —
    /// the recorded macro is keyboard-only. See MacroRecorder.Start for the
    /// rationale (absolute-screen coords don't survive un-stacked alt windows).
    /// </summary>
    public bool RecordKeyboardOnly { get; set; } = true;

    public PluginRuntime()
    {
        Accounts = new AccountRegistry();
        Accounts.AccountAdded += (_, info) =>
            Log($"account launched: {info.DisplayName} (user {info.RobloxUserId}, pid {info.Pid})");
        Accounts.AccountRemoved += (_, info) =>
        {
            Log($"account exited: {info.DisplayName} (user {info.RobloxUserId}, pid {info.Pid})");
            // Minor fix: Windows recycles PIDs, so a fresh alt launched later can
            // reuse a dead alt's pid — an un-pruned override would silently hand
            // the new alt a role choice that was never actually made for it.
            _roleOverrides.TryRemove(info.Pid, out CadenceRole _);
        };
        _foreground = new ForegroundWatcher(Accounts);
        _recorder = new MacroRecorder();
        _player = new MacroPlayer(_foreground, _metrics);
        _sequence = new SequencePlayer(_player, _foreground);
        _runner = new AssignmentRunner(_player, _foreground);
        _arranger = new PluginHost.WindowArrangeService(Accounts, _metrics, _foreground);
        // Assigned before the _sequence.Progress subscription below captures it, so the
        // closure sees a definitely-assigned field (the event can't fire until playback).
        _hotkeys = new HotkeyService();

        // Action bridge: accept RunMacro requests from sibling plugins (Ur-OCR).
        // Gated by the user preference; default on. The macro source is the same
        // on-disk library the recorder/sequence player use.
        if (UI.UserPreferences.Load().AcceptPluginRunRequests)
        {
            var invoker = new Ipc.MacroRunInvoker(new Macros.MacroStore(), Accounts, _foreground, _sequence);
            _bridgeServer = new Ipc.MacroRunnerServer(invoker);
            _ = _bridgeServer.RunAcceptLoopAsync(_bridgeCts.Token);
        }

        _sequence.Progress += (_, p) =>
        {
            SequenceProgressed?.Invoke(p);
            // Mirrors the AssignmentRunner.Progress handler below: a per-alt
            // refusal (e.g. the client-size resize refusal from
            // MacroPlayer.EnsureClientSize during a recipe's positioning step)
            // otherwise only reaches the toast via RecorderViewModel's
            // SequenceProgressed handler — it never lands in the activity log.
            // Log() explicitly so the reason survives after the toast fades.
            if (p.Phase == SequencePhase.Refused && !string.IsNullOrWhiteSpace(p.Reason))
                Log($"Sequence playback refused for {p.CurrentAlt?.DisplayName ?? "(unknown alt)"}: {p.Reason}");
            // Track whether a sequence is active so _player.Ended doesn't clear
            // the badge prematurely between alts.
            if (p.Phase == SequencePhase.Focusing && p.Index == 0)
            {
                _sequenceActive = true;
                _hotkeys.EnableAbortKey(); // Esc aborts for the whole sequence, incl. inter-alt delays
            }
            else if (p.Phase == SequencePhase.Done || p.Phase == SequencePhase.Aborted)
            {
                _sequenceActive = false;
                RaiseUI(() => CurrentlyPlayingMacroChanged?.Invoke(null));
                RefreshAbortKey();
            }
        };

        _runner.Progress += (_, p) =>
        {
            RaiseUI(() => AssignmentProgressed?.Invoke(p));
            if (p.Phase == AssignmentPhase.Refused)
                Log($"Assignment playback refused for {p.Current?.Alt.DisplayName ?? "(unknown alt)"}: {p.Reason}");
            // Warning covers both the unschedulable-alt notice (emitted once up front
            // in AssignmentRunner.RunAsync) and the 3+-consecutive-focus-failure
            // notice (emitted mid-run from ServiceKeepAliveAsync) — both already carry
            // the alt's display name inside Reason itself, so log it as-is rather than
            // re-wrapping with "for {alt}: " like the Refused case above. Previously
            // Warning reached no user surface at all (only Refused was logged here).
            else if (p.Phase == AssignmentPhase.Warning && !string.IsNullOrWhiteSpace(p.Reason))
                Log(p.Reason);
            // Started fires exactly once, only for the RunAsync call that actually
            // won the runner's single-flight CompareExchange guard — a losing
            // concurrent call (e.g. a still-unwinding prior run racing a recipe's
            // preemption) emits nothing. Publish the claim HERE, off the runner's
            // own confirmation that it is really about to service these alts, not
            // at the caller's call site before the race is even decided — that
            // ordering used to let a losing call's claim outlive the run it was
            // never attached to (see TryStartClaim's doc for the full story).
            else if (p.Phase == AssignmentPhase.Started && p.AllAssignments is not null)
                TryStartClaim(p.AllAssignments);
            // Stopped fires exactly once, right before AssignmentRunner.RunAsync's own
            // finally clears _activeCts — same moment for every termination path
            // (toggle-stop, Esc/Ctrl+Shift+A abort, host-lost, recipe preemption all
            // route through _runner.Abort() cancelling the loop's token). Release the
            // claim here instead of duplicating a Stop() call at every abort call site.
            else if (p.Phase == AssignmentPhase.Stopped)
                TryStopClaim();
        };

        _ = new AutoStopCoordinator(_player, Accounts);
        Store = new MacroStore();
        Recipes = new RecipeStore();
        _client = new PluginClient(PluginId, Accounts);
        _client.HostLost += OnHostLost;

        _hotkeys.HotkeyPressed += OnHotkey;
        _player.Started += (_, args) =>
        {
            _playerActive = true;
            _hotkeys.EnableAbortKey(); // bare Esc aborts only while a macro is playing
            State = PluginState.Playing;
            if (!args.Macro.IsClientSpace && args.Macro.Events.Any(e => e.Kind is MacroEventKind.MouseMove
                or MacroEventKind.MouseDown or MacroEventKind.MouseUp or MacroEventKind.MouseWheel))
            {
                Log("Legacy screen-coordinate macro — window position matters; use STACK or re-record for window-relative playback.");
            }
            RaiseUI(() => CurrentlyPlayingMacroChanged?.Invoke(args.Macro.Id));
            Log(args.TargetUserId == 0
                ? "playback start: multi-window (no target gating)"
                : $"playback start: target user {args.TargetUserId} ({args.BoundAccount?.DisplayName ?? "(unknown)"})");
        };
        _player.Ended += (_, _) =>
        {
            _playerActive = false;
            State = _recorder.IsRecording ? PluginState.Recording : PluginState.Idle;
            // Only clear the badge on standalone play. During a sequence the badge
            // should stay lit across the inter-alt delay; the sequence.Progress
            // Done/Aborted handler clears it instead.
            if (!_sequenceActive)
                RaiseUI(() => CurrentlyPlayingMacroChanged?.Invoke(null));
            RefreshAbortKey(); // release Esc unless a sequence/runner is still active
        };
    }

    // ---------- Public state surfaced to the VM ----------

    public PluginState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            RaiseUI(() => StateChanged?.Invoke());
        }
    }
    private PluginState _state = PluginState.Idle;

    public string HostVersion { get; private set; } = "(not connected)";
    public bool IsConnected { get; private set; }
    public Macro? LastMacro => _lastMacro;

    public event Action? StateChanged;
    public event Action<string>? StatusLogged;
    public event Action? ConnectionChanged;
    public event Action? MacrosChanged;
    public event Action<SequenceProgress>? SequenceProgressed;
    /// <summary>
    /// Fires with the macro Id when playback starts, and with null when
    /// playback ends (sequence-aware: null is suppressed between sequence
    /// alts so the badge stays lit across the inter-alt delay).
    /// </summary>
    public event Action<string?>? CurrentlyPlayingMacroChanged;
    /// <summary>
    /// Fires whenever _lastMacro changes — record-save, any play path,
    /// and on app-start load. Kept for backward compat; assignment model
    /// no longer drives Ctrl+Shift+P off this.
    /// </summary>
    public event Action<string?>? LastMacroChanged;

    // ---------- Assignment events ----------
    public event Action<int, Macro?>? AssignmentChanged;  // (altPid, newMacroOrNull)
    public event Action? AssignmentsReset;
    public event Action<AssignmentProgress>? AssignmentProgressed;

    /// <summary>
    /// Fires ONLY on the start half of the Ctrl+Shift+L chord — the stop half
    /// (a routine already running) is handled entirely inside <see cref="OnHotkey"/>
    /// and never reaches this event. "Which routine + which alts" lives on the
    /// VM (SelectedRoutine / AssignmentRow.IsCheckedForRoutine) — the runtime
    /// knows nothing about that UI state, so it just raises this and the VM
    /// decides whether/what to run. Fires on the hotkey thread; the subscriber
    /// is responsible for marshaling to the UI dispatcher before touching any
    /// ICommand or bound collection.
    /// </summary>
    public event Action? RunRoutineRequested;

    /// <summary>
    /// True while a routine (a looping recipe OR a loadout — RecipeRunner.RunAsync
    /// is "running" for both; a loadout just returns after its position steps
    /// instead of looping) is active via <see cref="RunRecipe"/>. The VM mirrors
    /// this into IsRoutineRunning to drive the routine strip's RUN/STOP toggle,
    /// same shape as IsRunnerActive/PlayStopButtonLabel for the plain assignment loop.
    /// </summary>
    public bool IsRecipeRunning => _activeRecipeRunner?.IsRunning ?? false;

    /// <summary>Fires whenever a recipe run starts or ends — see <see cref="IsRecipeRunning"/>.
    /// Fires on whatever thread the transition happened on; the subscriber marshals.</summary>
    public event Action? RecipeRunningChanged;

    /// <summary>
    /// Fire MacrosChanged on the UI thread. Called by the ViewModel after
    /// in-place mutations (Rename, Delete) that go directly through MacroStore.
    /// </summary>
    public void RaiseMacrosChanged() => RaiseUI(() => MacrosChanged?.Invoke());

    /// <summary>Foreground resolution at this instant. UI polls this on a 250ms timer.</summary>
    public AccountRegistry.AccountInfo? ResolveForegroundNow() => _foreground.ResolveForegroundAccount();

    /// <summary>Invoke the Ctrl+Shift+R path (record toggle). Hotkeys + UI buttons share the same handler.</summary>
    public void TriggerRecordToggle() => OnHotkey(HotkeyKind.RecordToggle);

    /// <summary>Fire the round-robin assignment loop. Ctrl+Shift+P hotkey + PLAY ASSIGNMENTS button share this path.</summary>
    public void TriggerPlayAssignments() => OnHotkey(HotkeyKind.Play);

    /// <summary>Invoke the Esc path (abort).</summary>
    public void TriggerAbort() => OnHotkey(HotkeyKind.Abort);

    // ---------- Assignment commands ----------

    public Macro? GetAssignment(int altPid) => _assignments.TryGetValue(altPid, out var m) ? m : null;

    public IReadOnlyDictionary<int, Macro?> AllAssignments => _assignments;

    public bool IsRunnerRunning => _runner.IsRunning;

    public void AssignMacroToAlt(int altPid, Macro? macro)
    {
        AssignmentMap.ApplyAssignment(_assignments, altPid, macro);

        Log(macro is null
            ? $"assignment cleared: pid {altPid} → keep-alive (Space)"
            : $"assignment: pid {altPid} → {macro.Name ?? "(unnamed)"}");
        RaiseUI(() => AssignmentChanged?.Invoke(altPid, macro));
    }

    /// <summary>
    /// UI-driven cadence-role override for one alt (Task 8's per-row Active/Keep-alive
    /// toggle and the "All equal" / "One focused" presets). Deliberately does NOT touch
    /// <see cref="_assignments"/> — Role and macro assignment are orthogonal axes, same
    /// as <see cref="UI.AssignmentRow.IsCheckedForRoutine"/> is orthogonal to
    /// AssignedMacro on the same row.
    /// </summary>
    public void SetRoleOverride(int altPid, CadenceRole role) => _roleOverrides[altPid] = role;

    /// <summary>
    /// Read-side counterpart to <see cref="SetRoleOverride"/> — null means "no
    /// explicit choice on record for this alt yet, derive from macro presence."
    /// Lets the VM tell an override-in-force apart from a display value it seeded
    /// itself, so re-deriving a row's Role after a macro assign/clear never
    /// clobbers a genuine user choice (see RecorderViewModel.SeedRowRole).
    /// </summary>
    public CadenceRole? GetRoleOverride(int altPid)
        => _roleOverrides.TryGetValue(altPid, out var r) ? r : null;

    public void ResetAssignments()
    {
        _assignments.Clear();
        // Minor fix: a role override left behind here is exactly as stale as one
        // left behind by a departed alt (see AccountRemoved above) — CLEAR wipes
        // every macro pairing back to a blank slate, so a role choice made against
        // one of those now-gone pairings shouldn't silently resurrect itself the
        // next time this pid gets a fresh macro assigned.
        _roleOverrides.Clear();
        Log("all assignments cleared.");
        RaiseUI(() => AssignmentsReset?.Invoke());
    }

    /// <summary>STACK button: minimize every alt window (snapshots positions first).</summary>
    public void ArrangeStack()
    {
        var (moved, note) = _arranger.StackAll();
        Log(note is null ? $"Minimized {moved} alt window(s)." : $"Stack: {note}");
    }

    /// <summary>GRID button: alt windows tiled over the anchor monitor's work area.</summary>
    public void ArrangeGrid()
    {
        var (moved, note) = _arranger.GridAll();
        Log(note is null ? $"Arranged {moved} alt window(s) in a grid." : $"Grid ({moved} moved): {note}");
    }

    /// <summary>RESET button: restore alt windows to their pre-arrange positions and un-minimize.</summary>
    public void ArrangeRestore()
    {
        var (restored, note) = _arranger.RestoreAll();
        Log(note is null ? $"Restored {restored} alt window(s) to their original positions." : $"Reset: {note}");
    }

    // ---------- Recipes (Task 7: wire RecipeRunner to the real runners) ----------

    /// <summary>
    /// Compose a <see cref="RecipeRunner"/> over the live <see cref="SequencePlayer"/>
    /// (RunOnce position steps) and <see cref="AssignmentRunner"/> (terminal Loop/
    /// KeepAlive), then kick it off fire-and-forget. Only one recipe runs at a time —
    /// a prior active run is aborted first. Progress is surfaced through the same
    /// Log/StatusLogged pipe the rest of playback uses (no separate recipe status
    /// UI — reuses the existing activity feed instead of inventing a new one).
    /// The bare-Esc / Ctrl+Shift+A abort chord (see <see cref="OnHotkey"/>) also
    /// aborts the active recipe run — RunAsync's own delegates are literally
    /// _sequence.PlayAsync / _runner.RunAsync, so this is the same abort surface
    /// callers already know, not a new one.
    /// </summary>
    public void RunRecipe(Recipe recipe, IReadOnlyList<AccountRegistry.AccountInfo> selectedAlts)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        if (selectedAlts is null || selectedAlts.Count == 0)
        {
            Log("Run recipe — no alts selected.");
            return;
        }

        if (_activeRecipeRunner is { IsRunning: true } prior)
        {
            Log("Another recipe is already running — aborting it to start this one.");
            prior.Abort();
        }

        // Recipe takes over the shared AssignmentRunner surface: the recipe's terminal
        // step drives the SAME _runner instance the plain Ctrl+Shift+P loop drives, and
        // with the single-flight guard a still-running plain loop would make that
        // terminal _runner.RunAsync silently refuse. Abort it now so the recipe owns
        // the runner by the time its terminal step reaches it.
        // Do NOT abort _sequence here — the recipe's own positioning uses
        // _sequence.PlayAsync moments later (inside the Task.Run below); aborting it
        // now risks the recipe's first position step refusing on a not-yet-cleared claim.
        if (_runner.Abort())
            Log("recipe takes over — aborting active loop");

        // Load the macro library once up front — resolveMacro is called once per
        // position step plus once for a Loop terminal, and re-reading + re-deserializing
        // every macro file on each call turns an N-step recipe into N+1 full library
        // reloads. Snapshot into a dictionary instead.
        var macros = Store.LoadAll().Macros.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var runner = new RecipeRunner(
            runOnce: (macro, alts, ct) => _sequence.PlayAsync(macro, alts, null, ct),
            // The recipe's terminal Loop/KeepAlive step is the OTHER place
            // _runner.RunAsync gets kicked off (the plain Ctrl+Shift+P path above is
            // the first). Same as that path, do NOT claim here — the Abort() just
            // above is fire-and-forget (not awaited), so a prior run may still be
            // unwinding when this RunAsync call is made, and it can legitimately
            // lose the single-flight race against that stale run. Claiming
            // unconditionally here would publish coverage for alts this call never
            // actually started servicing. The _runner.Progress subscription's
            // Started handler claims instead, and only fires for the call that
            // really wins.
            runLoop: (assignments, ct) => _runner.RunAsync(assignments, ct),
            resolveMacro: id => macros.TryGetValue(id, out var m) ? m : null);
        runner.Progress += (_, p) => Log($"Recipe '{recipe.Name ?? "(unnamed)"}': {p.StepLabel} ({p.Phase}).");

        _activeRecipeRunner = runner;
        RecipeRunningChanged?.Invoke();
        _hotkeys.EnableAbortKey(); // Esc/Ctrl+Shift+A aborts for the whole recipe run

        _ = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(recipe, selectedAlts);
                Log($"Recipe '{recipe.Name ?? "(unnamed)"}' finished.");
            }
            catch (Exception ex)
            {
                Log($"Recipe run error: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_activeRecipeRunner, runner))
                {
                    _activeRecipeRunner = null;
                    RecipeRunningChanged?.Invoke();
                }
                RefreshAbortKey();
            }
        });
    }

    // ---------- Recipe library + editor ----------

    /// <summary>
    /// Open (or bring forward) the saved-recipes library — every persisted
    /// recipe with per-row Run/Edit/Delete, plus a NEW RECIPE entry point that
    /// reuses <see cref="OpenRecipeEditor"/>. This is the entry point the tray's
    /// "Recipes" item and the main window's RECIPES button open now — the bare
    /// editor is reached FROM the library (New/Edit), never directly, so a
    /// saved recipe is never orphaned behind an editor-only shortcut. Non-modal,
    /// same rationale as <see cref="OpenRecipeEditor"/>.
    /// </summary>
    public void OpenRecipesLibrary(Window owner)
    {
        try
        {
            var window = new UI.RecipesWindow(this) { Owner = owner };
            window.Show();
        }
        catch (Exception ex)
        {
            Diagnostics.DiagLog.Write($"Recipes library failed to open: {ex.Message}");
        }
    }

    /// <summary>
    /// Open a fresh <see cref="UI.RecipeEditorWindow"/> seeded with the current macro
    /// library and the live alt set. Non-modal (Show, not ShowDialog) so a running
    /// recipe loop doesn't block the rest of the plugin. Persistence is wired off
    /// the window's Saved event — Task 7's seam — so the window itself stays
    /// decoupled from RecipeStore. Shared by RecipesWindow's NEW RECIPE button
    /// so there's exactly one place this wiring lives.
    /// </summary>
    public void OpenRecipeEditor(Window owner)
    {
        try
        {
            var library = Store.LoadAll().Macros;
            var alts = Accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
            var editor = new UI.RecipeEditorWindow(library, alts, this) { Owner = owner };
            editor.Saved += (_, _) =>
            {
                if (editor.BuiltRecipe is { } recipe)
                    Recipes.Save(recipe);
            };
            editor.Show();
        }
        catch (Exception ex)
        {
            // Menu click / button click — nowhere dedicated to report; leave a trace like OpenLogFolder does.
            Diagnostics.DiagLog.Write($"Recipe editor failed to open: {ex.Message}");
        }
    }

    // ---------- Heartbeat claim (Task 7) ----------

    /// <summary>
    /// Publish the accounts <see cref="_runner"/> is about to actively manage — see
    /// <see cref="ClaimFile"/>. Called from the <c>_runner.Progress</c> subscription's
    /// <see cref="AssignmentPhase.Started"/> handler (constructor) — that phase fires
    /// exactly once, only for the <c>_runner.RunAsync</c> call that actually won the
    /// single-flight <c>CompareExchange</c> guard, whether that call came from the
    /// plain Ctrl+Shift+P path in <see cref="OnHotkey"/> or the recipe terminal-loop
    /// path in <see cref="RunRecipe"/>.
    ///
    /// Deliberately NOT called at either call site directly, before RunAsync is
    /// kicked off: a losing concurrent call (e.g. a recipe's fire-and-forget
    /// <c>_runner.Abort()</c> racing a prior run that hasn't unwound yet — see
    /// RunRecipe's comment) would otherwise publish a claim for alts nobody actually
    /// started servicing, exactly the lying-claim failure mode this file's SAFETY
    /// DIRECTION exists to avoid. Hanging the claim off the runner's own confirmed
    /// start makes it symmetric with <see cref="TryStopClaim"/>, which already hangs
    /// off the runner's confirmed <see cref="AssignmentPhase.Stopped"/>.
    ///
    /// Publishing a claim is bookkeeping; keeping alts alive is the product. A failed
    /// write must never stop the cadence loop, so any I/O failure here is caught and
    /// logged rather than allowed to propagate into the caller (which would otherwise
    /// abort the loop before it even starts).
    /// </summary>
    private void TryStartClaim(IReadOnlyList<Assignment> assignments)
    {
        try { _claim.Start(assignments.Select(a => a.Alt.RobloxUserId)); }
        catch (Exception ex) { Log($"Claim publish failed (non-fatal — ur-afk may double-cover these alts briefly): {ex.Message}"); }
    }

    /// <summary>
    /// Release the claim — see the <c>AssignmentPhase.Stopped</c> handler on
    /// <see cref="_runner"/>'s Progress subscription, which is the single place this
    /// is called from. Fails SAFE even if this throws: the heartbeat's TTL (see
    /// <see cref="ClaimFile"/>) still expires on its own, so a failed delete here
    /// never leaves ur-afk permanently locked out.
    /// </summary>
    private void TryStopClaim()
    {
        try { _claim.Stop(); } catch (Exception ex) { Log($"Claim release failed (non-fatal — TTL expiry covers it): {ex.Message}"); }
    }

    // ---------- Lifecycle ----------

    public async Task StartAsync()
    {
        try
        {
            _hotkeys.Start();
            Log("Hotkeys ready: Ctrl+Shift+R record/stop · Ctrl+Shift+P play assignments · Ctrl+Shift+L run routine · Ctrl+Shift+A abort (Esc also aborts while playing).");

            var loaded = Store.LoadAll();
            Log($"Loaded {loaded.Macros.Count} macros from {Store.Directory}.");
            foreach (var f in loaded.Failures)
                Log($"  ! load failed: {Path.GetFileName(f.Path)} — {f.Reason}");
            if (loaded.Macros.Count > 0)
            {
                _lastMacro = loaded.Macros[^1];
                RaiseUI(() => MacrosChanged?.Invoke());
                RaiseUI(() => LastMacroChanged?.Invoke(_lastMacro?.Id));
            }

            Log("Connecting to RoRoRo over named pipe...");
            await _client.ConnectAsync();
            HostVersion = _client.HostVersion;
            IsConnected = true;
            RaiseUI(() => ConnectionChanged?.Invoke());
            Log($"Connected. Host version {HostVersion}. Snapshot has {Accounts.Snapshot().Count} accounts.");
        }
        catch (Exception ex)
        {
            Log($"Startup failed: {ex.Message}");
            IsConnected = false;
            RaiseUI(() => ConnectionChanged?.Invoke());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _bridgeCts.Cancel(); } catch { }
        try { _bridgeCts.Dispose(); } catch { }
        try { _hotkeys.Dispose(); } catch { }
        try { _recorder.Stop(); } catch { }
        try { _activeRecipeRunner?.Abort(); } catch { }
        // Belt-and-suspenders: a graceful app exit may race the runner's own
        // Stopped event (background task hasn't ticked yet) — dispose the claim
        // directly too so a clean quit doesn't wait out the TTL for no reason.
        try { _claim.Dispose(); } catch { }
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    // ---------- Host-loss safety ----------

    /// <summary>
    /// Fires when the gRPC connection to RoRoRo breaks unexpectedly (RoRoRo
    /// killed via Task Manager, pipe broken, etc.). Abort any active playback
    /// FIRST so the runner stops sending input to cached Roblox PIDs, then
    /// shut the plugin process down cleanly. Without this the plugin would
    /// keep running as a zombie sending input forever.
    /// </summary>
    private void OnHostLost()
    {
        Log("Host RoRoRo connection lost — aborting playback and exiting plugin.");
        try { _bridgeCts.Cancel(); } catch { }
        try { _runner.Abort(); } catch { }
        try { _sequence.Abort(); } catch { }
        try { _player.Abort(); } catch { }
        // The recipe runner has its own token — aborting the sub-runners above only
        // cancels THEIR internal tokens, not the recipe's. Without this, RecipeRunner
        // never observes cancellation and starts a fresh terminal loop against
        // orphaned PIDs after the host is gone.
        try { _activeRecipeRunner?.Abort(); } catch { }

        // Marshal to UI thread for the WPF shutdown. If we can't reach the
        // dispatcher (rare race), fall back to a hard exit so we don't linger.
        RaiseUI(() =>
        {
            try
            {
                System.Windows.Application.Current?.Shutdown(0);
            }
            catch
            {
                Environment.Exit(0);
            }
        });
    }

    // ---------- Hotkey handlers ----------

    private void OnHotkey(HotkeyKind kind)
    {
        switch (kind)
        {
            case HotkeyKind.RecordToggle:
                if (!_recorder.IsRecording) StartRecording();
                else StopAndSaveRecording();
                break;

            case HotkeyKind.Play:
                // A recipe owns the shared runner surface while it's active — refuse
                // rather than starting a concurrent plain loop underneath it.
                if (_activeRecipeRunner is { IsRunning: true })
                {
                    Log("recipe running — ignoring Play");
                    break;
                }

                // Toggle: if a round-robin is already running, Ctrl+Shift+P stops it
                // (same as Esc). Rescue affordance — the chord that started it can
                // also stop it, so users aren't trapped if they reach for the chord
                // out of muscle memory.
                if (_runner.IsRunning)
                {
                    if (_runner.Abort()) Log("Assignment loop stopped (Ctrl+Shift+P toggle).");
                    break;
                }

                var alts = Accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
                if (alts.Count == 0)
                {
                    Log("PlayAssignments — no RoRoRo-managed alts running.");
                    break;
                }

                // Build assignment list — every running alt gets a slot,
                // unassigned = null macro (keep-alive Space). Role: an explicit
                // Task-8 UI override wins outright; absent one, fall back to the
                // legacy derived rule (macro present -> Active, none -> KeepAlive).
                // Assignment.ResolveRole is the single source of truth for this —
                // CRITICAL 2: it also coerces a null-macro override back to
                // KeepAlive, so a stray "Active with no macro" override (which the
                // UI is supposed to prevent, but this is the belt to that brace)
                // can never actually reach the runner as Active.
                var assignments = alts.Select(a =>
                {
                    var macro = _assignments.TryGetValue(a.Pid, out var m) ? m : null;
                    var role = Assignment.ResolveRole(macro, GetRoleOverride(a.Pid));
                    return new Assignment(a, macro, role);
                }).ToList();

                // Counted by actual Role now, not by macro presence — a macro-assigned
                // alt manually backgrounded (Task 8's per-row toggle) farms nothing
                // this run, and the log line should say so rather than call it "active"
                // just because it happens to have a macro attached.
                var activeCount = assignments.Count(a => a.Role == CadenceRole.Active);
                var keepAliveCount = assignments.Count(a => a.Role == CadenceRole.KeepAlive);
                Log($"Playing assignments — {activeCount} active, {keepAliveCount} keep-alive. Esc or Ctrl+Shift+A to stop.");

                _hotkeys.EnableAbortKey(); // Esc aborts for the whole runner session, incl. keep-alive gaps
                // Claim publication happens off _runner's own Started progress event
                // (see the Progress subscription in the constructor) — NOT here —
                // so it only fires once RunAsync has actually won its single-flight
                // guard, not on the optimistic assumption that this call will win.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _runner.RunAsync(assignments);
                        Log("Assignment loop stopped.");
                    }
                    catch (Exception ex)
                    {
                        Log($"Assignment loop error: {ex.Message}");
                    }
                    finally
                    {
                        RefreshAbortKey();
                    }
                });
                break;

            case HotkeyKind.Abort:
                bool aborted = _runner.Abort() | _sequence.Abort() | _player.Abort()
                    | (_activeRecipeRunner?.Abort() ?? false);
                Log(aborted ? "Aborted." : "Abort ignored — nothing playing.");
                break;

            case HotkeyKind.RunRoutine:
                // Toggle: a routine already running (looping recipe OR loadout —
                // IsRunning covers both) means Ctrl+Shift+L stops it, same rescue
                // affordance TogglePlayStopCommand gives Ctrl+Shift+P. Otherwise
                // "which routine + which alts" is VM state (SelectedRoutine,
                // AssignmentRow.IsCheckedForRoutine) — the runtime doesn't own
                // it, so bridge the chord out and let the VM's RunRoutineCommand
                // (same CanExecute the RUN button honors) decide whether to fire.
                if (_activeRecipeRunner is { IsRunning: true })
                {
                    _activeRecipeRunner.Abort();
                    _sequence.Abort();
                    _runner.Abort();
                    _player.Abort();
                    Log("Routine stopped (Ctrl+Shift+L).");
                    break;
                }
                RunRoutineRequested?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Register the bare-Esc abort hotkey iff something is playing, else release
    /// it. Called on every playback start/end transition so Esc is grabbed only
    /// while a macro/sequence/runner is active and free for other apps otherwise.
    /// Enable/Disable are idempotent, so over-calling is harmless.
    /// </summary>
    private void RefreshAbortKey()
    {
        if (_runner.IsRunning || _sequenceActive || _playerActive || (_activeRecipeRunner?.IsRunning ?? false))
            _hotkeys.EnableAbortKey();
        else
            _hotkeys.DisableAbortKey();
    }

    private void StartRecording()
    {
        var account = _foreground.ResolveForegroundAccount();
        if (CurrentRecordMode == RecordMode.PerWindow && account is null)
        {
            Log("Record refused — foreground window isn't a RoRoRo-managed Roblox process. (Switch to multi-window mode to record anyway.)");
            return;
        }
        try
        {
            // Esc is always filtered (so Esc during record doesn't bake into the macro).
            // VK_R / VK_P are chord-only — the recorder handles the modifier check via
            // HotkeyService.ChordHotkeyVkCodes.
            // Per-window recordings anchor mouse coords to the bound window's client
            // area (v3 client space). AllWindows keeps absolute screen pixels.
            var anchorHwnd = CurrentRecordMode == RecordMode.PerWindow && account is not null
                ? _metrics.HwndForPid(account.Pid)
                : IntPtr.Zero;
            _recorder.Start(
                alwaysIgnore: new[] { HotkeyService.AbortVkCode },
                chordIgnore: HotkeyService.ChordHotkeyVkCodes,
                ignoreMouseEvents: RecordKeyboardOnly,
                clientAnchorHwnd: anchorHwnd);
            _recordingAnchorHwnd = anchorHwnd;
            _recordingClientSize = anchorHwnd != IntPtr.Zero ? _metrics.ClientSize(anchorHwnd) : null;
            _recordingMaximized = anchorHwnd != IntPtr.Zero ? _metrics.IsMaximized(anchorHwnd) : null;
            _recordingBoundAccount = account;
            // Presence fills game identity AFTER the launch event (0.4.0 contract
            // semantics), so the registry entry captured above may carry no game
            // yet. Kick a best-effort snapshot refresh now; by stop-time the
            // stamp re-resolves the pid and usually finds the game filled in.
            _ = _client.RefreshRunningAccountsAsync();
            State = PluginState.Recording;
            Log(CurrentRecordMode == RecordMode.AllWindows
                ? "Recording (multi-window mode) — capturing input across all windows."
                : $"Recording started — bound to user {account!.RobloxUserId} ({account.DisplayName}).");
        }
        catch (Exception ex)
        {
            Log($"Record failed to start: {ex.Message}");
        }
    }

    private void StopAndSaveRecording()
    {
        var events = _recorder.Stop();
        var bound = _recordingBoundAccount;
        var anchorHwnd = _recordingAnchorHwnd;
        _recordingBoundAccount = null;
        _recordingAnchorHwnd = IntPtr.Zero;
        // _recordingClientSize is read below for the macro fields; clear after construction.
        State = PluginState.Idle;

        if (events.Count == 0) { Log("Stop — 0 events captured."); return; }

        // Mid-recording resizes are unsupported: coords stay correct per-event, but
        // the stored client size is the record-start size. Warn so the user re-records.
        if (anchorHwnd != IntPtr.Zero && _recordingClientSize is { } startSize)
        {
            var endSize = _metrics.ClientSize(anchorHwnd);
            if (endSize is { } es && es != startSize)
                Log($"Warning: window was resized during recording ({startSize.W}x{startSize.H} → {es.W}x{es.H}) — mouse positions may be off; consider re-recording.");
        }
        // Client-space is only correct when the recording actually contains mouse
        // events AND the anchor window resolved during recording. Keyboard-only
        // PerWindow recordings (the default — RecordKeyboardOnly is true) have no
        // coordinates to be relative to; tagging them client-space would force a
        // resize (and a possible refusal) on playback for zero benefit — regressing
        // the dominant keyboard-only round-robin use case and fighting GRID. The
        // `_recordingClientSize is not null` clause also closes the HwndForPid==Zero
        // edge (anchor never resolved): screen space, honest schema.
        bool hasMouseEvents = events.Any(e => e.Kind is MacroEventKind.MouseMove
            or MacroEventKind.MouseDown or MacroEventKind.MouseUp or MacroEventKind.MouseWheel);
        var isClientSpace = CurrentRecordMode == RecordMode.PerWindow && hasMouseEvents && _recordingClientSize is not null;

        // Re-resolve the bound pid: the record-start refresh (or a later host
        // event) may have filled game identity into the registry since then.
        var boundFresh = bound is null ? null : Accounts.ResolveByPid(bound.Pid) ?? bound;

        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: $"Recording {DateTimeOffset.Now:HH:mm:ss}",
            RecordMode: CurrentRecordMode == RecordMode.AllWindows ? "AllWindows" : "PerWindow",
            RecordedAgainstUserId: boundFresh?.RobloxUserId,
            RecordedAgainstDisplayName: boundFresh?.DisplayName,
            InterAltDelayMs: null,
            RecordedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Events: events.ToList(),
            CoordSpace: isClientSpace ? Macro.CoordSpaceClient : Macro.CoordSpaceScreen,
            RecordedClientW: isClientSpace ? _recordingClientSize?.W : null,
            RecordedClientH: isClientSpace ? _recordingClientSize?.H : null,
            RecordedMaximized: isClientSpace ? _recordingMaximized : null,
            RecordedPlaceId: boundFresh is { PlaceId: > 0 } ? boundFresh.PlaceId : null,
            RecordedGameName: string.IsNullOrEmpty(boundFresh?.PlaceName) ? null : boundFresh!.PlaceName);

        try
        {
            Store.Save(macro);
            _recordingClientSize = null;
            _recordingMaximized = null;
            _lastMacro = macro;
            RaiseUI(() => MacrosChanged?.Invoke());
            RaiseUI(() => LastMacroChanged?.Invoke(_lastMacro?.Id));
            Log($"Saved macro: {events.Count} events, duration {macro.Duration.TotalSeconds:F1}s.");

            // Prompt for rename — user can Enter to accept the auto-name or type a new one.
            RaiseUI(() => PromptRename(macro));
        }
        catch (Exception ex)
        {
            Log($"Save failed: {ex.Message}");
        }
    }

    private void PromptRename(Macro macro)
    {
        try
        {
            var dlg = new UI.RenameMacroDialog(macro.Name ?? "")
            {
                Owner = Application.Current?.MainWindow,
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.NewName))
            {
                var newName = dlg.NewName.Trim();
                if (newName != macro.Name)
                {
                    var renamed = macro with { Name = newName };
                    Store.Save(renamed);
                    _lastMacro = renamed;
                    MacrosChanged?.Invoke();
                    LastMacroChanged?.Invoke(_lastMacro?.Id);
                    Log($"Renamed to: {newName}");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Rename prompt failed: {ex.Message}");
        }
    }

    // ---------- Helpers ----------

    private void Log(string message)
    {
        // Tee to the on-disk diagnostics file (full timestamps there); the
        // activity view keeps its short HH:mm:ss formatting.
        Diagnostics.DiagLog.Write(message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        RaiseUI(() => StatusLogged?.Invoke(line));
    }

    /// <summary>
    /// Marshal an event callback onto the UI dispatcher so VM handlers always run
    /// on the WPF UI thread. Hotkey + hook callbacks fire on background threads,
    /// so without this the VM would need its own dispatcher calls everywhere.
    /// </summary>
    private static void RaiseUI(Action callback)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null || disp.CheckAccess()) callback();
        else disp.BeginInvoke(callback);
    }
}

internal enum PluginState { Idle, Recording, Playing }

public enum RecordMode { PerWindow, AllWindows }
