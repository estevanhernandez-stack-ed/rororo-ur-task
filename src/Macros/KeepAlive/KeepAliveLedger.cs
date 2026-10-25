using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Single per-account source of truth for keep-alive: when input last landed, how
/// many attempts in a row missed, whether the account is switched off. Pure state:
/// no logging, no UI, no runner types. One lock, O(1) work, never calls out under it.
/// </summary>
internal sealed class KeepAliveLedger(Func<long> monoMs, Func<DateTimeOffset> wallNow)
{
    private sealed class Entry
    {
        public required long UserId;
        public required string AccountId;
        public required string DisplayName;
        public required KeepAliveLimit Limit;
        public long? LastInputMs;
        public InputSource? LastSource;
        public AccountKeepAliveState State = AccountKeepAliveState.Waiting;
        public bool Off;
        public int Misses;
        public Miss? LastMiss;
    }

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _byUser = new();
    private readonly Dictionary<string, long> _userByAccountId = new();

    /// <summary>Starts tracking an account. Re-tracking refreshes identity and limit and keeps history.</summary>
    public void Track(AccountRegistry.AccountInfo alt, KeepAliveLimit limit)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(alt.RobloxUserId, out var existing))
            {
                if (existing.AccountId != alt.AccountId) _userByAccountId.Remove(existing.AccountId);
                existing.AccountId = alt.AccountId;
                existing.DisplayName = alt.DisplayName;
                existing.Limit = limit;
            }
            else
            {
                _byUser[alt.RobloxUserId] = new Entry
                {
                    UserId = alt.RobloxUserId,
                    AccountId = alt.AccountId,
                    DisplayName = alt.DisplayName,
                    Limit = limit,
                };
            }
            _userByAccountId[alt.AccountId] = alt.RobloxUserId;
        }
    }

    public void Untrack(long userId)
    {
        lock (_gate)
        {
            if (!_byUser.Remove(userId, out var e)) return;
            if (_userByAccountId.TryGetValue(e.AccountId, out var mapped) && mapped == userId)
                _userByAccountId.Remove(e.AccountId);
        }
    }

    /// <summary>Input landed now. Clears the miss streak.</summary>
    public void StampLanded(long userId, InputSource source)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var e)) return;
            e.LastInputMs = monoMs();
            e.LastSource = source;
            e.Misses = 0;
            e.LastMiss = null;
        }
    }

    /// <summary>
    /// Folds in the host's last-activity time for an account. Only moves the last
    /// input forward, never back; a time in the future counts as now. Unknown accounts are ignored.
    /// </summary>
    public void MergeHostActivity(string accountId, DateTimeOffset lastActivity)
    {
        lock (_gate)
        {
            if (!_userByAccountId.TryGetValue(accountId, out var userId)) return;
            var e = _byUser[userId];
            var now = monoMs();
            var ageMs = (long)(wallNow() - lastActivity).TotalMilliseconds;
            var at = ageMs <= 0 ? now : now - ageMs;
            if (e.LastInputMs is { } current && at <= current) return;
            e.LastInputMs = at;
            e.LastSource = InputSource.Host;
        }
    }

    /// <summary>Records a failed attempt and returns the new consecutive-miss count (0 if untracked).</summary>
    public int RecordMiss(long userId, Miss miss)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var e)) return 0;
            e.LastMiss = miss;
            return ++e.Misses;
        }
    }

    public void SetState(long userId, AccountKeepAliveState state)
    {
        lock (_gate)
        {
            if (_byUser.TryGetValue(userId, out var e)) e.State = state;
        }
    }

    /// <summary>Switches an account off or on. Returns true when the account is tracked and the value changed.</summary>
    public bool SetOff(long userId, bool off)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var e) || e.Off == off) return false;
            e.Off = off;
            return true;
        }
    }

    /// <summary>
    /// Mono-clock ms by which input must land: last input plus the deadline, or now
    /// when nothing is known. Off does not change it; the scheduler skips off accounts.
    /// </summary>
    public long DueAtMs(long userId)
    {
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var e)) return monoMs();
            return e.LastInputMs is { } last ? last + (long)e.Limit.Deadline.TotalMilliseconds : monoMs();
        }
    }

    public AccountLedgerEntry? Get(long userId)
    {
        lock (_gate) return _byUser.TryGetValue(userId, out var e) ? ToRecord(e) : null;
    }

    public IReadOnlyList<AccountLedgerEntry> Snapshot()
    {
        lock (_gate) return _byUser.Values.Select(ToRecord).ToArray();
    }

    /// <summary>Converts a mono-clock reading to wall time using both clocks sampled together.</summary>
    public DateTimeOffset ToWall(long monoMsValue)
    {
        var mono = monoMs();
        var wall = wallNow();
        return wall - TimeSpan.FromMilliseconds(mono - monoMsValue);
    }

    private static AccountLedgerEntry ToRecord(Entry e) => new(
        e.UserId, e.DisplayName, e.AccountId, e.Limit, e.LastInputMs, e.LastSource,
        e.Off ? AccountKeepAliveState.Off : e.State, e.Off, e.Misses, e.LastMiss);
}
