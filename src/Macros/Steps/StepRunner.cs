using System.Globalization;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>Everything the runner needs from the outside world. Coordinates are client-space.</summary>
internal interface IStepIo
{
    bool Send(MacroEvent clientEvent);
    /// <summary>Button up in screen space at wherever the cursor is now. Needs no window, so a
    /// held button still comes up after the target has closed (the v3 release rule).</summary>
    bool ReleaseButton(int button);
    bool MoveRelative(int dx, int dy);
    /// <summary>Moves the pointer to a client-space point above the client area, on the target
    /// window's own title bar, where it can hover no block. A move only, never a click. False, with
    /// nothing moved, when that point is not on the target's own frame (a borderless window has
    /// none, another window may cover it) or the window has gone.</summary>
    bool ParkPointer(int clientX, int clientY);
    (int X, int Y)? CursorClient();
    (int W, int H)? ClientSize();
    PixelBlock? Capture(int clientX, int clientY, int w, int h);
    bool TargetInForeground();
    Task Delay(int ms, CancellationToken ct);
    long NowMs { get; }
}

/// <param name="SharedBaseline">True for a ClearAt pass: its reach holds share one baseline frame
/// instead of parking before every spot (StepRunner's BaselineFrame).</param>
/// <param name="Guard">A ClearAt pass's guard pixel, sampled before every hold's first input and
/// before every press; a changed colour stops the playback (StepRunner.CheckGuard). Null: none.</param>
internal sealed record StepContext(
    string AccountName, long UserId, string MacroId,
    (int W, int H) RecordedClient, (int W, int H) ActualClient, int DisplayScale,
    Func<string, PointAdjustment?> Adjust, Action<string> Log, bool SharedBaseline = false,
    ScreenGuard? Guard = null);

/// <summary>
/// Plays v4 steps. A point with its check on treats its delay as a ceiling: it presses as soon
/// as the box shows the expected colour, then searches nearby, then stops with a report.
/// </summary>
internal static class StepRunner
{
    private sealed class StopException(string reason, int stepIndex) : Exception(reason)
    {
        public int StepIndex { get; } = stepIndex;
    }

    /// <summary>The target lost the foreground, or input could not be sent, just before an input
    /// event. Not a check failure: it ends the run like the step-boundary foreground abort.</summary>
    private sealed class InputBlockedException(bool sendFailed) : Exception
    {
        public bool SendFailed { get; } = sendFailed;
    }

    /// <summary>What a run did, for <see cref="PlaybackResult.SkippedByReach"/>.</summary>
    private sealed class RunTally
    {
        public bool Pressed;
        public int ReachSkips;
        /// <summary>1-based steps whose reach hold showed no outline (<see cref="PlaybackResult.NoOutline"/>).</summary>
        public readonly List<int> NoOutline = new();
    }

    /// <summary>
    /// A ClearAt pass's one baseline (live finding 2026-09-28 21:00): the per-spot park and settle
    /// cost most of a pass, and most spots are skipped. The pass parks once, captures the union of
    /// every point's reach box, and each point measures its baseline from that frame. A break
    /// changes the scene near it, so it drops the frame and the next reach hold takes a fresh one:
    /// 1 + breaks parks per pass, not one per spot. Recorded macros never share.
    /// <para>Two guards against a stale frame (controller ruling 2026-09-28): the frame is also
    /// taken again after <see cref="MaxPointsPerFrame"/> points without a break, and a point about
    /// to press a third time checks its outline against a fresh frame first
    /// (<see cref="PlayReachHoldsAsync"/>). A stale baseline under white quartz would press forever.</para>
    /// </summary>
    private sealed class BaselineFrame(IReadOnlyList<MacroStep> steps)
    {
        public const int MaxPointsPerFrame = 12;
        public IReadOnlyList<MacroStep> Steps { get; } = steps;
        public int Points { get; } = steps.Count(s => s is HoldStep { Reach: not null });
        /// <summary>Null until the first reach hold, and again after a break.</summary>
        public PixelBlock? Frame;
        /// <summary>How many points have measured their baseline from the current frame.</summary>
        public int PointsOnFrame;
        public int Parks;
    }

    /// <summary>A reach hold re-checks its outline on a fresh frame before this hold (shared pass only).</summary>
    private const int FreshBaselineBeforeHold = 3;

    /// <summary>Forwards everything to the real IO and notes any button or key going down, so a
    /// press by any step kind (raw steps included) counts. Releases in RunAsync's finally are ups,
    /// never downs.</summary>
    private sealed class PressWatchIo(IStepIo inner, RunTally tally) : IStepIo
    {
        public bool Send(MacroEvent clientEvent)
        {
            if (clientEvent.Kind is MacroEventKind.MouseDown or MacroEventKind.KeyDown) tally.Pressed = true;
            return inner.Send(clientEvent);
        }
        public bool ReleaseButton(int button) => inner.ReleaseButton(button);
        public bool MoveRelative(int dx, int dy) => inner.MoveRelative(dx, dy);
        public bool ParkPointer(int clientX, int clientY) => inner.ParkPointer(clientX, clientY);
        public (int X, int Y)? CursorClient() => inner.CursorClient();
        public (int W, int H)? ClientSize() => inner.ClientSize();
        public PixelBlock? Capture(int clientX, int clientY, int w, int h) => inner.Capture(clientX, clientY, w, h);
        public bool TargetInForeground() => inner.TargetInForeground();
        public Task Delay(int ms, CancellationToken ct) => inner.Delay(ms, ct);
        public long NowMs => inner.NowMs;
    }

