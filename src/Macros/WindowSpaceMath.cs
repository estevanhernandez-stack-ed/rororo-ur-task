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
}
