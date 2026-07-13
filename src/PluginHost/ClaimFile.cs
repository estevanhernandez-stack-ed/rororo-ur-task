using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Publishes which accounts Ur Task is actively managing, so ur-afk (the fallback
/// keep-alive) stays off them and the two plugins don't both steal foreground to tap
/// the same alt.
///
/// Fails SAFE: a stale heartbeat (we crashed) or a missing file (we're not running)
/// both mean "Ur Task isn't covering these — fallback, take over." Refreshed every
/// 20s against a 60s TTL, so one slow tick never looks like a crash. Deleted on a
/// clean stop.
///
/// Deliberately shaped like the host-brokered claim registry the family will need when
/// Ur Reset lands (plugin / heartbeat / owned) — this file is that registry's first
/// implementation.
/// </summary>
internal sealed class ClaimFile : IDisposable
{
    private const int TtlSeconds = 60;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(20);

    private readonly string _path;
    private readonly object _gate = new();
    private Timer? _heartbeat;
    private long[] _owned = [];
    // Read/written only under _gate. Starts true (pre-Start(), there's nothing to
    // tick) and is re-armed to false on every Start() — see OnHeartbeatTick.
    private bool _stopped = true;

    public ClaimFile() : this(DefaultPath) { }
    public ClaimFile(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "claims", "ur-task.json");

    /// <summary>
    /// Test seam: invoked (if set) at the very top of <see cref="OnHeartbeatTick"/>,
    /// before it tries to acquire <see cref="_gate"/>. Lets a test pin a heartbeat
    /// tick "in flight" — dispatched by the Timer but not yet holding the lock — so
    /// it can deterministically force the interleaving where <see cref="Stop"/> runs
    /// to completion first, instead of racing real thread scheduling. Never set
    /// outside tests.
    /// </summary>
    internal Action? BeforeHeartbeatTickLock;

    public void Start(IEnumerable<long> ownedUserIds)
    {
        lock (_gate)
        {
            _owned = ownedUserIds.ToArray();
            _stopped = false;
            Write();
            _heartbeat?.Dispose();
            _heartbeat = new Timer(OnHeartbeatTick, null, RefreshEvery, RefreshEvery);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _heartbeat?.Dispose();
            _heartbeat = null;
            try { File.Delete(_path); } catch { /* best effort — TTL expiry covers us */ }
        }
    }

    /// <summary>
    /// The heartbeat timer's callback (an <c>internal</c> method rather than a
    /// private lambda, so a test can invoke it directly — see
    /// <see cref="BeforeHeartbeatTickLock"/>).
    ///
    /// Checks <see cref="_stopped"/> AFTER acquiring the lock, not before: the
    /// parameterless <c>Timer.Dispose()</c> that <see cref="Stop"/> calls does NOT
    /// block for a callback already dispatched to the threadpool, so a tick can
    /// still be "in flight" — past the point the Timer fired it, not yet past the
    /// point it acquires <see cref="_gate"/> — when Stop() runs. Without this
    /// check-after-acquire, that stale tick would get the lock once Stop() releases
    /// it and call <see cref="Write"/> — resurrecting the claim file seconds after a
    /// clean stop. That is exactly the dangerous direction this class's SAFETY
    /// DIRECTION (see the class doc comment) exists to prevent: a claim that outlives
    /// real coverage makes ur-afk skip alts nobody is actually keeping alive.
    /// </summary>
    internal void OnHeartbeatTick(object? state)
    {
        BeforeHeartbeatTickLock?.Invoke();
        lock (_gate)
        {
            if (_stopped) return;
            try { Write(); } catch { }
        }
    }

    /// <summary>
    /// Test seam: invoked (if set) after the temp file is fully written but
    /// immediately BEFORE the atomic rename onto <see cref="_path"/>. Lets a test
    /// deterministically pause mid-Write() and assert the destination still holds
    /// only the complete OLD content — proving the new content is never partially
    /// visible — instead of racing real OS write-timing to try to catch a torn read
    /// (which is slow and, on a fast local NTFS volume, may not reproduce at all
    /// within any practical test budget). Never set outside tests.
    /// </summary>
    internal Action? BeforeMove;

    private void Write()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var payload = new ClaimPayload("ur-task", DateTime.UtcNow, TtlSeconds, _owned);
        var json = JsonSerializer.Serialize(payload);

        // Temp-then-move: a reader must never catch a half-written file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        BeforeMove?.Invoke();
        File.Move(tmp, _path, overwrite: true);
    }

    public void Dispose() => Stop();

    private sealed record ClaimPayload(
        [property: JsonPropertyName("plugin")] string Plugin,
        [property: JsonPropertyName("heartbeatUtc")] DateTime HeartbeatUtc,
        [property: JsonPropertyName("ttlSeconds")] int TtlSeconds,
        [property: JsonPropertyName("ownedUserIds")] long[] OwnedUserIds);
}
