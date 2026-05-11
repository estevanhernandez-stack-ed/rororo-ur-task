using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask;

internal static class Program
{
    private const string PluginId = "626labs.ur-task";

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine($"RoRoRo Ur Task v0.1.0 starting (id: {PluginId})");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var registry = new AccountRegistry();
        registry.AccountAdded += (_, info) => Console.WriteLine(
            $"  + account: pid={info.Pid} userId={info.RobloxUserId} name={info.DisplayName}");
        registry.AccountRemoved += (_, info) => Console.WriteLine(
            $"  - account: pid={info.Pid} userId={info.RobloxUserId} name={info.DisplayName}");

        await using var client = new PluginClient(PluginId, registry);
        var foreground = new ForegroundWatcher(registry);

        try
        {
            Console.WriteLine($"Connecting to RoRoRo over named pipe...");
            await client.ConnectAsync(cts.Token);
            Console.WriteLine($"Connected. Host version: {client.HostVersion}");
            Console.WriteLine($"Subscribed to account-launched + account-exited streams.");
            Console.WriteLine($"Initial running-accounts snapshot: {registry.Snapshot().Count} entries.");
            Console.WriteLine("Press Ctrl+C to exit.");

            // Smoke loop for task 3: poll the foreground window every 2s and log
            // which RoRoRo-managed account (if any) owns it. Real consumers
            // (recording start, playback pre-flight, playback continuous check)
            // query the watcher on demand — this loop is just proof-of-life.
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var account = foreground.ResolveForegroundAccount();
                    if (account is not null)
                    {
                        Console.WriteLine(
                            $"  ~ foreground: pid={account.Pid} userId={account.RobloxUserId} ({account.DisplayName})");
                    }
                    await Task.Delay(2000, cts.Token);
                }
            }, cts.Token);

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
}
