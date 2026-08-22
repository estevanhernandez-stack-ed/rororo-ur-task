// src/Ipc/MacroRunnerServer.cs
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// Named-pipe server for the Ur-OCR → Ur Task action bridge. Listens on
/// <c>\\.\pipe\626labs-ur-task</c>, current-user only, one connection at a time.
/// Each connection is one length-prefixed JSON <see cref="RunMacroRequest"/> in,
/// one <see cref="RunMacroResponse"/> out, then close. Validation lives here;
/// the actual playback is delegated to <see cref="IMacroRunInvoker"/>.
/// </summary>
internal sealed class MacroRunnerServer
{
    public const string PipeName = "626labs-ur-task";

    private readonly IMacroRunInvoker _invoker;
    private readonly string _pipeName;

    public MacroRunnerServer(IMacroRunInvoker invoker, string? pipeName = null)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _pipeName = pipeName ?? PipeName;
    }

    /// <summary>Accept connections until cancelled. One client at a time.</summary>
    public async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        // Leave the caller's thread before touching the pipe. Async methods run
        // synchronously until their first await — without this, a pipe-creation
        // failure below would throw before ever yielding, and the retry loop
        // would spin synchronously on the caller (the UI thread at startup),
        // hanging the app windowless with one core pegged.
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreateServerPipe();
            }
            catch (IOException ex)
            {
                // Single-instance pipe already taken — another Ur Task instance
                // owns the bridge. Serving is pointless; stop cleanly and let
                // the owning instance keep handling sibling requests.
                Debug.WriteLine($"[MacroRunnerServer] bridge pipe unavailable — not serving: {ex.Message}");
                return;
            }

            try
            {
                await using (pipe)
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // A bad/hostile peer must not kill the loop. Log and accept the next one.
                Debug.WriteLine($"[MacroRunnerServer] connection error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Process exactly one request/response over an already-connected stream. Bridge 1.x grew
    /// heterogeneous methods (ListMacros/StopMacro beside RunMacro), so the frame is peeked as a
    /// method envelope first, then deserialized per method — each branch serializes its own
    /// response type. Refusal order is unchanged: empty → version-mismatch → unknown method →
    /// missing callerPluginId → invoker.
    /// </summary>
    public async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        var frame = await FrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (frame is null) return; // peer connected then closed

        byte[] outBytes;
        try
        {
            var env = JsonSerializer.Deserialize<RequestEnvelope>(frame, BridgeContract.Json);
            outBytes = await DispatchAsync(env, frame, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            outBytes = Bytes(RunMacroResponse.Refused("refused", "Malformed request JSON."));
        }

        await FrameCodec.WriteFrameAsync(stream, outBytes, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> DispatchAsync(RequestEnvelope? env, byte[] frame, CancellationToken ct)
    {
        if (env is null)
            return Bytes(RunMacroResponse.Refused("refused", "Empty request."));
        if (!BridgeContract.IsSupportedVersion(env.ContractVersion))
            return Bytes(RunMacroResponse.Refused("version-mismatch", $"Unsupported contractVersion '{env.ContractVersion}'."));

        switch (env.Method)
        {
            case BridgeContract.MethodRunMacro:
            {
                var req = JsonSerializer.Deserialize<RunMacroRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused("refused", "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(RunMacroResponse.Refused("refused", "Missing callerPluginId."));
                return Bytes(await _invoker.RunAsync(req, ct).ConfigureAwait(false));
            }
            case BridgeContract.MethodListMacros:
                return Bytes(ListMacrosResponse.Success(_invoker.ListMacros()));
            case BridgeContract.MethodStopMacro:
            {
                var req = JsonSerializer.Deserialize<StopMacroRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused("refused", "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(StopMacroResponse.Refused("refused", "Missing callerPluginId."));
                return Bytes(_invoker.StopMacro(req));
            }
            default:
                return Bytes(RunMacroResponse.Refused("refused", $"Unknown method '{env.Method}'."));
        }
    }

    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, BridgeContract.Json);

    private NamedPipeServerStream CreateServerPipe()
    {
        // Default ACL on a named pipe created by a normal user grants access to
        // that user; the pipe is loopback-only by construction. Single instance.
        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
}
