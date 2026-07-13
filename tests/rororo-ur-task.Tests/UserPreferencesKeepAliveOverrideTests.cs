using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

/// <summary>
/// Covers the settings-surface validation carried over from Task 2's review
/// (progress.md: "user-supplied override minutes are unvalidated ... Task 8 owns
/// the settings surface — must clamp"). AssignmentRunner already clamps
/// defensively at consumption time (MinKeepAliveIntervalMs/MaxKeepAliveIntervalMs,
/// 1-60 min) — that is a second line of defense, not a license for this layer to
/// accept garbage. A 0/negative override reads as "always due" downstream (the
/// exact desktop-hijack spin loop this whole feature exists to kill); an
/// absurdly large one risks the Task.Delay((int)ms) cast.
/// </summary>
public class UserPreferencesKeepAliveOverrideTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    [InlineData(int.MinValue)]
    public void SetKeepAliveOverrideMinutes_ZeroOrNegative_ClampsToTheOneMinuteFloor(int garbage)
    {
        var prefs = new UserPreferences();

        prefs.SetKeepAliveOverrideMinutes(123L, garbage);

        Assert.Equal(UserPreferences.MinKeepAliveOverrideMinutes, prefs.KeepAliveOverridesByPlaceId[123L]);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void SetKeepAliveOverrideMinutes_TooLarge_ClampsToTheSixtyMinuteCeiling(int huge)
    {
        var prefs = new UserPreferences();

        prefs.SetKeepAliveOverrideMinutes(123L, huge);

        Assert.Equal(UserPreferences.MaxKeepAliveOverrideMinutes, prefs.KeepAliveOverridesByPlaceId[123L]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(60)]
    public void SetKeepAliveOverrideMinutes_WithinRange_StoresAsIs(int sane)
    {
        var prefs = new UserPreferences();

        prefs.SetKeepAliveOverrideMinutes(123L, sane);

        Assert.Equal(sane, prefs.KeepAliveOverridesByPlaceId[123L]);
    }

    /// <summary>
    /// Defense in depth: a hand-edited or otherwise corrupted on-disk prefs file
    /// bypasses <see cref="UserPreferences.SetKeepAliveOverrideMinutes"/> entirely
    /// — System.Text.Json deserializes straight into the dictionary. Sanitizing
    /// after load closes that hole without requiring disk I/O in the test itself
    /// (this exercises the same sanitize step <c>Load()</c> calls, directly).
    /// </summary>
    [Fact]
    public void SanitizeKeepAliveOverrides_ClampsEveryOutOfRangeEntryAlreadyInTheDictionary()
    {
        var prefs = new UserPreferences();
        prefs.KeepAliveOverridesByPlaceId[1L] = 0;             // hand-edited "always due" hijack value
        prefs.KeepAliveOverridesByPlaceId[2L] = -50;
        prefs.KeepAliveOverridesByPlaceId[3L] = 9999;          // Task.Delay((int)ms) overflow risk
        prefs.KeepAliveOverridesByPlaceId[4L] = 12;            // already sane — must survive unchanged

        prefs.SanitizeKeepAliveOverrides();

        Assert.Equal(UserPreferences.MinKeepAliveOverrideMinutes, prefs.KeepAliveOverridesByPlaceId[1L]);
        Assert.Equal(UserPreferences.MinKeepAliveOverrideMinutes, prefs.KeepAliveOverridesByPlaceId[2L]);
        Assert.Equal(UserPreferences.MaxKeepAliveOverrideMinutes, prefs.KeepAliveOverridesByPlaceId[3L]);
        Assert.Equal(12, prefs.KeepAliveOverridesByPlaceId[4L]);
    }
}
