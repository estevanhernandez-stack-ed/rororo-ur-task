using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>One step of a v4 macro. <see cref="DelayMs"/> is the wait before the step; on a
/// point whose check is on, it becomes a ceiling rather than a wait (see StepRunner).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(KeyStep), "key")]
[JsonDerivedType(typeof(PointStep), "point")]
[JsonDerivedType(typeof(DragStep), "drag")]
[JsonDerivedType(typeof(WheelStep), "wheel")]
[JsonDerivedType(typeof(PointerMoveStep), "pointerMove")]
[JsonDerivedType(typeof(WaitStep), "wait")]
[JsonDerivedType(typeof(FirstMatchStep), "firstMatch")]
[JsonDerivedType(typeof(HoldStep), "hold")]
[JsonDerivedType(typeof(RawStep), "raw")]
public abstract record MacroStep(int DelayMs);

public sealed record KeyStep(int DelayMs, int VirtualKeyCode, bool Down) : MacroStep(DelayMs);

/// <summary>A click at a client-space point. Id is short and stable (not a list position) so
/// adjustments and candidates survive steps being added or reordered.
/// <para><see cref="Reach"/>: when set, the pointer moves onto the point and the step presses only
/// if the white outline shows there; otherwise it is skipped, not failed. It cannot be combined
/// with an enabled colour check (the colour check parks the pointer away, the outline needs it on).</para></summary>
public sealed record PointStep(
    int DelayMs, string Id, string? Label, int X, int Y, int Button = 1,
    ColorCheck? Check = null, bool CheckEnabled = false, OutlineCheck? Reach = null) : MacroStep(DelayMs);

/// <summary>Press a mouse button at a client-space point and keep it down while the check box
/// still shows the colour it had just before the press. Releases when the colour moves past the
/// tolerance, or at <see cref="MaxMs"/> when one is set; with no MaxMs there is no time limit
/// (ore-stop spec, decision 5). Id, adjustments and scaling work exactly as for a point.
/// <para><see cref="Reach"/>: when set, the hold presses only if the white outline shows with the
/// pointer on the spot (else the step is skipped). It then breaks one block and stops: hold for
/// <see cref="StepTiming.FirstHoldMs"/>, let go, look, and hold again, twice as long up to
/// <see cref="StepTiming.MaxHoldMs"/>, only while the same outline shows. Colour drift ends nothing
/// there, and MaxMs bounds the pressed time across holds (ore-stop pulse spec, "One block per
/// spot").</para></summary>
public sealed record HoldStep(
    int DelayMs, string Id, string? Label, int X, int Y, int Button = 1,
    HoldCheck? Check = null, int? MaxMs = null, OutlineCheck? Reach = null) : MacroStep(DelayMs);

/// <summary>Button held while the mouse moves by (Dx, Dy) from the start point.</summary>
public sealed record DragStep(int DelayMs, int Button, int StartX, int StartY, int Dx, int Dy, int DurationMs) : MacroStep(DelayMs);

public sealed record WheelStep(int DelayMs, int X, int Y, int Delta) : MacroStep(DelayMs);

/// <summary>Relative movement while Roblox holds the pointer (first person, shift-lock).</summary>
public sealed record PointerMoveStep(int DelayMs, int Dx, int Dy, int DurationMs) : MacroStep(DelayMs);

public sealed record WaitStep(int DelayMs) : MacroStep(DelayMs);

/// <summary>Stored as the spec spells it ("skip", "stopAndReport"). The store's global
/// JsonStringEnumConverter honours these member names.</summary>
public enum NoMatchAction
{
    [JsonStringEnumMemberName("skip")] Skip,
    [JsonStringEnumMemberName("stopAndReport")] StopAndReport,
    /// <summary>Skip only when every candidate shows its other state (the red dot of an Auto
    /// Mine that is already off). Anything else, such as a popup or captcha over the screen,
    /// stops and reports.</summary>
    [JsonStringEnumMemberName("skipIfOther")] SkipIfOther,
}

