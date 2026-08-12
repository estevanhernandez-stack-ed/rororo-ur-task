using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Labs626.UrTask.PluginHost;
using ROROROblox.App.Plugins;
using ROROROblox.PluginContract;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Integration test for <see cref="PluginClient"/> against a real RoRoRo gRPC host.
/// Spins up ROROROblox.App's PluginHostStartupService on a per-test named pipe
/// (same pattern ROROROblox.PluginTestHarness uses internally), connects the
/// plugin's own client, and verifies handshake + event delivery.
///
/// Uses production-shape interceptor wiring (currentPluginAccessor returns null;
/// plugin id resolves from the x-plugin-id header injected by
/// <c>HeaderInjectingCallInvoker</c>) so this test mirrors how the plugin
/// actually runs in production, not a fixed-accessor fake.
/// </summary>
public class PluginClientIntegrationTests
{
    private const string PluginId = "626labs.ur-task";

    [Fact]
    public async Task ConnectAsync_HandshakeSucceeds_AndEventsFlow()
    {
        var pipeName = $"rororo-ur-task-test-{Guid.NewGuid():N}";

        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = PluginId,
            Name = "RoRoRo Ur Task",
            Version = "0.1.0",
            ContractVersion = "1.0",
            Publisher = "626 Labs LLC",
            Description = "Test fixture",
            Capabilities = new[]
            {
                "host.events.account-launched",
                "host.events.account-exited",
            },
        };
        var installed = new InstalledPlugin
        {
            Manifest = manifest,
            InstallDir = Path.GetTempPath(),
            Consent = new ConsentRecord
            {
                PluginId = PluginId,
                GrantedCapabilities = manifest.Capabilities,
                AutostartEnabled = false,
            },
        };

        var bus = new InProcessPluginEventBus();
        var hostService = new PluginHostService(
            new SingleInstalledPluginLookup(installed),
            "1.4.0-test",
            "1.0",
            new FixedHostState("On"),
            new EmptyAccounts(),
            bus,
            new NoOpLauncher(),
            new PluginUITranslator(new NullUIHost()),
            new NullActivitySnapshotProvider(),
            new NullActivityMarker(),
            new NoOpAccountStopper());

        // Production-shape interceptor — accessor returns null, plugin id comes
        // from x-plugin-id header injected by HeaderInjectingCallInvoker.
        var interceptor = new CapabilityInterceptor(
            currentPluginAccessor: () => null,
            consentLookup: id => id == PluginId ? manifest.Capabilities : Array.Empty<string>());

        var startup = new PluginHostStartupService(
            hostService, interceptor,
            NullLogger<PluginHostStartupService>.Instance,
            pipeName);

