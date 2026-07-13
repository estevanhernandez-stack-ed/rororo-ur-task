namespace Labs626.UrTask.Macros;

/// <summary>Scheduler-internal state for one assignment. Not persisted.</summary>
internal sealed class ScheduledAlt
{
    public required Assignment Assignment { get; set; }

    /// <summary>Monotonic ms at which this alt next needs servicing. Actives ignore this.</summary>
    public long DueAtMs { get; set; }

    /// <summary>KeepAlive: the game's fire interval. Active: 0 (always runnable).</summary>
    public long IntervalMs { get; set; }

    /// <summary>
    /// Consecutive focus failures. Reset to 0 on any successful focus. Lets the runner
    /// tell "transient blip" from "this alt's window is gone" without dropping it on a
    /// single miss.
    /// </summary>
    public int ConsecutiveFocusFailures { get; set; }

    public bool IsKeepAlive => Assignment.Role == CadenceRole.KeepAlive;
}

/// <summary>What the runner should do next.</summary>
internal abstract record CadenceDecision
{
    public sealed record ServiceKeepAlive(ScheduledAlt Alt) : CadenceDecision;
    public sealed record RunActive(ScheduledAlt Alt) : CadenceDecision;
    public sealed record SleepUntil(long WakeAtMs) : CadenceDecision;
}

/// <summary>
/// The scheduling policy, as a PURE function — no Win32, no timers, no I/O — so the
/// hard cases (a keep-alive falling due inside a long macro pass; nothing due at all)
/// are deterministic under a fake clock.
///
/// Foreground is an EXCLUSIVE resource: one window at a time. Two task classes want it:
///   Active    — wants it continuously (farm back-to-back). NO hard deadline; a skipped
///               pass is just less farming.
///   KeepAlive — wants it for ~1s, but on a HARD deadline. Miss it and the game kicks
///               the alt.
/// So keep-alives win ties, but only when they actually need to — which is what lets
/// the loop SLEEP the rest of the time instead of stealing focus every 1.25s.
/// </summary>
internal static class CadenceScheduler
{
    public static CadenceDecision Decide(
        IReadOnlyList<ScheduledAlt> alts, long nowMs, long nextActivePassCostMs)
    {
        ScheduledAlt? urgent = null;
        ScheduledAlt? nextActive = null;
        long earliestDue = long.MaxValue;

        foreach (var alt in alts)
        {
            if (alt.IsKeepAlive)
            {
                if (alt.DueAtMs < earliestDue) earliestDue = alt.DueAtMs;

                // Would this alt miss its deadline if we ran one more active pass first?
                // (With no actives, nextActivePassCostMs is 0 and this is simply "is it due".)
                if (alt.DueAtMs <= nowMs + nextActivePassCostMs)
                {
                    // Earliest deadline first — the most overdue alt is the most at risk.
                    if (urgent is null || alt.DueAtMs < urgent.DueAtMs) urgent = alt;
                }
            }
            else
            {
                nextActive ??= alt;   // round-robin order is the caller's list order
            }
        }

        if (urgent is not null) return new CadenceDecision.ServiceKeepAlive(urgent);
        if (nextActive is not null) return new CadenceDecision.RunActive(nextActive);

        // Nothing active, nothing due: SLEEP. This is the whole feature.
        return new CadenceDecision.SleepUntil(earliestDue == long.MaxValue ? nowMs + 1_000 : earliestDue);
    }
}
