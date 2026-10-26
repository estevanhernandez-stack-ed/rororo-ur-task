using Grpc.Core;

namespace Labs626.UrTask.PluginHost;

/// <summary>RoRoRo's per-account activity clock, read and credited. A null read means unavailable.</summary>
internal interface IHostActivity
{
    Task<IReadOnlyList<(string AccountId, DateTimeOffset LastActivity)>?> GetAsync(CancellationToken ct);
    Task MarkActiveAsync(string accountId, CancellationToken ct);
}

/// <summary>
/// Decides what a failed host call costs: a declined capability is logged once per call kind for the
/// plugin's lifetime, any other failure at most once per distinct status per 10 minutes. Never throws.
/// </summary>
internal sealed class HostCallFailures(Action<string> log, Func<DateTimeOffset>? now = null)
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly HashSet<string> _denied = new();
    private readonly Dictionary<string, DateTimeOffset> _lastLogged = new();
    private readonly object _gate = new();

    public void Report(string capability, string fallback, Exception ex)
    {
        string? line = null;
        lock (_gate)
        {
            if (ex is RpcException { StatusCode: StatusCode.PermissionDenied })
            {
                if (_denied.Add(capability))
                    line = $"keep-alive: RoRoRo didn't grant {capability}; {fallback}";
            }
            else
            {
                var key = ex is RpcException r ? r.StatusCode.ToString() : ex.GetType().Name;
                var t = _now();
                if (!_lastLogged.TryGetValue(key, out var last) || t - last >= Quiet)
                {
                    _lastLogged[key] = t;
                    line = $"keep-alive: RoRoRo activity call ({capability}) failed: {key}; {fallback}";
                }
            }
        }
        if (line is null) return;
        try { log(line); } catch { }
    }
}

internal sealed class HostActivity(PluginClient client, Action<string> log) : IHostActivity
{
    private const string QueryCapability = "host.queries.account-activity";
    private const string MarkCapability = "host.commands.mark-account-active";
    private readonly HostCallFailures _failures = new(log);

    public async Task<IReadOnlyList<(string AccountId, DateTimeOffset LastActivity)>?> GetAsync(CancellationToken ct)
    {
        try
        {
            var list = await client.GetAccountActivityAsync(ct).ConfigureAwait(false);
            if (list is null) return null;
            var result = new List<(string, DateTimeOffset)>();
            foreach (var a in list.Items)
            {
                if (a.LastActivityUnixMs <= 0) continue;   // RoRoRo's "never active"
                result.Add((a.AccountId, DateTimeOffset.FromUnixTimeMilliseconds(a.LastActivityUnixMs)));
            }
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _failures.Report(QueryCapability, "keep-alive uses its own clock", ex);
            return null;
        }
    }

    public async Task MarkActiveAsync(string accountId, CancellationToken ct)
    {
        try { await client.MarkAccountActiveAsync(accountId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _failures.Report(MarkCapability, "RoRoRo's clock won't hear about our Spaces", ex); }
    }
}

/// <summary>Folds RoRoRo's activity into the ledger on an interval, first poll immediately.</summary>
internal sealed class HostActivityPoller(IHostActivity host, Macros.KeepAliveLedger ledger, Action<string> log)
{
    private readonly HostCallFailures _failures = new(log);

    public async Task RunAsync(TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(every, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One poll. Never throws: a failed or declined read is reported and the ledger stays as it was.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            var activity = await host.GetAsync(ct).ConfigureAwait(false);
            if (activity is null) return;
            foreach (var (accountId, last) in activity)
                ledger.MergeHostActivity(accountId, last);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _failures.Report("host.queries.account-activity", "keep-alive uses its own clock", ex); }
    }
}
