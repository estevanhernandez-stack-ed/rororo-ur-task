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
}
