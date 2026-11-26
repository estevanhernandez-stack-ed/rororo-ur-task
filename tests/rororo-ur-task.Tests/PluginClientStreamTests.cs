using System.IO;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>
/// How PluginClient reads the end of an account stream, without a host. The real client is only
/// exercised by the integration test, which needs the sibling host repo; the decision itself is
/// static and fed by delegates, so every branch runs here.
/// </summary>
public class PluginClientStreamTests
{
    private static RpcException Rpc(StatusCode code) => new(new Status(code, "detail from the host"));

    private static async IAsyncEnumerable<int> Stream(IEnumerable<int> events, Exception? thenThrow = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var e in events)
        {
            await Task.Yield();
            yield return e;
        }
        if (thenThrow is not null) throw thenThrow;
    }

    private sealed class Outcome
    {
        public readonly List<int> Events = new();
        public readonly List<string> Log = new();
        public int HostLost;
        public int Degraded;
    }

    private static async Task<Outcome> Consume(IEnumerable<int> events, Exception? thenThrow,
        Action<int>? onEvent = null, CancellationToken ct = default)
    {
        var o = new Outcome();
        await PluginClient.ConsumeStreamAsync(PluginClient.ExitedStream, () => Stream(events, thenThrow),
            e => { onEvent?.Invoke(e); o.Events.Add(e); },
            () => { o.Degraded++; return Task.CompletedTask; },
            () => o.HostLost++, o.Log.Add, ct);
        return o;
    }

    // ---------- L4-01: a refused stream is not a lost host ----------

    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unimplemented)]
    [InlineData(StatusCode.FailedPrecondition)]
    public async Task A_refused_stream_degrades_and_does_not_report_the_host_lost(StatusCode code)
    {
        var o = await Consume(Array.Empty<int>(), Rpc(code));

        Assert.Equal(0, o.HostLost);
        Assert.Equal(1, o.Degraded);
        var line = Assert.Single(o.Log);
        Assert.Contains("account-exited", line);
        Assert.Contains(code.ToString(), line);
    }

    [Fact]
    public async Task A_broken_stream_logs_the_status_before_it_reports_the_host_lost()
    {
        var o = await Consume(new[] { 1 }, Rpc(StatusCode.Unavailable));

        Assert.Equal(1, o.HostLost);
        Assert.Equal(0, o.Degraded);
        var line = Assert.Single(o.Log);
        Assert.Contains("account-exited", line);
        Assert.Contains("RpcException", line);
        Assert.Contains("Unavailable", line);
    }

    [Fact]
    public async Task A_non_rpc_failure_logs_its_type_before_it_reports_the_host_lost()
    {
        var o = await Consume(Array.Empty<int>(), new IOException("pipe is broken"));

        Assert.Equal(1, o.HostLost);
        var line = Assert.Single(o.Log);
        Assert.Contains("IOException", line);
        Assert.Contains("pipe is broken", line);
    }

    [Fact]
    public async Task A_listener_that_throws_is_logged_and_the_stream_keeps_being_read()
    {
        using var own = new CancellationTokenSource();
        var seen = new List<int>();
        var o = await Consume(new[] { 1, 2, 3 }, new OperationCanceledException(),
            e => { seen.Add(e); if (e == 2) { own.Cancel(); throw new InvalidOperationException("row already exists"); } },
            own.Token);

        Assert.Equal(new[] { 1, 2, 3 }, seen);       // event 3 still arrived
        Assert.Equal(new[] { 1, 3 }, o.Events);
        Assert.Equal(0, o.HostLost);                 // a subscriber bug is not host loss
        var line = Assert.Single(o.Log);
        Assert.Contains("InvalidOperationException", line);
        Assert.Contains("row already exists", line);
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied, true)]
    [InlineData(StatusCode.Unimplemented, true)]
    [InlineData(StatusCode.FailedPrecondition, true)]
    [InlineData(StatusCode.Unavailable, false)]
    [InlineData(StatusCode.Internal, false)]
    [InlineData(StatusCode.Unknown, false)]
    public void Classify_reads_each_status(StatusCode code, bool refusedNotLost)
    {
        var verdict = PluginClient.Classify(PluginClient.LaunchedStream, Rpc(code), ownTokenCancelled: false);

        Assert.Equal(refusedNotLost ? StreamEnd.Degrade : StreamEnd.HostLost, verdict.End);
        Assert.Contains("account-launched", verdict.Log);
        Assert.Contains(code.ToString(), verdict.Log);
    }

    [Fact]
    public void Classify_names_a_non_rpc_exception()
    {
        var verdict = PluginClient.Classify(PluginClient.LaunchedStream, new IOException("pipe is broken"), ownTokenCancelled: false);

        Assert.Equal(StreamEnd.HostLost, verdict.End);
        Assert.Contains("IOException", verdict.Log);
        Assert.Contains("pipe is broken", verdict.Log);
    }

    [Fact]
    public void Classify_keeps_the_plugins_own_shutdown_silent()
    {
        Assert.Equal(new StreamVerdict(StreamEnd.Normal, null),
            PluginClient.Classify(PluginClient.ExitedStream, new OperationCanceledException(), ownTokenCancelled: true));
        Assert.Equal(new StreamVerdict(StreamEnd.Normal, null),
            PluginClient.Classify(PluginClient.ExitedStream, Rpc(StatusCode.Cancelled), ownTokenCancelled: true));
        Assert.Equal(new StreamVerdict(StreamEnd.Normal, null),
            PluginClient.Classify(PluginClient.ExitedStream, null, ownTokenCancelled: true));
    }

    // ---------- the degraded mode: polling the ungated snapshot ----------

    private static AccountRegistry.AccountInfo Acct(int pid, string name = "Alt", long place = 0)
        => new(pid, RobloxUserId: 1000 + pid, DisplayName: name, AccountId: "a" + pid, PlaceId: place, PlaceName: "");

    [Fact]
    public void ExitedPids_are_the_known_pids_missing_from_the_fresh_snapshot()
    {
        Assert.Equal(new[] { 11, 13 }, PluginClient.ExitedPids(new[] { 11, 12, 13 }, new[] { 12, 14 }));
        Assert.Empty(PluginClient.ExitedPids(new[] { 11, 12 }, new[] { 12, 11 }));
        Assert.Empty(PluginClient.ExitedPids(Array.Empty<int>(), new[] { 12 }));
        Assert.Equal(new[] { 11 }, PluginClient.ExitedPids(new[] { 11 }, Array.Empty<int>()));
    }

    [Fact]
    public void NewOrChanged_skips_accounts_the_registry_already_holds_unchanged()
    {
        var known = new[] { Acct(11), Acct(12) };
        var running = new[] { Acct(11), Acct(12, place: 77), Acct(13) };

        Assert.Equal(new[] { Acct(12, place: 77), Acct(13) }, PluginClient.NewOrChanged(known, running));
        Assert.Empty(PluginClient.NewOrChanged(known, known));
    }

    private sealed class Snapshots(params object[] answers)
    {
        private readonly Queue<object> _answers = new(answers);
        public int Calls;
        public Task<IReadOnlyList<AccountRegistry.AccountInfo>> Fetch(CancellationToken ct)
        {
            Calls++;
            ct.ThrowIfCancellationRequested();
            var next = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
            return next is Exception ex
                ? Task.FromException<IReadOnlyList<AccountRegistry.AccountInfo>>(ex)
                : Task.FromResult((IReadOnlyList<AccountRegistry.AccountInfo>)next);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "timed out waiting for the poll");
    }

    [Fact]
    public async Task The_exited_poll_removes_accounts_the_host_no_longer_runs()
    {
        var registry = new AccountRegistry();
        registry.OnLaunched(11, 1011, "Alt", "a11");
        registry.OnLaunched(12, 1012, "Alt", "a12");
        var removed = new List<int>();
        registry.AccountRemoved += (_, info) => removed.Add(info.Pid);
        var added = 0;
        registry.AccountAdded += (_, _) => added++;
        var snapshots = new Snapshots(new[] { Acct(11), Acct(12) }, new[] { Acct(12), Acct(13) });
        using var cts = new CancellationTokenSource();
        int hostLost = 0;
        var log = new List<string>();

        var poll = PluginClient.PollRunningAccountsAsync(PluginClient.ExitedStream, exitedSide: true, registry,
            snapshots.Fetch, TimeSpan.FromMilliseconds(5), () => hostLost++, log.Add, cts.Token);
        await WaitFor(() => removed.Count > 0);
        cts.Cancel();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 11 }, removed);
        Assert.Equal(0, added);          // the exited side never adds: the launched stream still does that
        Assert.Equal(0, hostLost);       // its own cancellation is a quiet stop
        Assert.Empty(log);
    }

    [Fact]
    public async Task The_launched_poll_adds_new_accounts_once_and_never_removes()
    {
        var registry = new AccountRegistry();
        registry.OnLaunched(11, 1011, "Alt", "a11");
        var added = new List<int>();
        registry.AccountAdded += (_, info) => added.Add(info.Pid);
        var removed = 0;
        registry.AccountRemoved += (_, _) => removed++;
        var snapshots = new Snapshots(new[] { Acct(11) }, new[] { Acct(13) });
        using var cts = new CancellationTokenSource();

        var poll = PluginClient.PollRunningAccountsAsync(PluginClient.LaunchedStream, exitedSide: false, registry,
            snapshots.Fetch, TimeSpan.FromMilliseconds(5), () => { }, _ => { }, cts.Token);
        await WaitFor(() => snapshots.Calls >= 6);
        cts.Cancel();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 13 }, added);   // not once per poll, and not the unchanged 11
        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task A_poll_that_cannot_reach_the_host_reports_it_lost_and_stops()
    {
        var registry = new AccountRegistry();
        var snapshots = new Snapshots(new[] { Acct(11) }, Rpc(StatusCode.Unavailable));
        int hostLost = 0;
        var log = new List<string>();

        await PluginClient.PollRunningAccountsAsync(PluginClient.LaunchedStream, exitedSide: false, registry,
            snapshots.Fetch, TimeSpan.FromMilliseconds(5), () => hostLost++, log.Add, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, hostLost);
        Assert.Equal(2, snapshots.Calls);
        var line = Assert.Single(log);
        Assert.Contains("Unavailable", line);
    }

    [Fact]
    public async Task A_listener_that_throws_during_a_poll_is_logged_and_the_poll_goes_on()
    {
        var registry = new AccountRegistry();
        registry.AccountAdded += (_, info) => { if (info.Pid == 11) throw new InvalidOperationException("row already exists"); };
        // The cast keeps the one array from being taken as the params array itself.
        var snapshots = new Snapshots((object)new[] { Acct(11), Acct(12) });
        using var cts = new CancellationTokenSource();
        int hostLost = 0;
        var log = new List<string>();

        var poll = PluginClient.PollRunningAccountsAsync(PluginClient.LaunchedStream, exitedSide: false, registry,
            snapshots.Fetch, TimeSpan.FromMilliseconds(5), () => hostLost++, log.Add, cts.Token);
        await WaitFor(() => registry.ResolveByPid(12) is not null);
        cts.Cancel();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, hostLost);
        Assert.Contains(log, l => l.Contains("InvalidOperationException") && l.Contains("row already exists"));
    }
}