        await startup.StartAsync(CancellationToken.None);
        try
        {
            var registry = new AccountRegistry();
            await using var client = new PluginClient(PluginId, registry, pipeName);
            await client.ConnectAsync();

            Assert.Equal("1.4.0-test", client.HostVersion);

            // PluginClient.ConnectAsync starts the stream-consumer Tasks via Task.Run
            // but returns before they've actually established the streaming RPC on the
            // host side. If we publish immediately, the subscription doesn't exist yet
            // and the event is missed. Give the consumers a beat to register.
            await Task.Delay(500);

            // Publish a fake account-launched event through the host's event bus.
            // The plugin's SubscribeAccountLaunched stream should deliver it,
            // which AccountRegistry.OnLaunched picks up.
            var snapshot = new RunningAccountSnapshot(
                AccountId: Guid.NewGuid().ToString(),
                RobloxUserId: 9_999_999L,
                DisplayName: "TestAlt",
                ProcessId: 31337);
            bus.RaiseAccountLaunched(snapshot);

            // Give the event a moment to traverse: host → gRPC stream → plugin
            // consumer → AccountRegistry. 500ms is generous on a local pipe.
            var found = await WaitForAsync(
                () => registry.ResolveByPid(31337) is not null,
                timeoutMs: 2000);

            Assert.True(found, "AccountRegistry never received the launched event.");
            var info = registry.ResolveByPid(31337)!;
            Assert.Equal(9_999_999L, info.RobloxUserId);
            Assert.Equal("TestAlt", info.DisplayName);

            // Publish a matching exited event; AccountRegistry should clear the pid.
            bus.RaiseAccountExited(snapshot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var cleared = await WaitForAsync(
                () => registry.ResolveByPid(31337) is null,
                timeoutMs: 2000);
            Assert.True(cleared, "AccountRegistry never cleared the exited pid.");
        }
        finally
        {
            await startup.StopAsync(CancellationToken.None);
            await startup.DisposeAsync();
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    // ---------- Test doubles, mirroring ROROROblox.PluginTestHarness ----------

    private sealed class SingleInstalledPluginLookup : IInstalledPluginsLookup
    {
        private readonly InstalledPlugin _plugin;
        public SingleInstalledPluginLookup(InstalledPlugin p) { _plugin = p; }
        public InstalledPlugin? FindById(string id) => id == _plugin.Manifest.Id ? _plugin : null;
    }

    private sealed class FixedHostState : IPluginHostStateProvider
    {
        public FixedHostState(string s) { MultiInstanceState = s; }
        public bool MultiInstanceEnabled => MultiInstanceState == "On";
        public string MultiInstanceState { get; }
    }

    private sealed class EmptyAccounts : IRunningAccountsProvider
    {
        public IReadOnlyList<RunningAccountSnapshot> Snapshot() => Array.Empty<RunningAccountSnapshot>();
    }

    // Host's PluginHostService grew a required IActivitySnapshotProvider (9th ctor arg)
    // with the contract-0.4.0 game-aware work; the integration test only needs a
    // no-op snapshot source to construct it.
    /// <summary>
    /// Added 2026-08-11. The host grew two constructor dependencies and this test did not follow,
    /// so it stopped compiling — silently, because CI runs with <c>StandaloneTestsOnly=true</c> and
    /// never builds this file. A test the pipeline never compiles is a test that quietly stops
    /// being true, and this one is the plugin's only check against the real host.
    /// </summary>
    private sealed class NullActivityMarker : IAccountActivityMarker
    {
        public void Mark(string accountId) { }
    }

    private sealed class NoOpAccountStopper : IPluginAccountStopper
    {
        public IReadOnlyList<string> TrackedAccountIds => Array.Empty<string>();

        // False is the honest answer for a host tracking nothing — the contract defines the return
        // as "unparseable id, no tracked client, or the stop failed", and this stub has no clients.
        public bool StopAccount(string accountId) => false;
    }

    private sealed class NullActivitySnapshotProvider : IActivitySnapshotProvider
    {
        public IReadOnlyList<AccountActivitySnapshot> Snapshot() => Array.Empty<AccountActivitySnapshot>();
    }

    private sealed class NoOpLauncher : IPluginLaunchInvoker
    {
        public Task<(bool ok, string? failureReason, int processId)> RequestLaunchAsync(string accountId)
            => Task.FromResult<(bool, string?, int)>((false, "test stub", 0));

        // Brought into conformance with the host's IPluginLaunchInvoker, which
        // grew launch-to-target + current-server queries in RoRoRo v1.7.0.0.
        // Mirrors ROROROblox.PluginTestHarness's reference stub.
        public Task<(bool ok, string? failureReason, int processId)> RequestLaunchTargetAsync(
            string accountId, string? shareUrl, long? followUserId)
            => Task.FromResult<(bool, string?, int)>((false, "test stub", 0));

        public Task<CurrentServerInfo?> GetCurrentServerAsync()
            => Task.FromResult<CurrentServerInfo?>(null);
    }

    private sealed class NullUIHost : IPluginUIHost
    {
        public string AddTrayMenuItem(string p, string l, string? t, bool e, Action c) => string.Empty;
        public string AddRowBadge(string p, string t, string? c, string? tt) => string.Empty;
        public string AddStatusPanel(string p, string t, string b) => string.Empty;
        public void Update(string h, string l) { }
        public void Remove(string h) { }
    }
}
