namespace Labs626.UrTask.Macros;

/// <summary>
/// What keep-alive says while break-glass has the wheel. The runner is stopped, so nothing serves any
/// account; every tracked account shows as held, and one that idles to its deadline meanwhile gets a
/// <see cref="MissCause.HeldOff"/> miss (once per idle stretch) and the same "expect a reset" line a
/// failed keep-alive would write. Driven by a timer in the runtime; <see cref="Tick"/> does the work so
/// a test can drive it without one.
/// </summary>
internal sealed class BreakglassHold(KeepAliveLedger ledger, MissReporter reporter, Func<long> clockMs)
{
    internal const string Holder = "break-glass";

    private readonly object _gate = new();
    /// <summary>The last-input time each account had when its miss was recorded: a different one is a new stretch.</summary>
    private readonly Dictionary<long, long?> _recordedFor = new();

    /// <summary>Break-glass is on: every tracked account is held.</summary>
    public void Begin() => Tick();

    /// <summary>Break-glass is off: every account goes back to waiting.</summary>
    public void End()
    {
        lock (_gate)
        {
            _recordedFor.Clear();
            foreach (var e in ledger.Snapshot()) ledger.SetState(e.UserId, AccountKeepAliveState.Waiting);
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            var now = clockMs();
            foreach (var e in ledger.Snapshot())
            {
                // Held again every tick: the aborted loop may still be unwinding its own state writes,
                // and an account tracked since the last tick must show as held too.
                ledger.SetState(e.UserId, AccountKeepAliveState.Held);
                if (e.Off || e.LastInputMs is not { } last) continue;

                if (_recordedFor.TryGetValue(e.UserId, out var seen) && seen != last)
                {
                    _recordedFor.Remove(e.UserId);   // new input: the next idle stretch may warn again
                    reporter.Rearm(e.DisplayName);
                }
                var idleMs = now - last;
                if (idleMs < (long)e.Limit.Deadline.TotalMilliseconds || _recordedFor.ContainsKey(e.UserId)) continue;

                _recordedFor[e.UserId] = last;
                ledger.RecordMiss(e.UserId, new Miss(MissCause.HeldOff, Holder));
                reporter.ExpectReset(e.DisplayName, ledger.ToWall(last + (long)e.Limit.Limit.TotalMilliseconds), (int)(idleMs / 60_000));
            }
        }
    }
}
