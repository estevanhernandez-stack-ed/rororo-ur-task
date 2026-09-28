// src/Ipc/ClearAtMacro.cs
using System.Globalization;
using Labs626.UrTask.Macros;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// The ClearAt bridge call as a macro (ore-stop pulse spec, "The ClearAt bridge call"): one reach
/// hold per point, in order, played through the same path as a saved step macro and never saved.
/// Pure, so the refusals and the steps are tested without a pipe or a window.
/// </summary>
internal static class ClearAtMacro
{
    public const int MaxPoints = 64;

    /// <summary>The synthetic macro's id is this plus the playback id, so it can never match a
    /// saved macro or a saved point adjustment.</summary>
    public const string IdPrefix = "clearat-";

    /// <summary>
    /// StepValidator refuses a hold without a colour check, and StepRunner places that box inside
    /// the window before any input. A reach hold never reads it (beats ignore colour drift), so the
    /// synthetic one is the smallest box there is: 1x1 on the point itself, inside the window
    /// exactly when the point is.
    /// </summary>
    public static readonly HoldCheck PointCheck = new(new CheckBox(0, 0, 1, 1));

    /// <summary>Null when the call can play; otherwise one sentence naming the first problem. Every
    /// rule StepValidator would apply to the built steps is checked here first, so a bad call is
    /// refused before the ack instead of failing after it.</summary>
    public static string? Validate(ClearAtRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Target)) return "ClearAt needs a target account.";
        if (!long.TryParse(r.Target, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0)
            return $"ClearAt target '{r.Target}' is not a decimal user id.";
        if (r.Client is not { W: >= 1, H: >= 1 } client) return "ClearAt needs the client size the points were measured in.";
        var count = r.Points?.Count ?? 0;
        if (count is < 1 or > MaxPoints) return Inv($"ClearAt takes 1 to {MaxPoints} points; got {count}.");
        if (r.Outline is not { } o) return "ClearAt needs an outline box.";
        if (o.W < 1 || o.H < 1) return "ClearAt has an empty outline box.";
        if (o.W > OutlineCheck.MaxSide || o.H > OutlineCheck.MaxSide)
            return Inv($"ClearAt has an outline box larger than {OutlineCheck.MaxSide}x{OutlineCheck.MaxSide}.");
        if (o.MinCount < 1) return "ClearAt has an outline minCount below 1.";
        if (o.MinCount > o.W * o.H) return "ClearAt has an outline minCount larger than its box, so it can never pass.";
        if (o.WhiteMin is < 1 or > 255) return "ClearAt has an outline whiteMin outside 1 to 255.";
        if (r.MaxMsPerPoint is < 1) return "ClearAt has a maxMsPerPoint below 1.";

        var box = BoxFor(o);
        for (int i = 0; i < count; i++)
        {
            var p = r.Points![i];
            if (p is null) return Inv($"ClearAt point {i + 1} is empty.");
            var name = Inv($"ClearAt point {i + 1} '{LabelFor(p, i)}'");
            if (p.X < 0 || p.Y < 0 || p.X >= client.W || p.Y >= client.H)
                return Inv($"{name} at {p.X},{p.Y} is outside the {client.W}x{client.H} client.");
            if (!PointMath.InsideClient(PointMath.BoxRect((p.X, p.Y), box), (client.W, client.H)))
                return Inv($"{name} has an outline box outside the {client.W}x{client.H} client.");
        }
        return null;
    }

    /// <summary>The in-memory macro. Call only after <see cref="Validate"/> returned null.</summary>
    public static Macro Build(ClearAtRequest r, string macroId)
    {
        var o = r.Outline!;
        var reach = new OutlineCheck(BoxFor(o), o.MinCount, o.WhiteMin);
        var steps = r.Points!
            .Select((p, i) => (MacroStep)new HoldStep(
                DelayMs: 0, Id: Inv($"p{i + 1}"), Label: LabelFor(p, i), X: p.X, Y: p.Y, Button: 1,
                Check: PointCheck, MaxMs: r.MaxMsPerPoint, Reach: reach))
            .ToList();
        return new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion, Id: macroId, Name: NameFor(steps.Count),
            RecordMode: "PerWindow", RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
            InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: Array.Empty<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceClient, RecordedClientW: r.Client!.W, RecordedClientH: r.Client.H,
            AllGames: true, Steps: steps);
    }

    public static string NameFor(int points) => points == 1 ? "ClearAt (1 point)" : Inv($"ClearAt ({points} points)");

    /// <summary>The ur-task.log line when a ClearAt is accepted: every point by its label and where
    /// it was aimed, in the measured client space. The end line is the usual bridge line.</summary>
    public static string StartLine(string playbackId, Macro macro, string account)
        => $"bridge playback {playbackId} '{macro.Name}' on {account}: "
           + string.Join("; ", macro.Steps!.OfType<HoldStep>().Select(h => Inv($"'{h.Label}' at {h.X},{h.Y}")));

    private static CheckBox BoxFor(ClearAtOutline o) => new(-(o.W / 2), -(o.H / 2), o.W, o.H);

    private static string LabelFor(ClearAtPoint p, int index)
        => string.IsNullOrWhiteSpace(p.Label) ? Inv($"point {index + 1}") : p.Label;

    private static string Inv(FormattableString s) => FormattableString.Invariant(s);
}
