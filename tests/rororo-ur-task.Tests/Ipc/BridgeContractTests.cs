// tests/rororo-ur-task.Tests/Ipc/BridgeContractTests.cs
using System.Text.Json;
using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests.Ipc;

public class BridgeContractTests
{
    [Fact]
    public void Request_RoundTrips_CamelCase()
    {
        var req = new RunMacroRequest("1.0", "RunMacro", "f4e5d6c7-0000-0000-0000-000000000000",
            new[] { "123", "456" }, 500, "626labs.ur-ocr");

        var json = JsonSerializer.Serialize(req, BridgeContract.Json);
        Assert.Contains("\"contractVersion\":\"1.0\"", json);
        Assert.Contains("\"callerPluginId\":\"626labs.ur-ocr\"", json);

        var back = JsonSerializer.Deserialize<RunMacroRequest>(json, BridgeContract.Json)!;
        Assert.Equal("RunMacro", back.Method);
        Assert.Equal(2, back.Targets!.Count);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.7", true)]
    [InlineData("2.0", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsSupportedVersion_AcceptsOnly1x(string? version, bool expected)
        => Assert.Equal(expected, BridgeContract.IsSupportedVersion(version));

    [Fact]
    public void Refused_SetsReasonAndClearsOk()
    {
        var r = RunMacroResponse.Refused("busy", "Sequence already running.");
        Assert.False(r.Ok);
        Assert.Equal("busy", r.Reason);
        Assert.Null(r.PlaybackId);
    }

    [Fact]
    public void Accepted_SetsOkAndPlaybackId()
    {
        var r = RunMacroResponse.Accepted("pb-001");
        Assert.True(r.Ok);
        Assert.Equal("pb-001", r.PlaybackId);
        Assert.False(r.Queued);
        Assert.Null(r.Reason);
    }
}
