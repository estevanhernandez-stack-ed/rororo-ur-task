using System.Globalization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// What keep-alive says while a recording has it paused (<see cref="RecordingPause"/>). Nothing serves
/// any account meanwhile, so one that idles to its deadline gets a line naming it and when the game is
/// likely to reset it, once per idle stretch, while there is still time to stop recording.
/// <para>Modelled on <see cref="BreakglassHold"/>, with two differences. It speaks only for the
/// accounts the paused loop was serving as keep-alive accounts, not every account the ledger has ever
/// seen. And it only reads the ledger: no held state, no recorded miss. Driven by a timer in the
/// runtime; <see cref="Tick"/> does the work so a test can drive it without one.</para>
/// </summary>
internal sealed class RecordingHold(
    KeepAliveLedger ledger, Action<string> log, Func<long> clockMs, Func<long, bool> stillOpen)
{
    private readonly object _gate = new();
    private readonly HashSet<long> _served = new();
    /// <summary>The last-input time each account had when it was warned about: a different one is a new stretch.</summary>
    private readonly Dictionary<long, long> _warnedFor = new();

    private bool _active;
    private long _beganAtMs;

    /// <summary>The recording paused the loop that was serving these accounts as keep-alive accounts.</summary>
    public void Begin(IReadOnlyCollection<long> servedUserIds)
    {
        lock (_gate)
        {
            _served.Clear();
            _served.UnionWith(servedUserIds);
            _warnedFor.Clear();
            _beganAtMs = clockMs();
            _active = true;
        }
        Tick();
    }

    /// <summary>The recording stopped.</summary>
    public void End()
    {
        lock (_gate)
        {
            _active = false;   // a tick already queued on the timer must say nothing after this
            _served.Clear();
            _warnedFor.Clear();
        }
    }

    public void Tick()
    {
        var lines = new List<string>();
        lock (_gate)
        {
            if (!_active) return;
            var now = clockMs();
            foreach (var e in ledger.Snapshot())
            {
                if (!_served.Contains(e.UserId) || e.Off || !stillOpen(e.UserId)) continue;

                // No input on record: the loop was paused before it ever reached this account, so its
                // idle time counts from the pause. That can only undercount, never warn early.
                var last = e.LastInputMs ?? _beganAtMs;
                if (_warnedFor.TryGetValue(e.UserId, out var seen))
                {
                    if (seen == last) continue;
                    _warnedFor.Remove(e.UserId);   // new input: the next idle stretch may warn again
                }
                var idleMs = now - last;
                if (idleMs < (long)e.Limit.Deadline.TotalMilliseconds) continue;

                _warnedFor[e.UserId] = last;
                var resetAtMs = last + (long)e.Limit.Limit.TotalMilliseconds;
                var when = ledger.ToWall(resetAtMs).ToString("HH:mm", CultureInfo.InvariantCulture);
                lines.Add($"{e.DisplayName} has had no input for {idleMs / 60_000} min while you record. "
                    + (resetAtMs < now ? $"The game may have reset it around {when}. " : $"The game may reset it around {when}. ")
                    + "Stop recording to let keep-alive run.");
            }
        }
        foreach (var line in lines) log(line);   // outside the lock: the log reaches the UI
    }
}
