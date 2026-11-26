using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;
using static Labs626.UrTask.Tests.RunnerLedgerTests;

namespace Labs626.UrTask.Tests;

/// <summary>
/// RoRoRo fills in an account's game from presence, after the launch event. An account launched after
/// Ur Task connects is first announced with no game, so it starts on the unknown-game limit (18 min,
/// deadline 12). When the game arrives the account is announced again, and its limit has to follow in the
/// ledger and in the running loop, with its input history kept.
/// </summary>
public class GameLimitRefreshTests
{
    private const long Min = 60_000;
    private const string Ps99 = "Pet Simulator 99";
    private static readonly KeepAliveLimit Unknown = new(TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(12), false);

    // 42 is a stand-in place id, not a real one: the limits table is keyed by game name.
    private static AccountRegistry.AccountInfo InGame(int n, string game) => Alt(n) with { PlaceId = 42, PlaceName = game };

    /// A rig whose limits come from the game name, as the real deps' do: PS99 15/9, anything else 18/12.
    private static Rig GameAwareRig(long startMs, long runForMs, Action<Rig, long> onSleep)
        => Build(startMs, runForMs, onSleep: onSleep, tweak: d => d with
        {
            KeepAliveLimitFor = a => a.PlaceName == Ps99 ? Fifteen : Unknown,
            KeepAliveIntervalMs = a => (long)(a.PlaceName == Ps99 ? Fifteen : Unknown).Deadline.TotalMilliseconds,
        });

    [Fact]
    public async Task Re_adding_an_account_with_its_game_now_known_changes_its_limit_and_keeps_its_last_input()
    {
        long? inputBefore = null, inputAfter = null;
        KeepAliveLimit? limitBefore = null, limitAfter = null;
        var rig = GameAwareRig(startMs: 10 * Min, runForMs: 23 * Min, onSleep: (r, _) =>
        {
            if (limitAfter is not null || r.NowMs < 11 * Min) return;
            (inputBefore, limitBefore) = (r.Ledger.Get(1)!.LastInputMs, r.Ledger.Get(1)!.Limit);
            r.Runner.AddAssignment(Assignment.WithDerivedRole(InGame(1, Ps99), null));   // presence arrived
            (inputAfter, limitAfter) = (r.Ledger.Get(1)!.LastInputMs, r.Ledger.Get(1)!.Limit);
        });

        await rig.Run(Alt(1));   // launched with no game: the unknown-game limit

        Assert.Equal(Unknown, limitBefore);
        Assert.Equal(Fifteen, limitAfter);
        Assert.Equal(10 * Min + 1_000, inputBefore);          // the first Space, a second after the start
        Assert.Equal(inputBefore, inputAfter);                // history kept: only the limit moved
        // The next Space follows the new deadline: 9 minutes after the first, not 12.
        Assert.InRange(rig.Focused[1].at, 19 * Min, 19 * Min + 10_000);
    }

    [Fact]
    public async Task An_announce_with_no_game_does_not_undo_a_limit_already_learned()
    {
        var done = false;
        var rig = GameAwareRig(startMs: 10 * Min, runForMs: 21 * Min, onSleep: (r, _) =>
        {
            if (done || r.NowMs < 11 * Min) return;
            done = true;
            r.Runner.AddAssignment(Assignment.WithDerivedRole(Alt(1), null));   // announced again, game blank
        });

        await rig.Run(InGame(1, Ps99));

        Assert.True(done);
        Assert.Equal(Fifteen, rig.Ledger.Get(1)!.Limit);
        Assert.InRange(rig.Focused[1].at, 19 * Min, 19 * Min + 10_000);
    }

    // ---------- the ledger side: what PluginRuntime.TrackAccount does on every announce ----------

    private static KeepAliveLimit ByGame(AccountRegistry.AccountInfo a)
        => KeepAliveLimits.For(a.PlaceId, a.PlaceName, new UI.UserPreferences());

