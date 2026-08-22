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

    [Fact]
    public void RunMacroRequest_Repeat_RoundTrips_AndDefaultsFalse()
    {
        var withRepeat = new RunMacroRequest("1.0", "RunMacro", "m1", new[] { "123" }, null, "626labs.ur-mcp", Repeat: true);
        var json = JsonSerializer.Serialize(withRepeat, BridgeContract.Json);
        Assert.Contains("\"repeat\":true", json);
        Assert.True(JsonSerializer.Deserialize<RunMacroRequest>(json, BridgeContract.Json)!.Repeat);

        // Legacy payloads without "repeat" still deserialize, defaulting to false.
        var legacy = "{\"contractVersion\":\"1.0\",\"method\":\"RunMacro\",\"macroId\":\"m1\"}";
        Assert.False(JsonSerializer.Deserialize<RunMacroRequest>(legacy, BridgeContract.Json)!.Repeat);
    }

    [Fact]
    public void Envelope_ExtractsMethod_FromAnyRequest()
    {
        var json = "{\"contractVersion\":\"1.0\",\"method\":\"ListMacros\"}";
        var env = JsonSerializer.Deserialize<RequestEnvelope>(json, BridgeContract.Json)!;
        Assert.Equal("ListMacros", env.Method);
        Assert.Equal("1.0", env.ContractVersion);
    }

    [Fact]
    public void ListMacrosResponse_RoundTrips_CamelCase()
    {
        var resp = ListMacrosResponse.Success(new[] { new MacroSummary("id-1", "Farm") });
        var json = JsonSerializer.Serialize(resp, BridgeContract.Json);
        Assert.Contains("\"macros\":[{\"id\":\"id-1\",\"name\":\"Farm\"}]", json);
        Assert.True(JsonSerializer.Deserialize<ListMacrosResponse>(json, BridgeContract.Json)!.Ok);
    }

    [Fact]
    public void StopMacroRequest_RoundTrips()
    {
        var req = new StopMacroRequest("1.0", "StopMacro", "pb-1", null, "626labs.ur-mcp");
        var json = JsonSerializer.Serialize(req, BridgeContract.Json);
        var back = JsonSerializer.Deserialize<StopMacroRequest>(json, BridgeContract.Json)!;
        Assert.Equal("pb-1", back.PlaybackId);
        Assert.Equal("StopMacro", back.Method);
    }
}
