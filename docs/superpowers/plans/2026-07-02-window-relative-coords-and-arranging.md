# Window-Relative Coordinates + Window Arranging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mouse macros that replay correctly against any window position/monitor (schema v3, window-client coordinate space with auto-resize-to-match), plus one-click Stack/Grid arranging of the running alt windows.

**Architecture:** Recording converts mouse coords `ScreenToClient` against the bound window at capture time and stores the recorded client size in the macro (`CoordSpace = "client"`). Playback resolves the target window, resizes it once to the recorded client size (refuse-on-can't), and maps every mouse event `ClientToScreen` at inject time. Legacy macros (`CoordSpace = "screen"`) play byte-identically to today. A pure `WindowArranger` computes Stack/Grid rects; a thin service applies them via `SetWindowPos`.

**Tech Stack:** C# / .NET 10 WPF (`net10.0-windows`), Win32 P/Invoke, System.Text.Json, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-02-window-relative-coords-and-arranging-design.md`

## Global Constraints

- Version bump: `<Version>0.3.1</Version>` → `0.4.0` in `rororo-ur-task.csproj`; `"version": "0.3.1"` → `"0.4.0"` in `manifest.json` — both, matching (Task 8).
- `Macro.CurrentSchemaVersion` bumps 2 → 3. Coord space strings are exactly `"screen"` and `"client"` (constants on `Macro`).
- JSON stays camelCase + `JsonStringEnumConverter` (existing `MacroStore.JsonOptions` / `MacroV1Migrator.JsonOptions`). New nullable fields are omitted when null via `[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`.
- Multi-window (AllWindows) recordings stay `"screen"` space — raw replay path is byte-unchanged.
- Test command form: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~<TestClass>" --nologo`. NOTE: `HotkeyServiceTests` fail with win32 error 1409 if a live plugin instance is running — close it before full-suite runs.
- All work on branch `feat/window-relative-coords` off `main`. Commit at the end of every task.
- TDD: write the failing test first, watch it fail, implement minimal, watch it pass. No test may fire real `SendInput` mouse/keyboard events — playback tests use macros with **zero events** or assert refusal paths that return before the event loop.
- No new manifest capabilities — `SetWindowPos` is process-level Win32, not a host contract surface.

## File Structure

| File | Role |
|---|---|
| Modify `src/Macros/Macro.cs` | v3 fields (`CoordSpace`, `RecordedClientW/H`), constants, `IsClientSpace` |
| Modify `src/Macros/MacroV1Migrator.cs` | v1→v3 and v2→v3 migration |
| Create `src/Macros/WindowSpaceMath.cs` | Pure screen↔client + outer-size-for-client math |
| Create `src/PluginHost/IWindowMetrics.cs` | Window geometry interface (test seam) |
| Create `src/PluginHost/WindowMetrics.cs` | Thin Win32 implementation |
| Modify `src/Macros/MacroRecorder.cs` | Anchor-window client-space capture; `BuildMouseEvent` seam |
| Modify `src/Macros/MacroPlayer.cs` | Client-space preflight (resize/refuse) + per-event mapping |
| Create `src/PluginHost/WindowArranger.cs` | Pure Stack/Grid layout math (`RectPx`, `GridLayout`) |
| Create `src/PluginHost/WindowArrangeService.cs` | Apply layouts to running alts |
| Modify `src/PluginRuntime.cs` | Record wiring (anchor + client size + coord space), arrange entry points, legacy advisory |
| Modify `src/UI/RecorderViewModel.cs` + `src/UI/RecorderWindow.xaml` | STACK / GRID buttons |
| Modify `manifest.json`, `rororo-ur-task.csproj`, `CHANGELOG.md`, `README.md` | Version 0.4.0 + riders (Task 8) |
| Test files | `MacroV3MigrationTests.cs`, `WindowSpaceMathTests.cs`, `MacroRecorderClientSpaceTests.cs`, `MacroPlayerClientSpaceTests.cs`, `WindowArrangerTests.cs`, `WindowArrangeServiceTests.cs` |

---

### Task 1: Schema v3 + migration

**Files:**
- Modify: `src/Macros/Macro.cs`
- Modify: `src/Macros/MacroV1Migrator.cs`
- Modify: `tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs` (existing assertions expect v2)
- Test: `tests/rororo-ur-task.Tests/MacroV3MigrationTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `Macro.CoordSpaceScreen` / `Macro.CoordSpaceClient` (string consts), `Macro.CoordSpace` (`string?`), `Macro.RecordedClientW` / `Macro.RecordedClientH` (`int?`), `Macro.IsClientSpace` (bool), `Macro.CurrentSchemaVersion == 3`. Later tasks construct macros with these exact names.

- [ ] **Step 1: Create the branch**

```bash
git checkout main && git pull --ff-only origin main && git checkout -b feat/window-relative-coords
```

- [ ] **Step 2: Write the failing tests**

Create `tests/rororo-ur-task.Tests/MacroV3MigrationTests.cs`:

```csharp
using System.Text.Json;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MacroV3MigrationTests
{
    private const string V2MouseMacroJson = """
    {
      "schemaVersion": 2,
      "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
      "name": "old mouse macro",
      "recordMode": "PerWindow",
      "recordedAgainstUserId": 42,
      "recordedAgainstDisplayName": "Goldnail8",
      "interAltDelayMs": null,
      "recordedAtUnixMs": 1750000000000,
      "events": [
        { "timestampMs": 10, "kind": "MouseDown", "virtualKeyCode": 0, "x": 500, "y": 600, "mouseButton": 1, "wheelDelta": 0 }
      ]
    }
    """;

    [Fact]
    public void V2Macro_MigratesToV3_ScreenSpace()
    {
        var macro = MacroV1Migrator.LoadAndMigrate(V2MouseMacroJson);
        Assert.Equal(3, macro.SchemaVersion);
        Assert.Equal(Macro.CoordSpaceScreen, macro.CoordSpace);
        Assert.False(macro.IsClientSpace);
        Assert.Null(macro.RecordedClientW);
        Assert.Single(macro.Events);
        Assert.Equal(500, macro.Events[0].X); // absolute coords untouched
    }

    [Fact]
    public void V3ClientMacro_RoundTripsThroughStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        var store = new MacroStore(dir);
        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: "client macro",
            RecordMode: "PerWindow",
            RecordedAgainstUserId: null,
            RecordedAgainstDisplayName: null,
            InterAltDelayMs: null,
            RecordedAtUnixMs: 1,
            Events: new List<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceClient,
            RecordedClientW: 816,
            RecordedClientH: 638);
        store.Save(macro);

        var loaded = store.LoadAll();
        Assert.Empty(loaded.Failures);
        var back = Assert.Single(loaded.Macros);
        Assert.True(back.IsClientSpace);
        Assert.Equal(816, back.RecordedClientW);
        Assert.Equal(638, back.RecordedClientH);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ScreenSpaceMacro_SerializesWithoutClientSizeFields()
    {
        var macro = new Macro(
            SchemaVersion: Macro.CurrentSchemaVersion,
            Id: Guid.NewGuid().ToString(),
            Name: null, RecordMode: "PerWindow",
            RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
            InterAltDelayMs: null, RecordedAtUnixMs: 1,
            Events: new List<MacroEvent>(),
            CoordSpace: Macro.CoordSpaceScreen);
        var json = JsonSerializer.Serialize(macro, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        Assert.DoesNotContain("recordedClientW", json);
        Assert.DoesNotContain("recordedClientH", json);
        Assert.Contains("\"coordSpace\":\"screen\"", json.Replace(" ", ""));
    }

    [Fact]
    public void V3Json_MissingCoordSpace_DefaultsToScreen()
    {
        // Hand-edited or partial v3 file: coordSpace absent must not crash and must
        // default to screen so playback takes the legacy path.
        var json = V2MouseMacroJson.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3");
        var macro = MacroV1Migrator.LoadAndMigrate(json);
        Assert.Equal(Macro.CoordSpaceScreen, macro.CoordSpace);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroV3MigrationTests" --nologo`
Expected: compile FAILURE — `Macro` has no `CoordSpace`/`CoordSpaceScreen`/`IsClientSpace` members.

- [ ] **Step 4: Implement schema v3 on `Macro`**

In `src/Macros/Macro.cs`, replace the record declaration and version constant:

```csharp
using System.Text.Json.Serialization;

namespace Labs626.UrTask.Macros;

/// <summary>
/// Top-level macro envelope (v3). v3 adds a coordinate space: "client" macros
/// store mouse coords relative to the recorded window's client area (plus the
/// recorded client size) and replay against the target window wherever it sits;
/// "screen" macros (all pre-v3 recordings) keep absolute screen pixels and play
/// exactly as before. v1/v2 files migrate at load via <see cref="MacroV1Migrator"/>.
/// </summary>
public sealed record Macro(
    int SchemaVersion,
    string Id,
    string? Name,
    string? RecordMode,                 // "PerWindow" | "AllWindows"; null = PerWindow (legacy)
    long? RecordedAgainstUserId,        // soft metadata, not enforced
    string? RecordedAgainstDisplayName,
    int? InterAltDelayMs,               // per-macro override for SequencePlayer; null = default 500ms
    long RecordedAtUnixMs,
    IReadOnlyList<MacroEvent> Events,
    string? CoordSpace = null,          // "screen" | "client"; null treated as screen (legacy)
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RecordedClientW = null,        // physical px; set only when CoordSpace == "client"
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? RecordedClientH = null)
{
    /// <summary>Current schema version. Bump on shape changes.</summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Absolute screen pixels — all pre-v3 recordings + AllWindows mode.</summary>
    public const string CoordSpaceScreen = "screen";

    /// <summary>Window-client-relative pixels — v3 per-window recordings.</summary>
    public const string CoordSpaceClient = "client";

    public bool IsClientSpace =>
        string.Equals(CoordSpace, CoordSpaceClient, StringComparison.OrdinalIgnoreCase);

    public TimeSpan Duration => Events.Count == 0
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Events[^1].TimestampMs);
}
```

- [ ] **Step 5: Implement migration in `MacroV1Migrator`**

In `src/Macros/MacroV1Migrator.cs`, replace the `LoadAndMigrate` body's version handling (keep `JsonOptions` as-is):

```csharp
public static Macro LoadAndMigrate(string json)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    var schemaVersion = root.TryGetProperty("schemaVersion", out var sv) ? sv.GetInt32() : 1;

    if (schemaVersion >= 2)
    {
        var m = JsonSerializer.Deserialize<Macro>(json, JsonOptions)
            ?? throw new InvalidOperationException("Macro deserialized as null.");
        // v2 → v3 (and defensive default for v3 files missing coordSpace):
        // pre-v3 recordings are absolute screen pixels.
        return m with
        {
            SchemaVersion = Macro.CurrentSchemaVersion,
            CoordSpace = m.CoordSpace ?? Macro.CoordSpaceScreen,
        };
    }

    // v1 → v3 mapping (Bound* fields → RecordedAgainst* metadata).
    var id = root.GetProperty("id").GetString()!;
    var name = root.TryGetProperty("name", out var n) && n.ValueKind != JsonValueKind.Null ? n.GetString() : null;
    var recordedAgainstUserId = root.TryGetProperty("boundUserId", out var bu) ? bu.GetInt64() : (long?)null;
    var recordedAgainstDisplayName = root.TryGetProperty("boundDisplayName", out var bd) && bd.ValueKind != JsonValueKind.Null ? bd.GetString() : null;
    var recordedAtUnixMs = root.GetProperty("recordedAtUnixMs").GetInt64();

    var events = new List<MacroEvent>();
    if (root.TryGetProperty("events", out var evs))
    {
        foreach (var ev in evs.EnumerateArray())
        {
            events.Add(JsonSerializer.Deserialize<MacroEvent>(ev.GetRawText(), JsonOptions)!);
        }
    }

    return new Macro(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: id,
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: recordedAgainstUserId,
        RecordedAgainstDisplayName: recordedAgainstDisplayName,
        InterAltDelayMs: null,
        RecordedAtUnixMs: recordedAtUnixMs,
        Events: events,
        CoordSpace: Macro.CoordSpaceScreen);
}
```

Also update the class XML doc: "reads any-version macro JSON and returns a **v3** Macro."

- [ ] **Step 6: Update existing v1-migration tests**

Open `tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs`. Change every assertion of the form `Assert.Equal(2, macro.SchemaVersion)` (or equivalent expecting v2) to expect `3`, and add `Assert.Equal(Macro.CoordSpaceScreen, macro.CoordSpace);` to each migrated-macro assertion block. Do not change what the tests verify about `RecordedAgainst*` mapping. If any construct `new Macro(...)` positionally, they compile unchanged (new parameters have defaults).

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroV3MigrationTests|FullyQualifiedName~MacroV1MigrationTests" --nologo`
Expected: PASS (all).

- [ ] **Step 8: Commit**

```bash
git add src/Macros/Macro.cs src/Macros/MacroV1Migrator.cs tests/rororo-ur-task.Tests/MacroV3MigrationTests.cs tests/rororo-ur-task.Tests/MacroV1MigrationTests.cs
git commit -m "feat(schema): macro v3 — coordinate space + recorded client size"
```

---

### Task 2: Pure window-space math

**Files:**
- Create: `src/Macros/WindowSpaceMath.cs`
- Test: `tests/rororo-ur-task.Tests/WindowSpaceMathTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `WindowSpaceMath.ToClient((int X, int Y) screen, (int X, int Y) clientOrigin) → (int X, int Y)`, `WindowSpaceMath.ToScreen((int X, int Y) client, (int X, int Y) clientOrigin) → (int X, int Y)`, `WindowSpaceMath.OuterSizeForClient((int W, int H) currentOuter, (int W, int H) currentClient, (int W, int H) targetClient) → (int W, int H)`. Tasks 4, 5 call these exact signatures.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/WindowSpaceMathTests.cs`:

```csharp
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class WindowSpaceMathTests
{
    [Fact]
    public void ToClient_SubtractsOrigin()
    {
        Assert.Equal((50, 60), WindowSpaceMath.ToClient((150, 260), (100, 200)));
    }

    [Fact]
    public void ToScreen_AddsOrigin()
    {
        Assert.Equal((150, 260), WindowSpaceMath.ToScreen((50, 60), (100, 200)));
    }

    [Fact]
    public void RoundTrip_IsIdentity_IncludingNegativeClientCoords()
    {
        // A click left of the client area records negative — faithful replay contract.
        var origin = (300, 400);
        var screen = (250, 380);
        var client = WindowSpaceMath.ToClient(screen, origin);
        Assert.Equal((-50, -20), client);
        Assert.Equal(screen, WindowSpaceMath.ToScreen(client, origin));
    }

    [Fact]
    public void OuterSizeForClient_AppliesClientDeltaToOuter()
    {
        // Outer 830x680 wraps client 816x638 (chrome 14x42). Target client 900x700
        // ⇒ outer must become 914x742.
        Assert.Equal((914, 742),
            WindowSpaceMath.OuterSizeForClient((830, 680), (816, 638), (900, 700)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~WindowSpaceMathTests" --nologo`
Expected: compile FAILURE — `WindowSpaceMath` does not exist.

- [ ] **Step 3: Implement**

Create `src/Macros/WindowSpaceMath.cs`:

```csharp
namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure screen↔client coordinate mapping + outer-size arithmetic for
/// client-space macros. Kept free of Win32 so the math is unit-testable;
/// callers supply the client origin / rects from <c>IWindowMetrics</c>.
/// </summary>
public static class WindowSpaceMath
{
    /// <summary>Screen point → client-relative point (may be negative — faithful replay).</summary>
    public static (int X, int Y) ToClient((int X, int Y) screen, (int X, int Y) clientOrigin)
        => (screen.X - clientOrigin.X, screen.Y - clientOrigin.Y);

    /// <summary>Client-relative point → screen point.</summary>
    public static (int X, int Y) ToScreen((int X, int Y) client, (int X, int Y) clientOrigin)
        => (client.X + clientOrigin.X, client.Y + clientOrigin.Y);

    /// <summary>
    /// Outer (window-rect) size needed to make the client area hit
    /// <paramref name="targetClient"/>, given the current outer/client pair.
    /// Valid because chrome size is constant for a given window style + DPI.
    /// </summary>
    public static (int W, int H) OuterSizeForClient(
        (int W, int H) currentOuter, (int W, int H) currentClient, (int W, int H) targetClient)
        => (currentOuter.W - currentClient.W + targetClient.W,
            currentOuter.H - currentClient.H + targetClient.H);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~WindowSpaceMathTests" --nologo`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Macros/WindowSpaceMath.cs tests/rororo-ur-task.Tests/WindowSpaceMathTests.cs
git commit -m "feat(macros): pure window-space coordinate + outer-size math"
```

---

### Task 3: IWindowMetrics + Win32 implementation

**Files:**
- Create: `src/PluginHost/IWindowMetrics.cs`
- Create: `src/PluginHost/WindowMetrics.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (exact — Tasks 4, 5, 7 depend on these):

```csharp
namespace Labs626.UrTask.PluginHost;
public interface IWindowMetrics
{
    IntPtr HwndForPid(int pid);                          // IntPtr.Zero when unresolvable
    (int X, int Y)? ClientOrigin(IntPtr hwnd);           // client (0,0) in screen px
    (int W, int H)? ClientSize(IntPtr hwnd);
    (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd);
    bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h);
    (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd); // monitor work area (taskbar-aware)
}
```

No unit tests — thin Win32 wrapper, same convention as `Win32Focus` / `MacroPlayer` interop. The compile + later tasks' fake implementations are the gate.

- [ ] **Step 1: Create the interface**

Create `src/PluginHost/IWindowMetrics.cs`:

```csharp
namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Window geometry seam for client-space recording/playback and window
/// arranging. All coordinates are physical pixels (the process is
/// PerMonitorV2 DPI-aware — see app.manifest). Null returns mean the
/// window is gone or the Win32 call failed; callers treat that as
/// refuse/skip, never crash.
/// </summary>
public interface IWindowMetrics
{
    /// <summary>Main window handle for a pid; <see cref="IntPtr.Zero"/> when unresolvable.</summary>
    IntPtr HwndForPid(int pid);

    /// <summary>Screen position of the window's client (0,0).</summary>
    (int X, int Y)? ClientOrigin(IntPtr hwnd);

    /// <summary>Client-area size in physical pixels.</summary>
    (int W, int H)? ClientSize(IntPtr hwnd);

    /// <summary>Outer window rect (position + size).</summary>
    (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd);

    /// <summary>Move/resize the outer rect. Returns false on Win32 failure.</summary>
    bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h);

    /// <summary>Work area (taskbar-excluded) of the monitor hosting the window.</summary>
    (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd);
}
```

- [ ] **Step 2: Create the Win32 implementation**

Create `src/PluginHost/WindowMetrics.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Thin Win32 implementation of <see cref="IWindowMetrics"/>. No logic beyond
/// marshalling — anything decision-shaped lives in WindowSpaceMath /
/// WindowArranger so it can be unit-tested with fakes.
/// </summary>
internal sealed class WindowMetrics : IWindowMetrics
{
    public IntPtr HwndForPid(int pid)
    {
        try { return Process.GetProcessById(pid).MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    public (int X, int Y)? ClientOrigin(IntPtr hwnd)
    {
        var pt = new POINT { x = 0, y = 0 };
        return ClientToScreen(hwnd, ref pt) ? (pt.x, pt.y) : null;
    }

    public (int W, int H)? ClientSize(IntPtr hwnd)
        => GetClientRect(hwnd, out var r) ? (r.right - r.left, r.bottom - r.top) : null;

    public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd)
        => GetWindowRect(hwnd, out var r) ? (r.left, r.top, r.right - r.left, r.bottom - r.top) : null;

    public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        => SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);

    public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var wa = info.rcWork;
            return (wa.left, wa.top, wa.right - wa.left, wa.bottom - wa.top);
        }
        // Degenerate fallback: primary work area via SystemParametersInfo is more
        // interop for a case that only occurs when the window is gone — return a
        // conservative default instead.
        return (0, 0, 1920, 1080);
    }

    // ---------- Win32 interop ----------

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build rororo-ur-task.csproj --nologo`
Expected: Build succeeded (warnings about pre-existing nullable at PluginRuntime.cs are OK).

- [ ] **Step 4: Commit**

```bash
git add src/PluginHost/IWindowMetrics.cs src/PluginHost/WindowMetrics.cs
git commit -m "feat(pluginhost): IWindowMetrics seam + thin Win32 implementation"
```

---

### Task 4: Recorder client-space capture

**Files:**
- Modify: `src/Macros/MacroRecorder.cs`
- Modify: `src/PluginRuntime.cs` (record wiring)
- Test: `tests/rororo-ur-task.Tests/MacroRecorderClientSpaceTests.cs`

**Interfaces:**
- Consumes: `WindowSpaceMath.ToClient` (Task 2), `IWindowMetrics.HwndForPid/ClientSize` (Task 3).
- Produces: `MacroRecorder.Start(..., IntPtr clientAnchorHwnd = default)` — when non-zero, mouse events record client-relative. `MacroRecorder.BuildMouseEvent(int msg, int x, int y, uint mouseData, long timestampMs, (int X, int Y)? clientOrigin) → MacroEvent?` (internal static — the test seam). Task 8's README references "recording is window-relative by default".

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/MacroRecorderClientSpaceTests.cs`:

```csharp
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MacroRecorderClientSpaceTests
{
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_XBUTTONDOWN = 0x020B;

    [Fact]
    public void BuildMouseEvent_WithOrigin_RecordsClientRelative()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_LBUTTONDOWN, 150, 260, 0u, 10, (100, 200));
        Assert.NotNull(evt);
        Assert.Equal(MacroEventKind.MouseDown, evt!.Kind);
        Assert.Equal(50, evt.X);
        Assert.Equal(60, evt.Y);
        Assert.Equal(1, evt.MouseButton);
    }

    [Fact]
    public void BuildMouseEvent_WithoutOrigin_RecordsAbsolute()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_LBUTTONDOWN, 150, 260, 0u, 10, null);
        Assert.Equal(150, evt!.X);
        Assert.Equal(260, evt.Y);
    }

    [Fact]
    public void BuildMouseEvent_WheelDelta_SurvivesConversion()
    {
        // mouseData high word = signed wheel delta (120 = one notch up).
        var evt = MacroRecorder.BuildMouseEvent(WM_MOUSEWHEEL, 150, 260, 120u << 16, 10, (100, 200));
        Assert.Equal(MacroEventKind.MouseWheel, evt!.Kind);
        Assert.Equal(120, evt.WheelDelta);
        Assert.Equal(50, evt.X);
    }

    [Fact]
    public void BuildMouseEvent_XButton_MapsButtonId()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_XBUTTONDOWN, 0, 0, 2u << 16, 10, null);
        Assert.Equal(5, evt!.MouseButton); // X2
    }

    [Fact]
    public void BuildMouseEvent_MouseMove_MapsKind()
    {
        var evt = MacroRecorder.BuildMouseEvent(WM_MOUSEMOVE, 10, 20, 0u, 5, (10, 20));
        Assert.Equal(MacroEventKind.MouseMove, evt!.Kind);
        Assert.Equal(0, evt.X);
        Assert.Equal(0, evt.Y);
    }

    [Fact]
    public void BuildMouseEvent_UnknownMessage_ReturnsNull()
    {
        Assert.Null(MacroRecorder.BuildMouseEvent(0x9999, 0, 0, 0u, 0, null));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRecorderClientSpaceTests" --nologo`
Expected: compile FAILURE — `MacroRecorder.BuildMouseEvent` does not exist.

- [ ] **Step 3: Refactor `OnMouseEvent` into `BuildMouseEvent` + add the anchor**

In `src/Macros/MacroRecorder.cs`:

3a. Add fields next to `_ignoreMouseEvents`:

```csharp
private IntPtr _clientAnchorHwnd = IntPtr.Zero;
```

(No injectable origin seam — the conversion logic is tested through the static `BuildMouseEvent`; `ResolveClientOrigin` stays a thin Win32 call, same convention as the rest of the interop.)

3b. Extend `Start` (add the parameter at the end; existing call sites compile unchanged) and set the field inside the existing `lock (_lock)` block, next to `_ignoreMouseEvents = ignoreMouseEvents;`:

```csharp
public void Start(
    IReadOnlyCollection<int>? alwaysIgnore = null,
    IReadOnlyCollection<int>? chordIgnore = null,
    bool ignoreMouseEvents = true,
    IntPtr clientAnchorHwnd = default)
```

```csharp
_clientAnchorHwnd = clientAnchorHwnd;
```

Extend the `Start` XML doc with: `<paramref name="clientAnchorHwnd"/> — when set, mouse coordinates are recorded relative to this window's client area (v3 client-space macros); when default, absolute screen pixels (legacy/AllWindows).`

3c. Add the pure builder + origin helper:

```csharp
/// <summary>
/// Map one WH_MOUSE_LL message to a MacroEvent, converting to client-relative
/// coordinates when <paramref name="clientOrigin"/> is provided. Pure — the
/// test seam for capture-time conversion. Returns null for unhandled messages.
/// Move-thinning stays in <see cref="OnMouseEvent"/> (it's stateful).
/// </summary>
internal static MacroEvent? BuildMouseEvent(int msg, int x, int y, uint mouseData, long timestampMs, (int X, int Y)? clientOrigin)
{
    var (px, py) = clientOrigin is { } o ? WindowSpaceMath.ToClient((x, y), o) : (x, y);
    return msg switch
    {
        WM_MOUSEMOVE => new MacroEvent(timestampMs, MacroEventKind.MouseMove, 0, px, py, 0, 0),
        WM_LBUTTONDOWN => new MacroEvent(timestampMs, MacroEventKind.MouseDown, 0, px, py, 1, 0),
        WM_LBUTTONUP => new MacroEvent(timestampMs, MacroEventKind.MouseUp, 0, px, py, 1, 0),
        WM_RBUTTONDOWN => new MacroEvent(timestampMs, MacroEventKind.MouseDown, 0, px, py, 2, 0),
        WM_RBUTTONUP => new MacroEvent(timestampMs, MacroEventKind.MouseUp, 0, px, py, 2, 0),
        WM_MBUTTONDOWN => new MacroEvent(timestampMs, MacroEventKind.MouseDown, 0, px, py, 3, 0),
        WM_MBUTTONUP => new MacroEvent(timestampMs, MacroEventKind.MouseUp, 0, px, py, 3, 0),
        WM_MOUSEWHEEL => new MacroEvent(timestampMs, MacroEventKind.MouseWheel, 0, px, py, 0,
            (short)((mouseData >> 16) & 0xFFFF)),
        WM_XBUTTONDOWN or WM_XBUTTONUP => new MacroEvent(timestampMs,
            msg == WM_XBUTTONDOWN ? MacroEventKind.MouseDown : MacroEventKind.MouseUp,
            0, px, py, 3 + (short)((mouseData >> 16) & 0xFFFF), 0),
        _ => null,
    };
}

private (int X, int Y)? ResolveClientOrigin()
{
    if (_clientAnchorHwnd == IntPtr.Zero) return null;
    var pt = new POINT { x = 0, y = 0 };
    return ClientToScreen(_clientAnchorHwnd, ref pt) ? (pt.x, pt.y) : null;
}
```

Add the P/Invoke next to the existing interop block:

```csharp
[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
```

3d. Rewrite the body of `OnMouseEvent`'s capture switch to delegate (keep the move-thinning guard):

```csharp
private IntPtr OnMouseEvent(int nCode, IntPtr wParam, IntPtr lParam)
{
    if (nCode >= 0 && _events is not null && _clock is not null && !_ignoreMouseEvents)
    {
        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var msg = wParam.ToInt32();
        var nowMs = _clock.ElapsedMilliseconds;

        // ~30Hz thinning applies to moves only (see MouseMoveMinIntervalMs doc).
        bool record = true;
        if (msg == WM_MOUSEMOVE)
        {
            record = nowMs - _lastMouseMoveMs >= MouseMoveMinIntervalMs;
            if (record) _lastMouseMoveMs = nowMs;
        }

        if (record)
        {
            // Anchored recording where the anchor window has vanished: drop the
            // event rather than record a lie — auto-stop lands moments later.
            var origin = ResolveClientOrigin();
            if (_clientAnchorHwnd == IntPtr.Zero || origin is not null)
            {
                var evt = BuildMouseEvent(msg, info.pt.x, info.pt.y, info.mouseData, nowMs, origin);
                if (evt is not null) _events.Add(evt);
            }
        }
    }
    return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroRecorderClientSpaceTests" --nologo`
Expected: PASS (6 tests).

- [ ] **Step 5: Wire PluginRuntime record path**

In `src/PluginRuntime.cs`:

5a. Add the metrics field next to the other private fields (initializer runs before ctor body, so it's available everywhere):

```csharp
private readonly PluginHost.IWindowMetrics _metrics = new PluginHost.WindowMetrics();
```

5b. Add recording-anchor state next to `_recordingBoundAccount`:

```csharp
private IntPtr _recordingAnchorHwnd = IntPtr.Zero;
private (int W, int H)? _recordingClientSize;
```

5c. In `StartRecording()`, replace the `_recorder.Start(...)` call and the line after it:

```csharp
// Per-window recordings anchor mouse coords to the bound window's client
// area (v3 client space). AllWindows keeps absolute screen pixels.
var anchorHwnd = CurrentRecordMode == RecordMode.PerWindow && account is not null
    ? _metrics.HwndForPid(account.Pid)
    : IntPtr.Zero;
_recorder.Start(
    alwaysIgnore: new[] { HotkeyService.AbortVkCode },
    chordIgnore: HotkeyService.ChordHotkeyVkCodes,
    ignoreMouseEvents: RecordKeyboardOnly,
    clientAnchorHwnd: anchorHwnd);
_recordingAnchorHwnd = anchorHwnd;
_recordingClientSize = anchorHwnd != IntPtr.Zero ? _metrics.ClientSize(anchorHwnd) : null;
_recordingBoundAccount = account;
```

5d. In `StopAndSaveRecording()`, before constructing the macro, capture the end-size warning; then set the new fields on the `Macro`:

```csharp
// Mid-recording resizes are unsupported: coords stay correct per-event, but
// the stored client size is the record-start size. Warn so the user re-records.
if (_recordingAnchorHwnd != IntPtr.Zero && _recordingClientSize is { } startSize)
{
    var endSize = _metrics.ClientSize(_recordingAnchorHwnd);
    if (endSize is { } es && es != startSize)
        Log($"Warning: window was resized during recording ({startSize.W}x{startSize.H} → {es.W}x{es.H}) — mouse positions may be off; consider re-recording.");
}
var isClientSpace = CurrentRecordMode == RecordMode.PerWindow;
```

```csharp
var macro = new Macro(
    SchemaVersion: Macro.CurrentSchemaVersion,
    Id: Guid.NewGuid().ToString(),
    Name: $"Recording {DateTimeOffset.Now:HH:mm:ss}",
    RecordMode: CurrentRecordMode == RecordMode.AllWindows ? "AllWindows" : "PerWindow",
    RecordedAgainstUserId: bound?.RobloxUserId,
    RecordedAgainstDisplayName: bound?.DisplayName,
    InterAltDelayMs: null,
    RecordedAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    Events: events.ToList(),
    CoordSpace: isClientSpace ? Macro.CoordSpaceClient : Macro.CoordSpaceScreen,
    RecordedClientW: isClientSpace ? _recordingClientSize?.W : null,
    RecordedClientH: isClientSpace ? _recordingClientSize?.H : null);
```

Also reset the anchor state where `_recordingBoundAccount` is nulled (top of `StopAndSaveRecording`):

```csharp
_recordingAnchorHwnd = IntPtr.Zero;
// _recordingClientSize is read above for the macro fields; clear after construction.
```

(Concretely: null `_recordingClientSize` **after** the `Store.Save(macro)` block, not before the `Macro` construction.)

- [ ] **Step 6: Build + run the full standalone suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --nologo`
Expected: PASS (all; HotkeyServiceTests need no live plugin running).

- [ ] **Step 7: Commit**

```bash
git add src/Macros/MacroRecorder.cs src/PluginRuntime.cs tests/rororo-ur-task.Tests/MacroRecorderClientSpaceTests.cs
git commit -m "feat(recorder): client-space mouse capture anchored to the bound window"
```

---

### Task 5: Player client-space playback (resize preflight + per-event mapping)

**Files:**
- Modify: `src/Macros/MacroPlayer.cs`
- Modify: `src/PluginRuntime.cs` (ctor wiring + legacy advisory)
- Test: `tests/rororo-ur-task.Tests/MacroPlayerClientSpaceTests.cs`

**Interfaces:**
- Consumes: `Macro.IsClientSpace/RecordedClientW/RecordedClientH` (Task 1), `WindowSpaceMath.ToScreen/OuterSizeForClient` (Task 2), `IWindowMetrics` (Task 3), `AccountRegistry.AccountInfo.Pid` (existing).
- Produces: `MacroPlayer` ctor becomes `MacroPlayer(IForegroundWatcher foreground, IWindowMetrics metrics)`. Refusal reasons used by Task 8's CHANGELOG: `"Couldn't resize target to recorded client size {W}x{H}..."`.

Playback-safety rule for tests: client-space refusals happen **before** the event loop, and success-path tests use macros with **zero events** — no real input is ever synthesized in CI.

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/MacroPlayerClientSpaceTests.cs`:

```csharp
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class MacroPlayerClientSpaceTests
{
    private sealed class FakeForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? Current { get; set; }
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private sealed class FakeMetrics : IWindowMetrics
    {
        public IntPtr Hwnd = new(0x1234);
        public (int W, int H)? Client;
        public (int X, int Y, int W, int H)? Outer;
        public (int W, int H)? ClientAfterResize;
        public List<(int x, int y, int w, int h)> SetCalls = new();
        public bool SetResult = true;
        private bool _resized;

        public IntPtr HwndForPid(int pid) => Hwnd;
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => (0, 0);
        public (int W, int H)? ClientSize(IntPtr hwnd) => _resized ? (ClientAfterResize ?? Client) : Client;
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd) => Outer;
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        {
            SetCalls.Add((x, y, w, h));
            _resized = true;
            return SetResult;
        }
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => (0, 0, 2560, 1440);
    }

    private static Macro ClientMacro(int w = 816, int h = 638) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(), Name: "t", RecordMode: "PerWindow",
        RecordedAgainstUserId: null, RecordedAgainstDisplayName: null,
        InterAltDelayMs: null, RecordedAtUnixMs: 1,
        Events: new List<MacroEvent>(), // zero events — success path must not synthesize input
        CoordSpace: Macro.CoordSpaceClient, RecordedClientW: w, RecordedClientH: h);

    private static readonly AccountRegistry.AccountInfo Target = new(Pid: 111, RobloxUserId: 42, DisplayName: "Alt", AccountId: "a1");

    [Fact]
    public async Task ClientMacro_SizeAlreadyMatches_PlaysWithoutResize()
    {
        var metrics = new FakeMetrics { Client = (816, 638) };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(metrics.SetCalls);
    }

    [Fact]
    public async Task ClientMacro_SizeMismatch_ResizesByClientDelta_ThenPlays()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),          // chrome = 14 x 42
            ClientAfterResize = (816, 638),      // resize verified
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(816, 638), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var call = Assert.Single(metrics.SetCalls);
        Assert.Equal((10, 20, 830, 680), call);  // outer grows by client delta, position kept
    }

    [Fact]
    public async Task ClientMacro_ResizeDoesNotStick_Refuses()
    {
        var metrics = new FakeMetrics
        {
            Client = (700, 500),
            Outer = (10, 20, 714, 542),
            ClientAfterResize = (750, 520),      // window's own minimum fought back
        };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(ClientMacro(816, 638), targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
        Assert.Contains("recorded client size", result.Reason);
    }

    [Fact]
    public async Task ClientMacro_MissingRecordedSize_Refuses()
    {
        var macro = ClientMacro() with { RecordedClientW = null, RecordedClientH = null };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, new FakeMetrics { Client = (816, 638) });
        var result = await player.PlayAsync(macro, targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task ScreenMacro_NeverTouchesMetrics()
    {
        var metrics = new FakeMetrics { Client = (1, 1) };
        var screenMacro = ClientMacro() with { CoordSpace = Macro.CoordSpaceScreen, RecordedClientW = null, RecordedClientH = null };
        var player = new MacroPlayer(new FakeForeground { Current = Target }, metrics);
        var result = await player.PlayAsync(screenMacro, targetUserId: 42);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Empty(metrics.SetCalls); // legacy path is metrics-blind
    }
}
```

Note: if `IForegroundWatcher` has members beyond `ResolveForegroundAccount()`, implement them in `FakeForeground` by mirroring whatever the existing test fakes in `SequencePlayerTests.cs` do.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~MacroPlayerClientSpaceTests" --nologo`
Expected: compile FAILURE — `MacroPlayer` has no 2-arg ctor taking `IWindowMetrics`.

- [ ] **Step 3: Implement in `MacroPlayer`**

3a. Ctor + field:

```csharp
private readonly IForegroundWatcher _foreground;
private readonly IWindowMetrics _metrics;

public MacroPlayer(IForegroundWatcher foreground, IWindowMetrics metrics)
{
    _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
}
```

3b. In `PlayAsync`, after the preflight user check and **before** `_activeCts = ...` / `Started?.Invoke`, add the client-space preflight:

```csharp
IntPtr clientHwnd = IntPtr.Zero;
if (macro.IsClientSpace)
{
    clientHwnd = _metrics.HwndForPid(preflight.Pid);
    if (clientHwnd == IntPtr.Zero)
        return PlaybackResult.Refused("Target window handle unavailable.");
    var sizeRefusal = EnsureClientSize(clientHwnd, macro);
    if (sizeRefusal is not null) return sizeRefusal;
}
```

3c. In the event loop, replace `SendMacroEvent(evt);` with:

```csharp
var toSend = evt;
if (clientHwnd != IntPtr.Zero && evt.Kind is MacroEventKind.MouseMove
    or MacroEventKind.MouseDown or MacroEventKind.MouseUp or MacroEventKind.MouseWheel)
{
    // Client → screen at inject time: mid-playback window moves stay correct.
    var origin = _metrics.ClientOrigin(clientHwnd);
    if (origin is null)
        return PlaybackResult.Aborted($"Target window vanished at event {i + 1}/{macro.Events.Count}.");
    var (sx, sy) = WindowSpaceMath.ToScreen((evt.X, evt.Y), origin.Value);
    toSend = evt with { X = sx, Y = sy };
}
SendMacroEvent(toSend);
TrackHeldState(toSend, heldKeys, heldButtons);
```

(Delete the old `TrackHeldState(evt, ...)` line — it moves inside as shown.)

3d. Add the preflight helper (near `Abort`):

```csharp
/// <summary>
/// Ensure the target window's client area matches the macro's recorded size.
/// One SetWindowPos sized by the client delta, then re-measure — chrome is
/// constant per style+DPI, so one pass converges. The window is intentionally
/// left resized (round-robin then resizes each alt once, not per cycle).
/// Returns null to proceed, or a Refused result.
/// </summary>
private PlaybackResult? EnsureClientSize(IntPtr hwnd, Macro macro)
{
    if (macro.RecordedClientW is not int rw || macro.RecordedClientH is not int rh)
        return PlaybackResult.Refused("Client-space macro is missing its recorded client size — re-record it.");
    var current = _metrics.ClientSize(hwnd);
    if (current is null) return PlaybackResult.Refused("Could not read target window size.");
    if (current.Value == (rw, rh)) return null;

    var outer = _metrics.OuterRect(hwnd);
    if (outer is null) return PlaybackResult.Refused("Could not read target window rect.");
    var (tw, th) = WindowSpaceMath.OuterSizeForClient((outer.Value.W, outer.Value.H), current.Value, (rw, rh));
    _metrics.SetOuterRect(hwnd, outer.Value.X, outer.Value.Y, tw, th);

    var after = _metrics.ClientSize(hwnd);
    if (after != (rw, rh))
        return PlaybackResult.Refused(
            $"Couldn't resize target to recorded client size {rw}x{rh} (got {after?.W}x{after?.H}) — monitor too small or window minimum.");
    return null;
}
```

Add `using Labs626.UrTask.PluginHost;` if not present (it is — `IForegroundWatcher` comes from there).

3e. `PlayAllWindowsRawAsync` is untouched — AllWindows macros are always screen-space by construction (Task 4 invariant).

- [ ] **Step 4: Update `MacroPlayer` construction sites**

In `src/PluginRuntime.cs` ctor: `_player = new MacroPlayer(_foreground);` → `_player = new MacroPlayer(_foreground, _metrics);` (field initializer from Task 4 runs first — safe). Then grep for other constructions:

Run: `grep -rn "new MacroPlayer(" src tests`
Fix any other site the same way (tests may need a trivial fake metrics — reuse `FakeMetrics` shape).

- [ ] **Step 5: Legacy advisory in PluginRuntime**

In the `_player.Started` handler (PluginRuntime ctor), after the `State = PluginState.Playing;` line, add:

```csharp
if (!args.Macro.IsClientSpace && args.Macro.Events.Any(e => e.Kind is MacroEventKind.MouseMove
    or MacroEventKind.MouseDown or MacroEventKind.MouseUp or MacroEventKind.MouseWheel))
{
    Log("Legacy screen-coordinate macro — window position matters; use STACK or re-record for window-relative playback.");
}
```

- [ ] **Step 6: Run tests to verify they pass + full suite**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --nologo`
Expected: PASS (all — new client-space tests plus no regressions).

- [ ] **Step 7: Commit**

```bash
git add src/Macros/MacroPlayer.cs src/PluginRuntime.cs tests/rororo-ur-task.Tests/MacroPlayerClientSpaceTests.cs
git commit -m "feat(player): client-space playback — resize preflight + inject-time mapping"
```

---

### Task 6: WindowArranger pure layout math

**Files:**
- Create: `src/PluginHost/WindowArranger.cs`
- Test: `tests/rororo-ur-task.Tests/WindowArrangerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (exact — Task 7 depends on these):

```csharp
public readonly record struct RectPx(int X, int Y, int W, int H);
public sealed record GridLayout(IReadOnlyList<RectPx> Rects, bool Overlapping);
public static class WindowArranger
{
    public static IReadOnlyList<RectPx> ComputeStack(RectPx anchor, int count);
    public static GridLayout ComputeGrid(RectPx workArea, int count, int minW, int minH);
}
```

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/WindowArrangerTests.cs`:

```csharp
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class WindowArrangerTests
{
    [Fact]
    public void Stack_ReturnsAnchorRect_TimesCount()
    {
        var anchor = new RectPx(100, 50, 816, 638);
        var rects = WindowArranger.ComputeStack(anchor, 3);
        Assert.Equal(3, rects.Count);
        Assert.All(rects, r => Assert.Equal(anchor, r));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(9, 3, 3)]
    public void Grid_ColsRows_FollowCeilSqrt(int count, int expectedCols, int expectedRows)
    {
        var layout = WindowArranger.ComputeGrid(new RectPx(0, 0, 3000, 2000), count, minW: 100, minH: 100);
        Assert.Equal(count, layout.Rects.Count);
        Assert.False(layout.Overlapping);
        var cols = layout.Rects.Select(r => r.X).Distinct().Count();
        var rows = layout.Rects.Select(r => r.Y).Distinct().Count();
        Assert.Equal(expectedCols, cols);
        Assert.Equal(expectedRows, rows);
    }

    [Fact]
    public void Grid_FourWindows_TilesQuadrants()
    {
        var layout = WindowArranger.ComputeGrid(new RectPx(0, 0, 2000, 1200), 4, 100, 100);
        Assert.Equal(new RectPx(0, 0, 1000, 600), layout.Rects[0]);
        Assert.Equal(new RectPx(1000, 0, 1000, 600), layout.Rects[1]);   // row-major
        Assert.Equal(new RectPx(0, 600, 1000, 600), layout.Rects[2]);
        Assert.Equal(new RectPx(1000, 600, 1000, 600), layout.Rects[3]);
    }

    [Fact]
    public void Grid_RespectsWorkAreaOrigin()
    {
        var layout = WindowArranger.ComputeGrid(new RectPx(50, 40, 2000, 1200), 1, 100, 100);
        var r = Assert.Single(layout.Rects);
        Assert.Equal(new RectPx(50, 40, 2000, 1200), r);
    }

    [Fact]
    public void Grid_CellsBelowMinimum_ClampAndOverlap()
    {
        // 4 windows in 1000x600 with 700x500 minimum: cells clamp to min and
        // strides shrink so all windows stay inside the work area (overlapping).
        var layout = WindowArranger.ComputeGrid(new RectPx(0, 0, 1000, 600), 4, minW: 700, minH: 500);
        Assert.True(layout.Overlapping);
        Assert.All(layout.Rects, r => { Assert.Equal(700, r.W); Assert.Equal(500, r.H); });
        Assert.All(layout.Rects, r =>
        {
            Assert.InRange(r.X, 0, 300);  // 1000 - 700
            Assert.InRange(r.Y, 0, 100);  // 600 - 500
        });
        Assert.Equal(4, layout.Rects.Distinct().Count()); // cascaded, not identical
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~WindowArrangerTests" --nologo`
Expected: compile FAILURE — types do not exist.

- [ ] **Step 3: Implement**

Create `src/PluginHost/WindowArranger.cs`:

```csharp
namespace Labs626.UrTask.PluginHost;

/// <summary>Integer pixel rect (physical px) — WPF's Rect is double-typed; Win32 wants ints.</summary>
public readonly record struct RectPx(int X, int Y, int W, int H);

/// <summary>Grid result. Overlapping = cells were clamped to the minimum window size.</summary>
public sealed record GridLayout(IReadOnlyList<RectPx> Rects, bool Overlapping);

/// <summary>
/// Pure layout math for the window-arranging suite. No Win32 — callers
/// (WindowArrangeService) supply the work area and apply the rects.
/// </summary>
public static class WindowArranger
{
    /// <summary>Every window at the anchor rect — mouse-macro stacking + legacy screen macros.</summary>
    public static IReadOnlyList<RectPx> ComputeStack(RectPx anchor, int count)
        => Enumerable.Repeat(anchor, count).ToArray();

    /// <summary>
    /// Row-major grid over the work area: cols = ceil(sqrt(n)), rows = ceil(n/cols).
    /// Cells clamp to (minW, minH); when clamped, strides shrink so all windows stay
    /// inside the work area, overlapping in cascade order.
    /// </summary>
    public static GridLayout ComputeGrid(RectPx workArea, int count, int minW, int minH)
    {
        if (count <= 0) return new GridLayout(Array.Empty<RectPx>(), Overlapping: false);

        int cols = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling(count / (double)cols);

        int cellW = workArea.W / cols;
        int cellH = workArea.H / rows;
        bool overlapping = cellW < minW || cellH < minH;
        if (overlapping)
        {
            cellW = Math.Max(cellW, minW);
            cellH = Math.Max(cellH, minH);
        }

        // Stride: normally the cell size; when clamped, spread the clamped cells
        // evenly over the remaining span so every window stays on-screen.
        int strideX = cols > 1 ? (overlapping ? Math.Max(0, (workArea.W - cellW)) / (cols - 1) : cellW) : 0;
        int strideY = rows > 1 ? (overlapping ? Math.Max(0, (workArea.H - cellH)) / (rows - 1) : cellH) : 0;

        var rects = new List<RectPx>(count);
        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            rects.Add(new RectPx(workArea.X + col * strideX, workArea.Y + row * strideY, cellW, cellH));
        }
        return new GridLayout(rects, overlapping);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~WindowArrangerTests" --nologo`
Expected: PASS (6 test methods / 10 cases).

- [ ] **Step 5: Commit**

```bash
git add src/PluginHost/WindowArranger.cs tests/rororo-ur-task.Tests/WindowArrangerTests.cs
git commit -m "feat(pluginhost): pure Stack/Grid window layout math"
```

---

### Task 7: Arrange service + STACK/GRID buttons

**Files:**
- Create: `src/PluginHost/WindowArrangeService.cs`
- Modify: `src/PluginRuntime.cs` (entry points + logs)
- Modify: `src/UI/RecorderViewModel.cs` (commands)
- Modify: `src/UI/RecorderWindow.xaml` (buttons)
- Test: `tests/rororo-ur-task.Tests/WindowArrangeServiceTests.cs`

**Interfaces:**
- Consumes: `WindowArranger.ComputeStack/ComputeGrid` + `RectPx`/`GridLayout` (Task 6), `IWindowMetrics` (Task 3), `AccountRegistry.Snapshot()` (existing), `IForegroundWatcher.ResolveForegroundAccount()` (existing).
- Produces: `WindowArrangeService.StackAll() → (int moved, string? note)` and `GridAll() → (int moved, string? note)`; `PluginRuntime.ArrangeStack()/ArrangeGrid()` (void — they log); `RecorderViewModel.StackWindowsCommand`/`GridWindowsCommand` (ICommand).

- [ ] **Step 1: Write the failing tests**

Create `tests/rororo-ur-task.Tests/WindowArrangeServiceTests.cs`:

```csharp
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class WindowArrangeServiceTests
{
    private sealed class FakeForeground : IForegroundWatcher
    {
        public AccountRegistry.AccountInfo? Current { get; set; }
        public AccountRegistry.AccountInfo? ResolveForegroundAccount() => Current;
    }

    private sealed class FakeMetrics : IWindowMetrics
    {
        public Dictionary<int, IntPtr> PidToHwnd = new();
        public Dictionary<IntPtr, (int X, int Y, int W, int H)> Outers = new();
        public List<(IntPtr hwnd, int x, int y, int w, int h)> SetCalls = new();

        public IntPtr HwndForPid(int pid) => PidToHwnd.TryGetValue(pid, out var h) ? h : IntPtr.Zero;
        public (int X, int Y)? ClientOrigin(IntPtr hwnd) => (0, 0);
        public (int W, int H)? ClientSize(IntPtr hwnd)
            => Outers.TryGetValue(hwnd, out var o) ? (o.W, o.H) : null;
        public (int X, int Y, int W, int H)? OuterRect(IntPtr hwnd)
            => Outers.TryGetValue(hwnd, out var o) ? o : null;
        public bool SetOuterRect(IntPtr hwnd, int x, int y, int w, int h)
        { SetCalls.Add((hwnd, x, y, w, h)); Outers[hwnd] = (x, y, w, h); return true; }
        public (int X, int Y, int W, int H) WorkAreaFor(IntPtr hwnd) => (0, 0, 2000, 1200);
    }

    private static AccountRegistry RegistryWith(params int[] pids)
    {
        var reg = new AccountRegistry();
        foreach (var pid in pids) reg.OnLaunched(pid, userId: pid * 10, displayName: $"alt{pid}", accountId: $"a{pid}");
        return reg;
    }

    [Fact]
    public void StackAll_MovesEveryAltToAnchorRect()
    {
        var reg = RegistryWith(1, 2, 3);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1), [2] = new(0x2), [3] = new(0x3) },
            Outers = { [new(0x1)] = (100, 50, 800, 600), [new(0x2)] = (0, 0, 500, 400), [new(0x3)] = (900, 300, 640, 480) },
        };
        // Foreground = pid 1 → its rect is the anchor.
        var fg = new FakeForeground { Current = new AccountRegistry.AccountInfo(1, 10, "alt1", "a1") };
        var svc = new WindowArrangeService(reg, metrics, fg);

        var (moved, note) = svc.StackAll();
        Assert.Equal(3, moved);
        Assert.Null(note);
        Assert.All(metrics.SetCalls, c => Assert.Equal((100, 50, 800, 600), (c.x, c.y, c.w, c.h)));
    }

    [Fact]
    public void StackAll_NoForeground_AnchorsOnFirstAlt()
    {
        var reg = RegistryWith(7);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [7] = new(0x7) },
            Outers = { [new(0x7)] = (10, 10, 640, 480) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());
        var (moved, _) = svc.StackAll();
        Assert.Equal(1, moved);
    }

    [Fact]
    public void GridAll_TilesAcrossWorkArea()
    {
        var reg = RegistryWith(1, 2, 3, 4);
        var metrics = new FakeMetrics
        {
            PidToHwnd = { [1] = new(0x1), [2] = new(0x2), [3] = new(0x3), [4] = new(0x4) },
            Outers = { [new(0x1)] = (0, 0, 800, 600), [new(0x2)] = (0, 0, 800, 600), [new(0x3)] = (0, 0, 800, 600), [new(0x4)] = (0, 0, 800, 600) },
        };
        var svc = new WindowArrangeService(reg, metrics, new FakeForeground());
        var (moved, note) = svc.GridAll();
        Assert.Equal(4, moved);
        Assert.Null(note);
        Assert.Equal(4, metrics.SetCalls.Select(c => (c.x, c.y)).Distinct().Count()); // 4 distinct cells
    }

    [Fact]
    public void NoAltsRunning_ReturnsZeroAndNote()
    {
        var svc = new WindowArrangeService(new AccountRegistry(), new FakeMetrics(), new FakeForeground());
        var (moved, note) = svc.StackAll();
        Assert.Equal(0, moved);
        Assert.NotNull(note);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~WindowArrangeServiceTests" --nologo`
Expected: compile FAILURE — `WindowArrangeService` does not exist.

- [ ] **Step 3: Implement the service**

Create `src/PluginHost/WindowArrangeService.cs`:

```csharp
namespace Labs626.UrTask.PluginHost;

/// <summary>
/// Applies WindowArranger layouts to the running alt windows. Stack = every
/// alt at the anchor rect (foreground alt if any, else first in snapshot).
/// Grid = tiled over the anchor's monitor work area. The minimum window size
/// for grid clamping is discovered at apply time: apply, and windows enforce
/// their own floor via WM_GETMINMAXINFO — the pure layout uses a nominal
/// floor and the note reports overlap.
/// </summary>
internal sealed class WindowArrangeService
{
    // Nominal floor for grid cells. The real floor is whatever the window
    // enforces when SetWindowPos lands; this just keeps cells from computing
    // absurdly small before that.
    private const int NominalMinW = 640;
    private const int NominalMinH = 480;

    private readonly AccountRegistry _accounts;
    private readonly IWindowMetrics _metrics;
    private readonly IForegroundWatcher _foreground;

    public WindowArrangeService(AccountRegistry accounts, IWindowMetrics metrics, IForegroundWatcher foreground)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
    }

    public (int moved, string? note) StackAll()
    {
        var (windows, anchorHwnd, note) = ResolveWindows();
        if (windows.Count == 0) return (0, note);
        var anchor = _metrics.OuterRect(anchorHwnd);
        if (anchor is null) return (0, "Couldn't read the anchor window's rect.");
        var rects = WindowArranger.ComputeStack(
            new RectPx(anchor.Value.X, anchor.Value.Y, anchor.Value.W, anchor.Value.H), windows.Count);
        return (Apply(windows, rects), null);
    }

    public (int moved, string? note) GridAll()
    {
        var (windows, anchorHwnd, note) = ResolveWindows();
        if (windows.Count == 0) return (0, note);
        var wa = _metrics.WorkAreaFor(anchorHwnd);
        var layout = WindowArranger.ComputeGrid(
            new RectPx(wa.X, wa.Y, wa.W, wa.H), windows.Count, NominalMinW, NominalMinH);
        var moved = Apply(windows, layout.Rects);
        return (moved, layout.Overlapping ? "Work area too small for a clean grid — windows overlap in cascade order." : null);
    }

    private (List<IntPtr> windows, IntPtr anchor, string? note) ResolveWindows()
    {
        var alts = _accounts.Snapshot().OrderBy(a => a.DisplayName).ToList();
        if (alts.Count == 0) return (new List<IntPtr>(), IntPtr.Zero, "No RoRoRo-managed alts running.");

        var windows = new List<IntPtr>(alts.Count);
        foreach (var alt in alts)
        {
            var hwnd = _metrics.HwndForPid(alt.Pid);
            if (hwnd != IntPtr.Zero) windows.Add(hwnd);
        }
        if (windows.Count == 0) return (windows, IntPtr.Zero, "No alt windows resolvable.");

        // Anchor: the foreground alt's window when it's one of ours, else the first.
        var fg = _foreground.ResolveForegroundAccount();
        var anchor = fg is not null ? _metrics.HwndForPid(fg.Pid) : IntPtr.Zero;
        if (anchor == IntPtr.Zero || !windows.Contains(anchor)) anchor = windows[0];
        return (windows, anchor, null);
    }

    private int Apply(List<IntPtr> windows, IReadOnlyList<RectPx> rects)
    {
        int moved = 0;
        for (int i = 0; i < windows.Count && i < rects.Count; i++)
        {
            var r = rects[i];
            if (_metrics.SetOuterRect(windows[i], r.X, r.Y, r.W, r.H)) moved++;
        }
        return moved;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --filter "FullyQualifiedName~WindowArrangeServiceTests" --nologo`
Expected: PASS (4 tests).

- [ ] **Step 5: Wire PluginRuntime + ViewModel + XAML**

5a. `src/PluginRuntime.cs` — field next to `_metrics` (Task 4) and entry points near `ResetAssignments()`:

```csharp
private readonly PluginHost.WindowArrangeService _arranger;
```

In the ctor, after `_runner = new AssignmentRunner(...)`:

```csharp
_arranger = new PluginHost.WindowArrangeService(Accounts, _metrics, _foreground);
```

```csharp
/// <summary>STACK button: every alt window moved to the anchor rect.</summary>
public void ArrangeStack()
{
    var (moved, note) = _arranger.StackAll();
    Log(note is null ? $"Stacked {moved} alt window(s)." : $"Stack: {note}");
}

/// <summary>GRID button: alt windows tiled over the anchor monitor's work area.</summary>
public void ArrangeGrid()
{
    var (moved, note) = _arranger.GridAll();
    Log(note is null ? $"Arranged {moved} alt window(s) in a grid." : $"Grid ({moved} moved): {note}");
}
```

5b. `src/UI/RecorderViewModel.cs` — next to the existing command initializations (around line 31) and declarations (around line 165):

```csharp
StackWindowsCommand = new RelayCommand(() => _runtime.ArrangeStack(), CanArrange);
GridWindowsCommand = new RelayCommand(() => _runtime.ArrangeGrid(), CanArrange);
```

```csharp
public ICommand StackWindowsCommand { get; }
public ICommand GridWindowsCommand { get; }

private bool CanArrange() => _runtime.Accounts.Snapshot().Count > 0;
```

Spec requires the buttons disabled when no alts are running. Check how `PlayAssignmentsCommand` passes its `canExecute` to `RelayCommand` (RecorderViewModel ~line 56) and mirror that ctor form exactly; then find the existing `(PlayAssignmentsCommand as RelayCommand)?.RaiseCanExecuteChanged();` call site (~line 404 — it fires when account state changes) and add, immediately next to it:

```csharp
(StackWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
(GridWindowsCommand as RelayCommand)?.RaiseCanExecuteChanged();
```

5c. `src/UI/RecorderWindow.xaml` — place two buttons in the assignments header row, immediately before the existing RESET button (`Grid.Column="1"` with `ResetAssignmentsCommand`, around line 514). Match its style/markup conventions exactly (copy the RESET button's attributes, adjust column indices as needed — if the header Grid lacks free columns, add two `<ColumnDefinition Width="Auto"/>` entries and shift the RESET button's column):

```xml
<Button Style="{StaticResource SecondaryButton}"
        Command="{Binding StackWindowsCommand}"
        ToolTip="Move all running alt windows to the same position and size (for legacy screen-coordinate mouse macros)"
        Content="STACK" Margin="0,0,6,0" />
<Button Style="{StaticResource SecondaryButton}"
        Command="{Binding GridWindowsCommand}"
        ToolTip="Tile all running alt windows in a grid to watch the round-robin"
        Content="GRID" Margin="0,0,6,0" />
```

If `SecondaryButton` isn't the style the neighboring buttons use, match whatever they use.

- [ ] **Step 6: Build + full suite**

Run: `dotnet build rororo-ur-task.csproj --nologo && dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --nologo`
Expected: Build succeeded; all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PluginHost/WindowArrangeService.cs src/PluginRuntime.cs src/UI/RecorderViewModel.cs src/UI/RecorderWindow.xaml tests/rororo-ur-task.Tests/WindowArrangeServiceTests.cs
git commit -m "feat(ui): window arranging suite — STACK + GRID over running alts"
```

---

### Task 8: Version 0.4.0, manifest description fix, CHANGELOG, README

**Files:**
- Modify: `rororo-ur-task.csproj` (line with `<Version>`)
- Modify: `manifest.json` (version + description)
- Modify: `CHANGELOG.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: refusal/advisory copy from Task 5, button names from Task 7.
- Produces: release-ready v0.4.0 tree.

- [ ] **Step 1: Bump versions**

`rororo-ur-task.csproj`: `<Version>0.3.1</Version>` → `<Version>0.4.0</Version>`.
`manifest.json`: `"version": "0.3.1"` → `"version": "0.4.0"`, and replace the stale v0.1 description:

```json
"description": "Per-window-aware macro recording for RoRoRo-managed Roblox alts. Record once, play on any alt — round-robin assignments, keep-alive, window-relative mouse macros, and an action bridge for sibling plugins.",
```

- [ ] **Step 2: CHANGELOG entry**

Add at the top of `CHANGELOG.md` (below the intro):

```markdown
## 0.4.0 — 2026-07-02

### Added

- **Window-relative mouse macros (schema v3).** Per-window recordings now store mouse positions relative to the recorded window's client area, plus the recorded client size. Playback resizes the target window once to match (refusing with a clear reason when it can't — monitor too small or window minimum) and maps every event onto the target window wherever it sits, on any monitor. No more stacking windows for mouse macros. Existing macros keep playing exactly as before (absolute screen coordinates) with a one-line advisory in the activity log; re-record to upgrade. Multi-window recordings keep raw absolute replay. v1/v2 macro files migrate to v3 on load; migration is sticky on save.
- **Window arranging suite.** Two new buttons in the recorder window: **STACK** moves every running alt window to the same position and size (what legacy screen-coordinate mouse macros need); **GRID** tiles all running alts across the monitor's work area so you can watch the round-robin. Taskbar-aware; grids that can't fit at minimum window size overlap in cascade order and say so in the activity log.

### Fixed

- **Manifest description no longer claims v0.1 bound-playback behavior** ("playback refuses unless the foreground window matches" — binding was removed in v0.2).

Same host requirement as v0.3.x — RoRoRo v1.4.3.0+.
```

- [ ] **Step 3: README updates**

In `README.md`:

3a. Replace the "Recording mode and the mouse-click caveat" section body with:

```markdown
**By default, recording is keyboard-only** — mouse events (clicks, moves, wheel) are dropped during capture. Keyboard events route to whichever window has focus, which is exactly right for the dominant use case (jumps, walks, key-combo grinding).

If you need mouse capture (drag flows, click-precision sequences), untick "Record keyboard only" in the recorder window. As of **v0.4.0**, per-window mouse recordings are **window-relative**: positions are stored relative to the recorded window's client area, and playback resizes the target window to match and lands every click in the right spot — wherever the window sits, on any monitor. No window stacking required.

**Legacy mouse macros** (recorded before v0.4.0) still use absolute screen coordinates: they play exactly as before, and the target window must occupy the same screen region as at record time. Use the **STACK** button to line windows up for them — or just re-record to upgrade.

Playback of a window-relative macro refuses cleanly (and skips to the next alt) when the target window can't reach the recorded size — monitor too small, or below the window's minimum.
```

3b. Delete the line `Window-relative coordinates (record once, replay at any window position) is planned for a future release.` (it shipped).

3c. Add after that section:

```markdown
## Window arranging

Two buttons in the recorder window operate on all running alts:

- **STACK** — moves every alt window to the same position and size (anchored on the foreground alt). What legacy screen-coordinate mouse macros need.
- **GRID** — tiles all alt windows across the monitor's work area so you can watch the round-robin visit each one. If they can't fit at minimum size, they overlap in cascade order (the activity log says so).
```

- [ ] **Step 4: Full suite + build, then commit**

Run: `dotnet build rororo-ur-task.csproj --nologo && dotnet test tests/rororo-ur-task.Tests/rororo-ur-task.Tests.csproj -p:StandaloneTestsOnly=true --nologo`
Expected: Build succeeded; all tests PASS.

```bash
git add rororo-ur-task.csproj manifest.json CHANGELOG.md README.md
git commit -m "chore: v0.4.0 — window-relative macros + arranging suite (docs + manifest description fix)"
```

---

## Human verification (after all tasks, before release)

Not automatable — the implementer notes these for Este to run with live alts:

1. Record a mouse macro on one alt, move that alt's window to a different monitor/corner, play — clicks land correctly.
2. Play the same macro on a second alt whose window is a different size — window snaps to recorded size, clicks land.
3. STACK with 2–3 alts running — all windows pile onto the foreground alt's rect.
4. GRID with 2–4 alts — tiled, watchable round-robin.
5. A pre-v0.4 mouse macro still plays (advisory line appears in the activity log).
