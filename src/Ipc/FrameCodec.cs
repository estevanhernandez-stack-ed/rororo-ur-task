// src/Ipc/FrameCodec.cs
using System.Buffers.Binary;
using System.IO;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// Reads/writes the bridge wire frame: a 4-byte big-endian length prefix
/// followed by that many UTF-8 JSON bytes. Frames are capped because every
/// request is a tiny control message — a large length is a malformed or
/// hostile peer, not a real request.
/// </summary>
internal static class FrameCodec
{
    public const int MaxFrameBytes = 64 * 1024;

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException($"Frame too large: {payload.Length} > {MaxFrameBytes}.");

        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, payload.Length);
        await stream.WriteAsync(lenBuf, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (lenBuf is null) return null; // clean EOF before any bytes

        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > MaxFrameBytes)
            throw new InvalidDataException($"Bad frame length: {len}.");

        var payload = await ReadExactAsync(stream, len, ct).ConfigureAwait(false);
        if (payload is null) throw new EndOfStreamException("Truncated frame: length prefix without body.");
        return payload;
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        if (count == 0) return Array.Empty<byte>();
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) return read == 0 ? null : throw new EndOfStreamException("Truncated frame.");
            read += n;
        }
        return buf;
    }
}
