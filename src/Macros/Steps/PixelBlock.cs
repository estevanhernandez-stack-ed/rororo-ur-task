namespace Labs626.UrTask.Macros.Steps;

/// <summary>A captured rectangle of the client area. Pixels are 0xAARRGGBB, row-major.</summary>
public sealed class PixelBlock
{
    private readonly uint[] _argb;
    public int X { get; }
    public int Y { get; }
    public int W { get; }
    public int H { get; }

    public PixelBlock(int x, int y, int w, int h, uint[] argb)
    {
        if (argb is null || argb.Length != w * h) throw new ArgumentException("Pixel count does not match the block size.", nameof(argb));
        (X, Y, W, H, _argb) = (x, y, w, h, argb);
    }

    /// <summary>Average colour of a box given in client coordinates; null if it is not fully inside.</summary>
    public Rgb? AverageBox(int x, int y, int w, int h)
    {
        if (x < X || y < Y || x + w > X + W || y + h > Y + H || w <= 0 || h <= 0) return null;
        long r = 0, g = 0, b = 0;
        for (int row = y - Y; row < y - Y + h; row++)
        for (int col = x - X; col < x - X + w; col++)
        {
            var p = _argb[row * W + col];
            r += (p >> 16) & 0xFF; g += (p >> 8) & 0xFF; b += p & 0xFF;
        }
        long n = (long)w * h;
        return new Rgb((int)(r / n), (int)(g / n), (int)(b / n));
    }

    /// <summary>How many pixels of a box (client coordinates) have every channel at or above
    /// <paramref name="min"/>; null if the box is not fully inside.</summary>
    public int? CountNearWhite(int x, int y, int w, int h, int min) => MeasureNearWhite(x, y, w, h, min)?.Count;

    /// <summary>The near-white count of a box (client coordinates) and the bounding box of those
    /// pixels, in client coordinates too. A reach hold compares the bounds across a tap: a block
    /// that broke shows the next one down, which moves the outline's edges. Null if the box is not
    /// fully inside.</summary>
    public NearWhiteArea? MeasureNearWhite(int x, int y, int w, int h, int min)
    {
        if (x < X || y < Y || x + w > X + W || y + h > Y + H || w <= 0 || h <= 0) return null;
        int n = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int row = y - Y; row < y - Y + h; row++)
        for (int col = x - X; col < x - X + w; col++)
        {
            var p = _argb[row * W + col];
            if (((p >> 16) & 0xFF) < min || ((p >> 8) & 0xFF) < min || (p & 0xFF) < min) continue;
            n++;
            minX = Math.Min(minX, X + col); maxX = Math.Max(maxX, X + col);
            minY = Math.Min(minY, Y + row); maxY = Math.Max(maxY, Y + row);
        }
        return new NearWhiteArea(n, n == 0 ? null : (minX, minY, maxX, maxY));
    }
}

/// <summary>How many near-white pixels a box holds, and the inclusive client-space bounds they
/// span (null when there are none).</summary>
public readonly record struct NearWhiteArea(int Count, (int MinX, int MinY, int MaxX, int MaxY)? Bounds);
