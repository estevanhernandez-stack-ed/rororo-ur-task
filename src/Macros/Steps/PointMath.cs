namespace Labs626.UrTask.Macros.Steps;

/// <summary>Pure geometry for point steps. All points are client-space pixels.</summary>
public static class PointMath
{
    public const int SearchRadiusPx = 24;
    public const int SearchStepPx = 4;

    /// <summary>The client size a v4 macro asks for: the recorded size times current over
    /// recorded display scale (display-scale findings, proposal 3). Unknown scale: unchanged.</summary>
    public static (int W, int H) TargetClientSize((int W, int H) recorded, int? recordedScale, int currentScale)
        => recordedScale is int rs && rs > 0 && currentScale > 0 && rs != currentScale
            ? ((int)Math.Round(recorded.W * currentScale / (double)rs), (int)Math.Round(recorded.H * currentScale / (double)rs))
            : recorded;

    /// <summary>Where a recorded point lands in the window as it actually is. Covers both a
    /// display-scale change and any slack in the reached client size.</summary>
    public static (int X, int Y) Place((int X, int Y) recordedPoint, (int W, int H) recordedClient, (int W, int H) actualClient)
        => ((int)Math.Round(recordedPoint.X * actualClient.W / (double)recordedClient.W),
            (int)Math.Round(recordedPoint.Y * actualClient.H / (double)recordedClient.H));

    public static int ScaledRadius((int W, int H) recordedClient, (int W, int H) actualClient)
        => (int)Math.Round(SearchRadiusPx * actualClient.W / (double)recordedClient.W);

    /// <summary>Grid offsets within the radius, nearest first; (0,0) excluded because the
    /// point itself was already checked.</summary>
    public static IReadOnlyList<(int Dx, int Dy)> SearchOffsets(int radius)
    {
        var list = new List<(int Dx, int Dy)>();
        for (int dy = -radius; dy <= radius; dy += SearchStepPx)
        for (int dx = -radius; dx <= radius; dx += SearchStepPx)
        {
            if (dx == 0 && dy == 0) continue;
            if (dx * dx + dy * dy <= radius * radius) list.Add((dx, dy));
        }
        return list.OrderBy(o => o.Dx * o.Dx + o.Dy * o.Dy).ThenBy(o => o.Dy).ThenBy(o => o.Dx).ToList();
    }

    public static (int X, int Y, int W, int H) BoxRect((int X, int Y) point, CheckBox box)
        => (point.X + box.OffsetX, point.Y + box.OffsetY, box.W, box.H);

    /// <summary>A box placed at an already-placed point, with its offset and size scaled from
    /// the recorded client to the actual one, the same factors <see cref="Place"/> uses. For the
    /// block-sized reach box; the 5x5 colour boxes stay unscaled as before.</summary>
    public static (int X, int Y, int W, int H) ScaledRect((int X, int Y) point, CheckBox box, (int W, int H) recordedClient, (int W, int H) actualClient)
    {
        double sx = actualClient.W / (double)recordedClient.W, sy = actualClient.H / (double)recordedClient.H;
        return (point.X + (int)Math.Round(box.OffsetX * sx), point.Y + (int)Math.Round(box.OffsetY * sy),
                Math.Max(1, (int)Math.Round(box.W * sx)), Math.Max(1, (int)Math.Round(box.H * sy)));
    }

    /// <summary>A reach threshold scaled with the window: linear in the smaller factor, rounded
    /// down, never below 1. An outline's pixel count grows at least linearly with its size (its
    /// length does; its thickness may too), so linear is the safe side when growing.</summary>
    public static int ScaledCount(int count, (int W, int H) recordedClient, (int W, int H) actualClient)
    {
        var s = Math.Min(actualClient.W / (double)recordedClient.W, actualClient.H / (double)recordedClient.H);
        return Math.Max(1, (int)Math.Floor(count * s));
    }

    public static bool InsideClient((int X, int Y, int W, int H) r, (int W, int H) client)
        => r.X >= 0 && r.Y >= 0 && r.X + r.W <= client.W && r.Y + r.H <= client.H;

    /// <summary>Roblox's smallest outer window: 816x638 times the display scale, rounded down.
    /// Measured at 100% and 125% (docs/display-scale-findings.md).</summary>
    public static (int W, int H) RobloxMinOuter(int scalePercent)
        => (816 * scalePercent / 100, 638 * scalePercent / 100);
}