    [Fact]
    public void A_late_game_moves_the_ledger_limit_keeps_the_history_and_is_said_once()
    {
        long now = 10 * Min;
        var ledger = new KeepAliveLedger(() => now, () => DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(now));
        var registry = new AccountRegistry();
        var lines = new List<string>();
        var announces = 0;
        registry.AccountAdded += (_, info) =>
        {
            announces++;
            if (KeepAliveLimits.TrackAnnounced(ledger, info, ByGame) is { } line) lines.Add(line);
        };

        registry.OnLaunched(100, 1, "Bob", "acct-1");                 // the launch event: presence has no game yet
        Assert.Equal(Unknown, ledger.Get(1)!.Limit);
        ledger.StampLanded(1, InputSource.KeepAlive);                 // input at 10:00
        ledger.RecordMiss(1, new Miss(MissCause.OtherInFront, "x"));
        ledger.SetOff(1, true);
        now = 12 * Min;

        registry.OnLaunched(100, 1, "Bob", "acct-1");                 // a refresh that says nothing new
        Assert.Equal(1, announces);                                   // not an event: no log, no re-track, no UI churn
        Assert.Empty(lines);

        registry.OnLaunched(100, 1, "Bob", "acct-1", 42, Ps99);      // the refresh that carries the game
        Assert.Equal(2, announces);
        var e = ledger.Get(1)!;
        Assert.Equal(Fifteen, e.Limit);
        Assert.Equal(10 * Min, e.LastInputMs);                        // history kept
        Assert.Equal(1, e.ConsecutiveMisses);
        Assert.True(e.Off);
        Assert.Equal(10 * Min + 9 * Min, ledger.DueAtMs(1));          // due on the new deadline, from the same input
        Assert.Equal(new[] { "Bob is in Pet Simulator 99: keep-alive now uses its 15-minute limit." }, lines);

        registry.OnLaunched(100, 1, "Bob", "acct-1", 42, Ps99);      // and again: silent
        Assert.Equal(2, announces);
        Assert.Single(lines);
    }

    [Fact]
    public void An_announce_with_no_game_keeps_the_limit_the_ledger_holds()
    {
        var ledger = new KeepAliveLedger(() => 0, () => DateTimeOffset.UnixEpoch);
        Assert.Null(KeepAliveLimits.TrackAnnounced(ledger, InGame(1, Ps99), ByGame));   // first sight: nothing to say
        Assert.Equal(Fifteen, ledger.Get(1)!.Limit);

        var relaunched = new AccountRegistry.AccountInfo(22, 1, "alt1", "acct-1");       // new pid, game not known yet
        Assert.Null(KeepAliveLimits.TrackAnnounced(ledger, relaunched, ByGame));
        Assert.Equal(Fifteen, ledger.Get(1)!.Limit);
    }

    [Fact]
    public void A_different_game_moves_the_limit_again()
    {
        var ledger = new KeepAliveLedger(() => 0, () => DateTimeOffset.UnixEpoch);
        KeepAliveLimits.TrackAnnounced(ledger, InGame(1, Ps99), ByGame);

        var line = KeepAliveLimits.TrackAnnounced(ledger, InGame(1, "Grow a Garden"), ByGame);

        Assert.Equal(TimeSpan.FromMinutes(20), ledger.Get(1)!.Limit.Limit);
        Assert.Equal("alt1 is in Grow a Garden: keep-alive now uses its 20-minute limit.", line);
    }

    [Fact]
    public void The_limit_line_still_reads_when_only_a_place_id_is_known()
        => Assert.Equal(
            "alt1 changed game: keep-alive now uses the 16-minute limit for that game.",
            KeepAliveLimits.LimitChangedLine(Alt(1) with { PlaceId = 42 },
                new KeepAliveLimit(TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(10), false)));

