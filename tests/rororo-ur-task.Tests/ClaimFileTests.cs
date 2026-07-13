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
    public void Start_IsAtomic_NeverLeavesATornFile()
    {
        using var claim = new ClaimFile(Path_);
        claim.Start(new[] { 111L });
        // A reader must always get valid JSON — written temp-then-move, never in place.
        var json = File.ReadAllText(Path_);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("ur-task", doc.GetProperty("plugin").GetString());
    }
}
