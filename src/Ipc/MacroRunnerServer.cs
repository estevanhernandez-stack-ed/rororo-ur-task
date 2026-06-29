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

    public MacroRunnerServer(IMacroRunInvoker invoker)
        => _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));

    /// <summary>Accept connections until cancelled. One client at a time.</summary>
    public async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreateServerPipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
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

    private static NamedPipeServerStream CreateServerPipe()
    {
        // Default ACL on a named pipe created by a normal user grants access to
        // that user; the pipe is loopback-only by construction. Single instance.
        return new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
}
