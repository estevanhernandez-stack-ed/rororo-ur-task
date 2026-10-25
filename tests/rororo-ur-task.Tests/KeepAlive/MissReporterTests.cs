using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MissReporterTests
{
    private static readonly KeepAliveLimit L = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), false);
    private DateTimeOffset _wall = new(2026, 10, 25, 14, 0, 0, TimeSpan.Zero);
    private readonly List<string> _log = new();

    private MissReporter New() => new(_log.Add, () => _wall);

    private static AccountLedgerEntry Entry(int misses) =>
        new(1, "alt1", "acct-1", L, null, null, AccountKeepAliveState.Waiting, false, misses, null);

    [Fact]
    public void Cause_texts_match_the_spec_table()
    {
        Assert.Equal("window gone", MissReporter.CauseText(new Miss(MissCause.WindowGone, null)));
        Assert.Equal("Windows refused focus", MissReporter.CauseText(new Miss(MissCause.FocusRefused, null)));
        Assert.Equal("'T' (p) was in front", MissReporter.CauseText(new Miss(MissCause.OtherInFront, "'T' (p)")));
        Assert.Equal("Windows rejected the input", MissReporter.CauseText(new Miss(MissCause.SendRejected, null)));
        Assert.Equal("held off: break-glass", MissReporter.CauseText(new Miss(MissCause.HeldOff, "break-glass")));
    }

    [Fact]
    public void A_lone_miss_is_one_line_with_its_cause()
    {
        var r = New();
        r.Missed("alt1", new Miss(MissCause.WindowGone, null), 1);
        r.FlushMinute(force: true);
        Assert.Equal(new[] { "keep-alive: missed alt1, window gone; retry in 30s" }, _log);
    }

    [Fact]
    public void Different_causes_stay_separate_lines()
    {
        var r = New();
        r.Missed("alt1", new Miss(MissCause.WindowGone, null), 1);
        r.Missed("alt2", new Miss(MissCause.FocusRefused, null), 1);
        r.FlushMinute(force: true);
        Assert.Equal(2, _log.Count);
        Assert.DoesNotContain(_log, l => l.Contains("every due account"));
    }

    [Fact]
    public void Same_cause_for_everyone_collapses_and_a_landing_in_the_group_prevents_it()
    {
        var r = New();
        r.Missed("alt1", new Miss(MissCause.FocusRefused, null), 1);
        r.Missed("alt2", new Miss(MissCause.FocusRefused, null), 1);
        r.FlushMinute(force: true);
        Assert.Equal(new[] { "keep-alive: every due account missed: Windows refused focus" }, _log);

        _log.Clear();
        r.Missed("alt1", new Miss(MissCause.FocusRefused, null), 2);
        r.Missed("alt2", new Miss(MissCause.FocusRefused, null), 2);
        r.Landed(null, "alt3");
        r.FlushMinute(force: true);
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public void A_group_closes_after_a_minute_or_when_the_same_account_misses_again()
    {
        var r = New();
        r.Missed("alt1", new Miss(MissCause.FocusRefused, null), 1);
        r.FlushMinute();                                   // not a minute old yet
        Assert.Empty(_log);
        Assert.NotNull(r.UntilFlush);
        _wall += TimeSpan.FromSeconds(61);
        r.FlushMinute();
        Assert.Single(_log);

        _log.Clear();
        r.Missed("alt1", new Miss(MissCause.FocusRefused, null), 2);
        r.Missed("alt1", new Miss(MissCause.FocusRefused, null), 3);   // retry: the earlier miss is written first
        Assert.Single(_log);
    }

    [Fact]
    public void Recovery_line_counts_misses_and_minutes()
    {
        var r = New();
        r.Landed(Entry(misses: 3), "alt1", idleMs: 10 * 60_000 + 3_000);
        Assert.Equal(new[] { "keep-alive: alt1 landed again after 3 misses (10 min since its last Space)" }, _log);

        _log.Clear();
        r.Landed(Entry(misses: 0), "alt1");
        Assert.Empty(_log);
    }

    [Fact]
    public void Expect_reset_is_said_once_per_idle_stretch_and_rearms_on_a_landing()
    {
        var r = New();
        var at = new DateTimeOffset(2026, 10, 25, 14, 17, 0, TimeSpan.Zero);
        r.ExpectReset("alt1", at, 14);
        r.ExpectReset("alt1", at, 14);
        Assert.Equal(new[] { "keep-alive: alt1 has had no input for 14 min; expect a reset or kick around 14:17" }, _log);

        r.Landed(Entry(0), "alt1");
        r.ExpectReset("alt1", at, 14);
        Assert.Equal(2, _log.Count);
    }
}
