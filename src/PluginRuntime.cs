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
    private readonly HotkeyService _hotkeys;
    private readonly PluginClient _client;

    private AccountRegistry.AccountInfo? _recordingBoundAccount;
    private Macro? _lastMacro;

    public PluginRuntime()
    {
        Accounts = new AccountRegistry();
        _foreground = new ForegroundWatcher(Accounts);
        _recorder = new MacroRecorder();
        _player = new MacroPlayer(_foreground);
        _ = new AutoStopCoordinator(_player, Accounts);
        Store = new MacroStore();
        _hotkeys = new HotkeyService();
        _client = new PluginClient(PluginId, Accounts);

        _hotkeys.HotkeyPressed += OnHotkey;
        _player.Started += (_, args) =>
        {
            State = PluginState.Playing;
            Log($"playback start: macro recorded against user {args.Macro.RecordedAgainstUserId} ({args.Macro.RecordedAgainstDisplayName ?? "(unknown)"})");
        };
        _player.Ended += (_, _) =>
        {
            State = _recorder.IsRecording ? PluginState.Recording : PluginState.Idle;
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

    /// <summary>Foreground resolution at this instant. UI polls this on a 250ms timer.</summary>
    public AccountRegistry.AccountInfo? ResolveForegroundNow() => _foreground.ResolveForegroundAccount();

    /// <summary>Invoke the Ctrl+Shift+R path (record toggle). Hotkeys + UI buttons share the same handler.</summary>
    public void TriggerRecordToggle() => OnHotkey(HotkeyKind.RecordToggle);

    /// <summary>Invoke the Ctrl+Shift+P path (play last).</summary>
    public void TriggerPlay() => OnHotkey(HotkeyKind.Play);

    /// <summary>Invoke the Esc path (abort).</summary>
    public void TriggerAbort() => OnHotkey(HotkeyKind.Abort);

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
                if (_lastMacro is null) Log("Ctrl+Shift+P ignored — no macro to play.");
                else _ = Task.Run(async () =>
                {
                    var result = await _player.PlayAsync(_lastMacro);
                    Log($"playback result: {result.Outcome}{(result.Reason is null ? "" : " — " + result.Reason)}");
                });
                break;
            case HotkeyKind.Abort:
                if (_player.Abort()) Log("Playback aborted (Esc).");
                else Log("Esc ignored — nothing playing.");
                break;
        }
    }

    private void StartRecording()
    {
        var account = _foreground.ResolveForegroundAccount();
        if (account is null)
        {
            Log("Record refused — foreground window isn't a RoRoRo-managed Roblox process.");
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
            Log($"Recording started — bound to user {account.RobloxUserId} ({account.DisplayName}).");
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

        if (bound is null) { Log("Stop without bound account — discarded."); return; }
        if (events.Count == 0) { Log("Stop — 0 events captured."); return; }

        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: $"Recording {DateTimeOffset.Now:HH:mm:ss}",
            RecordMode: "PerWindow",
            RecordedAgainstUserId: bound.RobloxUserId,
            RecordedAgainstDisplayName: bound.DisplayName,
            InterAltDelayMs: null,
            RecordedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Events: events.ToList());

        try
        {
            Store.Save(macro);
            _lastMacro = macro;
            RaiseUI(() => MacrosChanged?.Invoke());
            Log($"Saved macro: {events.Count} events, duration {macro.Duration.TotalSeconds:F1}s.");
        }
        catch (Exception ex)
        {
            Log($"Save failed: {ex.Message}");
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
