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

    private readonly ForegroundWatcher _foreground;
    private readonly MacroRecorder _recorder;
    private readonly MacroPlayer _player;
    private readonly SequencePlayer _sequence;
    private readonly AssignmentRunner _runner;
    private readonly HotkeyService _hotkeys;
    private readonly PluginClient _client;
    private readonly PluginHost.IWindowMetrics _metrics = new PluginHost.WindowMetrics();

    private readonly CancellationTokenSource _bridgeCts = new();
    private Ipc.MacroRunnerServer? _bridgeServer;

    private AccountRegistry.AccountInfo? _recordingBoundAccount;
    private IntPtr _recordingAnchorHwnd = IntPtr.Zero;
    private (int W, int H)? _recordingClientSize;
    private Macro? _lastMacro;
    private bool _sequenceActive;
    private volatile bool _playerActive;

    // ---------- Assignment state ----------
    private readonly Dictionary<int, Macro?> _assignments = new(); // key: alt.Pid; value: assigned Macro or null for keep-alive

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
            Log($"account exited: {info.DisplayName} (user {info.RobloxUserId}, pid {info.Pid})");
        _foreground = new ForegroundWatcher(Accounts);
        _recorder = new MacroRecorder();
        _player = new MacroPlayer(_foreground);
        _sequence = new SequencePlayer(_player, _foreground);
        _runner = new AssignmentRunner(_player, _foreground);

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

        _runner.Progress += (_, p) => RaiseUI(() => AssignmentProgressed?.Invoke(p));

        _ = new AutoStopCoordinator(_player, Accounts);
        Store = new MacroStore();
        _hotkeys = new HotkeyService();
        _client = new PluginClient(PluginId, Accounts);
        _client.HostLost += OnHostLost;

        _hotkeys.HotkeyPressed += OnHotkey;
        _player.Started += (_, args) =>
        {
            _playerActive = true;
            _hotkeys.EnableAbortKey(); // bare Esc aborts only while a macro is playing
            State = PluginState.Playing;
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

    public void ResetAssignments()
    {
        _assignments.Clear();
        Log("all assignments cleared.");
        RaiseUI(() => AssignmentsReset?.Invoke());
    }

    // ---------- Lifecycle ----------

    public async Task StartAsync()
    {
        try
        {
            _hotkeys.Start();
            Log("Hotkeys ready: Ctrl+Shift+R record/stop · Ctrl+Shift+P play assignments · Ctrl+Shift+A abort (Esc also aborts while playing).");

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
                // unassigned = null macro (keep-alive Space).
                var assignments = alts.Select(a =>
                    new Assignment(a, _assignments.TryGetValue(a.Pid, out var m) ? m : null)).ToList();

                var explicitCount = assignments.Count(a => a.Macro is not null);
                var keepAliveCount = assignments.Count(a => a.Macro is null);
                Log($"Playing assignments — {explicitCount} explicit, {keepAliveCount} keep-alive. Esc or Ctrl+Shift+A to stop.");

                _hotkeys.EnableAbortKey(); // Esc aborts for the whole runner session, incl. keep-alive gaps
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
                bool aborted = _runner.Abort() | _sequence.Abort() | _player.Abort();
                Log(aborted ? "Aborted." : "Abort ignored — nothing playing.");
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
        if (_runner.IsRunning || _sequenceActive || _playerActive)
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
            _recordingBoundAccount = account;
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
        var isClientSpace = CurrentRecordMode == RecordMode.PerWindow;

        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: $"Recording {DateTimeOffset.Now:HH:mm:ss}",
            RecordMode: CurrentRecordMode == RecordMode.AllWindows ? "AllWindows" : "PerWindow",
            RecordedAgainstUserId: bound?.RobloxUserId,
            RecordedAgainstDisplayName: bound?.DisplayName,
            InterAltDelayMs: null,
            RecordedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Events: events.ToList(),
            CoordSpace: isClientSpace ? Macro.CoordSpaceClient : Macro.CoordSpaceScreen,
            RecordedClientW: isClientSpace ? _recordingClientSize?.W : null,
            RecordedClientH: isClientSpace ? _recordingClientSize?.H : null);

        try
        {
            Store.Save(macro);
            _recordingClientSize = null;
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
