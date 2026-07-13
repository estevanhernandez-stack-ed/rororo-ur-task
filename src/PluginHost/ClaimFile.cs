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

    public ClaimFile() : this(DefaultPath) { }
    public ClaimFile(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "claims", "ur-task.json");

    public void Start(IEnumerable<long> ownedUserIds)
    {
        lock (_gate)
        {
            _owned = ownedUserIds.ToArray();
            Write();
            _heartbeat?.Dispose();
            _heartbeat = new Timer(_ => { try { lock (_gate) Write(); } catch { } },
                                   null, RefreshEvery, RefreshEvery);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            try { File.Delete(_path); } catch { /* best effort — TTL expiry covers us */ }
        }
    }

    private void Write()
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var payload = new ClaimPayload("ur-task", DateTime.UtcNow, TtlSeconds, _owned);
        var json = JsonSerializer.Serialize(payload);

        // Temp-then-move: a reader must never catch a half-written file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    public void Dispose() => Stop();

    private sealed record ClaimPayload(
        [property: JsonPropertyName("plugin")] string Plugin,
        [property: JsonPropertyName("heartbeatUtc")] DateTime HeartbeatUtc,
        [property: JsonPropertyName("ttlSeconds")] int TtlSeconds,
        [property: JsonPropertyName("ownedUserIds")] long[] OwnedUserIds);
}
