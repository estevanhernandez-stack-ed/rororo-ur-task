namespace Labs626.UrTask.PluginHost;

/// <summary>Integer pixel rect (physical px) — WPF's Rect is double-typed; Win32 wants ints.</summary>
public readonly record struct RectPx(int X, int Y, int W, int H);

/// <summary>Grid result. Overlapping = cells were clamped to the minimum window size.</summary>
public sealed record GridLayout(IReadOnlyList<RectPx> Rects, bool Overlapping);

/// <summary>
/// Pure layout math for the window-arranging suite. No Win32 — callers
/// (WindowArrangeService) supply the work area and apply the rects.
/// </summary>
public static class WindowArranger
{
    /// <summary>Every window at the anchor rect — mouse-macro stacking + legacy screen macros.</summary>
    public static IReadOnlyList<RectPx> ComputeStack(RectPx anchor, int count)
        => Enumerable.Repeat(anchor, count).ToArray();

    /// <summary>
    /// Row-major grid over the work area: cols = ceil(sqrt(n)), rows = ceil(n/cols).
    /// Cells clamp to (minW, minH); when clamped, strides shrink so all windows stay
    /// inside the work area, overlapping in cascade order.
    /// </summary>
    public static GridLayout ComputeGrid(RectPx workArea, int count, int minW, int minH)
    {
        if (count <= 0) return new GridLayout(Array.Empty<RectPx>(), Overlapping: false);

        int cols = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling(count / (double)cols);

        int cellW = workArea.W / cols;
        int cellH = workArea.H / rows;
        bool overlapping = cellW < minW || cellH < minH;
        if (overlapping)
        {
            cellW = Math.Max(cellW, minW);
            cellH = Math.Max(cellH, minH);
        }

        // Stride: normally the cell size; when clamped, spread the clamped cells
        // evenly over the remaining span so every window stays on-screen.
        int strideX = cols > 1 ? (overlapping ? Math.Max(0, (workArea.W - cellW)) / (cols - 1) : cellW) : 0;
        int strideY = rows > 1 ? (overlapping ? Math.Max(0, (workArea.H - cellH)) / (rows - 1) : cellH) : 0;

        var rects = new List<RectPx>(count);
        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            rects.Add(new RectPx(workArea.X + col * strideX, workArea.Y + row * strideY, cellW, cellH));
        }
        return new GridLayout(rects, overlapping);
    }
}
