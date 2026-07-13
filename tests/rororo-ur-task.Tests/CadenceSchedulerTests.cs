using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class CadenceSchedulerTests
{
    private const long Min = 60_000;

    // AccountInfo(int Pid, long RobloxUserId, string DisplayName, string AccountId,
    //             long PlaceId = 0, string PlaceName = "")
    private static AccountRegistry.AccountInfo Alt(int pid, long userId) => new(pid, userId, $"alt{pid}", $"acct-{pid}");

    private static Macro NewMacro() => new(
        SchemaVersion: 3, Id: Guid.NewGuid().ToString(), Name: "m",
        RecordMode: "PerWindow", RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null, InterAltDelayMs: null,
        RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    private static ScheduledAlt KeepAlive(int pid, long dueAtMs, long intervalMs = 12 * Min) => new()
    {
        Assignment = new Assignment(Alt(pid, pid), null, CadenceRole.KeepAlive),
        DueAtMs = dueAtMs,
        IntervalMs = intervalMs,
    };

    private static ScheduledAlt Active(int pid) => new()
    {
        Assignment = new Assignment(Alt(pid, pid), NewMacro(), CadenceRole.Active),
        DueAtMs = 0,
        IntervalMs = 0,
    };

    /// THE feature. No active alts and nothing due => sleep. No focus steal.
    /// This is the case that makes a single keep-alive account stop hijacking
    /// the desktop every 1.25 seconds.
    [Fact]
    public void NoActives_NothingDue_SleepsUntilTheEarliestDeadline()
    {
        var alts = new[] { KeepAlive(1, dueAtMs: 10 * Min), KeepAlive(2, dueAtMs: 4 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 0);

        var sleep = Assert.IsType<CadenceDecision.SleepUntil>(d);
        Assert.Equal(4 * Min, sleep.WakeAtMs);   // earliest deadline wins
    }

    [Fact]
    public void KeepAliveDue_IsServiced()
    {
        var alts = new[] { KeepAlive(1, dueAtMs: 5 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 5 * Min, nextActivePassCostMs: 0);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(1, svc.Alt.Assignment.Alt.Pid);
    }

    /// Gap-fitting: the keep-alive isn't due YET, but it would blow its deadline
    /// if we ran another active pass first. It cuts the line.
    [Fact]
    public void KeepAliveDueWithinTheNextActivePass_IsServicedBeforeTheActive()
    {
        var alts = new ScheduledAlt[] { Active(1), KeepAlive(2, dueAtMs: 3 * Min) };

        // now=0, keep-alive due at 3min, but the next active pass costs 5min:
        // running the active first means servicing the keep-alive at 5min — too late.
        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 5 * Min);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(2, svc.Alt.Assignment.Alt.Pid);
    }

    /// The keep-alive comfortably survives another pass, so farming wins.
    [Fact]
    public void KeepAliveSafelyBeyondTheNextActivePass_ActiveRunsFirst()
    {
        var alts = new ScheduledAlt[] { Active(1), KeepAlive(2, dueAtMs: 30 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 5 * Min);

        var run = Assert.IsType<CadenceDecision.RunActive>(d);
        Assert.Equal(1, run.Alt.Assignment.Alt.Pid);
    }

    [Fact]
    public void TwoUrgentKeepAlives_EarliestDeadlineIsServicedFirst()
    {
        var alts = new[] { KeepAlive(1, dueAtMs: 9 * Min), KeepAlive(2, dueAtMs: 2 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 10 * Min, nextActivePassCostMs: 0);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(2, svc.Alt.Assignment.Alt.Pid);   // most overdue
    }

    /// Compat guard: an all-Active squad must still round-robin back-to-back,
    /// exactly as the old spin loop did. Actives are always runnable.
    [Fact]
    public void AllActive_AlwaysRunsAnActive_NeverSleeps()
    {
        var alts = new[] { Active(1), Active(2) };

        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 5 * Min);

        Assert.IsType<CadenceDecision.RunActive>(d);
    }

    [Fact]
    public void NoAltsAtAll_Sleeps()
    {
        var d = CadenceScheduler.Decide(Array.Empty<ScheduledAlt>(), nowMs: 0, nextActivePassCostMs: 0);
        Assert.IsType<CadenceDecision.SleepUntil>(d);
    }
}
