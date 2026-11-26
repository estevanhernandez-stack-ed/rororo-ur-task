using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

/// <summary>L3-02: a per-window recording stops when the account it is bound to closes.</summary>
public class RecordingExitWatchTests
{
    private static (AccountRegistry Accounts, RecordingExitWatch Watch, List<AccountRegistry.AccountInfo> Stops) Rig()
    {
        var accounts = new AccountRegistry();
        accounts.OnLaunched(100, 1, "Bob", "acc-bob");
        accounts.OnLaunched(200, 2, "Ann", "acc-ann");
        var stops = new List<AccountRegistry.AccountInfo>();
        return (accounts, new RecordingExitWatch(accounts, stops.Add), stops);
    }

    [Fact]
    public void Reports_the_bound_account_closing()
    {
        var (accounts, watch, stops) = Rig();
        watch.Watch(100);

        accounts.OnExited(100);

        var stopped = Assert.Single(stops);
        Assert.Equal("Bob", stopped.DisplayName);
    }

    [Fact]
    public void Says_nothing_when_another_account_closes()
    {
        var (accounts, watch, stops) = Rig();
        watch.Watch(100);

        accounts.OnExited(200);

        Assert.Empty(stops);
    }

    [Fact]
    public void Says_nothing_when_no_recording_is_watched()
    {
        var (accounts, _, stops) = Rig();

        accounts.OnExited(100);

        Assert.Empty(stops);
    }

    [Fact]
    public void Says_nothing_once_the_recording_has_ended()
    {
        var (accounts, watch, stops) = Rig();
        watch.Watch(100);
        watch.Clear();

        accounts.OnExited(100);

        Assert.Empty(stops);
    }

    [Fact]
    public void Reports_once_even_if_the_same_pid_comes_and_goes_again()
    {
        // Windows recycles pids: a new account on the old pid is not the recording's account.
        var (accounts, watch, stops) = Rig();
        watch.Watch(100);
        accounts.OnExited(100);

        accounts.OnLaunched(100, 3, "Cat", "acc-cat");
        accounts.OnExited(100);

        Assert.Single(stops);
    }

    [Fact]
    public void A_later_recording_is_watched_afresh()
    {
        var (accounts, watch, stops) = Rig();
        watch.Watch(100);
        watch.Clear();
        watch.Watch(200);

        accounts.OnExited(200);

        Assert.Equal("Ann", Assert.Single(stops).DisplayName);
    }
}
