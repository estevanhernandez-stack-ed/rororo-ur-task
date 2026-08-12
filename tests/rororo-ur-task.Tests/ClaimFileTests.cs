using System.IO;
using System.Text.Json;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class ClaimFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urtask-claim-" + Guid.NewGuid().ToString("N"));
    private string Path_ => System.IO.Path.Combine(_dir, "ur-task.json");

    public ClaimFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Start_WritesTheOwnedAccounts()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L, 222L });

        var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path_));
        Assert.Equal("ur-task", doc.GetProperty("plugin").GetString());
        Assert.Equal(60, doc.GetProperty("ttlSeconds").GetInt32());
        var owned = doc.GetProperty("ownedUserIds").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new[] { 111L, 222L }, owned);
    }

    /// Fails SAFE: if Ur Task stops cleanly, the claim goes away and ur-afk resumes
    /// covering those alts immediately.
    [Fact]
    public void Stop_DeletesTheClaim()
    {
        var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L });
        Assert.True(File.Exists(Path_));

        claim.Stop();
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L });
        Assert.False(File.Exists(Path_ + ".tmp"), "the .tmp intermediate must be renamed away, never left behind");
    }

    /// <summary>
    /// Replaces the old <c>Start_IsAtomic_NeverLeavesATornFile</c>, which asserted only
    /// that the file held valid JSON after a normal single-threaded <c>Start()</c> — a
    /// naive <c>File.WriteAllText(_path, json)</c> with NO temp-then-move would pass
    /// that exact assertion identically, so it never actually exercised the atomicity
    /// mechanism it claimed to guard.
    ///
    /// This one pins <c>Write()</c> mid-flight, immediately BEFORE the atomic rename
    /// (via the <see cref="ClaimFile.BeforeMove"/> test seam), and asserts the
    /// destination still holds the complete OLD content at that instant — proving the
    /// new content is never partially visible. A statistically-hammered concurrent
    /// reader was tried first and abandoned: against the real temp-then-move
    /// implementation on this machine's NTFS volume, even 2,000 iterations of a ~2MB
    /// payload with 4 concurrent readers never caught a torn read within ~9s (expected —
    /// File.Move is genuinely atomic), which means it ALSO couldn't be trusted to
    /// reliably go RED against a naive direct-write within any practical test budget —
    /// a slow, non-deterministic test either way. The seam makes the same property
    /// deterministic and near-instant instead. Verified RED (see below) against a
    /// temporarily-restored direct-write <c>Write()</c>, where <c>BeforeMove</c> is
    /// never invoked at all (there's no rename step to hook) and the wait for the seam
    /// times out.
    /// </summary>
    [Fact]
    public async Task Write_IsAtomic_DestinationNeverReflectsPartialContentBeforeTheRename()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L }); // establishes an initial, complete OLD file

        var seamHit = new ManualResetEventSlim(false);
        var proceed = new ManualResetEventSlim(false);
        claim.BeforeMove = () =>
        {
            seamHit.Set();
            proceed.Wait(TimeSpan.FromSeconds(5));
        };

        var startTask = Task.Run(() => claim.Start(new[] { 222L, 333L })); // the NEW content

        Assert.True(seamHit.Wait(TimeSpan.FromSeconds(5)),
            "Write() never reached the pre-rename seam — either the temp-then-move mechanism " +
            "doesn't exist (e.g. a naive direct write with no rename step to hook), or it hung. " +
            "Either way, the atomicity guarantee this test checks for isn't demonstrably in place.");

        // Paused BEFORE the atomic rename: the destination must still be exactly the
        // OLD, complete content — never a half-written NEW one.
        var midMoveOwned = ReadOwnedIds();
        Assert.Equal(new[] { 111L }, midMoveOwned);

        proceed.Set();
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        // After Start() returns, the destination is fully and only the NEW content.
        Assert.Equal(new[] { 222L, 333L }, ReadOwnedIds());
    }

    private List<long> ReadOwnedIds()
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path_));
        return doc.GetProperty("ownedUserIds").EnumerateArray().Select(e => e.GetInt64()).ToList();
    }

    /// <summary>
    /// Regression for the review's CRITICAL 1: the parameterless <c>Timer.Dispose()</c>
    /// that <see cref="ClaimFile.Stop"/> calls does not block for a callback already
    /// dispatched to the threadpool. If a heartbeat tick is "in flight" — already fired
    /// by the Timer, not yet holding the file's lock — when <c>Stop()</c> runs, the old
    /// code let that stale tick acquire the lock AFTER <c>Stop()</c> released it and call
    /// <c>Write()</c>, resurrecting the claim file seconds after a clean stop. That is
    /// the dangerous direction: a claim outliving real coverage makes ur-afk skip alts
    /// nobody is actually keeping alive.
    ///
    /// A single-threaded test can't catch this — it needs the actual ordering: tick
    /// begins BEFORE Stop(), completes AFTER Stop(). <see cref="ClaimFile.BeforeHeartbeatTickLock"/>
    /// is the internal test seam that makes that ordering deterministic instead of a
    /// timing gamble.
    /// </summary>
    [Fact]
    public async Task Stop_TickAlreadyInFlightBeforeStop_DoesNotResurrectFileAfterStop()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L });
        Assert.True(File.Exists(Path_));

        var tickReachedSeam = new ManualResetEventSlim(false);
        var releaseTick = new ManualResetEventSlim(false);
        claim.BeforeHeartbeatTickLock = () =>
        {
            tickReachedSeam.Set();
            releaseTick.Wait(TimeSpan.FromSeconds(5));
        };

        // Simulate a heartbeat tick the real Timer already dispatched to the
        // threadpool — pinned right before it tries to acquire the file's lock,
        // mirroring exactly what a parameterless Timer.Dispose() lets survive a
        // Stop() call.
        var tickTask = Task.Run(() => claim.OnHeartbeatTick(null));
        Assert.True(tickReachedSeam.Wait(TimeSpan.FromSeconds(5)), "tick never reached the seam");

        // Stop() must run to completion — acquire the lock, mark stopped, dispose
        // the timer, delete the file — WHILE the stale tick is still blocked before
        // the gate.
        claim.Stop();
        Assert.False(File.Exists(Path_), "Stop() must delete the file");

        // Now let the stale tick proceed into the lock. With the fix it observes
        // _stopped and no-ops; without the fix it resurrects the file.
        releaseTick.Set();
        await tickTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(File.Exists(Path_),
            "a heartbeat tick already in flight before Stop() must NOT resurrect the claim file after a clean stop");
    }

    /// <summary>
    /// The review explicitly calls out re-arming: Start() after a prior Stop() must
    /// still work (the _stopped flag must reset, not stick forever).
    /// </summary>
    [Fact]
    public void Start_AfterStop_ReArmsAndPublishesAgain()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L });
        claim.Stop();
        Assert.False(File.Exists(Path_));

        claim.Start(new[] { 222L });
        Assert.True(File.Exists(Path_), "Start() after a prior Stop() must re-arm and publish again");

        var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path_));
        var owned = doc.GetProperty("ownedUserIds").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new[] { 222L }, owned);
    }
}
