using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class WindowSpaceMathTests
{
    [Fact]
    public void ToClient_SubtractsOrigin()
    {
        Assert.Equal((50, 60), WindowSpaceMath.ToClient((150, 260), (100, 200)));
    }

    [Fact]
    public void ToScreen_AddsOrigin()
    {
        Assert.Equal((150, 260), WindowSpaceMath.ToScreen((50, 60), (100, 200)));
    }

    [Fact]
    public void RoundTrip_IsIdentity_IncludingNegativeClientCoords()
    {
        // A click left of the client area records negative — faithful replay contract.
        var origin = (300, 400);
        var screen = (250, 380);
        var client = WindowSpaceMath.ToClient(screen, origin);
        Assert.Equal((-50, -20), client);
        Assert.Equal(screen, WindowSpaceMath.ToScreen(client, origin));
    }

    [Fact]
    public void OuterSizeForClient_AppliesClientDeltaToOuter()
    {
        // Outer 830x680 wraps client 816x638 (chrome 14x42). Target client 900x700
        // ⇒ outer must become 914x742.
        Assert.Equal((914, 742),
            WindowSpaceMath.OuterSizeForClient((830, 680), (816, 638), (900, 700)));
    }

    [Fact]
    public void ClampToWorkArea_WindowLowOnScreen_ClampsYUpToWorkBottom_XUnchanged()
    {
        // 1920x1080 screen minus a 40px taskbar ⇒ work area bottom = 1040.
        var work = (X: 0, Y: 0, W: 1920, H: 1040);
        var rect = (X: 100, Y: 900, W: 800, H: 600); // bottom would be 1500 — spills past the taskbar

        var (x, y, fits) = WindowSpaceMath.ClampToWorkArea(rect, work);

        Assert.True(fits);
        Assert.Equal(100, x);
        Assert.Equal(440, y); // work.Y + work.H - rect.H == 0 + 1040 - 600
        Assert.Equal(work.Y + work.H, y + rect.H); // bottom lands exactly on work-area bottom
    }

    [Fact]
    public void ClampToWorkArea_WindowLargerThanWorkArea_DoesNotFit_PositionUnchanged()
    {
        var work = (X: 0, Y: 0, W: 1920, H: 1040);
        var rect = (X: 0, Y: 0, W: 2000, H: 600); // wider than the work area

        var (x, y, fits) = WindowSpaceMath.ClampToWorkArea(rect, work);

        Assert.False(fits);
        Assert.Equal(rect.X, x);
        Assert.Equal(rect.Y, y);
    }

    [Fact]
    public void ClampToWorkArea_WindowAlreadyInside_Unchanged()
    {
        var work = (X: 0, Y: 0, W: 1920, H: 1040);
        var rect = (X: 100, Y: 100, W: 800, H: 600);

        var (x, y, fits) = WindowSpaceMath.ClampToWorkArea(rect, work);

        Assert.True(fits);
        Assert.Equal(rect.X, x);
        Assert.Equal(rect.Y, y);
    }

    [Fact]
    public void ClampToWorkArea_NegativeXLeftOfWorkArea_ClampsToWorkLeft()
    {
        var work = (X: 0, Y: 0, W: 1920, H: 1040);
        var rect = (X: -50, Y: 100, W: 800, H: 600);

        var (x, y, fits) = WindowSpaceMath.ClampToWorkArea(rect, work);

        Assert.True(fits);
        Assert.Equal(work.X, x);
        Assert.Equal(rect.Y, y); // Y was already in range — untouched
    }

    [Theory]
    // Outer rect (100,200) 816x638, client origin (108,231): a 31 px title bar above the client.
    [InlineData(508, 219, true)]   // client (400,-12): on the title bar
    [InlineData(508, 231, false)]  // the client's own top row: a block could be there
    [InlineData(508, 199, false)]  // above the window
    [InlineData(99, 219, false)]   // left of the window
    [InlineData(916, 219, false)]  // right of the window (right edge is exclusive)
    public void OnFrameAboveClient_accepts_only_the_windows_own_frame_above_its_client(int x, int y, bool expected)
    {
        Assert.Equal(expected, WindowSpaceMath.OnFrameAboveClient((x, y), (100, 200, 816, 638), (108, 231)));
    }

    [Fact]
    public void OnFrameAboveClient_refuses_a_borderless_window()
    {
        // Outer rect and client share their top edge: nothing above the client belongs to the window.
        Assert.False(WindowSpaceMath.OnFrameAboveClient((500, 188), (100, 200, 800, 600), (100, 200)));
    }
}
