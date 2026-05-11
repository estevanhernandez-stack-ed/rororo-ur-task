using Labs626.UrTask.Hotkeys;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask;

/// <summary>
/// v0.1 smoke harness. Wires the gRPC client, the foreground watcher, the
/// macro recorder + player + auto-stop coordinator, and the global hotkeys
/// into a single console app. F8 toggles record/stop; F5 plays the most
/// recently saved macro; Esc aborts an active playback.
///
/// The UI (recorder window + tray icon) lands in tasks 9 + 10 and replaces
/// this Main with a proper WPF Application. v0.1 console mode is just enough
/// to run the full record/play loop end-to-end against a live RoRoRo for
/// checkpoint 3 verification.
/// </summary>
internal static class Program
{
    private const string PluginId = "626labs.ur-task";

    private static Macro? _lastMacro;
    private static AccountRegistry.AccountInfo? _recordingBoundAccount;

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"RoRoRo Ur Task v0.1.0 starting (id: {PluginId})");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Components
        var registry = new AccountRegistry();
        registry.AccountAdded += (_, info) => Log(
            $"  + account: pid={info.Pid} userId={info.RobloxUserId} ({info.DisplayName})");
        registry.AccountRemoved += (_, info) => Log(
            $"  - account: pid={info.Pid} userId={info.RobloxUserId} ({info.DisplayName})");

        var foreground = new ForegroundWatcher(registry);
        var recorder = new MacroRecorder();
        var player = new MacroPlayer(foreground);
        _ = new AutoStopCoordinator(player, registry);
        var store = new MacroStore();

        player.Started += (_, args) => Log(
            $"  > playing macro {args.Macro.Id} ({args.Macro.Events.Count} events, bound user {args.Macro.BoundUserId})");
        player.Ended += (_, args) => Log($"  ] playback ended (macro {args.Macro.Id})");

        using var hotkeys = new HotkeyService();
        hotkeys.HotkeyPressed += kind => OnHotkey(kind, recorder, player, store, foreground);

        await using var client = new PluginClient(PluginId, registry);

        try
        {
            Console.WriteLine("Connecting to RoRoRo over named pipe...");
            await client.ConnectAsync(cts.Token);
            Console.WriteLine($"Connected. Host version: {client.HostVersion}");
            Console.WriteLine($"Initial running-accounts snapshot: {registry.Snapshot().Count} entries.");

            var loaded = store.LoadAll();
            Console.WriteLine($"Loaded {loaded.Macros.Count} macros from {store.Directory}.");
            if (loaded.Failures.Count > 0)
            {
                Console.WriteLine($"  {loaded.Failures.Count} failed to load:");
                foreach (var f in loaded.Failures)
                    Console.WriteLine($"    - {Path.GetFileName(f.Path)}: {f.Reason}");
            }
            if (loaded.Macros.Count > 0)
            {
                _lastMacro = loaded.Macros[^1];
                Log($"Most-recent macro pinned for F5: {_lastMacro.Id} ({_lastMacro.Events.Count} events)");
            }

            hotkeys.Start();
            Console.WriteLine("Hotkeys: F8 = record/stop · F5 = play last · Esc = abort.");
            Console.WriteLine("Press Ctrl+C to exit.");

            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Shutting down.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Plugin error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static void OnHotkey(HotkeyKind kind, MacroRecorder recorder, MacroPlayer player,
        MacroStore store, ForegroundWatcher foreground)
    {
        switch (kind)
        {
            case HotkeyKind.RecordToggle:
                if (!recorder.IsRecording) StartRecording(recorder, foreground);
                else StopAndSaveRecording(recorder, store);
                break;
            case HotkeyKind.Play:
                if (_lastMacro is null)
                {
                    Log("F5 ignored — no macro to play.");
                    return;
                }
                _ = Task.Run(async () =>
                {
                    var result = await player.PlayAsync(_lastMacro);
                    Log($"  result: {result.Outcome}{(result.Reason is null ? "" : " — " + result.Reason)}");
                });
                break;
            case HotkeyKind.Abort:
                if (player.Abort()) Log("Esc — playback aborted.");
                else Log("Esc ignored — nothing playing.");
                break;
        }
    }

    private static void StartRecording(MacroRecorder recorder, ForegroundWatcher foreground)
    {
        var account = foreground.ResolveForegroundAccount();
        if (account is null)
        {
            Log("F8 record refused — foreground window isn't a RoRoRo-managed Roblox process.");
            return;
        }
        Log($"F8 record start — bound to user {account.RobloxUserId} ({account.DisplayName}).");
        try
        {
            recorder.Start(HotkeyService.RegisteredVkCodes);
            _recordingBoundAccount = account;
        }
        catch (Exception ex)
        {
            Log($"Record failed to start: {ex.Message}");
        }
    }

    private static void StopAndSaveRecording(MacroRecorder recorder, MacroStore store)
    {
        var events = recorder.Stop();
        var bound = _recordingBoundAccount;
        _recordingBoundAccount = null;

        if (bound is null)
        {
            Log("F8 stop without bound account — discarded.");
            return;
        }
        if (events.Count == 0)
        {
            Log("F8 stop — 0 events captured, nothing saved.");
            return;
        }

        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: $"Recording {DateTimeOffset.Now:HH:mm:ss}",
            BoundUserId: bound.RobloxUserId,
            BoundAccountId: bound.AccountId,
            BoundDisplayName: bound.DisplayName,
            RecordedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Events: events.ToList());

        try
        {
            store.Save(macro);
            _lastMacro = macro;
            Log($"F8 stop — saved macro {macro.Id}: {events.Count} events, duration {macro.Duration.TotalSeconds:F1}s.");
        }
        catch (Exception ex)
        {
            Log($"F8 stop — save failed: {ex.Message}");
        }
    }

    private static void Log(string message) => Console.WriteLine(message);
}
