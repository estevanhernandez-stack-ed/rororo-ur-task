using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;
using ROROROblox.PluginContract;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// gRPC connection to RoRoRo over its per-user named pipe. Owns the channel,
/// the handshake, the GetRunningAccounts seed of the AccountRegistry, and the
/// two long-running event-stream consumer tasks (account-launched, account-exited).
///
/// Lifecycle: <see cref="ConnectAsync"/> is one shot. Stream consumers run until
/// the cancellation token cancels or the host closes the connection. Disposal
/// cancels everything and tears down the channel.
/// </summary>
internal sealed class PluginClient : IAsyncDisposable
{
    private const string DefaultPipeName = "rororo-plugin-host";
    private const string ContractVersion = "1.0";
    private const int ConnectTimeoutMs = 10_000;

    private readonly string _pluginId;
    private readonly string _pipeName;
    private readonly AccountRegistry _accounts;
    private GrpcChannel? _channel;
    private RoRoRoHost.RoRoRoHostClient? _client;
    private Task? _launchedConsumer;
    private Task? _exitedConsumer;
    private CancellationTokenSource? _consumerCts;
    private int _hostLostFired = 0;

    /// <summary>
    /// Fires when the gRPC connection to RoRoRo breaks unexpectedly mid-session
    /// (host process killed, pipe closed without a clean Cancelled). Owner
    /// (PluginRuntime) should abort any active playback and shut down the
    /// plugin process — otherwise the AssignmentRunner happily keeps sending
    /// input to cached Roblox PIDs indefinitely, creating a zombie plugin.
    /// Guaranteed to fire at most once per PluginClient lifetime.
    /// </summary>
    public event Action? HostLost;

    public PluginClient(string pluginId, AccountRegistry accounts, string? pipeName = null)
    {
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _pipeName = pipeName ?? DefaultPipeName;
    }

    /// <summary>The RoRoRo host version reported by the handshake response.</summary>
    public string HostVersion { get; private set; } = "unknown";

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_client is not null) throw new InvalidOperationException("Already connected.");

        _channel = GrpcChannel.ForAddress("http://pipe", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ict) =>
                {
                    var pipe = new NamedPipeClientStream(".", _pipeName,
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(ConnectTimeoutMs, ict).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        pipe.Dispose();
                        throw new IOException(
                            $"Named pipe '{_pipeName}' not available after {ConnectTimeoutMs}ms. " +
                            "Is RoRoRo running?");
                    }
                    return pipe;
                },
            },
        });

        var invoker = new HeaderInjectingCallInvoker(_channel.CreateCallInvoker(), _pluginId);
        _client = new RoRoRoHost.RoRoRoHostClient(invoker);

        var handshake = await _client.HandshakeAsync(new HandshakeRequest
        {
            PluginId = _pluginId,
            ContractVersion = ContractVersion,
        }, cancellationToken: ct).ConfigureAwait(false);

        if (!handshake.Accepted)
        {
            throw new InvalidOperationException(
                $"RoRoRo rejected handshake: {handshake.RejectReason}");
        }

        HostVersion = handshake.HostVersion;

        // Seed the registry with any accounts that were already running before
        // this plugin connected — the event streams only deliver going-forward
        // changes; the GetRunningAccounts snapshot fills the gap.
        var running = await _client.GetRunningAccountsAsync(new Empty(),
            cancellationToken: ct).ConfigureAwait(false);
        foreach (var a in running.Accounts)
        {
            _accounts.OnLaunched(a.ProcessId, a.RobloxUserId, a.DisplayName, a.AccountId,
                a.PlaceId, a.PlaceName);
        }

        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _launchedConsumer = Task.Run(() => ConsumeLaunchedAsync(_consumerCts.Token));
        _exitedConsumer = Task.Run(() => ConsumeExitedAsync(_consumerCts.Token));
    }

    /// <summary>
    /// Re-fetch the running-accounts snapshot to refresh soft metadata —
    /// notably game identity, which presence fills in AFTER the launch event
    /// fired (0.4.0 contract semantics). Best-effort: failures are swallowed
    /// and callers use whatever the registry already holds.
    /// </summary>
    public async Task RefreshRunningAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            if (_client is null) return;
            var running = await _client.GetRunningAccountsAsync(new Empty(),
                cancellationToken: ct).ConfigureAwait(false);
            foreach (var a in running.Accounts)
            {
                _accounts.OnLaunched(a.ProcessId, a.RobloxUserId, a.DisplayName, a.AccountId,
                    a.PlaceId, a.PlaceName);
            }
        }
        catch (Exception)
        {
            // Soft-metadata refresh only — must never disturb the recording flow.
        }
    }

    private async Task ConsumeLaunchedAsync(CancellationToken ct)
    {
        try
        {
            using var call = _client!.SubscribeAccountLaunched(new SubscriptionRequest(),
                cancellationToken: ct);
            await foreach (var evt in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _accounts.OnLaunched(evt.ProcessId, evt.RobloxUserId,
                    evt.DisplayName, evt.AccountId, evt.PlaceId, evt.PlaceName);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // host closed the stream; expected on shutdown.
        }
        catch (Exception)
        {
            // Any other exception (Unavailable, Internal, IO) signals the host
            // died unexpectedly — pipe broken, RoRoRo killed, etc.
            SignalHostLost();
        }
    }

    private async Task ConsumeExitedAsync(CancellationToken ct)
    {
        try
        {
            using var call = _client!.SubscribeAccountExited(new SubscriptionRequest(),
                cancellationToken: ct);
            await foreach (var evt in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _accounts.OnExited(evt.ProcessId);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // host closed the stream; expected on shutdown.
        }
        catch (Exception)
        {
            SignalHostLost();
        }
    }

    /// <summary>
    /// Fire HostLost exactly once, then cancel the other consumer so it
    /// doesn't fire a duplicate. Safe to call from either consumer's catch
    /// (or both racing).
    /// </summary>
    private void SignalHostLost()
    {
        if (Interlocked.CompareExchange(ref _hostLostFired, 1, 0) != 0) return;
        try { _consumerCts?.Cancel(); } catch { /* race with dispose */ }
        try { HostLost?.Invoke(); } catch { /* handler exceptions swallowed */ }
    }

    public async ValueTask DisposeAsync()
    {
        _consumerCts?.Cancel();
        if (_launchedConsumer is not null) await _launchedConsumer.ConfigureAwait(false);
        if (_exitedConsumer is not null) await _exitedConsumer.ConfigureAwait(false);
        _consumerCts?.Dispose();
        _channel?.Dispose();
    }
}
