using System.Globalization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// The arbiter between a playback and keep-alive (spec "Pause and pick up"). A long macro and
/// keep-alive used to fight over the foreground with no coordination; now keep-alive asks for a
/// break and the playback parks at its next step boundary, so a step is never interrupted.
/// <para>One playback is in at a time (<see cref="EnterPlayback"/>). A break is granted at once when
/// no playback is in, when the playback parks (<see cref="AtBoundaryAsync"/>) or when it exits.
/// A request while a break is pending or held joins it: every requester gets its own lease, and
/// the playback resumes when the last lease is disposed. A playback that cannot reach a boundary
/// within <c>preemptAfter</c> is cancelled through <see cref="PreemptToken"/>; the break is granted
/// when it exits, after its held buttons and keys have come up.</para>
/// <para>All state sits under one lock. Nothing is awaited and no token is cancelled while it is
/// held, and every TaskCompletionSource runs its continuations asynchronously, so a grant never
/// runs keep-alive's code on the playback's thread (or the other way round).</para>
/// </summary>
internal sealed class ForegroundGate(Func<long> monoMs, Action<string> log)
{
    private sealed class Break(string forAccount, long requestedAtMs)
    {
        public readonly string For = forAccount;
        public readonly long RequestedAtMs = requestedAtMs;
        public int Leases = 1;
        public bool Granted;
        public bool Parked;
        /// <summary>The timer has decided to cancel; the cancel itself runs after the lock.</summary>
        public bool Preempting;
        public readonly TaskCompletionSource GrantedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleasedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Timer = new();
    }

    private readonly object _lock = new();
    private bool _playbackIn;
    private long? _target;
    private string? _label;
    private CancellationTokenSource _preempt = new();
    private Break? _break;
    private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Test seam: runs between the preempt decision and the cancel, the gap a playback
    /// can park in.</summary>
    internal Action? OnPreemptDecided;

    /// <summary>Cancelled when keep-alive preempts the playback that is in. A fresh token per
    /// playback: read it after <see cref="EnterPlayback"/>.</summary>
    public CancellationToken PreemptToken { get { lock (_lock) return _preempt.Token; } }

    /// <summary>Paused: a playback is parked at a step boundary (or a sequence between targets). For: the account the pending or held
    /// break is for (the first requester). PlaybackTarget: the account the playback in plays on.</summary>
    public (bool Paused, string? For, long? PlaybackTarget) Status
    {
        get { lock (_lock) return (_break?.Parked == true, _break?.For, _playbackIn ? _target : null); }
    }

    /// <summary>True while a break is pending or held: the next boundary parks.</summary>
    public bool BreakWanted { get { lock (_lock) return _break is not null; } }

    /// <summary>Registers the playback. A second one waits for the first to exit (MacroPlayer is
    /// single-flight, so in practice it never does). Disposing unregisters it and grants a
    /// pending break; disposing twice does nothing.</summary>
    public IDisposable EnterPlayback(long targetUserId, string label)
    {
        lock (_lock)
        {
            while (_playbackIn) Monitor.Wait(_lock);
            _playbackIn = true;
            _target = targetUserId;
            _label = label;
            _preempt = new CancellationTokenSource();
            _entered.TrySetResult();
        }
        return new Lease(ExitPlayback);
    }

    private void ExitPlayback()
    {
        string? granted = null;
        Break? b0 = null;
        lock (_lock)
        {
            _playbackIn = false;
            if (_break is { Granted: false } b)
            {
                b0 = b;
                Grant(b);
                granted = string.Create(CultureInfo.InvariantCulture,
                    $"keep-alive break for {b.For}: granted, the playback on {_label} ended ({Waited(b)})");
            }
            _target = null;
            _label = null;
            if (_preempt.IsCancellationRequested) _preempt = new CancellationTokenSource(); // a spent preempt never outlives its playback
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Monitor.PulseAll(_lock);
        }
        if (granted is not null) { b0!.Timer.Cancel(); log(granted); }
    }

    /// <summary>Completes when a playback is in (at once if one already is). Keep-alive's watchdog
    /// waits on this instead of ticking while nothing plays.</summary>
    public Task WhenPlaybackInAsync(CancellationToken ct)
    {
        lock (_lock) return _playbackIn ? Task.CompletedTask : _entered.Task.WaitAsync(ct);
    }

