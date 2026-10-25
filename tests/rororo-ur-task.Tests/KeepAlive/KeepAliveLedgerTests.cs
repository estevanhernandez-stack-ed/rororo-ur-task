using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class KeepAliveLedgerTests
{
    private static readonly KeepAliveLimit Fifteen =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(9), false);

    private long _now;
    private DateTimeOffset _wall = new(2026, 10, 25, 14, 0, 0, TimeSpan.FromHours(-5));

    private KeepAliveLedger NewLedger() => new(() => _now, () => _wall);

    private void Advance(TimeSpan by)
    {
        _now += (long)by.TotalMilliseconds;
        _wall += by;
    }

    private static AccountRegistry.AccountInfo Alt(long userId = 1, string accountId = "acc-1") =>
        new(Pid: 100 + (int)userId, RobloxUserId: userId, DisplayName: "alt" + userId, AccountId: accountId);

    [Fact]
    public void Due_is_last_input_plus_deadline()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);
        Advance(TimeSpan.FromMinutes(1));
        ledger.StampLanded(1, InputSource.KeepAlive);

        Assert.Equal(60_000 + 9 * 60_000, ledger.DueAtMs(1));
        var e = ledger.Get(1)!;
        Assert.Equal(60_000, e.LastInputMs);
        Assert.Equal(InputSource.KeepAlive, e.LastSource);
    }

    [Fact]
    public void No_input_on_either_clock_means_due_now()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);
        Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(30_000, ledger.DueAtMs(1));
        Assert.Null(ledger.Get(1)!.LastInputMs);
        Assert.Null(ledger.Get(1)!.LastSource);
    }

    [Fact]
    public void Host_activity_counts_only_when_later()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);
        Advance(TimeSpan.FromMinutes(5));
        ledger.StampLanded(1, InputSource.Macro); // wall 14:05, mono 300_000
        Advance(TimeSpan.FromMinutes(3));          // wall 14:08, mono 480_000

        var wall0 = new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.FromHours(-5));
        ledger.MergeHostActivity("acc-1", wall0.AddMinutes(3)); // 14:03, earlier
        Assert.Equal(5 * 60_000, ledger.Get(1)!.LastInputMs);
        Assert.Equal(InputSource.Macro, ledger.Get(1)!.LastSource);

        ledger.MergeHostActivity("acc-1", wall0.AddMinutes(7)); // 14:07, later
        Assert.Equal(7 * 60_000, ledger.Get(1)!.LastInputMs);
        Assert.Equal(InputSource.Host, ledger.Get(1)!.LastSource);
        Assert.Equal(7 * 60_000 + 9 * 60_000, ledger.DueAtMs(1));
    }

    [Fact]
    public void Host_activity_in_the_future_is_capped_at_now()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);
        Advance(TimeSpan.FromMinutes(2));

        ledger.MergeHostActivity("acc-1", _wall.AddMinutes(5));

        Assert.Equal(2 * 60_000, ledger.Get(1)!.LastInputMs);
        Assert.Equal(InputSource.Host, ledger.Get(1)!.LastSource);
        Assert.Equal(2 * 60_000 + 9 * 60_000, ledger.DueAtMs(1));
    }

    [Fact]
    public void Host_activity_for_an_untracked_account_is_ignored()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);

        ledger.MergeHostActivity("someone-else", _wall);

        Assert.Null(ledger.Get(1)!.LastInputMs);
        Assert.Single(ledger.Snapshot());
    }

    [Fact]
    public void Misses_count_up_and_a_landing_clears_them()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);

        Assert.Equal(1, ledger.RecordMiss(1, new Miss(MissCause.OtherInFront, "'x' (y)")));
        var second = new Miss(MissCause.FocusRefused, null);
        Assert.Equal(2, ledger.RecordMiss(1, second));
        Assert.Equal(2, ledger.Get(1)!.ConsecutiveMisses);
        Assert.Equal(second, ledger.Get(1)!.LastMiss);

        ledger.StampLanded(1, InputSource.KeepAlive);

        Assert.Equal(0, ledger.Get(1)!.ConsecutiveMisses);
        Assert.Null(ledger.Get(1)!.LastMiss);
    }

    [Fact]
    public void Off_is_kept_and_visible_in_the_snapshot()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);
        Advance(TimeSpan.FromMinutes(1));
        ledger.StampLanded(1, InputSource.KeepAlive);
        var dueBefore = ledger.DueAtMs(1);

        Assert.True(ledger.SetOff(1, true));

        var e = Assert.Single(ledger.Snapshot());
        Assert.True(e.Off);
        Assert.Equal(AccountKeepAliveState.Off, e.State);
        Assert.Equal(dueBefore, ledger.DueAtMs(1));

        Assert.True(ledger.SetOff(1, false));
        Assert.False(ledger.Get(1)!.Off);
        Assert.Equal(AccountKeepAliveState.Waiting, ledger.Get(1)!.State);
    }

    [Fact]
    public void Untrack_then_track_keeps_no_history()
    {
        var ledger = NewLedger();
        ledger.Track(Alt(), Fifteen);
        ledger.StampLanded(1, InputSource.Paste);
        ledger.RecordMiss(1, new Miss(MissCause.WindowGone, null));
        ledger.SetOff(1, true);

        ledger.Untrack(1);
        Assert.Null(ledger.Get(1));
        Assert.Empty(ledger.Snapshot());

        ledger.Track(Alt(), Fifteen);
        var e = ledger.Get(1)!;
        Assert.Null(e.LastInputMs);
        Assert.Equal(0, e.ConsecutiveMisses);
        Assert.False(e.Off);
        Assert.Equal(AccountKeepAliveState.Waiting, e.State);
    }

    [Fact]
    public void ToWall_converts_mono_through_the_paired_clocks()
    {
        var ledger = NewLedger();
        Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(_wall.AddMinutes(-4), ledger.ToWall(6 * 60_000));
    }
}
