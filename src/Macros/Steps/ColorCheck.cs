using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros.Steps;

/// <summary>An average colour. Serialized as exactly { "r", "g", "b" } — the shape Ur OCR matches.</summary>
public readonly record struct Rgb(int R, int G, int B)
{
    public double DistanceTo(Rgb o)
    {
        var dr = R - o.R; var dg = G - o.G; var db = B - o.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Not stored: System.Text.Json writes public get-only properties, and a "hex" key
    /// would break the shared { r, g, b } shape (caught by the Ur OCR session, 2026-09-27).</summary>
    [JsonIgnore]
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The box a check averages, as an offset from its point plus a size. The same box is
/// used when sampling and when checking — Ur OCR samples the clicked pixel but checks the
/// region centre, and this design deliberately does not repeat that.
/// </summary>
public sealed record CheckBox(int OffsetX = -2, int OffsetY = -2, int W = 5, int H = 5)
{
    public const int MaxSide = 9;

    [JsonIgnore] // same reason as Rgb.Hex: keep the stored box to { offsetX, offsetY, w, h }
    public bool IsValid => W >= 1 && H >= 1 && W <= MaxSide && H <= MaxSide;
}

/// <summary>
/// What a point expects to see. <see cref="Other"/> is the colour of the other state
/// (the grey of a locked tile, the red of "Off"): greys drift 20–40 under darkening and
/// hover, so a fixed tolerance alone cannot tell green from grey.
/// <para>Hand- or agent-written JSON: <c>expect</c> is required, because <see cref="Rgb"/> is a value
/// type and a missing one would silently read as black. A file without it fails to load, with the
/// serializer's reason. A missing <c>box</c> reads as null and <c>StepValidator</c> refuses it
/// with a sentence before playback.</para>
/// </summary>
public sealed record ColorCheck(
    CheckBox Box,
    [property: JsonRequired] Rgb Expect,
    Rgb? Other = null,
    int Tolerance = ColorCheck.DefaultTolerance)
{
    /// <summary>Unproven default (Ur OCR only ever shipped single-pixel checks). Checks log
    /// their measured distance so this can be tuned from real runs.</summary>
    public const int DefaultTolerance = 15;
}

/// <summary>
/// What a hold watches: a box, and how far its average colour may move from the sample taken
/// just before the press. There is no expected colour: the hold learns it at press time, so one
/// macro works for every ore.
/// <para>Hand- or agent-written JSON: a missing <c>box</c> reads as null and <c>StepValidator</c>
/// refuses it with a sentence before playback.</para>
/// </summary>
public sealed record HoldCheck(CheckBox Box, int Tolerance = ColorCheck.DefaultTolerance);

public readonly record struct ColorVerdict(bool Matched, double Distance, double? DistanceToOther);

public static class ColorMatcher
{
    /// <summary>Matched = within tolerance of Expect AND, when Other is set, closer to Expect than to Other.</summary>
    public static ColorVerdict Evaluate(Rgb seen, ColorCheck check)
    {
        var d = seen.DistanceTo(check.Expect);
        double? dOther = check.Other is { } o ? seen.DistanceTo(o) : null;
        var matched = d <= check.Tolerance && (dOther is null || d < dOther.Value);
        return new ColorVerdict(matched, d, dOther);
    }

    /// <summary>True when the box shows the check's OTHER state: within tolerance of Other and
    /// nearer it than Expect. The single definition of "settled without matching" — first match
    /// uses it to decide whether a candidate ahead of a match can be skipped.</summary>
    public static bool ShowsOther(Rgb seen, ColorCheck check)
        => check.Other is { } o
           && seen.DistanceTo(o) <= check.Tolerance
           && seen.DistanceTo(o) < seen.DistanceTo(check.Expect);
}

/// <summary>Plain-language colour names for failure reports, with the hex for precision.</summary>
public static class ColorNamer
{
    public static string Describe(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));

        string name;
        if (s < 0.2 || d < 0.08)
        {
            name = l < 0.15 ? "black" : l > 0.85 ? "white" : "grey";
        }
        else
        {
            double h = max == r ? 60 * (((g - b) / d) % 6)
                     : max == g ? 60 * ((b - r) / d + 2)
                     : 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
            name = h < 15 || h >= 345 ? "red"
                 : h < 40 ? "orange"
                 : h < 70 ? "yellow"
                 : h < 165 ? "green"
                 : h < 195 ? "cyan"
                 : h < 255 ? "blue"
                 : h < 290 ? "purple"
                 : "pink";
            if (l < 0.3) name = "dark " + name;
        }
        return $"{name} {c.Hex}";
    }
}
