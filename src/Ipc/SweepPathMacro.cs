// src/Ipc/SweepPathMacro.cs
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// The SweepPath bridge call as a macro (ore-stop sweep spec, "The Ur Task side"): one
/// <see cref="SweepStep"/>, played through the same path as a saved step macro and never saved.
/// Pure, so the refusals and the step are tested without a pipe or a window.
/// </summary>
internal static class SweepPathMacro
{
    /// <summary>Four rings around the character are 80 points; the near-side rows and the detours
    /// round the centre add some. Ur OCR caps its path at the same number.</summary>
    public const int MaxPoints = 256;

    /// <summary>The block size range: a block is about 22 px on the open surface and 170 px down a
    /// one-block shaft; 240 is the largest outline box Ur Task takes.</summary>
    public const int MinStep = 8;
    public const int MaxStep = OutlineCheck.MaxSide;

    /// <summary>The main breaks a bottom-layer block in about 0.3 s; the default dwell is 400 ms.</summary>
    public const int MinDwellMs = 50;
    public const int MaxDwellMs = 5000;

    /// <summary>The synthetic macro's id is this plus the playback id, so it can never match a saved
    /// macro, a saved point adjustment, or ClearAt's shared-baseline prefix.</summary>
    public const string IdPrefix = "sweep-";

    /// <summary>Null when the call can play; otherwise one sentence naming the first problem, before
    /// the ack. Every hop must be a whole number of steps (the lattice anchored at the start) and go
    /// somewhere, and the path must end on its start block, where the button comes up.</summary>
    public static string? Validate(SweepPathRequest r)
    {
        if (ClearAtMacro.TargetProblem(r.Target, "SweepPath") is { } targetProblem) return targetProblem;
        if (r.Client is not { W: >= 1, H: >= 1 } client) return "SweepPath needs the client size the path was measured in.";
        var count = r.Path?.Count ?? 0;
        if (count is < 3 or > MaxPoints) return Inv($"SweepPath takes 3 to {MaxPoints} points; got {count}.");
        if (r.Step is < MinStep or > MaxStep) return Inv($"SweepPath needs a step of {MinStep} to {MaxStep} px; got {r.Step}.");
        if (r.DwellMs is < MinDwellMs or > MaxDwellMs)
            return Inv($"SweepPath needs a dwellMs of {MinDwellMs} to {MaxDwellMs}; got {r.DwellMs}.");
        if (r.Guard is not { } g) return "SweepPath needs a guard: the pixel it watches while the button is down.";
        if (ClearAtMacro.ValidateGuard(g, client, "SweepPath") is { } guardProblem) return guardProblem;

        var path = r.Path!;
        var start = path[0];
        for (int i = 0; i < count; i++)
        {
            var p = path[i];
            if (p is null) return Inv($"SweepPath point {i + 1} is empty.");
            if (p.X < 0 || p.Y < 0 || p.X >= client.W || p.Y >= client.H)
                return Inv($"SweepPath point {i + 1} at {p.X},{p.Y} is outside the {client.W}x{client.H} client.");
            if ((p.X - start.X) % r.Step != 0 || (p.Y - start.Y) % r.Step != 0)
                return Inv($"SweepPath point {i + 1} at {p.X},{p.Y} is not a whole number of {r.Step} px steps from the start at {start.X},{start.Y}.");
            if (i > 0 && path[i - 1] is { } prev && prev.X == p.X && prev.Y == p.Y)
                return Inv($"SweepPath point {i + 1} repeats point {i}; every move must go to another block.");
        }
        var last = path[^1];
        if (last.X != start.X || last.Y != start.Y)
            return Inv($"SweepPath must end on its start block at {start.X},{start.Y}, where the button comes up; it ends at {last.X},{last.Y}.");
        return null;
    }

    /// <summary>The in-memory macro. Call only after <see cref="Validate"/> returned null.</summary>
    public static Macro Build(SweepPathRequest r, string macroId)
    {
        var g = r.Guard!;
        return new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion, Id: macroId, Name: NameFor(r.Path!.Count),
            RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
            InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: Array.Empty<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceClient, RecordedClientW: r.Client!.W, RecordedClientH: r.Client.H,
            AllGames: true, Steps: new MacroStep[] { new SweepStep(0, r.Path.ToList(), r.DwellMs) })
        {
            Guard = new ScreenGuard(g.X, g.Y, g.W, g.H, g.Expect!.Value, g.Tolerance),
        };
    }

    public static string NameFor(int points) => Inv($"SweepPath ({points} points)");

    /// <summary>The ur-task.log line when a SweepPath is accepted. The end line is the usual bridge line.</summary>
    public static string StartLine(string playbackId, Macro macro, string account, SweepPathRequest r)
        => Inv($"bridge playback {playbackId} '{macro.Name}' on {account}: from {r.Path![0].X},{r.Path[0].Y}, {r.Step} px steps, {r.DwellMs} ms a point");

    /// <summary>The guard it watches. Call only after <see cref="Validate"/> returned null.</summary>
    public static string GuardLine(SweepPathRequest r)
    {
        var g = r.Guard!;
        return Inv($"SweepPath guard at ({g.X},{g.Y}), expecting {g.Expect!.Value.Hex} ±{g.Tolerance}");
    }

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);
}