    /// The row keeps the account info it was created with (launch time: no game). Its keep-alive switch
    /// used to re-track the account from that, which put a learned limit back to the unknown-game one.
    [Fact]
    public void The_rows_keep_alive_switch_does_not_put_a_learned_limit_back()
    {
        var ledger = new KeepAliveLedger(() => 0, () => DateTimeOffset.UnixEpoch);
        var view = new UI.KeepAliveView(ledger, new ForegroundGate(() => 0, _ => { }), () => 0, ByGame);
        var row = new UI.AssignmentRow(Alt(1), view) { Role = CadenceRole.KeepAlive };   // created at launch, no game
        KeepAliveLimits.TrackAnnounced(ledger, Alt(1), ByGame);
        KeepAliveLimits.TrackAnnounced(ledger, InGame(1, Ps99), ByGame);                 // the game arrived later

        row.KeepAliveOff = true;
        row.KeepAliveOff = false;

        Assert.Equal(Fifteen, ledger.Get(1)!.Limit);
    }

    // ---------- making the re-announce happen: the refresh rides the 30 s activity poll ----------

    [Fact]
    public void A_refresh_is_wanted_only_while_a_running_account_has_no_game_name()
    {
        Assert.False(KeepAliveLimits.AnyGameMissing([]));
        Assert.False(KeepAliveLimits.AnyGameMissing([InGame(1, Ps99), InGame(2, "Fisch")]));
        Assert.True(KeepAliveLimits.AnyGameMissing([InGame(1, Ps99), Alt(2)]));
        Assert.True(KeepAliveLimits.AnyGameMissing([Alt(1) with { PlaceId = 42 }]));   // an id alone names no game
    }

    private sealed class NoActivity : IHostActivity
    {
        public int Polls;
        public Task<IReadOnlyList<(string AccountId, DateTimeOffset LastActivity)>?> GetAsync(CancellationToken ct)
        {
            Polls++;
            return Task.FromResult<IReadOnlyList<(string, DateTimeOffset)>?>(null);
        }
        public Task MarkActiveAsync(string accountId, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_poll_asks_again_each_time_until_every_game_is_known_and_says_nothing()
    {
        var registry = new AccountRegistry();
        registry.OnLaunched(100, 1, "Bob", "acct-1");
        var ledger = new KeepAliveLedger(() => 0, () => DateTimeOffset.UnixEpoch);
        var host = new NoActivity();
        var lines = new List<string>();
        var asked = 0;
        using var cts = new CancellationTokenSource();
        // What PluginRuntime.RefreshGameIdentityAsync does, with RoRoRo answering on the third ask.
        Task Rider(CancellationToken _)
        {
            if (!KeepAliveLimits.AnyGameMissing(registry.Snapshot())) return Task.CompletedTask;
            if (++asked == 3) registry.OnLaunched(100, 1, "Bob", "acct-1", 42, Ps99);
            else registry.OnLaunched(100, 1, "Bob", "acct-1");
            return Task.CompletedTask;
        }

        var run = new HostActivityPoller(host, ledger, lines.Add).RunAsync(TimeSpan.FromMilliseconds(5), cts.Token, Rider);
        while (host.Polls < 8) await Task.Delay(2);
        cts.Cancel();
        await run;

        Assert.Equal(3, asked);                                       // stopped asking once the game was known
        Assert.Equal(Ps99, registry.ResolveByPid(100)!.PlaceName);
        Assert.Empty(lines);                                          // no line per poll
    }

    [Fact]
    public async Task A_rider_that_throws_never_stops_the_poll()
    {
        var host = new NoActivity();
        var ledger = new KeepAliveLedger(() => 0, () => DateTimeOffset.UnixEpoch);
        var lines = new List<string>();
        using var cts = new CancellationTokenSource();

        var run = new HostActivityPoller(host, ledger, lines.Add)
            .RunAsync(TimeSpan.FromMilliseconds(5), cts.Token, _ => throw new InvalidOperationException("boom"));
        while (host.Polls < 3) await Task.Delay(2);
        cts.Cancel();
        await run;

        Assert.True(host.Polls >= 3);
        Assert.Empty(lines);
    }
}
