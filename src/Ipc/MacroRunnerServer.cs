// src/Ipc/MacroRunnerServer.cs
using System.Globalization;
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
    private readonly IStatusProvider? _status;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>How long one connection gets to send its request, and separately to take its
    /// reply. Every request is a tiny control message from a local peer, so a peer that needs
    /// longer has stalled, and it is holding the only pipe instance.</summary>
    internal static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromSeconds(2);
    internal TimeSpan IoTimeout { get; init; } = DefaultIoTimeout;

    /// <summary>The wait before trying again after the pipe could not be created: it doubles from
    /// the start to the cap, and starts over once a pipe has been created.</summary>
    internal static readonly TimeSpan CreateRetryStart = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan CreateRetryCap = TimeSpan.FromSeconds(30);

    /// <summary>Test seam over pipe creation; it is handed the real create.</summary>
    internal Func<Func<NamedPipeServerStream>, NamedPipeServerStream>? CreatePipe { get; set; }

    /// <param name="log">The diagnostics log. Tests leave it out, so a test run never writes to
    /// the real ur-task.log.</param>
    /// <param name="delay">The wait between pipe-creation retries; tests skip it.</param>
    public MacroRunnerServer(IMacroRunInvoker invoker, string? pipeName = null, IStatusProvider? status = null,
        Action<string>? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _log = log ?? (_ => { });
        _delay = delay ?? Task.Delay;
        _status = status;
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

        // Every line here goes to the diagnostics log. They used to go to Debug.WriteLine, which
        // is compiled out of a Release build, so a dead bridge left no trace at all.
        var retry = CreateRetryStart;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe is { } create ? create(CreateServerPipe) : CreateServerPipe();
                retry = CreateRetryStart;
            }
            catch (IOException ex)
            {
                // Single-instance pipe already taken — another Ur Task instance
                // owns the bridge. Serving is pointless; stop cleanly and let
                // the owning instance keep handling sibling requests.
                Log($"bridge pipe: another Ur Task already owns it, so this one is not serving bridge requests ({ex.Message})");
                return;
            }
            catch (Exception ex)
            {
                // Anything else (creating over a squatter with different settings is an
                // UnauthorizedAccessException) used to fault this discarded task: the bridge was
                // gone for the session. Try again instead, less often each time.
                Log(string.Create(CultureInfo.InvariantCulture,
                    $"bridge pipe: could not create it ({ex.GetType().Name}: {ex.Message}); trying again in {retry.TotalSeconds:0} s"));
                try { await _delay(retry, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                retry = retry + retry < CreateRetryCap ? retry + retry : CreateRetryCap;
                continue;
            }

            try
            {
                await using (pipe)
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await HandleConnectionAsync(pipe, ct).ConfigureAwait(false);
                }
            }
            // Only the bridge's own token ends the loop. A cancellation from anywhere else (a
            // handler, a connection's deadline) is that connection's problem, not a shutdown.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // A bad/hostile peer must not kill the loop. Log and accept the next one.
                Log($"bridge pipe: dropped a connection: {ex.Message}");
            }
        }
    }

    private void Log(string line)
    {
        try { _log(line); } catch { /* a log that throws must not end the accept loop */ }
    }

    /// <summary>
    /// Process exactly one request/response over an already-connected stream. Bridge 1.x grew
    /// heterogeneous methods (ListMacros/StopMacro/GetPlayback/ClearAt/SweepPath beside RunMacro), so the frame is peeked as a
    /// method envelope first, then deserialized per method — each branch serializes its own
    /// response type. Refusal order is unchanged: empty → version-mismatch → unknown method →
    /// missing callerPluginId → invoker.
    /// </summary>
    public async Task HandleConnectionAsync(Stream stream, CancellationToken ct)
    {
        // The pipe has one instance, served one connection at a time. A peer that connects and
        // sends nothing, or never takes its reply, would hold it for good: every other caller
        // times out and StopMacro can no longer be sent. So the read and the write each get their
        // own deadline, on a token of this connection's own (linked to the bridge's).
        byte[]? frame;
        using (var reading = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            reading.CancelAfter(IoTimeout);
            try { frame = await FrameCodec.ReadFrameAsync(stream, reading.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                    $"the peer sent no request within {IoTimeout.TotalSeconds:0.#} s"));
            }
        }
        if (frame is null) return; // peer connected then closed

        byte[] outBytes;
        try
        {
            var env = JsonSerializer.Deserialize<RequestEnvelope>(frame, BridgeContract.Json);
            outBytes = await DispatchAsync(env, frame, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            outBytes = Bytes(RunMacroResponse.Refused(Reasons.Refused, "Malformed request JSON."));
        }

        using var writing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writing.CancelAfter(IoTimeout);
        try { await FrameCodec.WriteFrameAsync(stream, outBytes, writing.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"the peer did not take its reply within {IoTimeout.TotalSeconds:0.#} s"));
        }
    }

    private async Task<byte[]> DispatchAsync(RequestEnvelope? env, byte[] frame, CancellationToken ct)
    {
        if (env is null)
            return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Empty request."));
        if (!BridgeContract.IsSupportedVersion(env.ContractVersion))
            return Bytes(RunMacroResponse.Refused(Reasons.VersionMismatch, $"Unsupported contractVersion '{env.ContractVersion}'."));

        switch (env.Method)
        {
            case BridgeContract.MethodRunMacro:
            {
                var req = JsonSerializer.Deserialize<RunMacroRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Missing callerPluginId."));
                return Bytes(await _invoker.RunAsync(req, ct).ConfigureAwait(false));
            }
            case BridgeContract.MethodListMacros:
                return Bytes(ListMacrosResponse.Success(_invoker.ListMacros()));
            case BridgeContract.MethodStopMacro:
            {
                var req = JsonSerializer.Deserialize<StopMacroRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(StopMacroResponse.Refused(Reasons.Refused, "Missing callerPluginId."));
                return Bytes(_invoker.StopMacro(req));
            }
            case BridgeContract.MethodGetPlayback:
            {
                var req = JsonSerializer.Deserialize<GetPlaybackRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(GetPlaybackResponse.Refused(Reasons.Refused, "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(GetPlaybackResponse.Refused(Reasons.Refused, "Missing callerPluginId."));
                return Bytes(_invoker.GetPlayback(req));
            }
            case BridgeContract.MethodClearAt:
            {
                var req = JsonSerializer.Deserialize<ClearAtRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Missing callerPluginId."));
                return Bytes(await _invoker.ClearAtAsync(req, ct).ConfigureAwait(false));
            }
            case BridgeContract.MethodSweepPath:
            {
                var req = JsonSerializer.Deserialize<SweepPathRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(RunMacroResponse.Refused(Reasons.Refused, "Missing callerPluginId."));
                return Bytes(await _invoker.SweepPathAsync(req, ct).ConfigureAwait(false));
            }
            case BridgeContract.MethodGetStatus:
            {
                var req = JsonSerializer.Deserialize<GetStatusRequest>(frame, BridgeContract.Json);
                if (req is null)
                    return Bytes(GetStatusResponse.Refused(Reasons.Refused, "Empty request."));
                if (string.IsNullOrWhiteSpace(req.CallerPluginId))
                    return Bytes(GetStatusResponse.Refused(Reasons.Refused, "Missing callerPluginId."));
                if (_status is null)
                    return Bytes(GetStatusResponse.Refused(Reasons.Refused, "GetStatus isn't available."));
                return Bytes(_status.GetStatus());
            }
            default:
                return Bytes(RunMacroResponse.Refused(Reasons.Refused, $"Unknown method '{env.Method}'."));
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
