using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
    private Task? _themeConsumer;
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

    /// <summary>
    /// The host's active palette: once on connect, then again on every theme switch. Resolved
    /// colours only — there is no theme id to look up, which is the whole point of the feed
    /// replacing the old read-RoRoRo's-settings-file approach.
    /// </summary>
    public event Action<ThemePalette>? ThemeChanged;

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

        // Paint to the host's theme immediately. Same reason the running-accounts snapshot is
        // fetched above: the stream only carries changes going forward, and most sessions never
        // touch RoRoRo's theme picker, so a subscribe-only plugin would sit on its fallback
        // colour indefinitely. Best-effort — a host too old to answer this is still a host worth
        // talking to, so theming degrades and nothing else does.
        try
        {
            var theme = await _client.GetThemeAsync(new Empty(), cancellationToken: ct)
                .ConfigureAwait(false);
            Diagnostics.DiagLog.Write($"Theme: host palette received on connect (bg {theme.Bg}, cyan {theme.Cyan}).");
            ThemeChanged?.Invoke(theme);
        }
        catch (RpcException ex)
        {
            // No feed on this host, or none applied yet. Keep the fallback palette -- but SAY so.
            // Silence here is what made the first end-to-end check eyes-only: connected-but-wrong-
            // colour and never-asked look identical from outside, and they need different fixes.
            Diagnostics.DiagLog.Write(
                $"Theme: GetTheme failed ({ex.StatusCode}); staying on the fallback palette. "
                + "A host older than 1.19 has no theme feed.");
        }

        _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _launchedConsumer = Task.Run(() => ConsumeLaunchedAsync(_consumerCts.Token));
        _exitedConsumer = Task.Run(() => ConsumeExitedAsync(_consumerCts.Token));
        _themeConsumer = Task.Run(() => ConsumeThemeAsync(_consumerCts.Token));
    }

    /// <summary>RoRoRo's per-account activity clock. Null when not connected. Throws RpcException
    /// (PermissionDenied when the capability was declined).</summary>
    public async Task<AccountActivityList?> GetAccountActivityAsync(CancellationToken ct = default)
    {
        var client = _client;
        if (client is null) return null;
        return await client.GetAccountActivityAsync(new Empty(), cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Tells RoRoRo an account just got input from us. No-op when not connected.</summary>
    public async Task MarkAccountActiveAsync(string accountId, CancellationToken ct = default)
    {
        var client = _client;
        if (client is null) return;
        await client.MarkAccountActiveAsync(new MarkAccountActiveRequest { AccountId = accountId },
            cancellationToken: ct).ConfigureAwait(false);
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

    private Task ConsumeLaunchedAsync(CancellationToken ct) => ConsumeStreamAsync(
        LaunchedStream, () => ReadLaunchedAsync(ct),
        evt => _accounts.OnLaunched(evt.ProcessId, evt.RobloxUserId,
            evt.DisplayName, evt.AccountId, evt.PlaceId, evt.PlaceName),
        () => PollRunningAccountsAsync(LaunchedStream, exitedSide: false, _accounts, FetchRunningAsync,
            DegradedPollInterval, SignalHostLost, Diagnostics.DiagLog.Write, ct),
        SignalHostLost, Diagnostics.DiagLog.Write, ct);

    private Task ConsumeExitedAsync(CancellationToken ct) => ConsumeStreamAsync(
        ExitedStream, () => ReadExitedAsync(ct),
        evt => _accounts.OnExited(evt.ProcessId),
        () => PollRunningAccountsAsync(ExitedStream, exitedSide: true, _accounts, FetchRunningAsync,
            DegradedPollInterval, SignalHostLost, Diagnostics.DiagLog.Write, ct),
        SignalHostLost, Diagnostics.DiagLog.Write, ct);

    private async IAsyncEnumerable<AccountLaunchedEvent> ReadLaunchedAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _client!.SubscribeAccountLaunched(new SubscriptionRequest(),
            cancellationToken: ct);
        await foreach (var evt in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    private async IAsyncEnumerable<AccountExitedEvent> ReadExitedAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var call = _client!.SubscribeAccountExited(new SubscriptionRequest(),
            cancellationToken: ct);
        await foreach (var evt in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    internal const string LaunchedStream = "account-launched";
    internal const string ExitedStream = "account-exited";

    /// <summary>
    /// Reads one account stream to its end and decides what the end means. Static, and fed by
    /// delegates, so the decision can be tested without a host: the two consumers above differ
    /// only in what they open and what they tell the registry.
    /// </summary>
    internal static async Task ConsumeStreamAsync<T>(string stream, Func<IAsyncEnumerable<T>> open,
        Action<T> onEvent, Func<Task> degrade, Action hostLost, Action<string> log, CancellationToken ct)
    {
        Exception? error = null;
        try
        {
            await foreach (var evt in open().ConfigureAwait(false))
            {
                Notify(stream, () => onEvent(evt), log);
            }
        }
        catch (Exception ex) { error = ex; }

        var verdict = Classify($"{stream} stream", error, ct.IsCancellationRequested);
        // Always before SignalHostLost: the runtime's "connection lost" line says nothing about
        // why, and until this was logged neither did anything else.
        if (verdict.Log is not null) log(verdict.Log);
        if (verdict.End == StreamEnd.HostLost) hostLost();
        else if (verdict.End == StreamEnd.Degrade) await degrade().ConfigureAwait(false);
    }

    /// <summary>
    /// What the end of an account stream (or of a poll standing in for one) means. Pure, so every
    /// branch is unit-tested. <paramref name="what"/> names it in the log line.
    /// <para>
    /// A refused stream (the user unticked that capability on RoRoRo's consent sheet, or the host
    /// is too old to have the method) is not a dead host: the handshake and the snapshot worked.
    /// Exiting over it told the user RoRoRo had gone while it sat there running.
    /// </para>
    /// </summary>
    internal static StreamVerdict Classify(string what, Exception? ex, bool ownTokenCancelled)
    {
        switch (ex)
        {
            case RpcException { StatusCode: StatusCode.PermissionDenied or StatusCode.Unimplemented or StatusCode.FailedPrecondition } refused:
                return new(StreamEnd.Degrade,
                    $"Accounts: the host refused the {what} ({refused.StatusCode}). RoRoRo is still there, so the plugin "
                    + $"keeps running and asks for the running accounts every {DegradedPollInterval.TotalSeconds:0} s instead.");
            case null:
            case OperationCanceledException:
            case RpcException { StatusCode: StatusCode.Cancelled }:
                // Our own shutdown ends a stream this way, and so does a host that closes down
                // gracefully: its stream handlers swallow their cancellation and simply return.
                // Only our own token tells the two apart. Read as a normal stop when we did not
                // ask for it, the plugin ran on with a frozen account list and never noticed.
                if (ownTokenCancelled) return new(StreamEnd.Normal, null);
                var how = ex is null ? "ended with no error" : $"was cancelled ({(ex as RpcException)?.StatusCode.ToString() ?? ex.GetType().Name})";
                return new(StreamEnd.HostLost,
                    $"Accounts: the {what} {how}, and the plugin did not ask it to. The host has gone, "
                    + "so treating it as lost rather than running on with an account list that can no longer change.");
            case RpcException rpc:
                return new(StreamEnd.HostLost,
                    $"Accounts: the {what} failed (RpcException, status {rpc.StatusCode}: {rpc.Status.Detail}). Treating the host as lost.");
            default:
                return new(StreamEnd.HostLost,
                    $"Accounts: the {what} failed ({ex.GetType().Name}: {ex.Message}). Treating the host as lost.");
        }
    }

    /// <summary>Tells the registry about one event. The registry raises its events synchronously
    /// into the runtime and the UI, so a subscriber bug would otherwise surface here, in the
    /// stream reader, and be reported as a lost host.</summary>
    private static void Notify(string stream, Action tell, Action<string> log)
    {
        try { tell(); }
        catch (Exception ex)
        {
            log($"Accounts: a listener threw on an {stream} update ({ex.GetType().Name}: {ex.Message}). Carrying on.");
        }
    }

    internal static readonly TimeSpan DegradedPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Pids the registry knew that the host's fresh snapshot no longer lists.</summary>
    internal static IReadOnlyList<int> ExitedPids(IEnumerable<int> knownPids, IEnumerable<int> runningPids)
    {
        var running = runningPids.ToHashSet();
        return knownPids.Where(pid => !running.Contains(pid)).ToArray();
    }

    /// <summary>Snapshot accounts the registry does not hold exactly as listed. Re-reporting every
    /// account on every poll would raise AccountAdded, and write an "account launched" line, every
    /// 30 s for accounts that have not changed.</summary>
    internal static IReadOnlyList<AccountRegistry.AccountInfo> NewOrChanged(
        IEnumerable<AccountRegistry.AccountInfo> known, IEnumerable<AccountRegistry.AccountInfo> running)
    {
        var held = known.ToHashSet();
        return running.Where(a => !held.Contains(a)).ToArray();
    }

    /// <summary>
    /// The degraded mode for one refused stream: ask the ungated GetRunningAccounts on an interval
    /// and tell the registry what that stream would have. The launched side adds what is new; the
    /// exited side removes what is gone. Each side does only its own half, so a stream that still
    /// works is never second-guessed by a snapshot.
    /// <para>
    /// With both streams refused these polls are also the only thing that notices the host dying,
    /// so a failed poll is reported the way a failed stream is.
    /// </para>
    /// </summary>
    internal static async Task PollRunningAccountsAsync(string stream, bool exitedSide, AccountRegistry accounts,
        Func<CancellationToken, Task<IReadOnlyList<AccountRegistry.AccountInfo>>> fetchRunning,
        TimeSpan interval, Action hostLost, Action<string> log, CancellationToken ct)
    {
        bool refusalLogged = false;
        while (!ct.IsCancellationRequested)
        {
            Exception? error = null;
            try
            {
                // Read before the request goes out. An account that launches while the answer is
                // on its way is not in this list, so the exited side cannot take it for an exit.
                var known = accounts.Snapshot();
                var running = await fetchRunning(ct).ConfigureAwait(false);
                if (exitedSide)
                {
                    foreach (var pid in ExitedPids(known.Select(a => a.Pid), running.Select(a => a.Pid)))
                        Notify(stream, () => accounts.OnExited(pid), log);
                }
                else
                {
                    foreach (var a in NewOrChanged(known, running))
                        Notify(stream, () => accounts.OnLaunched(a.Pid, a.RobloxUserId, a.DisplayName, a.AccountId, a.PlaceId, a.PlaceName), log);
                }
            }
            catch (Exception ex) { error = ex; }

            if (error is not null)
            {
                var verdict = Classify($"running-accounts check (standing in for the {stream} stream)", error, ct.IsCancellationRequested);
                if (verdict.End == StreamEnd.Normal) return;
                if (verdict.End == StreamEnd.HostLost)
                {
                    log(verdict.Log!);
                    hostLost();
                    return;
                }
                // Refused as well. The call is ungated today, so this is a host that changed under
                // us: say so once and keep asking, the host is alive and may answer later.
                if (!refusalLogged)
                {
                    refusalLogged = true;
                    log($"Accounts: the host refused the running-accounts check too ({(error as RpcException)?.StatusCode}). "
                        + $"The account list cannot be kept current without the {stream} stream; still trying.");
                }
            }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<IReadOnlyList<AccountRegistry.AccountInfo>> FetchRunningAsync(CancellationToken ct)
    {
        var running = await _client!.GetRunningAccountsAsync(new Empty(), cancellationToken: ct).ConfigureAwait(false);
        return running.Accounts
            .Select(a => new AccountRegistry.AccountInfo(a.ProcessId, a.RobloxUserId, a.DisplayName, a.AccountId, a.PlaceId, a.PlaceName))
            .ToArray();
    }

    /// <summary>
    /// Theme stream consumer.
    /// <para>
    /// <b>Deliberately does not call <see cref="SignalHostLost"/>.</b> The other two consumers do,
    /// because losing account events means the plugin's model of the world is wrong. Losing the
    /// theme feed means the window is the wrong colour, and tearing down a working macro recorder
    /// over a colour would be a worse bug than the one this feed fixes. Failures here are
    /// swallowed and the last palette stays on screen.
    /// </para>
    /// </summary>
    private async Task ConsumeThemeAsync(CancellationToken ct)
    {
        try
        {
            using var call = _client!.SubscribeThemeChanged(new SubscriptionRequest(),
                cancellationToken: ct);
            await foreach (var palette in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Diagnostics.DiagLog.Write($"Theme: host pushed a palette (bg {palette.Bg}, cyan {palette.Cyan}).");
                ThemeChanged?.Invoke(palette);
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            // Host gone, stream refused, host too old to have the RPC at all. All the same
            // answer: stop following, keep the colours we have, stay usable -- and log, because
            // "no longer following the theme" is invisible until the user switches theme and
            // nothing happens.
            Diagnostics.DiagLog.Write(
                $"Theme: stopped following the host ({ex.GetType().Name}). "
                + "Colours stay as they are; the plugin keeps working.");
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

/// <summary>What the end of an account stream means for the plugin.</summary>
internal enum StreamEnd
{
    /// <summary>The plugin's own shutdown. Nothing to do and nothing to say.</summary>
    Normal,
    /// <summary>The host refused the stream but is alive: poll the running accounts instead.</summary>
    Degrade,
    /// <summary>The host is gone: the plugin stops rather than send input to stale windows.</summary>
    HostLost,
}

/// <param name="Log">The diagnostics line to write first; null when the end is silent.</param>
internal readonly record struct StreamVerdict(StreamEnd End, string? Log);
