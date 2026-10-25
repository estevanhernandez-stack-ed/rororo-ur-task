using Labs626.UrTask.Macros;
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

public class KeepAliveLimitsTests
{
    private static (TimeSpan, TimeSpan, bool) Tuple(KeepAliveLimit l) => (l.Limit, l.Deadline, l.Clamped);

    [Theory]
    [InlineData("🎉 [PSX] Pet Simulator 99! 🐾", 15, 9)]
    [InlineData("Pet Simulator 99", 15, 9)]
    [InlineData("Grow a Garden", 20, 14)]
    [InlineData("[UPD] Grow a Garden 🌱", 20, 14)]
    [InlineData("  grow a garden  ", 20, 14)]
    [InlineData("Fisch", 15, 9)]
    [InlineData("Some Unknown Game", 18, 12)]
    [InlineData(null, 18, 12)]
    public void Limit_and_deadline_by_game(string? name, int limitMin, int deadlineMin)
    {
        var l = KeepAliveLimits.For(null, name, new UserPreferences());
        Assert.Equal((TimeSpan.FromMinutes(limitMin), TimeSpan.FromMinutes(deadlineMin), false), Tuple(l));
    }

    [Fact]
    public void A_place_override_is_the_deadline_and_clamps_at_two_minutes()
    {
        var prefs = new UserPreferences { KeepAliveOverridesByPlaceId = { [42] = 10, [43] = 1 } };
        Assert.Equal((TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(10), false), Tuple(KeepAliveLimits.For(42, "x", prefs)));
        Assert.Equal((TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(2), true), Tuple(KeepAliveLimits.For(43, "x", prefs)));
    }

    [Fact]
    public void PlaceId_with_no_matching_override_falls_through_to_the_name_table()
    {
        var l = KeepAliveLimits.For(12345L, "Fisch", new UserPreferences());
        Assert.Equal((TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), false), Tuple(l));
    }

    [Fact]
    public void Normalize_drops_emoji_brackets_and_punctuation()
        => Assert.Equal("pet simulator 99", KeepAliveLimits.NormalizeGameName("🎉 [PSX] Pet Simulator 99! 🐾"));
}
