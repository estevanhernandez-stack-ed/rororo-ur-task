using Grpc.Core;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class HostActivityTests
{
    private static readonly KeepAliveLimit Fifteen = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), false);
    private static readonly DateTimeOffset Wall = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);

    private sealed class FakeHost(Func<IReadOnlyList<(string, DateTimeOffset)>?> get) : IHostActivity
    {
        public int Polls;
        public Task<IReadOnlyList<(string AccountId, DateTimeOffset LastActivity)>?> GetAsync(CancellationToken ct)
        {
            Polls++;
            return Task.FromResult(get());
        }
        public Task MarkActiveAsync(string accountId, CancellationToken ct) => Task.CompletedTask;
    }

    private static (KeepAliveLedger ledger, long[] now) Ledger(params AccountRegistry.AccountInfo[] alts)
    {
        var now = new long[] { 600_000 };
        var ledger = new KeepAliveLedger(() => now[0], () => Wall);
        foreach (var a in alts) ledger.Track(a, Fifteen);
        return (ledger, now);
    }

    private static async Task Poll(HostActivityPoller poller, FakeHost host, int polls)
    {
        using var cts = new CancellationTokenSource();
        var run = poller.RunAsync(TimeSpan.FromMilliseconds(5), cts.Token);
        while (host.Polls < polls) await Task.Delay(2);
        cts.Cancel();
        await run;
    }

    [Fact]
    public async Task Poll_merges_activity_into_the_ledger()
    {
        var a = new AccountRegistry.AccountInfo(1, 1, "alt1", "acct-1");
        var (ledger, _) = Ledger(a);
        var host = new FakeHost(() => new[] { ("acct-1", Wall - TimeSpan.FromMinutes(2)) });
        await Poll(new HostActivityPoller(host, ledger, _ => { }), host, 1);

        var e = ledger.Get(1)!;
        Assert.Equal(InputSource.Host, e.LastSource);
        Assert.Equal(600_000 - 120_000, e.LastInputMs);
    }

    [Fact]
    public async Task Permission_denied_logs_once_and_keeps_running()
    {
        var a = new AccountRegistry.AccountInfo(1, 1, "alt1", "acct-1");
        var (ledger, _) = Ledger(a);
        var host = new FakeHost(() => throw new RpcException(new Status(StatusCode.PermissionDenied, "no")));
        var lines = new List<string>();
        await Poll(new HostActivityPoller(host, ledger, lines.Add), host, 3);

        var line = Assert.Single(lines);
        Assert.Contains("host.queries.account-activity", line);
        Assert.Null(ledger.Get(1)!.LastInputMs);
    }

    [Fact]
    public async Task Other_failures_are_quiet_and_do_not_stop_the_poll()
    {
        var (ledger, _) = Ledger();
        var host = new FakeHost(() => throw new RpcException(new Status(StatusCode.Unavailable, "gone")));
        var lines = new List<string>();
        await Poll(new HostActivityPoller(host, ledger, lines.Add), host, 3);
        Assert.Single(lines);
    }

    [Fact]
    public async Task Unknown_ids_and_future_times_are_ignored()
    {
        var a = new AccountRegistry.AccountInfo(1, 1, "alt1", "acct-1");
        var (ledger, _) = Ledger(a);
        var host = new FakeHost(() => new[]
        {
            ("stranger", Wall),
            ("acct-1", Wall + TimeSpan.FromHours(3)),
        });
        await Poll(new HostActivityPoller(host, ledger, _ => { }), host, 1);

        // Unknown id ignored; the future time counts as now, never later than now.
        Assert.Equal(1, ledger.Snapshot().Count);
        Assert.Equal(600_000, ledger.Get(1)!.LastInputMs);
    }
}
