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

    /// <summary>
    /// High-water mark, in ms, of the longest Active pass actually observed for this
    /// alt (focus + settle + verify + PlayAsync, timed end to end by the caller).
    /// <see cref="Decide"/>'s urgency lookahead needs a conservative UPPER BOUND on the
    /// next pass's cost, and a static estimate derived from Macro.Duration alone
    /// under-counts real Win32 costs (AttachAndFocus, MacroPlayer's preflight resize)
    /// that never show up in the macro data at all. Self-correcting: the caller times
    /// every pass and ratchets this up, never down, so the estimate only ever becomes
    /// more conservative, never less.
    /// </summary>
    public long ObservedPassCostMs { get; set; }

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
///
/// The safety margin against Roblox's 20-minute idle floor does NOT live here — it's
/// baked into the fire intervals in <see cref="KeepAliveIntervals"/> (11/17/12 minutes).
/// This scheduler just reacts to whatever <c>DueAtMs</c> it's handed. A maintainer
/// tightening those intervals toward 20 minutes erodes the gap-fit boundary this class
/// depends on — backstop games (17-minute interval) only have ~3 minutes of headroom
/// to begin with.
/// </summary>
internal static class CadenceScheduler
{
    /// <summary>
    /// Decides what the runner should do next: service an overdue-or-soon-due keep-alive,
    /// run the next active macro pass, or sleep until nothing needs attention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stateless and does not rotate the list.</b> When multiple Active alts are present,
    /// this always returns the FIRST one encountered in <paramref name="alts"/> order. Fair
    /// round-robin across Active alts is entirely the CALLER's responsibility — the caller
    /// must rotate/requeue the serviced alt (e.g. move it to the back of the list) between
    /// calls, or every alt after the first Active silently starves.
    /// </para>
    /// <para>
    /// <b><paramref name="nextActivePassCostMs"/> must be a conservative UPPER BOUND</b> on
    /// how long one more Active pass would take — not an average, not a typical case. This
    /// method is called BETWEEN passes and cannot preempt one already running, so the entire
    /// hard-deadline guarantee for keep-alives rests on that estimate never being exceeded.
    /// An estimate that's too low lets a keep-alive get scheduled behind an active pass that
    /// outruns it, which is exactly the deadline miss this scheduler exists to prevent.
    /// </para>
    /// <para>
    /// <b><see cref="CadenceDecision.SleepUntil.WakeAtMs"/> is always strictly greater than
    /// <paramref name="nowMs"/>.</b> The caller sleeps for <c>WakeAtMs - nowMs</c> without
    /// clamping to a minimum, so this invariant is what keeps that sleep from collapsing to
    /// zero (or negative) and busy-spinning the loop.
    /// </para>
    /// </remarks>
    /// <param name="alts">
    /// Every alt currently under management, KeepAlive and Active mixed, in the caller's
    /// chosen order. List order is significant — see the round-robin note above.
    /// </param>
    /// <param name="nowMs">Current monotonic time in milliseconds.</param>
    /// <param name="nextActivePassCostMs">
    /// A conservative upper bound, in milliseconds, on how long one more Active macro pass
    /// would take. Used as the gap-fit lookahead: a keep-alive is urgent if it would miss its
    /// deadline should one more Active pass run before it. Pass 0 when no Active alt is queued.
    /// Should be <c>&gt;= 0</c>; because it's derived from user-editable on-disk macro data,
    /// a negative value is not trusted — <see cref="Decide"/> defensively clamps it to 0
    /// before use.
    /// </param>
    /// <returns>
    /// The single next action: <see cref="CadenceDecision.ServiceKeepAlive"/> for the most
    /// overdue urgent keep-alive, else <see cref="CadenceDecision.RunActive"/> for the first
    /// Active in list order, else <see cref="CadenceDecision.SleepUntil"/> until the earliest
    /// keep-alive deadline. When <paramref name="alts"/> contains no keep-alives AT ALL, that
    /// falls back to a short <c>nowMs + 1s</c> poll — but note any Active present is picked
    /// first, so in practice that fallback is only ever reached when <paramref name="alts"/>
    /// is completely empty (see the inline comment on it below for why that's effectively
    /// dead code from <see cref="AssignmentRunner"/>'s own caller, not a live "no keep-alives"
    /// case).
    /// </returns>
    public static CadenceDecision Decide(
        IReadOnlyList<ScheduledAlt> alts, long nowMs, long nextActivePassCostMs)
    {
        ScheduledAlt? urgent = null;
        ScheduledAlt? nextActive = null;
        long earliestDue = long.MaxValue;

        // nextActivePassCostMs is derived from Macro.Duration, which comes from the last
        // event's timestamp in a user-editable on-disk JSON macro file — a bare long with
        // no load-time validation. It can be pathological in either direction:
        //   - Too large (e.g. long.MaxValue): a raw `+` would overflow nowMs +
        //     nextActivePassCostMs to negative, making `DueAtMs <= <negative>` false for
        //     every alt — nothing would ever look urgent, RunActive would win forever, and
        //     every keep-alive would get silently kicked. SaturatingAdd clamps this to
        //     long.MaxValue, keeping the urgency check unconditionally true for any real
        //     DueAtMs — the safe failure mode.
        //   - Negative: SaturatingAdd only guards the positive-overflow direction (its own
        //     contract assumes non-negative inputs) — fed a negative b it takes the `a + b`
        //     branch and returns a horizon EARLIER than nowMs. Any keep-alive with DueAtMs
        //     between that horizon and nowMs is already overdue but reads as "not urgent",
        //     which is the exact same silent-kick failure from the other direction. Clamp
        //     to 0 first so a negative cost degrades to "no active pass in the way" — same
        //     as the caller explicitly passing 0 — and SaturatingAdd's non-negative-input
        //     assumption actually holds at its only call site.
        long urgencyHorizonMs = SaturatingAdd(nowMs, Math.Max(0, nextActivePassCostMs));

        foreach (var alt in alts)
        {
            if (alt.IsKeepAlive)
            {
                if (alt.DueAtMs < earliestDue) earliestDue = alt.DueAtMs;

                // Would this alt miss its deadline if we ran one more active pass first?
                // (With no actives, nextActivePassCostMs is 0 and this is simply "is it due".)
                if (alt.DueAtMs <= urgencyHorizonMs)
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
        //
        // earliestDue == long.MaxValue is the "no keep-alives exist at all" sentinel (the
        // seed value never got overwritten by the loop above). It must stay reserved for
        // that meaning — if a future change ever wants long.MaxValue as a legitimate
        // DueAtMs (e.g. to encode a paused alt that's never due), this fallback needs to
        // change too, or a paused alt would be indistinguishable from "no keep-alives".
        //
        // Honesty check on the `nowMs + 1_000` arm: by the time execution reaches here,
        // `nextActive` was already checked and found null, so alts contains no Active
        // either — earliestDue == long.MaxValue at this point therefore means alts
        // contains NEITHER a keep-alive NOR an Active, i.e. alts is empty. Decide's only
        // production caller, AssignmentRunner.RunAsync, returns immediately on an empty
        // assignment list (before Decide is ever called) and otherwise resolves every
        // alt to Active or KeepAlive — so this arm is unreachable from that caller. It
        // stays as a defensive default for Decide's OWN contract as a pure, general
        // function (e.g. a test — or a future caller — invoking it directly with an
        // empty list), not because RunAsync can ever actually reach it.
        return new CadenceDecision.SleepUntil(earliestDue == long.MaxValue ? nowMs + 1_000 : earliestDue);
    }

    /// <summary>
    /// Adds two non-negative millisecond timestamps, clamping to <see cref="long.MaxValue"/>
    /// instead of wrapping to negative on overflow.
    /// </summary>
    private static long SaturatingAdd(long a, long b)
        => long.MaxValue - a < b ? long.MaxValue : a + b;
}
