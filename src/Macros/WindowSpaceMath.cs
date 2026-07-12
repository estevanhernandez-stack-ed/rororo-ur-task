namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure screen↔client coordinate mapping + outer-size arithmetic for
/// client-space macros. Kept free of Win32 so the math is unit-testable;
/// callers supply the client origin / rects from <c>IWindowMetrics</c>.
/// </summary>
public static class WindowSpaceMath
{
    /// <summary>Screen point → client-relative point (may be negative — faithful replay).</summary>
    public static (int X, int Y) ToClient((int X, int Y) screen, (int X, int Y) clientOrigin)
        => (screen.X - clientOrigin.X, screen.Y - clientOrigin.Y);

    /// <summary>Client-relative point → screen point.</summary>
    public static (int X, int Y) ToScreen((int X, int Y) client, (int X, int Y) clientOrigin)
        => (client.X + clientOrigin.X, client.Y + clientOrigin.Y);

    /// <summary>
    /// Outer (window-rect) size needed to make the client area hit
    /// <paramref name="targetClient"/>, given the current outer/client pair.
    /// Valid because chrome size is constant for a given window style + DPI.
    /// </summary>
    public static (int W, int H) OuterSizeForClient(
        (int W, int H) currentOuter, (int W, int H) currentClient, (int W, int H) targetClient)
        => (currentOuter.W - currentClient.W + targetClient.W,
            currentOuter.H - currentClient.H + targetClient.H);

    /// <summary>
    /// Clamp a target outer rect's POSITION so the whole window fits inside the
    /// monitor work area (screen minus taskbar). Returns the adjusted top-left
    /// and whether it fits at all — false when the window is larger than the
    /// work area in either dimension, since no position keeps it fully
    /// on-screen; the caller should refuse rather than let clicks land
    /// off-screen.
    /// </summary>
    public static (int X, int Y, bool Fits) ClampToWorkArea(
        (int X, int Y, int W, int H) rect, (int X, int Y, int W, int H) work)
    {
        if (rect.W > work.W || rect.H > work.H)
            return (rect.X, rect.Y, false);
        int maxX = work.X + work.W - rect.W;
        int maxY = work.Y + work.H - rect.H;
        int x = Math.Min(Math.Max(rect.X, work.X), maxX);
        int y = Math.Min(Math.Max(rect.Y, work.Y), maxY);
        return (x, y, true);
    }
}