    /// <summary>Only the playback that is in calls this: parking grants a pending break, so a
    /// caller outside the playback would hand keep-alive the foreground mid-step. Anyone else uses
    /// <see cref="WaitWhileBreakHeldAsync"/>.
    /// <para>Returns at once unless a break is pending or held. Otherwise marks the playback parked,
    /// grants a pending break, and waits until every lease is released. A parked playback gets a
    /// fresh <see cref="PreemptToken"/>: a preempt decided just before it parked may still cancel
    /// the old one, and must not end a playback that did reach its boundary. Pass a token that is
    /// not linked to the preempt token, and re-read <see cref="PreemptToken"/> after it returns.</para></summary>
    public Task AtBoundaryAsync(int stepIndex, int stepCount, CancellationToken ct)
    {
        Break b;
        string? granted = null;
        lock (_lock)
        {
            if (_break is null) return Task.CompletedTask;
            b = _break;
            b.Parked = true;
            if (b.Preempting) _preempt = new CancellationTokenSource(); // the timer cancels the old one
            if (!b.Granted)
            {
                Grant(b);
                granted = string.Create(CultureInfo.InvariantCulture,
                    $"keep-alive break for {b.For}: granted, {_label ?? "the playback"} parked at step {stepIndex + 1}/{stepCount} ({Waited(b)})");
            }
        }
        if (granted is not null) { b.Timer.Cancel(); log(granted); }
        return ParkedAsync(b, ct);
    }

    /// <summary>For callers outside a playback (a sequence between targets): waits while a granted
    /// break holds the foreground, and never grants one. True when it waited, so the caller knows
    /// keep-alive moved the foreground. A pending break is the playback's business, not ours.</summary>
    public async Task<bool> WaitWhileBreakHeldAsync(CancellationToken ct)
    {
        bool waited = false;
        while (true)
        {
            Task released;
            lock (_lock)
            {
                if (_break is not { Granted: true } b) return waited;
                released = b.ReleasedTcs.Task;
            }
            await released.WaitAsync(ct).ConfigureAwait(false);
            waited = true; // another break may have been granted since: look again
        }
    }

    private async Task ParkedAsync(Break b, CancellationToken ct)
    {
        try { await b.ReleasedTcs.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally { lock (_lock) b.Parked = false; }
    }

    /// <summary>Asks for the foreground. Granted at once when no playback is in; otherwise when it
    /// parks or exits. After <paramref name="preemptAfter"/> without either, the playback is
    /// cancelled through <see cref="PreemptToken"/> and the break is granted when it exits. A
    /// request while a break is pending or held joins it. Dispose the lease to end your share.</summary>
    public async Task<IDisposable> RequestBreakAsync(string forAccount, TimeSpan preemptAfter, CancellationToken ct)
    {
        Break b;
        bool startTimer = false;
        string line;
        lock (_lock)
        {
            if (_break is { } joined)
            {
                b = joined;
                b.Leases++;
                line = $"keep-alive break for {forAccount}: joins the break for {b.For}";
            }
            else
            {
                b = _break = new Break(forAccount, monoMs());
                if (!_playbackIn)
                {
                    Grant(b);
                    line = $"keep-alive break for {forAccount}: granted at once, no playback in";
                }
                else
                {
                    startTimer = true;
                    line = $"keep-alive break for {forAccount}: waiting for the playback on {_label} to reach a step boundary";
                }
            }
        }
        log(line);
        if (startTimer) _ = PreemptLaterAsync(b, preemptAfter);

        try { await b.GrantedTcs.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Release(b); // our share of a break we will never use
            throw;
        }
        return new Lease(() => Release(b));
    }

    private async Task PreemptLaterAsync(Break b, TimeSpan after)
    {
        try { await Task.Delay(after, b.Timer.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        CancellationTokenSource? cut = null;
        string? label = null;
        lock (_lock)
        {
            if (_break == b && !b.Granted && _playbackIn) { cut = _preempt; label = _label; b.Preempting = true; }
        }
        if (cut is null) return;
        OnPreemptDecided?.Invoke();
        log(string.Create(CultureInfo.InvariantCulture,
            $"keep-alive break for {b.For}: the playback on {label} reached no step boundary in {after.TotalSeconds:F0} s, stopping it"));
        cut.Cancel(); // outside the lock: the runner's continuations may run inline
    }

    /// <summary>Under the lock. The caller stops the preempt timer once it has let go of the lock
    /// (a cancelled Task.Delay may resume its awaiter inline).</summary>
    private static void Grant(Break b)
    {
        b.Granted = true;
        b.GrantedTcs.TrySetResult();
    }

    private string Waited(Break b) => string.Create(CultureInfo.InvariantCulture,
        $"waited {(monoMs() - b.RequestedAtMs) / 1000.0:F1} s");

    private void Release(Break b)
    {
        bool ended = false;
        lock (_lock)
        {
            if (--b.Leases > 0) return;
            if (_break == b) _break = null;
            b.ReleasedTcs.TrySetResult();
            ended = b.Granted;
        }
        b.Timer.Cancel();
        if (ended) log($"keep-alive break for {b.For}: ended");
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        }
    }
}
