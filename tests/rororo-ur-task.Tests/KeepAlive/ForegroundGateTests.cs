using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

/// <summary>The arbiter between a playback and keep-alive: a break is granted at once with no
/// playback in, or when the playback parks at a step boundary or ends.</summary>
public class ForegroundGateTests
{
    private readonly List<string> _log = new();
    private ForegroundGate Gate() => new(() => 0, _log.Add);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Break_with_no_playback_grants_at_once()
    {
        var gate = Gate();
        var request = gate.RequestBreakAsync("Alt One", Long, default);
        Assert.True(request.IsCompletedSuccessfully);
        Assert.Equal((false, "Alt One", (long?)null), gate.Status);
        (await request).Dispose();
        Assert.Equal((false, (string?)null, (long?)null), gate.Status);
    }

    [Fact]
    public async Task Request_against_an_ending_playback_grants()
    {
        var gate = Gate();
        var playback = gate.EnterPlayback(42, "macro");
        Assert.Equal((false, (string?)null, (long?)42), gate.Status);
        var request = gate.RequestBreakAsync("Alt One", Long, default);
        Assert.False(request.IsCompleted);

        playback.Dispose();
        var lease = await request.WaitAsync(TimeSpan.FromSeconds(5));
        playback.Dispose(); // idempotent: no second grant, no throw
        Assert.Equal((false, "Alt One", (long?)null), gate.Status);
        lease.Dispose();
        lease.Dispose();
        Assert.Equal((false, (string?)null, (long?)null), gate.Status);
    }

    [Fact]
    public async Task Second_request_joins_the_first_break()
    {
        var gate = Gate();
        using var playback = gate.EnterPlayback(42, "macro");
        var first = gate.RequestBreakAsync("Alt One", Long, default);
        var second = gate.RequestBreakAsync("Alt Two", Long, default);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var boundary = gate.AtBoundaryAsync(3, 10, default);
        var lease1 = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var lease2 = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((true, "Alt One", (long?)42), gate.Status);

        // A third request while the break is held joins it and is granted at once.
        var third = gate.RequestBreakAsync("Alt Three", Long, default);
        var lease3 = await third.WaitAsync(TimeSpan.FromSeconds(5));

        lease1.Dispose();
        lease1.Dispose(); // a disposed lease is idempotent: it must not release lease2's share
        lease2.Dispose();
        await Task.Delay(50);
        Assert.False(boundary.IsCompleted);
        lease3.Dispose();
        await boundary.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((false, (string?)null, (long?)42), gate.Status);
    }

    [Fact]
    public async Task Boundary_without_a_break_returns_at_once()
    {
        var gate = Gate();
        using var playback = gate.EnterPlayback(42, "macro");
        Assert.True(gate.AtBoundaryAsync(0, 3, default).IsCompletedSuccessfully);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_cancelled_pending_request_leaves_the_gate_free()
    {
        var gate = Gate();
        using var playback = gate.EnterPlayback(42, "macro");
        using var cts = new CancellationTokenSource();
        var request = gate.RequestBreakAsync("Alt One", Long, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(gate.AtBoundaryAsync(0, 3, default).IsCompletedSuccessfully);
        Assert.Equal((false, (string?)null, (long?)42), gate.Status);
    }

    [Fact]
    public async Task Preempt_cancels_the_playback_token_and_grants_when_it_ends()
    {
        var gate = Gate();
        var playback = gate.EnterPlayback(42, "macro");
        var preempt = gate.PreemptToken;
        var request = gate.RequestBreakAsync("Alt One", TimeSpan.FromMilliseconds(100), default);

        await Task.Delay(TimeSpan.FromSeconds(5), preempt).ContinueWith(_ => { });
        Assert.True(preempt.IsCancellationRequested);
        playback.Dispose();
        (await request.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();

        // The next playback gets a fresh token.
        using var next = gate.EnterPlayback(43, "macro");
        Assert.False(gate.PreemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_parked_playback_is_never_preempted()
    {
        var gate = Gate();
        using var playback = gate.EnterPlayback(42, "macro");
        var preempt = gate.PreemptToken;
        var request = gate.RequestBreakAsync("Alt One", TimeSpan.FromMilliseconds(100), default);
        var boundary = gate.AtBoundaryAsync(0, 2, default);
        var lease = await request.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);
        Assert.False(preempt.IsCancellationRequested);
        lease.Dispose();
        await boundary.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_second_playback_waits_for_the_first_to_exit()
    {
        var gate = Gate();
        var first = gate.EnterPlayback(42, "one");
        var second = Task.Run(() => gate.EnterPlayback(43, "two"));
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((long?)43, gate.Status.PlaybackTarget);
    }
}
