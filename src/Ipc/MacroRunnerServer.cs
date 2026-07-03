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

    /// <summary>Process exactly one request/response over an already-connected stream.</summary>
    public async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        var frame = await FrameCodec.ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (frame is null) return; // peer connected then closed

        RunMacroResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<RunMacroRequest>(frame, BridgeContract.Json);
            response = await ValidateAndDispatchAsync(request, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = RunMacroResponse.Refused("refused", "Malformed request JSON.");
        }

        var outBytes = JsonSerializer.SerializeToUtf8Bytes(response, BridgeContract.Json);
        await FrameCodec.WriteFrameAsync(stream, outBytes, ct).ConfigureAwait(false);
    }

    private async Task<RunMacroResponse> ValidateAndDispatchAsync(RunMacroRequest? request, CancellationToken ct)
    {
        if (request is null)
            return RunMacroResponse.Refused("refused", "Empty request.");
        if (!BridgeContract.IsSupportedVersion(request.ContractVersion))
            return RunMacroResponse.Refused("version-mismatch", $"Unsupported contractVersion '{request.ContractVersion}'.");
        if (!string.Equals(request.Method, BridgeContract.Method, StringComparison.Ordinal))
            return RunMacroResponse.Refused("refused", $"Unknown method '{request.Method}'.");
        if (string.IsNullOrWhiteSpace(request.CallerPluginId))
            return RunMacroResponse.Refused("refused", "Missing callerPluginId.");

        return await _invoker.RunAsync(request, ct).ConfigureAwait(false);
    }

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