/// <summary>Candidates are checked in order; the first that matches is pressed.</summary>
public sealed record FirstMatchStep(
    int DelayMs, string Id, string? Label, IReadOnlyList<PointStep> Candidates,
    NoMatchAction OnNoMatch = NoMatchAction.StopAndReport) : MacroStep(DelayMs);

/// <summary>A stretch the converter could not understand, replayed as recorded. Event
/// timestamps are relative to the start of the step.</summary>
public sealed record RawStep(int DelayMs, IReadOnlyList<MacroEvent> Events, string Note) : MacroStep(DelayMs);

public static class StepTiming
{
    public const int JumpWiggleMs = 150;
    public const int PressHoldMs = 80;
    public const int PollMs = 100;
    public const int CheckGraceMs = 3000;

    /// <summary>A hold lets go only after this many polls in a row past its tolerance, so one
    /// frame of hit particles or pickaxe swing over the box does not end it early.</summary>
    public const int HoldDriftPolls = 2;

    /// <summary>How long a reach check keeps looking for the outline after the pointer lands,
    /// before it skips the step. The game draws the outline on hover, which can take a frame or
    /// two; a pass ends the wait at once, so only a skip pays it.</summary>
    public const int ReachGraceMs = 300;

    /// <summary>The first press of a hold behind a reach check. The game hides the outline while the
    /// button is down, so a reach hold presses, lets go, and looks. Surface stone breaks on one short
    /// press; a hard block (crystal, the bottom layer) needs the button held, and its progress does
    /// not carry across releases, so each press after a hit is twice as long as the one before, up
    /// to <see cref="MaxHoldMs"/>.</summary>
    public const int FirstHoldMs = 300;

    /// <summary>The longest single press of a reach hold: 300, 600, 1200, 2400, then this each time.</summary>
    public const int MaxHoldMs = 3000;

    /// <summary>After a hold's release, how long before the look starts, so the outline can come
    /// back on hover. Also the wait after the baseline park, for the outline to clear.</summary>
    public const int LookSettleMs = 150;

    /// <summary>How far any edge of the outline's bounding box may move across a hold and still be
    /// the same block. A break shows the next block down, whose outline moves and shrinks; a hit
    /// leaves the edges where they were, within a few pixels.</summary>
    public const int OutlineMoveTolerancePx = 6;

    /// <summary>Rough playing time of a step, for Macro.Duration and UI only.</summary>
    public static long EstimateMs(MacroStep s) => s.DelayMs + s switch
    {
        PointStep => JumpWiggleMs + PressHoldMs,
        FirstMatchStep => JumpWiggleMs + PressHoldMs,
        // An open hold's length is unknowable, so it counts as nothing: the estimate is a floor.
        HoldStep h => JumpWiggleMs + (h.MaxMs ?? 0),
        DragStep d => JumpWiggleMs + d.DurationMs,
        WheelStep => JumpWiggleMs,
        PointerMoveStep p => p.DurationMs,
        RawStep r => r.Events.Count == 0 ? 0 : r.Events[^1].TimestampMs,
        _ => 0,
    };
}

