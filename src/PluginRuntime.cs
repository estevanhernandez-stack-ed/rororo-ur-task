using System.IO;
using System.Windows;
using Labs626.UrTask.Hotkeys;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask;

/// <summary>
/// Owns all the moving parts (gRPC client, account registry, foreground
/// watcher, macro recorder + player + store, auto-stop coordinator,
/// hotkeys) and wires the Ctrl+Shift+R / Ctrl+Shift+P / Esc hotkey handlers. The ViewModel
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
    private readonly HotkeyService _hotkeys;
    private readonly PluginClient _client;

    private AccountRegistry.AccountInfo? _recordingBoundAccount;
    private Macro? _lastMacro;
    private bool _allWindowsConfirmedThisSession;
    private bool _sequenceActive;

    public RecordMode CurrentRecordMode { get; set; } = RecordMode.PerWindow;

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
        _sequence.Progress += (_, p) =>
        {
            SequenceProgressed?.Invoke(p);
            // Track whether a sequence is active so _player.Ended doesn't clear
            // the badge prematurely between alts.
            if (p.Phase == SequencePhase.Focusing && p.Index == 0)
                _sequenceActive = true;
            else if (p.Phase == SequencePhase.Done || p.Phase == SequencePhase.Aborted)
            {
                _sequenceActive = false;
                RaiseUI(() => CurrentlyPlayingMacroChanged?.Invoke(null));
            }
        };
        _ = new AutoStopCoordinator(_player, Accounts);
        Store = new MacroStore();
        _hotkeys = new HotkeyService();
        _client = new PluginClient(PluginId, Accounts);

        _hotkeys.HotkeyPressed += OnHotkey;
        _player.Started += (_, args) =>
        {
            State = PluginState.Playing;
            RaiseUI(() => CurrentlyPlayingMacroChanged?.Invoke(args.Macro.Id));
            Log(args.TargetUserId == 0
                ? "playback start: multi-window (no target gating)"
                : $"playback start: target user {args.TargetUserId} ({args.BoundAccount?.DisplayName ?? "(unknown)"})");
        };
        _player.Ended += (_, _) =>
        {
            State = _recorder.IsRecording ? PluginState.Recording : PluginState.Idle;
            // Only clear the badge on standalone play. During a sequence the badge
            // should stay lit across the inter-alt delay; the sequence.Progress
            // Done/Aborted handler clears it instead.
            if (!_sequenceActive)
                RaiseUI(() => CurrentlyPlayingMacroChanged?.Invoke(null));
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
    /// Fire MacrosChanged on the UI thread. Called by the ViewModel after
    /// in-place mutations (Rename, Delete) that go directly through MacroStore.
    /// </summary>
    public void RaiseMacrosChanged() => RaiseUI(() => MacrosChanged?.Invoke());

    /// <summary>Foreground resolution at this instant. UI polls this on a 250ms timer.</summary>
    public AccountRegistry.AccountInfo? ResolveForegroundNow() => _foreground.ResolveForegroundAccount();

    /// <summary>Invoke the Ctrl+Shift+R path (record toggle). Hotkeys + UI buttons share the same handler.</summary>
    public void TriggerRecordToggle() => OnHotkey(HotkeyKind.RecordToggle);

    /// <summary>Invoke the Ctrl+Shift+P path (play last on smart-default target).</summary>
    public void TriggerPlayLast() => OnHotkey(HotkeyKind.Play);

    /// <summary>
    /// Smart-default per-card PLAY: play on the focused alt if a Roblox alt is
    /// foreground, else open the picker in single-select mode as fallback.
    /// AllWindows macros still route through the pre-flight confirm regardless.
    /// </summary>
    public void TriggerPlayMacro(string macroId)
    {
        var macro = LoadMacroById(macroId);
        if (macro is null) return;

        if (macro.RecordMode == "AllWindows")
        {
            PlayAllWindowsMacro(macro);
            return;
        }

        var alts = Accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
        if (alts.Count == 0) { Log("PlayMacro — no RoRoRo-managed alts running."); return; }

        var fg = _foreground.ResolveForegroundAccount();

        _lastMacro = macro;

        if (fg is not null)
        {
            // Smart default — play directly on focused alt, no modal.
            _ = Task.Run(async () =>
            {
                var result = await _player.PlayAsync(macro, fg.RobloxUserId);
                Log($"playback result on {fg.DisplayName}: {result.Outcome}{(result.Reason is null ? "" : " — " + result.Reason)}");
            });
            return;
        }

        // Fallback: picker in single-select.
        OpenPickerAndPlay(macro, alts, multiSelect: false);
    }

    /// <summary>
    /// Explicit batch path wired from the ⋯ → "Play on multiple alts..." menu.
    /// Always opens picker in multi-select mode.
    /// </summary>
    public void TriggerPlayMacroOnMultiple(string macroId)
    {
        var macro = LoadMacroById(macroId);
        if (macro is null) return;

        if (macro.RecordMode == "AllWindows")
        {
            Log("Multi-window macros are played raw — multi-alt batch doesn't apply.");
            PlayAllWindowsMacro(macro);
            return;
        }

        var alts = Accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
        if (alts.Count == 0) { Log("PlayMacro — no RoRoRo-managed alts running."); return; }

        _lastMacro = macro;
        OpenPickerAndPlay(macro, alts, multiSelect: true);
    }

    private Macro? LoadMacroById(string macroId)
    {
        var loaded = Store.LoadAll();
        var macro = loaded.Macros.FirstOrDefault(m => m.Id == macroId);
        if (macro is null) Log($"PlayMacro({macroId}) — macro not found.");
        return macro;
    }

    private void OpenPickerAndPlay(Macro macro, List<AccountRegistry.AccountInfo> alts, bool multiSelect)
    {
        IReadOnlyList<AccountRegistry.AccountInfo>? selected = null;
        var disp = Application.Current?.Dispatcher;
        if (disp is not null)
        {
            disp.Invoke(() =>
            {
                var fg = _foreground.ResolveForegroundAccount();
                long? preferredUserId = fg?.RobloxUserId ?? macro.RecordedAgainstUserId;
                var picker = new UI.PlaybackTargetPickerWindow(macro.Name ?? "macro", alts, preferredUserId, multiSelect);
                var owner = Application.Current?.MainWindow;
                if (owner is not null) picker.Owner = owner;
                if (picker.ShowDialog() == true) selected = picker.SelectedTargets;
            });
        }

        if (selected is null || selected.Count == 0)
        {
            Log("Target picker — cancelled.");
            return;
        }

        var targets = selected.ToList();
        if (targets.Count == 1)
        {
            _ = Task.Run(async () =>
            {
                var result = await _player.PlayAsync(macro, targets[0].RobloxUserId);
                Log($"playback result on {targets[0].DisplayName}: {result.Outcome}{(result.Reason is null ? "" : " — " + result.Reason)}");
            });
        }
        else
        {
            _ = Task.Run(async () =>
            {
                var seqResult = await _sequence.PlayAsync(macro, targets);
                Log($"sequence done: {seqResult.Completed}/{targets.Count} succeeded · {seqResult.Failed} failed · {seqResult.Skipped} skipped");
            });
        }
    }

    /// <summary>Invoke the Esc path (abort).</summary>
    public void TriggerAbort() => OnHotkey(HotkeyKind.Abort);

    private void PlayAllWindowsMacro(Macro macro)
    {
        if (!_allWindowsConfirmedThisSession)
        {
            bool confirmed = false;
            var disp = Application.Current?.Dispatcher;
            if (disp is not null)
            {
                disp.Invoke(() =>
                {
                    var dlg = new UI.MultiWindowConfirmDialog();
                    var owner = Application.Current?.MainWindow;
                    if (owner is not null) dlg.Owner = owner;
                    confirmed = dlg.ShowDialog() == true;
                });
            }
            if (!confirmed)
            {
                Log("Multi-window playback — cancelled.");
                return;
            }
            _allWindowsConfirmedThisSession = true;
        }

        _lastMacro = macro;
        _ = Task.Run(async () =>
        {
            var result = await _player.PlayAllWindowsRawAsync(macro);
            Log($"multi-window playback: {result.Outcome}{(result.Reason is null ? "" : " — " + result.Reason)}");
        });
    }

    // ---------- Lifecycle ----------

    public async Task StartAsync()
    {
        try
        {
            _hotkeys.Start();
            Log("Hotkeys ready: Ctrl+Shift+R record/stop · Ctrl+Shift+P play · Esc abort.");

            var loaded = Store.LoadAll();
            Log($"Loaded {loaded.Macros.Count} macros from {Store.Directory}.");
            foreach (var f in loaded.Failures)
                Log($"  ! load failed: {Path.GetFileName(f.Path)} — {f.Reason}");
            if (loaded.Macros.Count > 0)
            {
                _lastMacro = loaded.Macros[^1];
                RaiseUI(() => MacrosChanged?.Invoke());
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
        try { _hotkeys.Dispose(); } catch { }
        try { _recorder.Stop(); } catch { }
        await _client.DisposeAsync().ConfigureAwait(false);
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
                if (_lastMacro is null) { Log("Ctrl+Shift+P ignored — no macro to play."); break; }
                var lastMacro = _lastMacro;
                _ = Task.Run(async () =>
                {
                    var fg = _foreground.ResolveForegroundAccount();
                    if (fg is not null)
                    {
                        // Smart default: play on whatever RoRoRo-managed alt is in foreground.
                        var result = await _player.PlayAsync(lastMacro, fg.RobloxUserId);
                        Log($"playback result on {fg.DisplayName}: {result.Outcome}{(result.Reason is null ? "" : " — " + result.Reason)}");
                        return;
                    }

                    // Fallback: open picker in single-select mode so the user can choose a target.
                    var alts = Accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
                    if (alts.Count == 0)
                    {
                        Log("Ctrl+Shift+P ignored — no RoRoRo-managed alts running.");
                        return;
                    }

                    AccountRegistry.AccountInfo? picked = null;
                    var disp = Application.Current?.Dispatcher;
                    if (disp is not null)
                    {
                        disp.Invoke(() =>
                        {
                            var picker = new UI.PlaybackTargetPickerWindow(
                                lastMacro.Name ?? "macro",
                                alts,
                                preferredUserId: lastMacro.RecordedAgainstUserId,
                                multiSelect: false);
                            var owner = Application.Current?.MainWindow;
                            if (owner is not null) picker.Owner = owner;
                            if (picker.ShowDialog() == true && picker.SelectedTargets is { Count: > 0 } sel)
                                picked = sel[0];
                        });
                    }

                    if (picked is null)
                    {
                        Log("Ctrl+Shift+P — picker cancelled.");
                        return;
                    }

                    var fallbackResult = await _player.PlayAsync(lastMacro, picked.RobloxUserId);
                    Log($"playback result on {picked.DisplayName}: {fallbackResult.Outcome}{(fallbackResult.Reason is null ? "" : " — " + fallbackResult.Reason)}");
                });
                break;
            case HotkeyKind.Abort:
                bool aborted = _sequence.Abort() | _player.Abort();
                Log(aborted ? "Aborted (Esc)." : "Esc ignored — nothing playing.");
                break;
        }
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
            _recorder.Start(
                alwaysIgnore: new[] { HotkeyService.AbortVkCode },
                chordIgnore: HotkeyService.ChordHotkeyVkCodes);
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
        _recordingBoundAccount = null;
        State = PluginState.Idle;

        if (events.Count == 0) { Log("Stop — 0 events captured."); return; }

        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: $"Recording {DateTimeOffset.Now:HH:mm:ss}",
            RecordMode: CurrentRecordMode == RecordMode.AllWindows ? "AllWindows" : "PerWindow",
            RecordedAgainstUserId: bound?.RobloxUserId,
            RecordedAgainstDisplayName: bound?.DisplayName,
            InterAltDelayMs: null,
            RecordedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Events: events.ToList());

        try
        {
            Store.Save(macro);
            _lastMacro = macro;
            RaiseUI(() => MacrosChanged?.Invoke());
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
