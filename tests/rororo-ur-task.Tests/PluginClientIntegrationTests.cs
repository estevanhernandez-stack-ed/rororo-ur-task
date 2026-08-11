using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Labs626.UrTask.PluginHost;
using ROROROblox.App.Plugins;
using ROROROblox.App.Plugins.Adapters;
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
            new NoActivityMarker(),
            new NoStopper(),
            new FixedTheme());

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

    // Added when this file was repaired against the host's current PluginHostService signature.
    // It had drifted three parameters behind: the cross-repo ProjectReference means this suite
    // silently stops compiling whenever RoRoRo's contract surface grows, and nothing in either
    // repo's CI notices, because RoRoRo's CI does not build this project and this project's CI
    // does not have RoRoRo checked out.
    private sealed class NoActivityMarker : IAccountActivityMarker
    {
        public void Mark(string accountId) { }
    }

    private sealed class NoStopper : IPluginAccountStopper
    {
        public IReadOnlyList<string> TrackedAccountIds => Array.Empty<string>();
        public bool StopAccount(string accountId) => false;
    }

    /// <summary>Fixed palette so the theme feed has something to serve (contract 0.8.0).</summary>
    private sealed class FixedTheme : IThemePaletteSource
    {
        public ROROROblox.Core.Theming.ResolvedPalette? Latest { get; } = new(
            Bg: "#101010", Cyan: "#D4D4D4", Magenta: "#6E6E6E", White: "#F5F5F5",
            MutedText: "#989898", Divider: "#333333", RowBg: "#2A2A2A",
            RowExpiredBg: "#3D3D3D", RowExpiredAccent: "#D4D4D4", Navy: "#101010",
            InteractiveEdge: "#D4D4D4");
    }

    private sealed class EmptyAccounts : IRunningAccountsProvider
    {
        public IReadOnlyList<RunningAccountSnapshot> Snapshot() => Array.Empty<RunningAccountSnapshot>();
    }

    // Host's PluginHostService grew a required IActivitySnapshotProvider (9th ctor arg)
    // with the contract-0.4.0 game-aware work; the integration test only needs a
    // no-op snapshot source to construct it.
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
