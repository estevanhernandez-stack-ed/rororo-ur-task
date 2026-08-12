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
    ///
    /// Also locks the LIST-ORDER contract: Decide is stateless and returns the FIRST
    /// Active it encounters (see the XML doc on Decide). Nothing here should ever change
    /// to picking by, say, lowest pid or least-recently-run — that's the caller's job via
    /// list rotation. If a future "optimization" reorders the pick, this must catch it.
    [Fact]
    public void AllActive_AlwaysRunsAnActive_NeverSleeps()
    {
        // Deliberately NOT in pid order: pid 2 first, pid 1 second. If Decide ever
        // regressed to picking by lowest pid instead of list order, this fixture is
        // what catches it — with an ascending fixture, "first in list" and "lowest
        // pid" agree and a regression would pass green.
        var alts = new[] { Active(2), Active(1) };

        var d = CadenceScheduler.Decide(alts, nowMs: 0, nextActivePassCostMs: 5 * Min);

        var run = Assert.IsType<CadenceDecision.RunActive>(d);
        Assert.Same(alts[0], run.Alt);   // FIRST in list order, not lowest pid / any other rule
    }

    /// Anti-hot-spin guard: WakeAtMs must be strictly greater than nowMs. A regression to
    /// SleepUntil(nowMs) type-checks fine but is a genuine 100%-CPU hot spin — the caller
    /// sleeps for `WakeAtMs - nowMs` with no clamping, so a zero or negative gap means the
    /// loop never actually sleeps.
    [Fact]
    public void NoAltsAtAll_Sleeps()
    {
        // Non-zero nowMs: at nowMs = 0, a hardcoded `return SleepUntil(1000)` that
        // ignores nowMs entirely would also pass. A non-zero now pins the
        // relative-to-now semantics, not just the sign of the comparison.
        const long nowMs = 5 * Min;
        var d = CadenceScheduler.Decide(Array.Empty<ScheduledAlt>(), nowMs, nextActivePassCostMs: 0);
        var sleep = Assert.IsType<CadenceDecision.SleepUntil>(d);
        Assert.True(sleep.WakeAtMs > nowMs, "WakeAtMs must be strictly greater than nowMs or the loop hot-spins.");
    }

    /// Finding 1 (overflow): nextActivePassCostMs is derived from Macro.Duration, which comes
    /// from the last event's timestamp in a user-editable on-disk JSON macro file. A
    /// pathological value must not overflow `nowMs + nextActivePassCostMs` to negative — if it
    /// did, `DueAtMs <= <negative>` would be false for every alt, nothing would ever look
    /// urgent, RunActive would win forever, and every keep-alive would get silently kicked.
    /// The urgency check must saturate instead, so a due keep-alive still gets serviced.
    [Fact]
    public void PathologicalNextActivePassCost_DoesNotOverflow_DueKeepAliveStillServiced()
    {
        var alts = new ScheduledAlt[] { Active(1), KeepAlive(2, dueAtMs: 5 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 5 * Min, nextActivePassCostMs: long.MaxValue);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(2, svc.Alt.Assignment.Alt.Pid);
    }

    /// Finding 1 (negative cost): the mirror image of the overflow case above.
    /// SaturatingAdd only guards positive overflow — an un-clamped negative
    /// nextActivePassCostMs moves the urgency horizon EARLIER than nowMs, so a keep-alive
    /// that's already due reads as "not urgent" while an Active alt is present. RunActive
    /// then wins on every call — forever, for a large negative — silently kicking the
    /// keep-alive offline exactly the way the overflow case does. The cost must be
    /// clamped to non-negative before the add so a due keep-alive still gets serviced.
    /// An Active alt MUST be in the fixture: without one, RunActive can never win, so the
    /// test would pass for the wrong reason.
    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void NegativeNextActivePassCost_DoesNotStarve_DueKeepAliveStillServiced(long badCostMs)
    {
        var alts = new ScheduledAlt[] { Active(1), KeepAlive(2, dueAtMs: 5 * Min) };

        var d = CadenceScheduler.Decide(alts, nowMs: 5 * Min, nextActivePassCostMs: badCostMs);

        var svc = Assert.IsType<CadenceDecision.ServiceKeepAlive>(d);
        Assert.Equal(2, svc.Alt.Assignment.Alt.Pid);
    }
}
