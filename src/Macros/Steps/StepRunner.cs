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
    (int X, int Y)? CursorClient();
    (int W, int H)? ClientSize();
    PixelBlock? Capture(int clientX, int clientY, int w, int h);
    bool TargetInForeground();
    Task Delay(int ms, CancellationToken ct);
    long NowMs { get; }
}

internal sealed record StepContext(
    string AccountName, long UserId, string MacroId,
    (int W, int H) RecordedClient, (int W, int H) ActualClient, int DisplayScale,
    Func<string, PointAdjustment?> Adjust, Action<string> Log);

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

    public static async Task<PlaybackResult> RunAsync(IReadOnlyList<MacroStep> steps, StepContext ctx, IStepIo io, CancellationToken ct)
    {
        var invalid = StepValidator.Validate(steps);
        if (invalid is not null) return PlaybackResult.Refused(invalid);

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
                        await PlayPointAsync(p, i, ctx, io, heldButtons, ct);
                        break;
                    case FirstMatchStep f:
                        await PlayFirstMatchAsync(f, i, ctx, io, heldButtons, ct);
                        break;
                    case HoldStep h:
                        await PlayHoldAsync(h, i, ctx, io, heldButtons, ct);
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
            return PlaybackResult.Completed();
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
        }
    }

    // ---------- points ----------

    private static async Task PlayPointAsync(PointStep p, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var (recorded, check) = Resolve(p, ctx);
        var at = Place(ctx, recorded);

        if (p.Reach is not null)
        {
            // StepValidator refuses a reach check together with an enabled colour check.
            await PlayReachedPointAsync(p, at, index, ctx, io, heldButtons, ct);
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
    private static async Task PlayReachedPointAsync(PointStep p, (int X, int Y) at, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var label = p.Label ?? p.Id;
        var name = $"{ctx.AccountName}: step {index + 1} '{label}'";
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        var reach = ReachFor(p.Reach, at, client, ctx, name, index)!.Value;

        await io.Delay(p.DelayMs, ct);
        await JumpAsync(io, at, ct);
        var (seen, count) = await AwaitOutlineAsync(reach, io, name, index, ct);
        LogOutline(ctx, index, label, seen, count, reach.Need);
        if (!seen) return;
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
    /// <see cref="StepTiming.ReachGraceMs"/> skips the step without pressing. During the hold, the
    /// outline gone for <see cref="StepTiming.HoldDriftPolls"/> polls running lets go: the block
    /// broke or is out of reach.</para>
    /// </summary>
    private static async Task PlayHoldAsync(HoldStep h, int index, StepContext ctx, IStepIo io, HashSet<int> heldButtons, CancellationToken ct)
    {
        var label = h.Label ?? h.Id;
        var name = $"{ctx.AccountName}: step {index + 1} '{label}'";
        var adj = ctx.Adjust(h.Id);
        var at = Place(ctx, adj is null ? (h.X, h.Y) : (adj.X, adj.Y));
        var check = h.Check!; // StepValidator refuses a hold without one
        var box = PointMath.BoxRect(at, check.Box);
        var client = io.ClientSize() ?? throw new StopException($"{name} could not see the window.", index);
        if (!PointMath.InsideClient(box, client)) throw new StopException($"{name} checks a box outside the window.", index);
        var reach = ReachFor(h.Reach, at, client, ctx, name, index);

        await io.Delay(h.DelayMs, ct);
        await JumpAsync(io, at, ct);
        if (reach is { } r0)
        {
            var (seen0, count0) = await AwaitOutlineAsync(r0, io, name, index, ct);
            LogOutline(ctx, index, label, seen0, count0, r0.Need);
            if (!seen0) return;
        }
        var start = SampleGuarded(io, box) ?? throw new StopException($"{name} could not see the window.", index);

        SendGuarded(io, new MacroEvent(0, MacroEventKind.MouseDown, 0, at.X, at.Y, h.Button, 0));
        heldButtons.Add(h.Button);
        var pressedAt = io.NowMs;
        long? limit = h.MaxMs;
        string why;
        try
        {
            int drifted = 0, gone = 0;
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
                if (reach is { } r)
                {
                    var count = CountGuarded(io, r.Rect, r.WhiteMin) ?? throw new StopException($"{name} could not see the window.", index);
                    gone = count >= r.Need ? 0 : gone + 1;
                    if (gone >= StepTiming.HoldDriftPolls)
                    {
                        why = string.Create(CultureInfo.InvariantCulture, $"outline gone ({count} near-white px, needs {r.Need})");
                        break;
                    }
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

    /// <summary>With the pointer already on the spot: count until the outline shows, for at most
    /// <see cref="StepTiming.ReachGraceMs"/>. A lost foreground aborts and a failed capture stops
    /// the step; neither ever reads as "no outline".</summary>
    private static async Task<(bool Seen, int Count)> AwaitOutlineAsync(ReachPlan r, IStepIo io, string name, int index, CancellationToken ct)
    {
        var start = io.NowMs;
        while (true)
        {
            var count = CountGuarded(io, r.Rect, r.WhiteMin) ?? throw new StopException($"{name} could not see the window.", index);
            if (count >= r.Need) return (true, count);
            if (io.NowMs - start >= StepTiming.ReachGraceMs) return (false, count);
            await io.Delay(StepTiming.PollMs, ct);
        }
    }

    /// <summary>Both outcomes are logged with the count, so the threshold can be tuned from real runs.</summary>
    private static void LogOutline(StepContext ctx, int index, string label, bool seen, int count, int need)
        => ctx.Log(seen
            ? string.Create(CultureInfo.InvariantCulture, $"step {index + 1} '{label}' outline seen ({count} near-white px, needs {need})")
            : string.Create(CultureInfo.InvariantCulture, $"step {index + 1} '{label}' no outline, skipped ({count} near-white px, needs {need})"));

    /// <summary>A near-white count guarded like <see cref="SampleGuarded"/>. Null when the capture
    /// fails or comes back the wrong shape.</summary>
    private static int? CountGuarded(IStepIo io, (int X, int Y, int W, int H) r, int whiteMin)
    {
        GuardForeground(io);
        return io.Capture(r.X, r.Y, r.W, r.H)?.CountNearWhite(r.X, r.Y, r.W, r.H, whiteMin);
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

    private static (int X, int Y) Place(StepContext ctx, (int X, int Y) recorded)
        => PointMath.Place(recorded, ctx.RecordedClient, ctx.ActualClient);
}
