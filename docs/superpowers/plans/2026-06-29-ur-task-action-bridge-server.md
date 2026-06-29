# Ur Task Action-Bridge Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Ur Task side of the Ur-OCR → Ur Task action bridge — a named-pipe server that accepts a `RunMacro` request from a sibling plugin and plays a stored macro on resolved alts — plus the A2 probe guide and the event day-one playbook.

**Architecture:** A `NamedPipeServerStream` listens on `\\.\pipe\626labs-ur-task` (current-user only, single connection). Each request is a length-prefixed UTF-8 JSON frame. Transport (framing + JSON + validation + dispatch) is isolated in `MacroRunnerServer` behind an `IMacroRunInvoker` seam, so the wire logic is unit-testable with a fake invoker over an in-process pipe pair — no real playback, no ROROROblox host. The real `MacroRunInvoker` wires `MacroStore` → `AccountRegistry`/`IForegroundWatcher` → `SequencePlayer`. A settings toggle gates the server on.

**Tech Stack:** C#/.NET 10 (`net10.0-windows`), `System.IO.Pipes`, `System.Text.Json`, xUnit. Companion design spec: [`docs/superpowers/specs/2026-06-29-ur-ocr-action-bridge-design.md`](../specs/2026-06-29-ur-ocr-action-bridge-design.md). Wire contract: [`docs/v0.3-ur-ocr-bridge.md`](../../v0.3-ur-ocr-bridge.md).

## Global Constraints

- **Pipe name:** `626labs-ur-task` (full path `\\.\pipe\626labs-ur-task`).
- **Security:** current-user-only pipe ACL; single concurrent connection; reject impersonation.
- **Wire format:** 4-byte **big-endian** length prefix, then UTF-8 JSON. Same shape both directions. Cap frames at 64 KB.
- **Request:** `method` must equal `"RunMacro"`; `contractVersion` must be in `1.x` (else refuse `version-mismatch`); `callerPluginId` required (else refuse `refused`).
- **`targets`:** array of strings — each a decimal Roblox user-id, or the single sentinel `"foreground"`. Null/omitted ⇒ treat as `["foreground"]`.
- **Busy semantics:** if a sequence is already running, refuse with reason `"busy"` — **do not queue**.
- **Refusal reasons (exact strings):** `busy` | `unknown-macro` | `no-targets-resolved` | `refused` | `version-mismatch`.
- **Settings:** "Accept run requests from other plugins" — default **on**.
- **Tests:** every test in this plan must pass under `-p:StandaloneTestsOnly=true` (the unit-test CI). No dependency on the ROROROblox sibling repo. Use the main plugin's own types only.
- **Namespace:** new code lives in `Labs626.UrTask.Ipc` under `src/Ipc/`.
- **No version bump in this plan.** The manifest capability string is host-coordinated and ships separately (see "Deferred").

---

### Task 1: Length-prefixed frame codec

**Files:**
- Create: `src/Ipc/FrameCodec.cs`
- Test: `tests/rororo-ur-task.Tests/Ipc/FrameCodecTests.cs`

**Interfaces:**
- Produces: `internal static class FrameCodec` with
  `Task WriteFrameAsync(Stream, ReadOnlyMemory<byte>, CancellationToken)` and
  `Task<byte[]?> ReadFrameAsync(Stream, CancellationToken)` (returns `null` on clean EOF before any bytes).

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~FrameCodecTests"`
Expected: FAIL — `FrameCodec` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~FrameCodecTests"`
Expected: PASS — 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Ipc/FrameCodec.cs tests/rororo-ur-task.Tests/Ipc/FrameCodecTests.cs
git commit -m "feat(ipc): length-prefixed frame codec for the action bridge"
```

---

### Task 2: Bridge contract DTOs + validation

**Files:**
- Create: `src/Ipc/BridgeContract.cs`
- Test: `tests/rororo-ur-task.Tests/Ipc/BridgeContractTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `RunMacroRequest(string ContractVersion, string Method, string MacroId, IReadOnlyList<string>? Targets, int? InterAltDelayMs, string? CallerPluginId)`
  - `RunMacroResponse(bool Ok, string? PlaybackId, bool Queued, string? Reason, string? Detail)` with statics `Accepted(string playbackId)` and `Refused(string reason, string? detail = null)`.
  - `static class BridgeContract` with `JsonSerializerOptions Json`, `const string Method = "RunMacro"`, `bool IsSupportedVersion(string? contractVersion)` (true iff it starts with `"1."`).

