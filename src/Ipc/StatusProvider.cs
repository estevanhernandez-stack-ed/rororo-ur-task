using System.Globalization;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Ipc;

/// <summary>Answers the bridge's GetStatus. Reads only; must never wait on the keep-alive runner.</summary>
internal interface IStatusProvider
{
    GetStatusResponse GetStatus();
}

/// <summary>One tracked account's keep-alive deadline, for the RunMacro heads-up.</summary>
internal sealed record KeepAliveDeadline(long UserId, string Name, DateTimeOffset At, bool Off);

/// <summary>
/// Builds the GetStatus answer from the ledger, the gate and the playback registry. Takes delegates for
/// the runner, break-glass and preference reads so it tests without PluginRuntime. Ledger and gate reads
/// are snapshots under their own short locks, so nothing here blocks on a running pass.
/// </summary>
internal sealed class StatusProvider(
    KeepAliveLedger ledger,
    ForegroundGate gate,
    PlaybackRegistry playbacks,
    Func<bool> isBreakglass,
    Func<bool> isRunnerRunning,
    Func<bool> turbo,
    Func<IReadOnlySet<long>> activeUserIds) : IStatusProvider
{
    public GetStatusResponse GetStatus()
    {
        var keepAlive = new KeepAliveStatus(
            isBreakglass() ? "breakglass" : isRunnerRunning() ? "running" : "stopped",
            turbo());

        var active = activeUserIds();
        var accounts = ledger.Snapshot().Select(e => ToAccount(e, active)).ToArray();

        var (paused, pausedFor, target) = gate.Status;
        var pbs = playbacks.Running()
            .Select(p =>
            {
                var parked = paused && target is { } t && p.Targets.Contains(t);
                return new PlaybackStatus(p.Id, parked, parked ? pausedFor : null);
            })
            .ToArray();

        return new GetStatusResponse(true, keepAlive, accounts, pbs, null, null);
    }

    /// <summary>The deadline of every account keep-alive serves, for the RunMacro note. Empty while keep-alive is
    /// stopped (no break will come) and without Active accounts (keep-alive never serves them).</summary>
    public IReadOnlyList<KeepAliveDeadline> Deadlines()
    {
        if (!isRunnerRunning()) return [];
        var active = activeUserIds();
        return ledger.Snapshot()
            .Where(e => !active.Contains(e.UserId))
            .Select(e => new KeepAliveDeadline(e.UserId, e.DisplayName, ledger.ToWall(ledger.DueAtMs(e.UserId)), e.Off))
            .ToArray();
    }

    private AccountStatus ToAccount(AccountLedgerEntry e, IReadOnlySet<long> active)
    {
        var role = e.Off ? "off" : active.Contains(e.UserId) ? "active" : "keep-alive";
        string? lastAt = null;
        int? idle = null;
        if (e.LastInputMs is { } last)
        {
            lastAt = ledger.ToWall(last).ToString("O", CultureInfo.InvariantCulture);
            idle = (int)Math.Max(0, (ledger.NowMs - last) / 1000);
        }
        return new AccountStatus(
            UserId: e.UserId.ToString(CultureInfo.InvariantCulture),
            Name: e.DisplayName,
            Role: role,
            LastInputAt: lastAt,
            LastInputSource: e.LastSource is { } src ? SourceText(src) : null,
            IdleSeconds: idle,
            DeadlineAt: ledger.ToWall(ledger.DueAtMs(e.UserId)).ToString("O", CultureInfo.InvariantCulture),
            LimitMinutes: (int)e.Limit.Limit.TotalMinutes,
            Misses: e.ConsecutiveMisses,
            LastMiss: e.LastMiss is { } m ? MissReporter.CauseText(m) : null,
            State: StateText(e.State));
    }

    private static string SourceText(InputSource s) => s switch
    {
        InputSource.KeepAlive => "keep-alive",
        InputSource.Macro => "macro",
        InputSource.Paste => "paste",
        _ => "host",
    };

    private static string StateText(AccountKeepAliveState s) => s switch
    {
        AccountKeepAliveState.Waiting => "waiting",
        AccountKeepAliveState.Serving => "serving",
        AccountKeepAliveState.Off => "off",
        AccountKeepAliveState.Held => "held",
        _ => "paused-for-macro",
    };
}