public static class StepValidator
{
    /// <summary>Null when the list can play; otherwise one sentence naming the first problem.
    /// Runs before any input, and null-guards what hand- or agent-written JSON can leave out
    /// (point id, check box), so a bad file ends in a sentence, never an exception.</summary>
    public static string? Validate(IReadOnlyList<MacroStep> steps)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < steps.Count; i++)
        {
            var n = i + 1;
            switch (steps[i])
            {
                case PointStep p:
                {
                    if (string.IsNullOrWhiteSpace(p.Id)) return $"Step {n} has no point id.";
                    if (!ids.Add(p.Id)) return $"Step {n} reuses point id '{p.Id}'.";
                    var name = $"Step {n} '{p.Label ?? p.Id}'";
                    if (p.CheckEnabled && p.Check is null) return $"{name} has its check on but no colour sample.";
                    if (p.Check is { } pc && BoxProblem(pc, name) is { } err) return err;
                    if (p.Reach is not null && p.CheckEnabled) return $"{name} has both a colour check and a reach check; use one.";
                    if (ReachProblem(p.Reach, name) is { } rerr) return rerr;
                    break;
                }
                case FirstMatchStep f:
                {
                    if (string.IsNullOrWhiteSpace(f.Id)) return $"Step {n} has no point id.";
                    var name = $"Step {n} '{f.Label ?? f.Id}'";
                    if (!ids.Add(f.Id)) return $"Step {n} reuses point id '{f.Id}'.";
                    if (f.Candidates is null || f.Candidates.Count == 0) return $"{name} is a first match with no candidates.";
                    foreach (var c in f.Candidates)
                    {
                        if (string.IsNullOrWhiteSpace(c.Id)) return $"{name}: a candidate has no point id.";
                        if (!ids.Add(c.Id)) return $"{name} reuses point id '{c.Id}'.";
                        if (c.Check is null) return $"{name}: candidate '{c.Label ?? c.Id}' has no colour to check.";
                        if (c.Reach is not null) return $"{name}: candidate '{c.Label ?? c.Id}' has a reach check, which a first match does not use.";
                        if (BoxProblem(c.Check, $"{name}: candidate '{c.Label ?? c.Id}'") is { } err) return err;
                        if (f.OnNoMatch == NoMatchAction.SkipIfOther && c.Check.Other is null)
                            return $"{name}: candidate '{c.Label ?? c.Id}' has no other colour, so skipIfOther can never skip.";
                    }
                    break;
                }
                case HoldStep h:
                {
                    if (string.IsNullOrWhiteSpace(h.Id)) return $"Step {n} has no point id.";
                    if (!ids.Add(h.Id)) return $"Step {n} reuses point id '{h.Id}'.";
                    var name = $"Step {n} '{h.Label ?? h.Id}'";
                    if (h.Check is null) return $"{name} is a hold with no check.";
                    if (BoxProblem(h.Check.Box, name) is { } err) return err;
                    if (h.Check.Tolerance < 1) return $"{name} has a hold tolerance below 1.";
                    if (h.MaxMs is < 1) return $"{name} has a maxMs below 1.";
                    if (ReachProblem(h.Reach, name) is { } rerr) return rerr;
                    break;
                }
            }
        }
        return null;
    }

    /// <summary>A missing box (null from JSON), an empty one, and an oversized one each get
    /// their own sentence.</summary>
    private static string? BoxProblem(ColorCheck check, string who) => BoxProblem(check.Box, who);

    private static string? BoxProblem(CheckBox? box, string who)
    {
        if (box is null) return $"{who} has a check with no box.";
        if (box.W < 1 || box.H < 1) return $"{who} has an empty check box.";
        if (box.W > CheckBox.MaxSide || box.H > CheckBox.MaxSide) return $"{who} has a check box larger than 9x9.";
        return null;
    }

    /// <summary>Null when there is no reach check or it can play. A minCount larger than the box
    /// could never pass, so every step would silently skip; that is refused too.</summary>
    private static string? ReachProblem(OutlineCheck? reach, string who)
    {
        if (reach is null) return null;
        if (reach.Box is null) return $"{who} has a reach check with no box.";
        if (reach.Box.W < 1 || reach.Box.H < 1) return $"{who} has an empty reach box.";
        if (reach.Box.W > OutlineCheck.MaxSide || reach.Box.H > OutlineCheck.MaxSide)
            return $"{who} has a reach box larger than {OutlineCheck.MaxSide}x{OutlineCheck.MaxSide}.";
        if (reach.MinCount < 1) return $"{who} has a reach minCount below 1.";
        if (reach.MinCount > reach.Box.W * reach.Box.H) return $"{who} has a reach minCount larger than its box, so it can never pass.";
        if (reach.WhiteMin is < 1 or > 255) return $"{who} has a reach whiteMin outside 1 to 255.";
        return null;
    }
}