- [ ] **Step 1: Write the failing test**

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~BridgeContractTests"`
Expected: FAIL — `RunMacroRequest` / `BridgeContract` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Ipc/BridgeContract.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Ipc;

public sealed record RunMacroRequest(
    string ContractVersion,
    string Method,
    string MacroId,
    IReadOnlyList<string>? Targets,   // decimal user-ids, or ["foreground"]; null ⇒ foreground
    int? InterAltDelayMs,
    string? CallerPluginId);

public sealed record RunMacroResponse(
    bool Ok,
    string? PlaybackId,
    bool Queued,
    string? Reason,
    string? Detail)
{
    public static RunMacroResponse Accepted(string playbackId) => new(true, playbackId, false, null, null);
    public static RunMacroResponse Refused(string reason, string? detail = null) => new(false, null, false, reason, detail);
}

internal static class BridgeContract
{
    public const string Method = "RunMacro";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>True iff the caller's contract version is in the supported 1.x line.</summary>
    public static bool IsSupportedVersion(string? contractVersion)
        => !string.IsNullOrEmpty(contractVersion) && contractVersion.StartsWith("1.", StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~BridgeContractTests"`
Expected: PASS — all theory cases + 2 facts pass.

- [ ] **Step 5: Commit**

```bash
git add src/Ipc/BridgeContract.cs tests/rororo-ur-task.Tests/Ipc/BridgeContractTests.cs
git commit -m "feat(ipc): RunMacro request/response contract + version gate"
```

---

### Task 3: MacroRunnerServer — connection handling, validation, dispatch

**Files:**
- Create: `src/Ipc/IMacroRunInvoker.cs`
- Create: `src/Ipc/MacroRunnerServer.cs`
- Test: `tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs`

**Interfaces:**
- Consumes: `FrameCodec`, `RunMacroRequest`, `RunMacroResponse`, `BridgeContract`.
- Produces:
  - `internal interface IMacroRunInvoker { Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct); }`
  - `internal sealed class MacroRunnerServer` with ctor `(IMacroRunInvoker invoker)`, `const string PipeName = "626labs-ur-task"`, `Task HandleConnectionAsync(Stream stream, CancellationToken ct)` (one request/response — used directly by tests), and `Task RunAcceptLoopAsync(CancellationToken ct)` (creates the real `NamedPipeServerStream` accept loop).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Labs626.UrTask.Ipc;

namespace Labs626.UrTask.Tests.Ipc;

public class MacroRunnerServerTests
{
    private sealed class FakeInvoker : IMacroRunInvoker
    {
        public RunMacroResponse Next { get; set; } = RunMacroResponse.Accepted("01TEST");
        public RunMacroRequest? Seen { get; private set; }
        public Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(Next);
        }
    }

    // Drives one request through HandleConnectionAsync over an in-process named-pipe pair.
    private static async Task<RunMacroResponse> RoundTripAsync(MacroRunnerServer server, RunMacroRequest req)
    {
        var name = "626labs-ur-task-test-" + Guid.NewGuid().ToString("N");
        await using var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var cli = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        var waitConnect = srv.WaitForConnectionAsync();
        await cli.ConnectAsync(2000);
        await waitConnect;

        var serverSide = server.HandleConnectionAsync(srv, default);

        var payload = JsonSerializer.SerializeToUtf8Bytes(req, BridgeContract.Json);
        await FrameCodec.WriteFrameAsync(cli, payload, default);
        var respBytes = await FrameCodec.ReadFrameAsync(cli, default);
        await serverSide;

        return JsonSerializer.Deserialize<RunMacroResponse>(respBytes!, BridgeContract.Json)!;
    }

    private static RunMacroRequest Valid(string method = "RunMacro", string version = "1.0", string? caller = "626labs.ur-ocr")
        => new(version, method, Guid.NewGuid().ToString(), new[] { "foreground" }, null, caller);

    [Fact]
    public async Task ValidRequest_DispatchesToInvoker_AndReturnsAck()
    {
        var invoker = new FakeInvoker { Next = RunMacroResponse.Accepted("01ABC") };
        var server = new MacroRunnerServer(invoker);

        var resp = await RoundTripAsync(server, Valid());

        Assert.True(resp.Ok);
        Assert.Equal("01ABC", resp.PlaybackId);
        Assert.NotNull(invoker.Seen);
    }

    [Fact]
    public async Task WrongMethod_RefusedWithoutDispatch()
    {
        var invoker = new FakeInvoker();
        var resp = await RoundTripAsync(new MacroRunnerServer(invoker), Valid(method: "Explode"));
        Assert.False(resp.Ok);
        Assert.Equal("refused", resp.Reason);
        Assert.Null(invoker.Seen);
    }

    [Fact]
    public async Task UnsupportedVersion_RefusedVersionMismatch()
    {
        var resp = await RoundTripAsync(new MacroRunnerServer(new FakeInvoker()), Valid(version: "2.0"));
        Assert.False(resp.Ok);
        Assert.Equal("version-mismatch", resp.Reason);
    }

    [Fact]
    public async Task MissingCallerPluginId_Refused()
    {
        var resp = await RoundTripAsync(new MacroRunnerServer(new FakeInvoker()), Valid(caller: null));
        Assert.False(resp.Ok);
        Assert.Equal("refused", resp.Reason);
    }

    [Fact]
    public async Task BusyInvoker_PropagatesBusyRefusal()
    {
        var invoker = new FakeInvoker { Next = RunMacroResponse.Refused("busy", "Sequence already running.") };
        var resp = await RoundTripAsync(new MacroRunnerServer(invoker), Valid());
        Assert.False(resp.Ok);
        Assert.Equal("busy", resp.Reason);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunnerServerTests"`
Expected: FAIL — `IMacroRunInvoker` / `MacroRunnerServer` do not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Ipc/IMacroRunInvoker.cs
namespace Labs626.UrTask.Ipc;

/// <summary>
/// Seam between the bridge transport and macro playback. The server owns
/// pipes + framing + validation; the invoker owns "resolve the macro + targets
/// and play them." Split so the transport is unit-testable with a fake.
/// </summary>
internal interface IMacroRunInvoker
{
    Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct);
}
```

```csharp
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
                using var pipe = CreateServerPipe();
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunnerServerTests"`
Expected: PASS — 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Ipc/IMacroRunInvoker.cs src/Ipc/MacroRunnerServer.cs tests/rororo-ur-task.Tests/Ipc/MacroRunnerServerTests.cs
git commit -m "feat(ipc): MacroRunnerServer — framing, validation, dispatch over named pipe"
```

---

### Task 4: MacroRunInvoker — resolve macro + targets, play

**Files:**
- Create: `src/Ipc/MacroRunInvoker.cs`
- Test: `tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs`

**Interfaces:**
- Consumes: `IMacroRunInvoker`, `RunMacroRequest`, `RunMacroResponse`, `MacroStore` (`LoadAll()`), `AccountRegistry` (`Snapshot()`, `AccountInfo`), `IForegroundWatcher` (`ResolveForegroundAccount()`), `SequencePlayer` (`PlayAsync`, `IsRunning`).
- Produces: `internal sealed class MacroRunInvoker : IMacroRunInvoker`. Resolution order: busy → unknown-macro → no-targets-resolved → play.

**Note on testability:** the resolution/refusal logic is unit-tested with a fake macro source + fake target resolver injected via an `internal` ctor; real `SequencePlayer` playback (Win32 focus) is exercised by the synthetic end-to-end in Task 8, not here.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs
using Labs626.UrTask.Ipc;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests.Ipc;

public class MacroRunInvokerTests
{
    private static Macro NewMacro(string id) => new(
        SchemaVersion: 2, Id: id, Name: "recovery", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 0, Events: new List<MacroEvent>());

    private static AccountRegistry.AccountInfo Alt(long userId)
        => new(1000 + (int)userId, userId, $"alt-{userId}", $"acct-{userId}");

    // Builds an invoker with injected fakes. play returns the playbackId it was asked to use.
    private static MacroRunInvoker Build(
        IReadOnlyList<Macro> macros,
        IReadOnlyList<AccountRegistry.AccountInfo> running,
        bool busy,
        List<long>? playedUserIds = null)
        => new MacroRunInvoker(
            loadMacros: () => macros,
            snapshot: () => running,
            resolveForegroundUserId: () => running.Count > 0 ? running[0].RobloxUserId : (long?)null,
            isBusy: () => busy,
            play: (macro, targets, delay, ct) =>
            {
                playedUserIds?.AddRange(targets.Select(t => t.RobloxUserId));
                return Task.FromResult("01PLAYED");
            });

    [Fact]
    public async Task UnknownMacro_Refused()
    {
        var inv = Build(macros: Array.Empty<Macro>(), running: new[] { Alt(123) }, busy: false);
        var req = new RunMacroRequest("1.0", "RunMacro", Guid.NewGuid().ToString(), new[] { "123" }, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.Equal("unknown-macro", r.Reason);
    }

    [Fact]
    public async Task Busy_Refused()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var inv = Build(new[] { m }, new[] { Alt(123) }, busy: true);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "123" }, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.Equal("busy", r.Reason);
    }

    [Fact]
    public async Task NoResolvableTargets_Refused()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var inv = Build(new[] { m }, running: new[] { Alt(123) }, busy: false);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "999" }, null, "626labs.ur-ocr"); // 999 not running
        var r = await inv.RunAsync(req, default);
        Assert.Equal("no-targets-resolved", r.Reason);
    }

    [Fact]
    public async Task ForegroundSentinel_ResolvesToForegroundAlt_AndPlays()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var played = new List<long>();
        var inv = Build(new[] { m }, running: new[] { Alt(123) }, busy: false, playedUserIds: played);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, new[] { "foreground" }, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.True(r.Ok);
        Assert.Equal("01PLAYED", r.PlaybackId);
        Assert.Equal(new long[] { 123 }, played);
    }

    [Fact]
    public async Task NullTargets_TreatedAsForeground()
    {
        var m = NewMacro(Guid.NewGuid().ToString());
        var played = new List<long>();
        var inv = Build(new[] { m }, running: new[] { Alt(123) }, busy: false, playedUserIds: played);
        var req = new RunMacroRequest("1.0", "RunMacro", m.Id, null, null, "626labs.ur-ocr");
        var r = await inv.RunAsync(req, default);
        Assert.True(r.Ok);
        Assert.Equal(new long[] { 123 }, played);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunInvokerTests"`
Expected: FAIL — `MacroRunInvoker` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Ipc/MacroRunInvoker.cs
using System.Globalization;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Ipc;

/// <summary>
/// Resolves a <see cref="RunMacroRequest"/> against the macro library + running
/// alts and hands it to <see cref="SequencePlayer"/>. Resolution order matches
/// the contract refusal reasons: busy → unknown-macro → no-targets-resolved → play.
/// </summary>
internal sealed class MacroRunInvoker : IMacroRunInvoker
{
    public const string ForegroundSentinel = "foreground";

    private readonly Func<IReadOnlyList<Macro>> _loadMacros;
    private readonly Func<IReadOnlyList<AccountRegistry.AccountInfo>> _snapshot;
    private readonly Func<long?> _resolveForegroundUserId;
    private readonly Func<bool> _isBusy;
    private readonly Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<string>> _play;

    // Production ctor wires the real collaborators.
    public MacroRunInvoker(MacroStore store, AccountRegistry accounts, IForegroundWatcher foreground, SequencePlayer player)
        : this(
            loadMacros: () => store.LoadAll().Macros,
            snapshot: () => accounts.Snapshot().ToList(),
            resolveForegroundUserId: () => foreground.ResolveForegroundAccount()?.RobloxUserId,
            isBusy: () => player.IsRunning,
            play: async (macro, targets, delay, ct) =>
            {
                await player.PlayAsync(macro, targets, delay, ct).ConfigureAwait(false);
                return Guid.NewGuid().ToString("N"); // playback id for the ack
            })
    { }

    // Test ctor.
    internal MacroRunInvoker(
        Func<IReadOnlyList<Macro>> loadMacros,
        Func<IReadOnlyList<AccountRegistry.AccountInfo>> snapshot,
        Func<long?> resolveForegroundUserId,
        Func<bool> isBusy,
        Func<Macro, IReadOnlyList<AccountRegistry.AccountInfo>, int?, CancellationToken, Task<string>> play)
    {
        _loadMacros = loadMacros;
        _snapshot = snapshot;
        _resolveForegroundUserId = resolveForegroundUserId;
        _isBusy = isBusy;
        _play = play;
    }

    public async Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct)
    {
        if (_isBusy())
            return RunMacroResponse.Refused("busy", "A sequence is already running.");

        var macro = _loadMacros().FirstOrDefault(m => string.Equals(m.Id, request.MacroId, StringComparison.OrdinalIgnoreCase));
        if (macro is null)
            return RunMacroResponse.Refused("unknown-macro", $"No macro with id '{request.MacroId}'.");

        var targets = ResolveTargets(request.Targets);
        if (targets.Count == 0)
            return RunMacroResponse.Refused("no-targets-resolved", "None of the requested targets are running.");

        var playbackId = await _play(macro, targets, request.InterAltDelayMs, ct).ConfigureAwait(false);
        return RunMacroResponse.Accepted(playbackId);
    }

    private IReadOnlyList<AccountRegistry.AccountInfo> ResolveTargets(IReadOnlyList<string>? requested)
    {
        var running = _snapshot();

        // Null/omitted or explicit ["foreground"] ⇒ the current foreground alt.
        bool isForeground = requested is null || requested.Count == 0
            || (requested.Count == 1 && string.Equals(requested[0], ForegroundSentinel, StringComparison.OrdinalIgnoreCase));
        if (isForeground)
        {
            var fgUserId = _resolveForegroundUserId();
            var fg = fgUserId is null ? null : running.FirstOrDefault(a => a.RobloxUserId == fgUserId.Value);
            return fg is null ? Array.Empty<AccountRegistry.AccountInfo>() : new[] { fg };
        }

        // Explicit user-ids — preserve requested order, drop unresolved.
        var resolved = new List<AccountRegistry.AccountInfo>(requested.Count);
        foreach (var t in requested)
        {
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
            {
                var hit = running.FirstOrDefault(a => a.RobloxUserId == userId);
                if (hit is not null) resolved.Add(hit);
            }
        }
        return resolved;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRunInvokerTests"`
Expected: PASS — 5 passed.

> If `Macro`'s constructor parameters differ from the `NewMacro` helper above, copy the exact shape from `tests/rororo-ur-task.Tests/AssignmentRunnerTests.cs` (its `NewMacro` helper is the source of truth) before running.

- [ ] **Step 5: Commit**

```bash
git add src/Ipc/MacroRunInvoker.cs tests/rororo-ur-task.Tests/Ipc/MacroRunInvokerTests.cs
git commit -m "feat(ipc): MacroRunInvoker — resolve macro + targets, refusal ordering"
```

---

### Task 5: Settings toggle — accept run requests

**Files:**
- Modify: `src/UI/UserPreferences.cs` (add one property next to the existing bools at lines 18-20)
- Test: `tests/rororo-ur-task.Tests/UserPreferencesBridgeToggleTests.cs`

**Interfaces:**
- Produces: `UserPreferences.AcceptPluginRunRequests` (bool, default `true`).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/rororo-ur-task.Tests/UserPreferencesBridgeToggleTests.cs
using Labs626.UrTask.UI;

namespace Labs626.UrTask.Tests;

public class UserPreferencesBridgeToggleTests
{
    [Fact]
    public void AcceptPluginRunRequests_DefaultsOn()
        => Assert.True(new UserPreferences().AcceptPluginRunRequests);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~UserPreferencesBridgeToggleTests"`
Expected: FAIL — `AcceptPluginRunRequests` not defined.

- [ ] **Step 3: Write minimal implementation**

Add to `src/UI/UserPreferences.cs` immediately after line 20 (`KeyboardOnlyRecording`):

```csharp
    public bool AcceptPluginRunRequests { get; set; } = true; // default: true (sibling plugins like Ur-OCR can fire macros)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~UserPreferencesBridgeToggleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/UI/UserPreferences.cs tests/rororo-ur-task.Tests/UserPreferencesBridgeToggleTests.cs
git commit -m "feat(settings): AcceptPluginRunRequests toggle (default on)"
```

---

### Task 6: Wire the server into plugin startup

**Files:**
- Modify: `src/PluginRuntime.cs` (construct + start the server in the same place the other collaborators are built — `_runner` is created around line 61; the runtime already holds `_player` (IMacroPlayer), `_foreground` (IForegroundWatcher), and `Accounts` (AccountRegistry))
- Modify: `src/App.xaml.cs` (stop the server on shutdown if the runtime exposes a dispose/stop hook; otherwise the `CancellationTokenSource` below covers it)

**Interfaces:**
- Consumes: `MacroRunnerServer`, `MacroRunInvoker`, `MacroStore`, `UserPreferences`.
- Produces: a running accept loop when `AcceptPluginRunRequests` is true.

**No unit test** — this is process lifecycle wiring; it's validated by the synthetic end-to-end (Task 8). Keep it minimal.

- [ ] **Step 1: Add a private field + start method to `PluginRuntime`**

In `src/PluginRuntime.cs`, add fields near the other private readonly fields:

```csharp
    private readonly CancellationTokenSource _bridgeCts = new();
    private Ipc.MacroRunnerServer? _bridgeServer;
```

- [ ] **Step 2: Start the server where the runtime finishes wiring (after `_runner` is constructed, ~line 61)**

```csharp
        // Action bridge: accept RunMacro requests from sibling plugins (Ur-OCR).
        // Gated by the user preference; default on. The macro source is the same
        // on-disk library the recorder/sequence player use.
        if (UserPreferences.Load().AcceptPluginRunRequests)
        {
            var invoker = new Ipc.MacroRunInvoker(new Macros.MacroStore(), Accounts, _foreground, _sequence);
            _bridgeServer = new Ipc.MacroRunnerServer(invoker);
            _ = _bridgeServer.RunAcceptLoopAsync(_bridgeCts.Token);
        }
```

> Use the runtime's existing `SequencePlayer` instance (the field is `_sequence` per `PluginRuntime`'s sequence wiring). If the field name differs, use whatever the runtime already calls its `SequencePlayer`. Do **not** construct a second `SequencePlayer` — playback state must be shared so `busy` is accurate.

- [ ] **Step 3: Cancel the loop on shutdown**

In the runtime's existing teardown (the `HostLost` / dispose path that already calls `_runner.Abort()`), add:

```csharp
        try { _bridgeCts.Cancel(); } catch { }
```

- [ ] **Step 4: Build + run the full standalone suite**

Run: `dotnet build src/../rororo-ur-task.csproj -o "$env:TEMP/urtask-build"` then
`dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true`
Expected: build succeeds; all unit tests pass (the new IPC tests + existing suite). If a live plugin instance holds the build output, redirect with `-o` to a temp dir.

- [ ] **Step 5: Commit**

```bash
git add src/PluginRuntime.cs src/App.xaml.cs
git commit -m "feat(ipc): start the action-bridge server on plugin launch (pref-gated)"
```

---

### Task 7: A2 probe guide (ships now, zero plugin code)

**Files:**
- Create: `docs/guides/ur-ocr-a2-probe.md`

**Interfaces:** none — documentation deliverable.

- [ ] **Step 1: Write the guide**

```markdown
# A2 probe: fire Ur Task from an Ur-OCR color trigger (no bridge yet)

This works **today**, with the shipping versions of both plugins — no update
required. It validates the whole "detect an on-screen event → run my macros"
flow before the macro bridge lands.

## What it does

Ur-OCR already fires a **keybind** when a screen region matches a color. Ur Task
already plays your assignment set on **Ctrl+Shift+P**. Point one at the other.

## Setup

1. In **Ur Task**, set up your assignment table as usual (assign macros to alts,
   or leave alts on keep-alive). Confirm **Ctrl+Shift+P** plays the set.
2. In **Ur-OCR**, add a **color trigger**:
   - Pick the screen region that shows the event indicator.
   - Pick the target color + tolerance.
   - Set the **keybind** to **Ctrl+Shift+P**.
   - Set a cooldown longer than one full assignment pass so it doesn't re-fire
     mid-run.
3. Anchor your Roblox window (fixed position + size) — Ur-OCR reads absolute
   screen pixels, so a moved window breaks the region.
4. Arm the trigger. When the color appears, Ur-OCR presses Ctrl+Shift+P and Ur
   Task runs the set. Watch Ur Task's activity log to confirm.

## Limits (why the bridge is still coming)

- Fires the **whole assignment set**, not one specific macro on specific alts.
- No structured result back to Ur-OCR — you read Ur Task's activity log.

The A3 macro bridge replaces the keybind with a direct "run *this* macro on
*these* alts" call. Until then, this gets you a working event→action loop.
```

- [ ] **Step 2: Commit**

```bash
git add docs/guides/ur-ocr-a2-probe.md
git commit -m "docs(guide): A2 probe — Ur-OCR color trigger fires Ur Task via Ctrl+Shift+P"
```

---

### Task 8: Event day-one playbook + synthetic end-to-end checklist

**Files:**
- Create: `docs/guides/event-day-one-playbook.md`

**Interfaces:** none — documentation deliverable. Captures the manual validation the spec calls for (synthetic swatch test now; event-specific authoring when the event drops).

- [ ] **Step 1: Write the playbook**

```markdown
# Event day-one playbook + synthetic bridge test

## Part A — Validate the machine now (no event needed)

1. **Bridge unit tests** — `dotnet test ... -p:StandaloneTestsOnly=true` is green
   (FrameCodec / BridgeContract / MacroRunnerServer / MacroRunInvoker).
2. **Synthetic detection** — open a window filled with a known color (an image,
   or a solid-fill window). Add an Ur-OCR color trigger on that region. Confirm
   it fires when the color is shown and respects cooldown.
3. **Synthetic end-to-end** — set that trigger's action to run a harmless test
   macro (e.g. a macro that types into Notepad) on the foreground target. Show
   the swatch → confirm Ur Task plays the macro and logs a playback id.

If Part A passes, the pipeline is proven; only event-specific values remain.

## Part B — When the next event (or the current egg) is live

1. Screenshot the event/egg UI.
2. Sample the event-indicator color; pick the smallest reliable region. **Do not
   reuse old values** (e.g. K0ii's `0xFF115F` / coords) without re-checking —
   each egg/event differs.
3. Record or author the **recovery macro** against the live UI coordinates.
4. Set a cooldown that clears the recovery animation.
5. Anchor the Roblox window (fixed position + size).
6. Arm the trigger; watch Ur Task's activity log for the fire + playback.

## Notes

- Screen reading only works on a rendered window — the reactive trigger runs on
  the **active** alt; the macro's `targets` decide which alts get the action.
- Coordinates are screen-absolute in v1. Moving the window breaks both detection
  and clicks. Window-anchored coordinates are a phase-2 upgrade.
```

- [ ] **Step 2: Commit**

```bash
git add docs/guides/event-day-one-playbook.md
git commit -m "docs(guide): event day-one playbook + synthetic bridge test"
```

---

## Deferred (separate plans / coordination)

- **Ur-OCR client side** (own plan, in the `Ur-OCR` repo when cloned): widen `IKeyPress` → `IFireAction` (`KeyChordFireAction | RunMacroFireAction`), add the action discriminator + macro target to `Storage/Trigger.cs`, build the `RunMacroFireAction` named-pipe client (reuse the `PluginHost/PluginClient.cs` pattern), and add the action selector + macro picker to `UI/TriggerEditView.xaml`.
- **Explicit capability string** (host-coordinated): declare `plugins.accept-run-requests` (Ur Task) + `plugins.send-run-requests` (Ur-OCR) in both manifests **after** the ROROROblox host recognizes them on the consent sheet. Ships with a version bump, not in this plan.

## Self-Review

- **Spec coverage:** §3 contract → Tasks 1-3; §4 Ur Task server → Tasks 3-6; settings/consent toggle → Task 5 (capability deferred per §10 host dependency); §6 A2 phase → Task 7; §8 testing strategy → Task 8 + the in-process-pipe unit tests; §9 out-of-scope items not implemented (correct). Ur-OCR side (§5) + capability (§10) deferred with explicit follow-on notes.
- **Placeholders:** none — every code step carries full code; the two soft notes (Macro ctor shape in Task 4, SequencePlayer field name in Task 6) point at the exact in-repo source of truth rather than leaving a blank.
- **Type consistency:** `IMacroRunInvoker.RunAsync(RunMacroRequest, CancellationToken)` is consumed identically in Tasks 3, 4, 6; `RunMacroResponse.Accepted/Refused` used consistently; `MacroRunInvoker` ctor (real + test) matches its usage in Tasks 4 and 6; `SequencePlayer.PlayAsync(Macro, IReadOnlyList<AccountInfo>, int?, CancellationToken)` matches the verified signature.