    public static async Task<PlaybackResult> RunAsync(IReadOnlyList<MacroStep> steps, StepContext ctx, IStepIo io, CancellationToken ct)
    {
        var invalid = StepValidator.Validate(steps);
        if (invalid is not null) return PlaybackResult.Refused(invalid);

        var tally = new RunTally();
        var shared = ctx.SharedBaseline ? new BaselineFrame(steps) : null;
        io = new PressWatchIo(io, tally);
        var heldKeys = new HashSet<int>();
        var heldButtons = new HashSet<int>();
        int i = 0;
        try
        {
            for (; i < steps.Count; i++)
            {
                // Not a check failure: no StepIndex, so GetPlayback reports "aborted", not "check-failed".
                if (!io.TargetInForeground())
                    return PlaybackResult.Aborted($"Foreground shifted away from {ctx.AccountName} at step {i + 1}/{steps.Count}.");

                switch (steps[i])
                {
                    case KeyStep k:
                        await io.Delay(k.DelayMs, ct);
                        SendGuarded(io, new MacroEvent(0, k.Down ? MacroEventKind.KeyDown : MacroEventKind.KeyUp, k.VirtualKeyCode, 0, 0, 0, 0));
                        if (k.Down) heldKeys.Add(k.VirtualKeyCode); else heldKeys.Remove(k.VirtualKeyCode);
                        break;
                    case PointStep p:
                        await PlayPointAsync(p, i, ctx, io, heldButtons, tally, ct);
                        break;
                    case FirstMatchStep f:
                        await PlayFirstMatchAsync(f, i, ctx, io, heldButtons, ct);
                        break;
                    case HoldStep h:
                        await PlayHoldAsync(h, i, ctx, io, heldButtons, tally, shared, ct);
                        break;
                    case SweepStep s:
                        await PlaySweepAsync(s, i, ctx, io, heldButtons, ct);
                        break;
                    case DragStep d:
                        await PlayDragAsync(d, ctx, io, heldButtons, ct);
                        break;
                    case WheelStep w:
                        await io.Delay(w.DelayMs, ct);
                        var wp = Place(ctx, (w.X, w.Y));
                        await JumpAsync(io, wp, ct);
                        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseWheel, 0, wp.X, wp.Y, 0, w.Delta));
                        break;
                    case PointerMoveStep m:
                        await io.Delay(m.DelayMs, ct);
                        await PlayRelativeAsync(m, io, ct);
                        break;
                    case WaitStep w:
                        await io.Delay(w.DelayMs, ct);
                        break;
                    case RawStep r:
                        await io.Delay(r.DelayMs, ct);
                        await PlayRawAsync(r, ctx, io, heldKeys, heldButtons, ct);
                        break;
                }
            }
            var done = tally.ReachSkips > 0 && !tally.Pressed ? PlaybackResult.CompletedSkippedByReach() : PlaybackResult.Completed();
            return done with { NoOutline = tally.NoOutline.ToArray() };
        }
        catch (InputBlockedException b)
        {
            // Same shape as the step-boundary abort: no StepIndex, so it never reads as check-failed.
            return PlaybackResult.Aborted(b.SendFailed
                ? $"Could not send input to {ctx.AccountName} at step {i + 1}/{steps.Count}."
                : $"Foreground shifted away from {ctx.AccountName} at step {i + 1}/{steps.Count}.");
        }
        catch (StopException s)
        {
            return PlaybackResult.AbortedAt(s.Message, s.StepIndex);
        }
        catch (OperationCanceledException)
        {
            return PlaybackResult.Aborted("Playback cancelled."); // Esc or StopMacro, not a check failure
        }
        finally
        {
            // Keys need no window. Buttons go up in screen space at the cursor, never through the
            // client-space Send: when the window has gone (AutoStopCoordinator aborts exactly
            // then), Send drops the event and the button would stay down system-wide.
            foreach (var vk in heldKeys) io.Send(new MacroEvent(0, MacroEventKind.KeyUp, vk, 0, 0, 0, 0));
            foreach (var b in heldButtons) io.ReleaseButton(b);
            if (shared is not null)
                ctx.Log(string.Create(CultureInfo.InvariantCulture,
                    $"ClearAt: shared baseline ({shared.Parks} {(shared.Parks == 1 ? "park" : "parks")} for {shared.Points} {(shared.Points == 1 ? "point" : "points")})"));
        }
    }

    // ---------- points ----------

    private static async Task PlayPointAsync(PointStep p, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, RunTally tally, CancellationToken ct)
    {
        var (recorded, check) = Resolve(p, ctx);
        var at = Place(ctx, recorded);

        if (p.Reach is not null)
        {
            // StepValidator refuses a reach check together with an enabled colour check.
            await PlayReachedPointAsync(p, at, index, ctx, io, heldButtons, tally, ct);
            return;
        }

        if (!p.CheckEnabled || check is null)
        {
            await io.Delay(p.DelayMs, ct);
            await PressAsync(io, at, p.Button, heldButtons, ct);
            return;
        }

        var name = $"{ctx.AccountName}: step {index + 1} '{p.Label ?? p.Id}'";
        var box = PointMath.BoxRect(at, check.Box);
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        if (!PointMath.InsideClient(box, client)) throw new StopException($"{name} checks a box outside the window.", index);

        await ParkCursorAsync(io, new[] { box }, client, ct);
        var start = io.NowMs;
        var ceiling = p.DelayMs + StepTiming.CheckGraceMs;
        Rgb seen;
        while (true)
        {
            // Every poll: a window that came to the front covers the target, and the capture
            // would read its pixels as a false colour (spec §2 and §5).
            if (!io.TargetInForeground()) throw new StopException($"{name} could not see the window.", index);
            var block = io.Capture(box.X, box.Y, box.W, box.H) ?? throw new StopException($"{name} could not see the window.", index);
            // A capture of the wrong shape is a window it could not see, never an exception.
            seen = block.AverageBox(box.X, box.Y, box.W, box.H) ?? throw new StopException($"{name} could not see the window.", index);
            var v = ColorMatcher.Evaluate(seen, check);
            if (v.Matched)
            {
                // Logged so the default tolerance can be tuned from real runs (spec, section 1).
                ctx.Log(string.Create(CultureInfo.InvariantCulture,
                    $"step {index + 1} '{p.Label ?? p.Id}' matched at distance {v.Distance:F0} after {(io.NowMs - start) / 1000.0:F1} s"));
                await PressAsync(io, at, p.Button, heldButtons, ct);
                return;
            }
            if (io.NowMs - start >= ceiling) break;
            await io.Delay(StepTiming.PollMs, ct);
        }

        var found = Search(io, at, check, client, ctx);
        if (found is { } hit)
        {
            ctx.Log($"step {index + 1} '{p.Label ?? p.Id}' found {Describe(hit.X - at.X, hit.Y - at.Y)}");
            await PressAsync(io, hit, p.Button, heldButtons, ct);
            return;
        }

        var d = seen.DistanceTo(check.Expect);
        var secs = (io.NowMs - start) / 1000.0;
        throw new StopException(string.Create(CultureInfo.InvariantCulture,
            $"{name} expected {ColorNamer.Describe(check.Expect)}, saw {ColorNamer.Describe(seen)} (distance {d:F0}) after {secs:F1} s at {ctx.DisplayScale}%."), index);
    }

    /// <summary>A point behind a reach check: wait its delay, move onto the point, and press only
    /// if the white outline shows there (ore-stop pulse spec). No outline skips the step.</summary>
    private static async Task PlayReachedPointAsync(PointStep p, (int X, int Y) at, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, RunTally tally, CancellationToken ct)
    {
        var label = p.Label ?? p.Id;
        var name = $"{ctx.AccountName}: step {index + 1} '{label}'";
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        var reach = ReachFor(p.Reach, at, client, ctx, name, index)!.Value;

        await io.Delay(p.DelayMs, ct);
        await JumpAsync(io, at, ct);
        var (seen, area) = await AwaitOutlineAsync(reach, baseline: 0, io, name, index, ct); // points take no baseline
        LogOutline(ctx, index, label, seen, area.Count, reach.Need);
        if (!seen) { tally.ReachSkips++; return; }
        await ClickAsync(io, at, p.Button, heldButtons, ct); // already on the point: no second jump
    }

    private static (int X, int Y)? Search(IStepIo io, (int X, int Y) at, ColorCheck check, (int W, int H) client, StepContext ctx)
    {
        var radius = PointMath.ScaledRadius(ctx.RecordedClient, ctx.ActualClient);
        var b0 = PointMath.BoxRect(at, check.Box);
        int x0 = Math.Max(0, b0.X - radius), y0 = Math.Max(0, b0.Y - radius);
        int x1 = Math.Min(client.W, b0.X + b0.W + radius), y1 = Math.Min(client.H, b0.Y + b0.H + radius);
        if (x1 <= x0 || y1 <= y0) return null;
        var block = io.Capture(x0, y0, x1 - x0, y1 - y0);
        if (block is null) return null;
        foreach (var (dx, dy) in PointMath.SearchOffsets(radius))
        {
            var cand = (X: at.X + dx, Y: at.Y + dy);
            var r = PointMath.BoxRect(cand, check.Box);
            if (!PointMath.InsideClient(r, client)) continue;
            var seen = block.AverageBox(r.X, r.Y, r.W, r.H);
            if (seen is { } s && ColorMatcher.Evaluate(s, check).Matched) return cand;
        }
        return null;
    }

    private static string Describe(int dx, int dy)
    {
        var parts = new List<string>();
        if (dx != 0) parts.Add($"{Math.Abs(dx)} px {(dx < 0 ? "left" : "right")}");
        if (dy != 0) parts.Add($"{Math.Abs(dy)} px {(dy < 0 ? "up" : "down")}");
        return string.Join(" and ", parts);
    }

    // ---------- first match ----------

    private static async Task PlayFirstMatchAsync(FirstMatchStep f, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var name = $"{ctx.AccountName}: step {index + 1} '{f.Label ?? f.Id}'";
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        var cands = f.Candidates.Select(c =>
        {
            var (rec, check) = Resolve(c, ctx);
            var at = Place(ctx, rec);
            return (Step: c, At: at, Check: check!, Box: PointMath.BoxRect(at, check!.Box));
        }).ToList();
        foreach (var c in cands)
            if (!PointMath.InsideClient(c.Box, client))
                throw new StopException($"{name}: candidate '{c.Step.Label ?? c.Step.Id}' checks a box outside the window.", index);

        // Hover tints a button, so the pointer leaves every candidate box before the first sample.
        await ParkCursorAsync(io, cands.Select(c => c.Box).ToList(), client, ct);
        var start = io.NowMs;
        var ceiling = f.DelayMs + StepTiming.CheckGraceMs;
        var seen = new Rgb[cands.Count];
        bool allOther = false; // every candidate settled on its other state, none matched
        while (true)
        {
            if (!io.TargetInForeground()) throw new StopException($"{name} could not see the window.", index);
            bool allResolved = true;
            int firstMatch = -1;
            for (int k = 0; k < cands.Count; k++)
            {
                var b = cands[k].Box;
                var block = io.Capture(b.X, b.Y, b.W, b.H) ?? throw new StopException($"{name} could not see the window.", index);
                seen[k] = block.AverageBox(b.X, b.Y, b.W, b.H) ?? throw new StopException($"{name} could not see the window.", index);
                if (ColorMatcher.Evaluate(seen[k], cands[k].Check).Matched) { if (firstMatch < 0) firstMatch = k; continue; }
                // Resolved without matching only when it shows its other state.
                if (!ColorMatcher.ShowsOther(seen[k], cands[k].Check)) allResolved = false;
            }
            var expired = io.NowMs - start >= ceiling;
            if (firstMatch >= 0 && (allResolved || expired || AllBeforeResolved(cands, seen, firstMatch)))
            {
                ctx.Log($"step {index + 1} '{f.Label ?? f.Id}' chose '{cands[firstMatch].Step.Label ?? cands[firstMatch].Step.Id}'");
                await PressAsync(io, cands[firstMatch].At, cands[firstMatch].Step.Button, heldButtons, ct);
                return;
            }
            if (expired || (allResolved && firstMatch < 0)) { allOther = allResolved && firstMatch < 0; break; }
            await io.Delay(StepTiming.PollMs, ct);
        }

        if (f.OnNoMatch == NoMatchAction.Skip || (f.OnNoMatch == NoMatchAction.SkipIfOther && allOther))
        {
            ctx.Log($"step {index + 1} '{f.Label ?? f.Id}' found no match and skipped");
            return;
        }
        var secs = (io.NowMs - start) / 1000.0;
        // Full colour names (never cut: "dark blue" must not become "dark"), plus expected colour
        // and distance for each candidate, per the failure-text constraint.
        var detail = string.Join("; ", cands.Select((c, k) => string.Create(CultureInfo.InvariantCulture,
            $"'{c.Step.Label ?? c.Step.Id}' expected {ColorNamer.Describe(c.Check.Expect)}, saw {ColorNamer.Describe(seen[k])} (distance {seen[k].DistanceTo(c.Check.Expect):F0})")));
        throw new StopException(string.Create(CultureInfo.InvariantCulture,
            $"{name} found no candidate that matched after {secs:F1} s at {ctx.DisplayScale}% ({detail})."), index);
    }

    /// <summary>A match may be pressed early when every candidate ahead of it shows its other state.</summary>
    private static bool AllBeforeResolved(List<(PointStep Step, (int X, int Y) At, ColorCheck Check, (int X, int Y, int W, int H) Box)> cands, Rgb[] seen, int firstMatch)
    {
        for (int k = 0; k < firstMatch; k++)
            if (!ColorMatcher.ShowsOther(seen[k], cands[k].Check)) return false;
        return true;
    }

    // ---------- hold ----------

    /// <summary>
    /// Press and keep holding while the box stays within tolerance of the colour it showed just
    /// before the press. The starting sample is taken after the jump, so the pointer's own hover
    /// tint is already in it. Lets go when the colour has moved for
    /// <see cref="StepTiming.HoldDriftPolls"/> polls in a row, or at MaxMs. With no MaxMs there is
    /// no time limit: ore is never abandoned for taking long (ore-stop spec, decision 5).
    /// <para>With a reach check (ore-stop pulse spec): after the jump, no white outline within
    /// <see cref="StepTiming.ReachGraceMs"/> skips the step without pressing. Otherwise it holds
    /// on one block until it breaks (<see cref="PlayReachHoldsAsync"/>), and colour drift plays no
    /// part.</para>
    /// </summary>
    private static async Task PlayHoldAsync(HoldStep h, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, RunTally tally, BaselineFrame? shared, CancellationToken ct)
    {
        var label = h.Label ?? h.Id;
        var name = $"{ctx.AccountName}: step {index + 1} '{label}'";
        var at = HoldAt(h, ctx);
        var check = h.Check!; // StepValidator refuses a hold without one
        var box = PointMath.BoxRect(at, check.Box);
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        if (!PointMath.InsideClient(box, client)) throw new StopException($"{name} checks a box outside the window.", index);
        var reach = ReachFor(h.Reach, at, client, ctx, name, index);

        await io.Delay(h.DelayMs, ct);
        CheckGuard(ctx, io, index, name); // before the park, the hover or any press for this point
        // Baseline before the pointer lands (controller ruling 2026-09-28): white quartz passes the
        // raw count with no hover, so a reach hold looks for the rise over the box as it is unhovered.
        // A ClearAt pass shares one frame (BaselineFrame); a recorded hold parks for its own.
        var baseline = reach is not { } rb ? 0
            : shared is not null ? await SharedBaselineAsync(shared, rb, io, client, ctx, index, label, name, ct)
            : await BaselineAsync(rb, io, client, ctx, index, label, name, ct);
        CheckGuard(ctx, io, index, name); // the park took a settle: look again before the hover
        await JumpAsync(io, at, ct);
        if (reach is { } r0)
        {
            var (seen0, area0) = await AwaitOutlineAsync(r0, baseline, io, name, index, ct);
            LogOutline(ctx, index, label, seen0, area0.Count, r0.Need, baseline);
            if (!seen0) { tally.ReachSkips++; tally.NoOutline.Add(index + 1); return; }
            Func<Task<int>>? fresh = shared is null ? null : () =>
            {
                shared.Frame = null; // a fresh park and frame, kept for the rest of the pass
                return SharedBaselineAsync(shared, r0, io, client, ctx, index, label, name, ct);
            };
            var broke = await PlayReachHoldsAsync(h, r0, baseline, area0, at, index, label, name, ctx, io, heldButtons, fresh, tally, ct);
            if (broke && shared is not null) shared.Frame = null; // the scene nearby changed: a fresh frame next
            return;
        }
        var start = SampleGuarded(io, box) ?? throw new StopException($"{name} could not see the window.", index);

        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, h.Button, 0));
        heldButtons.Add(h.Button);
        var pressedAt = io.NowMs;
        long? limit = h.MaxMs;
        string why;
        try
        {
            int drifted = 0;
            while (true)
            {
                var elapsed = io.NowMs - pressedAt;
                if (elapsed >= limit) { why = $"reached its {limit} ms limit"; break; }
                await io.Delay((int)Math.Min(StepTiming.PollMs, (limit ?? long.MaxValue) - elapsed), ct);
                if (io.NowMs - pressedAt >= limit) continue; // the limit lets go at the top, without another sample
                var seen = SampleGuarded(io, box) ?? throw new StopException($"{name} could not see the window.", index);
                var d = seen.DistanceTo(start);
                drifted = d <= check.Tolerance ? 0 : drifted + 1;
                if (drifted >= StepTiming.HoldDriftPolls)
                {
                    why = string.Create(CultureInfo.InvariantCulture,
                        $"colour moved from {ColorNamer.Describe(start)} to {ColorNamer.Describe(seen)} (distance {d:F0})");
                    break;
                }
            }
            SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, at.X, at.Y, h.Button, 0));
            heldButtons.Remove(h.Button);
        }
        catch
        {
            // Esc, StopMacro, a focus change or a window it can no longer see. RunAsync's finally
            // releases the button, which is still in heldButtons. The log still says how long it
            // held, so an ore that never broke is visible in ur-task.log.
            ctx.Log(string.Create(CultureInfo.InvariantCulture,
                $"step {index + 1} '{label}' held {(io.NowMs - pressedAt) / 1000.0:F1} s, then the playback ended"));
            throw;
        }
        ctx.Log(string.Create(CultureInfo.InvariantCulture,
            $"step {index + 1} '{label}' held {(io.NowMs - pressedAt) / 1000.0:F1} s, released: {why}"));
    }

    /// <summary>
    /// A hold behind a reach check breaks one block and stops (ore-stop pulse spec, "One block per
    /// spot"). A long press digs a column: the main breaks a block about every 0.3 s, and the block
    /// below lights up at the same spot. So it holds for <see cref="StepTiming.FirstHoldMs"/>, lets
    /// go, waits <see cref="StepTiming.LookSettleMs"/>, and looks (the game hides the outline while
    /// the button is down). No outline: the block broke. The outline's bounding box moved (any edge
    /// more than <see cref="StepTiming.OutlineMoveTolerancePx"/>): the block broke and the next one
    /// down is showing, which it leaves alone. The same box: a hit, not a break, so it holds again,
    /// twice as long, up to <see cref="StepTiming.MaxHoldMs"/> (a hard block's progress does not
    /// carry across releases, so short presses never break it). A hit darkens the block, so colour
    /// drift ends nothing here.
    /// <para>MaxMs bounds the pressed time across holds: the last hold is cut to what is left, and
    /// the step ends at the limit without a look. With no MaxMs there is no limit (decision 5).</para>
    /// <para>In a shared ClearAt pass (<paramref name="freshBaseline"/> set), two "same box" looks
    /// may mean a stale baseline rather than a hard block, so before hold
    /// <see cref="FreshBaselineBeforeHold"/> it parks, takes a fresh baseline, jumps back and looks
    /// again. No outline over the fresh baseline stops the point: it pressed, so it is no skip.</para>
    /// <para>True when the block broke; false when the step ended at its MaxMs limit or on the
    /// fresh-baseline check. The fresh-baseline stop also names the step in
    /// <see cref="RunTally.NoOutline"/>: nothing broke there.</para>
    /// </summary>
    private static async Task<bool> PlayReachHoldsAsync(HoldStep h, ReachPlan r, int baseline, NearWhiteArea before, (int X, int Y) at, int index, string label, string name, StepContext ctx, IStepIo io, HashSet<int> heldButtons, Func<Task<int>>? freshBaseline, RunTally tally, CancellationToken ct)
    {
        long pressedMs = 0, holdStart = -1;
        int holds = 0, nextHoldMs = StepTiming.FirstHoldMs;
        string outcome;
        bool broke = false;
        try
        {
            while (true)
            {
                CheckGuard(ctx, io, index, name); // before every hold, and before a fresh baseline's park
                if (freshBaseline is not null && holds == FreshBaselineBeforeHold - 1)
                {
                    baseline = await freshBaseline();
                    await JumpAsync(io, at, ct);
                    var (still, area) = await AwaitOutlineAsync(r, baseline, io, name, index, ct);
                    if (!still) { outcome = "no outline on a fresh baseline, stopped"; tally.NoOutline.Add(index + 1); break; }
                    before = area;
                    CheckGuard(ctx, io, index, name); // the park and look took time: look again
                }
                var holdMs = h.MaxMs is { } max ? Math.Min(nextHoldMs, max - pressedMs) : nextHoldMs;
                SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, h.Button, 0));
                heldButtons.Add(h.Button);
                holds++;
                holdStart = io.NowMs;
                while (io.NowMs - holdStart < holdMs)
                {
                    // Foreground only: the outline is hidden while pressed and a hit moves the colour.
                    await io.Delay((int)Math.Min(StepTiming.PollMs, holdMs - (io.NowMs - holdStart)), ct);
                    GuardForeground(io);
                }
                SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, at.X, at.Y, h.Button, 0));
                heldButtons.Remove(h.Button);
                pressedMs += io.NowMs - holdStart;
                holdStart = -1;
                if (pressedMs >= h.MaxMs)
                {
                    outcome = string.Create(CultureInfo.InvariantCulture,
                        $"pressed {pressedMs / 1000.0:F1} s over {holds} hold(s), released: reached its limit");
                    break;
                }

                await io.Delay(StepTiming.LookSettleMs, ct);
                var (seen, after) = await AwaitOutlineAsync(r, baseline, io, name, index, ct); // the same baseline as the pre-press check
                var brokeLine = string.Create(CultureInfo.InvariantCulture, $"broke after {holds} hold(s), {pressedMs / 1000.0:F1} s held");
                if (!seen) { outcome = brokeLine; broke = true; break; }
                if (OutlineMoved(before.Bounds, after.Bounds)) { outcome = brokeLine + " (outline moved to the next block)"; broke = true; break; }
                before = after;
                nextHoldMs = Math.Min(nextHoldMs * 2, StepTiming.MaxHoldMs); // a hit, not a break: hold longer
            }
        }
        catch
        {
            // As for any hold: RunAsync's finally releases a button still in heldButtons.
            var total = pressedMs + (holdStart >= 0 ? io.NowMs - holdStart : 0);
            ctx.Log(string.Create(CultureInfo.InvariantCulture,
                $"step {index + 1} '{label}' pressed {total / 1000.0:F1} s over {holds} hold(s), then the playback ended"));
            throw;
        }
        ctx.Log(string.Create(CultureInfo.InvariantCulture, $"step {index + 1} '{label}' {outcome}"));
        return broke;
    }

    /// <summary>True when any edge of the outline's bounding box moved more than
    /// <see cref="StepTiming.OutlineMoveTolerancePx"/>. A seen outline always has bounds (its count
    /// is at least Need over the baseline); a missing one reads as moved, never as the same block.</summary>
    private static bool OutlineMoved((int MinX, int MinY, int MaxX, int MaxY)? a, (int MinX, int MinY, int MaxX, int MaxY)? b)
    {
        if (a is not { } p || b is not { } q) return true;
        const int tol = StepTiming.OutlineMoveTolerancePx;
        return Math.Abs(p.MinX - q.MinX) > tol || Math.Abs(p.MinY - q.MinY) > tol
            || Math.Abs(p.MaxX - q.MaxX) > tol || Math.Abs(p.MaxY - q.MaxY) > tol;
    }

    // ---------- the ClearAt guard (live safety bug 2026-09-28 23:49) ----------

    /// <summary>
    /// A ClearAt hold landed on another player and opened their profile, Friend and Trade buttons
    /// and all; the pass only noticed at its end. With a guard (<see cref="StepContext.Guard"/>)
    /// every point and every hold first averages the guard box, placed and scaled like a reach box,
    /// the same way colour checks sample. Further than its tolerance from the expected colour, or a
    /// box it cannot see, stops the playback as a failed check before any more input: a menu or a
    /// profile covers the game. Released buttons stay released; RunAsync's finally lets go of any
    /// still down. No guard: nothing is sampled.
    /// </summary>
    private static void CheckGuard(StepContext ctx, IStepIo io, int index, string name, string who = "ClearAt")
    {
        if (GuardBroken(ctx, io, index, name) is { } seen) throw new StopException(GuardStopText(who, ctx.Guard!, seen), index);
    }

    /// <summary>The colour the guard box shows when it has moved past its tolerance; null when it
    /// holds or there is no guard. The box is placed and scaled like a reach box. A box it cannot
    /// see stops the step, as any capture does.</summary>
    private static Rgb? GuardBroken(StepContext ctx, IStepIo io, int index, string name)
    {
        if (ctx.Guard is not { } g) return null;
        var rect = PointMath.ScaledRect(Place(ctx, (g.X, g.Y)), new CheckBox(0, 0, g.W, g.H), ctx.RecordedClient, ctx.ActualClient);
        var seen = SampleGuarded(io, rect) ?? throw new StopException($"{name} could not see the window.", index);
        return seen.DistanceTo(g.Expect) <= g.Tolerance ? null : seen;
    }

    private static string GuardStopText(string who, ScreenGuard g, Rgb seen) => string.Create(CultureInfo.InvariantCulture,
        $"{who} stopped: the guard at ({g.X},{g.Y}) isn't the expected colour (saw {seen.Hex}); something may be over the game (a menu or a player's profile).");

    // ---------- sweep (ore-stop sweep spec, "The Ur Task side") ----------

    /// <summary>
    /// One continuous hold along a path. Whatever block is under the pointer while the button is down
    /// gets mined, so there is no per-point look, baseline or park. After the usual jump the button
    /// goes down on Path[0], the start block beside the character; the pointer moves to each later
    /// point with one real mouse move (Roblox ignores a bare cursor jump; a hop over the HUD is the
    /// same single straight input) and waits DwellMs there; the button comes up on the last point,
    /// which the bridge made sure is Path[0] again. A release is a click, and the start block is the
    /// one place a click can't land on a player or a chest.
    /// <para>The guard is sampled before the press and before every
    /// <see cref="StepTiming.SweepGuardEveryPoints"/>-th point while held, and inside a dwell whenever
    /// <see cref="StepTiming.SweepGuardMaxGapMs"/> has passed without a sample. A change goes back to the
    /// start block, lets go there, and stops the playback as a failed check. The foreground is checked
    /// before every input and every <see cref="StepTiming.PollMs"/> of a dwell; losing it lets go in
    /// place through RunAsync's finally, as every hold does. Esc, StopMacro and a window it can no
    /// longer see also go back to the start block to let go, when the target is still in front.</para>
    /// </summary>
    private static async Task PlaySweepAsync(SweepStep s, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        const string who = "SweepPath";
        var name = $"{ctx.AccountName}: step {index + 1} '{who}'";
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        var path = s.Path.Select(p => Place(ctx, (p.X, p.Y))).ToList();
        if (path.Any(p => p.X < 0 || p.Y < 0 || p.X >= client.W || p.Y >= client.H))
            throw new StopException($"{name} has a point outside the window.", index);
        var start = path[0];

        await io.Delay(s.DelayMs, ct);
        CheckGuard(ctx, io, index, name, who);   // nothing is down yet: a covered game gets no input at all
        await JumpAsync(io, start, ct);          // Roblox drops a press after focus unless it saw movement
        CheckGuard(ctx, io, index, name, who);   // the jump took a wiggle: look again before the press
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, start.X, start.Y, s.Button, 0));
        heldButtons.Add(s.Button);
        var pressedAt = io.NowMs;
        var reached = 1;
        var guardedAt = io.NowMs;
        void Sample()
        {
            guardedAt = io.NowMs;
            if (GuardBroken(ctx, io, index, name) is { } seen)
                throw new StopException(GuardStopText(who, ctx.Guard!, seen), index);
        }
        void SampleIfDue() { if (io.NowMs - guardedAt >= StepTiming.SweepGuardMaxGapMs) Sample(); }
        try
        {
            await DwellAsync(io, s.DwellMs, ct, SampleIfDue);
            for (int k = 1; k < path.Count; k++)
            {
                if (k % StepTiming.SweepGuardEveryPoints == 0) Sample();
                SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0, path[k].X, path[k].Y, 0, 0));
                reached++;
                if (k < path.Count - 1) await DwellAsync(io, s.DwellMs, ct, SampleIfDue); // the last point is where it lets go
            }
            SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, path[^1].X, path[^1].Y, s.Button, 0));
            heldButtons.Remove(s.Button);
        }
        // Deliberately broad: any exception while the button is down goes back to the start block before it is rethrown.
        catch (Exception e)
        {
            // A lost foreground lets RunAsync's finally release in place, as every hold does.
            var onStart = e is not InputBlockedException && ReleaseOnStart(io, start, s.Button, heldButtons);
            ctx.Log(string.Create(CultureInfo.InvariantCulture,
                $"swept {reached} of {path.Count} points in {(io.NowMs - pressedAt) / 1000.0:F1} s, then the playback ended{(onStart ? "; released on the start block" : "")}"));
            throw;
        }
        ctx.Log(string.Create(CultureInfo.InvariantCulture, $"swept {path.Count} points in {(io.NowMs - pressedAt) / 1000.0:F1} s"));
    }

    /// <summary>Waits <paramref name="ms"/> with the button down, checking the foreground every
    /// <see cref="StepTiming.PollMs"/>, and running <paramref name="onPoll"/> at each check.</summary>
    private static async Task DwellAsync(IStepIo io, int ms, CancellationToken ct, Action? onPoll = null)
    {
        var start = io.NowMs;
        while (io.NowMs - start < ms)
        {
            await io.Delay((int)Math.Min(StepTiming.PollMs, ms - (io.NowMs - start)), ct);
            GuardForeground(io);
            onPoll?.Invoke();
        }
    }

    /// <summary>Best effort and never a wait (the token may already be cancelled): with the target
    /// still in front, move to the start block and let go there. False when it could not; RunAsync's
    /// finally then lets go wherever the pointer is.</summary>
    private static bool ReleaseOnStart(IStepIo io, (int X, int Y) start, int button, HashSet<int> heldButtons)
    {
        if (!heldButtons.Contains(button) || !io.TargetInForeground()) return false;
        if (!io.Send(new MacroEvent(0, MacroEventKind.MouseMove, 0, start.X, start.Y, 0, 0))) return false;
        if (!io.Send(new MacroEvent(0, MacroEventKind.MouseUp, 0, start.X, start.Y, button, 0))) return false;
        heldButtons.Remove(button);
        return true;
    }

    // ---------- reach (the white outline) ----------

    /// <summary>A reach check placed in the actual window: the box, the scaled threshold, and the
    /// white floor.</summary>
    private readonly record struct ReachPlan((int X, int Y, int W, int H) Rect, int Need, int WhiteMin);

    /// <summary>Null when the step has no reach check. The box scales like the point; one that
    /// leaves the window stops the step before any input, like a colour box does.</summary>
    private static ReachPlan? ReachFor(OutlineCheck? reach, (int X, int Y) at, (int W, int H) client, StepContext ctx, string name, int index)
    {
        if (reach is null) return null;
        var rect = PointMath.ScaledRect(at, reach.Box, ctx.RecordedClient, ctx.ActualClient);
        if (!PointMath.InsideClient(rect, client)) throw new StopException($"{name} checks a reach box outside the window.", index);
        return new ReachPlan(rect, PointMath.ScaledCount(reach.MinCount, ctx.RecordedClient, ctx.ActualClient), reach.WhiteMin);
    }

    /// <summary>With the pointer already on the spot: measure until the outline shows, for at most
    /// <see cref="StepTiming.ReachGraceMs"/>. The outline shows when the count rises at least Need
    /// over <paramref name="baseline"/> (0 for reach points). The area carries the count and the
    /// near-white bounds of the last look. A lost foreground aborts and a failed capture stops the
    /// step; neither ever reads as "no outline".</summary>
    private static async Task<(bool Seen, NearWhiteArea Area)> AwaitOutlineAsync(ReachPlan r, int baseline, IStepIo io, string name, int index, CancellationToken ct)
    {
        var start = io.NowMs;
        while (true)
        {
            var area = MeasureGuarded(io, r.Rect, r.WhiteMin) ?? throw new StopException($"{name} could not see the window.", index);
            if (area.Count - baseline >= r.Need) return (true, area);
            if (io.NowMs - start >= StepTiming.ReachGraceMs) return (false, area);
            await io.Delay(StepTiming.PollMs, ct);
        }
    }

    /// <summary>Where the baseline park puts the pointer: this far above the client area, on the
    /// title bar, where it can hover no block.</summary>
    private const int TitleBarParkY = -12;

    /// <summary>
    /// A reach hold's baseline (controller ruling 2026-09-28): the box's near-white count with the
    /// pointer off it. White quartz passes the count with no hover at all, so an out-of-reach quartz
    /// block would otherwise read as outlined and be pressed forever. The pointer parks on the
    /// window's title bar (client centre x, <see cref="TitleBarParkY"/>), a move only, and waits
    /// <see cref="StepTiming.LookSettleMs"/> for the hover outline to clear ("One block per spot":
    /// a spot one box-width away can light the neighbouring block, whose edge falls in the box).
    /// <para>A window with no title bar (borderless) refuses the park. Then, as before, a pointer
    /// already inside the box moves one box-width outside it (right of the box, else left, at its
    /// middle height, clamped to the client) and waits the same settle. A failed capture stops the
    /// step like any other.</para>
    /// </summary>
    private static async Task<int> BaselineAsync(ReachPlan r, IStepIo io, (int W, int H) client, StepContext ctx, int index, string label, string name, CancellationToken ct)
    {
        await ParkForBaselineAsync(r.Rect, r.Rect.W, io, client, ctx, index, label, ct);
        return MeasureGuarded(io, r.Rect, r.WhiteMin)?.Count ?? throw new StopException($"{name} could not see the window.", index);
    }

    /// <summary>
    /// A ClearAt point's baseline from the pass's shared frame (<see cref="BaselineFrame"/>). With
    /// no frame (the first reach hold, or the first after a break) it parks as
    /// <see cref="BaselineAsync"/> does, the union of every point's reach box standing in for the
    /// one box, and captures that union once. A frame that does not hold this point's box (the
    /// window changed size since) is taken again. A failed capture stops the step like any other.
    /// </summary>
    private static async Task<int> SharedBaselineAsync(BaselineFrame shared, ReachPlan r, IStepIo io, (int W, int H) client, StepContext ctx, int index, string label, string name, CancellationToken ct)
    {
        if (shared.PointsOnFrame < BaselineFrame.MaxPointsPerFrame
            && shared.Frame?.MeasureNearWhite(r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H, r.WhiteMin) is { } kept)
        {
            shared.PointsOnFrame++;
            return kept.Count;
        }

        var union = ReachUnion(shared.Steps, r.Rect, ctx, client);
        await ParkForBaselineAsync(union, r.Rect.W, io, client, ctx, index, label, ct);
        shared.Parks++;
        GuardForeground(io);
        shared.Frame = io.Capture(union.X, union.Y, union.W, union.H);
        shared.PointsOnFrame = 1;
        return shared.Frame?.MeasureNearWhite(r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H, r.WhiteMin)?.Count
            ?? throw new StopException($"{name} could not see the window.", index);
    }

    /// <summary>The smallest rect holding <paramref name="own"/> and every reach hold's box that
    /// fits the window. A box outside the window is left out; its own step stops when it plays.</summary>
    private static (int X, int Y, int W, int H) ReachUnion(IReadOnlyList<MacroStep> steps, (int X, int Y, int W, int H) own, StepContext ctx, (int W, int H) client)
    {
        int x0 = own.X, y0 = own.Y, x1 = own.X + own.W, y1 = own.Y + own.H;
        foreach (var h in steps.OfType<HoldStep>())
        {
            if (h.Reach is null) continue;
            var b = PointMath.ScaledRect(HoldAt(h, ctx), h.Reach.Box, ctx.RecordedClient, ctx.ActualClient);
            if (!PointMath.InsideClient(b, client)) continue;
            x0 = Math.Min(x0, b.X); y0 = Math.Min(y0, b.Y);
            x1 = Math.Max(x1, b.X + b.W); y1 = Math.Max(y1, b.Y + b.H);
        }
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Moves the pointer off <paramref name="away"/> before a baseline count: the title-bar park,
    /// then <see cref="StepTiming.LookSettleMs"/>. When the window refuses the park, a pointer
    /// inside <paramref name="away"/> moves <paramref name="gap"/> px outside it (right, else left,
    /// at its middle height, clamped to the client) and waits the same settle.
    /// </summary>
    private static async Task ParkForBaselineAsync((int X, int Y, int W, int H) away, int gap, IStepIo io, (int W, int H) client, StepContext ctx, int index, string label, CancellationToken ct)
    {
        GuardForeground(io);
        if (io.ParkPointer(client.W / 2, TitleBarParkY))
        {
            await io.Delay(StepTiming.LookSettleMs, ct);
            return;
        }
        ctx.Log($"step {index + 1} '{label}' could not park the pointer on the title bar; took the baseline beside the box");
        if (io.CursorClient() is { } c && InsideRect(c, away))
        {
            var y = Math.Clamp(away.Y + away.H / 2, 0, client.H - 1);
            var x = Math.Clamp(away.X + away.W + gap, 0, client.W - 1);
            if (InsideRect((x, y), away)) x = Math.Clamp(away.X - gap, 0, client.W - 1);
            SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0, x, y, 0, 0));
            await io.Delay(StepTiming.LookSettleMs, ct);
        }
    }

    private static bool InsideRect((int X, int Y) p, (int X, int Y, int W, int H) b)
        => p.X >= b.X && p.X < b.X + b.W && p.Y >= b.Y && p.Y < b.Y + b.H;

    /// <summary>Both outcomes are logged with the count, so the threshold can be tuned from real
    /// runs. A reach hold also names its baseline; a reach point has none.</summary>
    private static void LogOutline(StepContext ctx, int index, string label, bool seen, int count, int need, int? baseline = null)
    {
        var n = baseline is int b
            ? string.Create(CultureInfo.InvariantCulture, $"{count} near-white px over a baseline of {b}, needs {need}")
            : string.Create(CultureInfo.InvariantCulture, $"{count} near-white px, needs {need}");
        ctx.Log(seen
            ? string.Create(CultureInfo.InvariantCulture, $"step {index + 1} '{label}' outline seen ({n})")
            : string.Create(CultureInfo.InvariantCulture, $"step {index + 1} '{label}' no outline, skipped ({n})"));
    }

    /// <summary>A near-white measure (count and bounds) guarded like <see cref="SampleGuarded"/>.
    /// Null when the capture fails or comes back the wrong shape.</summary>
    private static NearWhiteArea? MeasureGuarded(IStepIo io, (int X, int Y, int W, int H) r, int whiteMin)
    {
        GuardForeground(io);
        return io.Capture(r.X, r.Y, r.W, r.H)?.MeasureNearWhite(r.X, r.Y, r.W, r.H, whiteMin);
    }

    /// <summary>A capture guarded like a send: another window in front covers the target, so a
    /// lost foreground is a foreground abort, never a colour change. Null when the capture fails
    /// or comes back the wrong shape.</summary>
    private static Rgb? SampleGuarded(IStepIo io, (int X, int Y, int W, int H) box)
    {
        GuardForeground(io);
        return io.Capture(box.X, box.Y, box.W, box.H)?.AverageBox(box.X, box.Y, box.W, box.H);
    }

    // ---------- movement ----------

    /// <summary>SendInput goes to whatever window is in front, so every input event re-checks the
    /// target first, and a refused send stops the run. The finally block's releases bypass this on
    /// purpose: held keys and buttons must come up whatever is in front.</summary>
    private static void SendGuarded(IStepIo io, MacroEvent e)
    {
        GuardForeground(io);
        if (!io.Send(e)) throw new InputBlockedException(sendFailed: true);
    }

    private static void GuardForeground(IStepIo io)
    {
        if (!io.TargetInForeground()) throw new InputBlockedException(sendFailed: false);
    }

    private static async Task PressAsync(IStepIo io, (int X, int Y) at, int button, HashSet<int> heldButtons, CancellationToken ct)
    {
        await JumpAsync(io, at, ct);
        await ClickAsync(io, at, button, heldButtons, ct);
    }

    /// <summary>Down, <see cref="StepTiming.PressHoldMs"/>, up, with the pointer already on the point.</summary>
    private static async Task ClickAsync(IStepIo io, (int X, int Y) at, int button, HashSet<int> heldButtons, CancellationToken ct)
    {
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, button, 0));
        heldButtons.Add(button);
        await io.Delay(StepTiming.PressHoldMs, ct);
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, at.X, at.Y, button, 0));
        heldButtons.Remove(button);
    }

    /// <summary>Jump then wiggle: Roblox drops the first click after focus unless it sees movement.</summary>
    private static async Task JumpAsync(IStepIo io, (int X, int Y) at, CancellationToken ct)
    {
        var third = StepTiming.JumpWiggleMs / 3;
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0, at.X - 3, at.Y - 2, 0, 0));
        await io.Delay(third, ct);
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0, at.X + 2, at.Y + 1, 0, 0));
        await io.Delay(third, ct);
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0, at.X, at.Y, 0, 0));
        await io.Delay(StepTiming.JumpWiggleMs - 2 * third, ct);
    }

    private const int ParkMargin = 8;

    private static bool NearBox((int X, int Y) c, (int X, int Y, int W, int H) b)
        => c.X >= b.X - ParkMargin && c.X <= b.X + b.W + ParkMargin && c.Y >= b.Y - ParkMargin && c.Y <= b.Y + b.H + ParkMargin;

    /// <summary>Hover changes a button's colour, so the pointer leaves every box before a check.
    /// Tries the four corners 30 px outside each box and takes the first spot inside the client
    /// area that is near none of the boxes.</summary>
    private static async Task ParkCursorAsync(IStepIo io, IReadOnlyList<(int X, int Y, int W, int H)> boxes, (int W, int H) client, CancellationToken ct)
    {
        if (io.CursorClient() is not { } c) return;
        if (!boxes.Any(b => NearBox(c, b))) return;
        foreach (var b in boxes)
        {
            var spots = new[]
            {
                (X: b.X + b.W + 30, Y: b.Y + b.H + 30), (X: b.X - 30, Y: b.Y + b.H + 30),
                (X: b.X + b.W + 30, Y: b.Y - 30),       (X: b.X - 30, Y: b.Y - 30),
            };
            foreach (var s in spots)
            {
                var q = (X: Math.Clamp(s.X, 0, client.W - 1), Y: Math.Clamp(s.Y, 0, client.H - 1));
                if (boxes.Any(bb => NearBox(q, bb))) continue;
                SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0, q.X, q.Y, 0, 0));
                await io.Delay(0, ct);
                return;
            }
        }
    }

    private static async Task PlayDragAsync(DragStep d, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        await io.Delay(d.DelayMs, ct);
        var from = Place(ctx, (d.StartX, d.StartY));
        var to = Place(ctx, (d.StartX + d.Dx, d.StartY + d.Dy));
        await JumpAsync(io, from, ct);
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, from.X, from.Y, d.Button, 0));
        heldButtons.Add(d.Button);
        const int slices = 10;
        for (int s = 1; s <= slices; s++)
        {
            await io.Delay(Math.Max(1, d.DurationMs / slices), ct);
            SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseMove, 0,
                from.X + (to.X - from.X) * s / slices, from.Y + (to.Y - from.Y) * s / slices, 0, 0));
        }
        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseUp, 0, to.X, to.Y, d.Button, 0));
        heldButtons.Remove(d.Button);
    }

    private static async Task PlayRelativeAsync(PointerMoveStep m, IStepIo io, CancellationToken ct)
    {
        const int slices = 10;
        int sentX = 0, sentY = 0;
        for (int s = 1; s <= slices; s++)
        {
            await io.Delay(Math.Max(1, m.DurationMs / slices), ct);
            int tx = m.Dx * s / slices, ty = m.Dy * s / slices;
            GuardForeground(io);
            if (!io.MoveRelative(tx - sentX, ty - sentY)) throw new InputBlockedException(sendFailed: true);
            (sentX, sentY) = (tx, ty);
        }
    }

    private static async Task PlayRawAsync(RawStep r, StepContext ctx, IStepIo io, HashSet<int> heldKeys, HashSet<int> heldButtons, CancellationToken ct)
    {
        long last = 0;
        foreach (var e in r.Events)
        {
            var wait = (int)Math.Clamp(e.TimestampMs - last, 0, int.MaxValue);
            if (wait > 0) await io.Delay(wait, ct);
            last = e.TimestampMs;
            var ev = e;
            if (e.Kind is MacroEventKind.MouseMove or MacroEventKind.MouseDown or MacroEventKind.MouseUp or MacroEventKind.MouseWheel)
            {
                var at = Place(ctx, (e.X, e.Y));
                ev = e with { X = at.X, Y = at.Y };
            }
            SendGuarded(io, ev);
            switch (ev.Kind)
            {
                case MacroEventKind.KeyDown: heldKeys.Add(ev.VirtualKeyCode); break;
                case MacroEventKind.KeyUp: heldKeys.Remove(ev.VirtualKeyCode); break;
                case MacroEventKind.MouseDown: heldButtons.Add(ev.MouseButton); break;
                case MacroEventKind.MouseUp: heldButtons.Remove(ev.MouseButton); break;
            }
        }
    }

    // ---------- helpers ----------

    private static ((int X, int Y) Recorded, ColorCheck? Check) Resolve(PointStep p, StepContext ctx)
    {
        var adj = ctx.Adjust(p.Id);
        var check = p.Check;
        if (check is not null && adj is not null)
            check = check with { Expect = adj.Expect ?? check.Expect, Other = adj.Other ?? check.Other };
        return (adj is null ? (p.X, p.Y) : (adj.X, adj.Y), check);
    }

    /// <summary>Where a hold presses in the actual window, its saved adjustment applied.</summary>
    private static (int X, int Y) HoldAt(HoldStep h, StepContext ctx)
        => ctx.Adjust(h.Id) is { } adj ? Place(ctx, (adj.X, adj.Y)) : Place(ctx, (h.X, h.Y));

    private static (int X, int Y) Place(StepContext ctx, (int X, int Y) recorded)
        => PointMath.Place(recorded, ctx.RecordedClient, ctx.ActualClient);
}
