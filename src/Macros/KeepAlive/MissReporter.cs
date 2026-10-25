using System.Globalization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Writes the keep-alive lines that say why: each miss with its cause, one line when a whole
/// group of accounts missed for the same cause and holder, a recovery line when an account lands
/// again, and the warning that a reset is coming. Output only: the ledger already holds the facts.
///
/// Misses are held back until their group closes so seven accounts that miss for one cause become
/// one line. A group closes a minute after its first miss, when the same account misses again, or
/// when the runner forces it (it does at the end of a run and before an Active pass).
/// </summary>
internal sealed class MissReporter(Action<string> log, Func<DateTimeOffset> wallNow)
{
    internal const int GroupWindowSeconds = 60;
    internal const int RetrySeconds = 30;
    private static readonly TimeSpan GroupWindow = TimeSpan.FromSeconds(GroupWindowSeconds);

    private readonly object _gate = new();
    private readonly List<(string Name, Miss Miss)> _pending = new();
    private readonly HashSet<string> _resetAnnounced = new();
    private DateTimeOffset _groupStart;
    private bool _landedInGroup;

    /// <summary>The words for a cause, as the spec table gives them.</summary>
    public static string CauseText(Miss miss) => miss.Cause switch
    {
        MissCause.WindowGone => "window gone",
        MissCause.FocusRefused => "Windows refused focus",
        MissCause.OtherInFront => $"{miss.Holder ?? "another window"} was in front",
        MissCause.SendRejected => "Windows rejected the input",
        MissCause.HeldOff => $"held off: {miss.Holder ?? "unknown"}",
        _ => miss.Cause.ToString(),
    };

    /// <summary>How long until the open group is a minute old, or null when nothing is held back.</summary>
    public TimeSpan? UntilFlush
    {
        get
        {
            lock (_gate)
            {
                if (_pending.Count == 0) return null;
                var left = GroupWindow - (wallNow() - _groupStart);
                return left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }
        }
    }

    /// <summary>An account's Space landed. Says so when it had missed before, and re-arms the reset warning.</summary>
    /// <param name="idleMs">How long it had been since the account's last input, when that is known.</param>
    public void Landed(AccountLedgerEntry? before, string name, long? idleMs = null)
    {
        var lines = new List<string>();
        lock (_gate)
        {
            if (_pending.Any(p => p.Name == name)) FlushLocked(lines);
            else if (_pending.Count > 0) _landedInGroup = true;
            _resetAnnounced.Remove(name);

            if (before is { ConsecutiveMisses: > 0 })
            {
                var misses = before.ConsecutiveMisses;
                var since = idleMs is { } ms
                    ? $" ({(int)Math.Round(ms / 60_000.0)} min since its last Space)"
                    : "";
                lines.Add($"keep-alive: {name} landed again after {misses} {(misses == 1 ? "miss" : "misses")}{since}");
            }
        }
        Write(lines);
    }

    public void Missed(string name, Miss miss, int consecutive)
    {
        var lines = new List<string>();
        lock (_gate)
        {
            var now = wallNow();
            if (_pending.Count > 0 && (now - _groupStart >= GroupWindow || _pending.Any(p => p.Name == name)))
                FlushLocked(lines);
            if (_pending.Count == 0) { _groupStart = now; _landedInGroup = false; }
            _pending.Add((name, miss));
        }
        Write(lines);
    }

    /// <summary>Writes the open group once it is a minute old, or at once with <paramref name="force"/>.</summary>
    public void FlushMinute(bool force = false)
    {
        var lines = new List<string>();
        lock (_gate)
        {
            if (_pending.Count > 0 && (force || wallNow() - _groupStart >= GroupWindow)) FlushLocked(lines);
        }
        Write(lines);
    }

    /// <summary>The account will be reset or kicked before another retry can land. Said once per idle stretch.</summary>
    public void ExpectReset(string name, DateTimeOffset at, int idleMinutes)
    {
        lock (_gate)
        {
            if (!_resetAnnounced.Add(name)) return;
        }
        log($"keep-alive: {name} has had no input for {idleMinutes} min; expect a reset or kick around " +
            at.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    private void FlushLocked(List<string> lines)
    {
        if (_pending.Count == 0) return;
        var first = _pending[0].Miss;
        var allSame = _pending.All(p => p.Miss.Cause == first.Cause && p.Miss.Holder == first.Holder);
        if (_pending.Count >= 2 && allSame && !_landedInGroup)
            lines.Add($"keep-alive: every due account missed: {CauseText(first)}");
        else
            foreach (var (name, miss) in _pending)
                lines.Add($"keep-alive: missed {name}, {CauseText(miss)}; retry in {RetrySeconds}s");
        _pending.Clear();
        _landedInGroup = false;
    }

    private void Write(List<string> lines)
    {
        foreach (var l in lines) log(l);
    }
}
