// tests/rororo-ur-task.Tests/Ipc/FrameCodecTests.cs
using System.IO;
using System.Text;
using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests.Ipc;

public class FrameCodecTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsPayload()
    {
        var payload = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        using var ms = new MemoryStream();

        await FrameCodec.WriteFrameAsync(ms, payload, default);
        ms.Position = 0;
        var read = await FrameCodec.ReadFrameAsync(ms, default);

        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadFrame_OnEmptyStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        var read = await FrameCodec.ReadFrameAsync(ms, default);
        Assert.Null(read);
    }

    [Fact]
    public async Task WriteFrame_OverCap_Throws()
    {
        var tooBig = new byte[FrameCodec.MaxFrameBytes + 1];
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await FrameCodec.WriteFrameAsync(ms, tooBig, default));
    }
}
