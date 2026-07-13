using Labs626.UrTask.Macros;
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

public class KeepAliveIntervalsTests
{
    private static UserPreferences NoPrefs() => new();

    // Games that ship NO anti-AFK — we are the only thing keeping them alive.
    [Theory]
    [InlineData("Grow a Garden")]
    [InlineData("Adopt Me")]
    [InlineData("Brookhaven RP")]
    [InlineData("Bee Swarm Simulator")]
    [InlineData("Blox Fruits")]
    public void PrimaryKeeperGames_Fire_Every11Minutes(string game)
        => Assert.Equal(TimeSpan.FromMinutes(11), KeepAliveIntervals.For(null, game, NoPrefs()));

    // Games with their OWN anti-AFK (~15 min self-rejoin) — we're only a backstop,
    // so we steal focus less often.
    [Theory]
    [InlineData("Pet Simulator 99")]
    [InlineData("Fisch")]
    [InlineData("Anime Vanguards")]
    [InlineData("Blade Ball")]
    public void BackstopGames_Fire_Every17Minutes(string game)
        => Assert.Equal(TimeSpan.FromMinutes(17), KeepAliveIntervals.For(null, game, NoPrefs()));

    [Fact]
    public void UnknownGame_FallsBackTo12Minutes()
        => Assert.Equal(TimeSpan.FromMinutes(12), KeepAliveIntervals.For(null, "Some Unshipped Game", NoPrefs()));

    /// No game stamp at all (presence hasn't filled identity) must still work —
    /// the feature degrades to the safe default, it does not break.
    [Fact]
    public void NoGameStampAtAll_FallsBackTo12Minutes()
        => Assert.Equal(TimeSpan.FromMinutes(12), KeepAliveIntervals.For(null, null, NoPrefs()));

    [Fact]
    public void NameMatch_IsCaseAndWhitespaceInsensitive()
        => Assert.Equal(TimeSpan.FromMinutes(11), KeepAliveIntervals.For(null, "  grow a garden  ", NoPrefs()));

    [Fact]
    public void UserOverrideByPlaceId_BeatsTheShippedTable()
    {
        var prefs = new UserPreferences();
        prefs.KeepAliveOverridesByPlaceId[999L] = 5;
        // Even though the name says backstop (17), the explicit override wins.
        Assert.Equal(TimeSpan.FromMinutes(5), KeepAliveIntervals.For(999L, "Fisch", prefs));
    }
}
