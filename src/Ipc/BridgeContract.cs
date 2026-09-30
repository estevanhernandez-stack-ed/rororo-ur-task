using System.Text.Json;
using System.Text.Json.Serialization;
using Labs626.UrTask.Macros.Steps;

namespace Labs626.UrTask.Ipc;

public sealed record RunMacroRequest(
    string ContractVersion,
    string Method,
    string MacroId,
    IReadOnlyList<string>? Targets,   // decimal user-ids, or ["foreground"]; null ⇒ foreground
    int? InterAltDelayMs,
    string? CallerPluginId,
    bool Repeat = false);             // loop macro end→start until StopMacro/abort (bridge 1.x additive)

public sealed record RunMacroResponse(
    bool Ok,
    string? PlaybackId,
    bool Queued,
    string? Reason,
    string? Detail)
{
    public static RunMacroResponse Accepted(string playbackId) => new(true, playbackId, false, null, null); // Queued=false: contract refuses-when-busy, no server-side queue path
    public static RunMacroResponse Refused(string reason, string? detail = null) => new(false, null, false, reason, detail);
}

/// <summary>Minimal shape for peeking method + version before typed deserialization.</summary>
public sealed record RequestEnvelope(string? ContractVersion, string? Method);

public sealed record MacroSummary(string Id, string Name);

public sealed record ListMacrosResponse(
    bool Ok,
    IReadOnlyList<MacroSummary>? Macros,
    string? Reason,
    string? Detail)
{
    public static ListMacrosResponse Success(IReadOnlyList<MacroSummary> macros) => new(true, macros, null, null);
    public static ListMacrosResponse Refused(string reason, string? detail = null) => new(false, null, reason, detail);
}

public sealed record StopMacroRequest(
    string ContractVersion,
    string Method,
    string? PlaybackId,                 // stop a specific playback; null ⇒ stop all active
    IReadOnlyList<string>? Targets,     // reserved for target-scoped stop; ignored while playback is single-flight
    string? CallerPluginId);

public sealed record StopMacroResponse(
    bool Ok,
    int Stopped,                        // how many playbacks were cancelled
    string? Reason,
    string? Detail)
{
    public static StopMacroResponse Done(int stopped) => new(true, stopped, null, null);
    public static StopMacroResponse Refused(string reason, string? detail = null) => new(false, 0, reason, detail);
}

public sealed record GetPlaybackRequest(
    string ContractVersion,
    string Method,
    string? PlaybackId,
    string? CallerPluginId);

/// <summary>State is running | finished | stopped | failed. On failed, Reason is a short code
/// (check-failed, refused, aborted, error) and Detail is the sentence Claude shows. On finished,
/// Reason is null, or "skipped" when the playback pressed nothing because reach checks skipped
/// on every alt (0.11.0, additive; Ur MCP ignores the reason of a finished run).
/// <para>NoOutline (0.12.0, additive): on a finished ClearAt only, the 1-based points that showed
/// no outline, empty when every point had one. Null everywhere else, and a null is not written,
/// so every other reply keeps its old shape.</para></summary>
public sealed record GetPlaybackResponse(
    bool Ok,
    string? State,
    string? Reason,
    string? Detail,
    int? StepIndex,
    int[]? NoOutline = null)
{
    public static GetPlaybackResponse Refused(string reason, string? detail = null) => new(false, null, reason, detail, null);
}

/// <summary>The client size a ClearAt's points and outline box were measured in.</summary>
public sealed record ClearAtClient(int W, int H);

/// <summary>One ClearAt point in <see cref="ClearAtRequest.Client"/> pixels. A null or blank label
/// plays and logs as "point N".</summary>
public sealed record ClearAtPoint(int X, int Y, string? Label = null);

/// <summary>The outline check every ClearAt point uses: a W x H box centred on the point, passing
/// at MinCount or more pixels whose every channel is at least WhiteMin (see OutlineCheck).</summary>
public sealed record ClearAtOutline(int W, int H, int MinCount, int WhiteMin = OutlineCheck.DefaultWhiteMin);

/// <summary>A pixel box in <see cref="ClearAtRequest.Client"/> pixels that must stay within
/// Tolerance of Expect (RGB distance) while the call plays, or it stops before any more input: the
/// Auto Mine dot, which a menu or a player's profile covers. Expect is nullable so a missing one is
/// refused rather than read as black.</summary>
public sealed record ClearAtGuard(int X, int Y, int W, int H, Rgb? Expect, int Tolerance);

/// <summary>
/// Press where the caller points (bridge 1.x, additive; ore-stop pulse spec, "The ClearAt bridge
/// call"). Each point, in order, plays as a reach hold on the target account, and the whole list
/// is ONE playback: the answer is a <see cref="RunMacroResponse"/>, and GetPlayback, StopMacro and
/// Esc treat it like any RunMacro playback. MaxMsPerPoint null means no time limit.
/// </summary>
public sealed record ClearAtRequest(
    string ContractVersion,
    string Method,
    string? CallerPluginId,
    string? Target,                    // decimal user id
    ClearAtClient? Client,
    IReadOnlyList<ClearAtPoint>? Points,
    ClearAtOutline? Outline,
    int? MaxMsPerPoint = null,
    ClearAtGuard? Guard = null);

/// <summary>
/// One continuous hold along a path (bridge 1.x, additive; ore-stop sweep spec, "The Ur Task side").
/// Path is in Client pixels, on a Step px lattice anchored at Path[0] (the start block beside the
/// character), and ends on Path[0] again, where the button comes up. DwellMs is the wait at each
/// point. Guard is the pixel that must keep its colour while the button is down; it is required.
/// Answered with a <see cref="RunMacroResponse"/>; GetPlayback, StopMacro and Esc treat it like any
/// RunMacro playback.
/// </summary>
public sealed record SweepPathRequest(
    string ContractVersion,
    string Method,
    string? CallerPluginId,
    string? Target,                    // decimal user id
    ClearAtClient? Client,
    IReadOnlyList<SweepPoint>? Path,
    int Step,
    int DwellMs,
    ClearAtGuard? Guard);

internal static class BridgeContract
{
    public const string Method = "RunMacro";          // back-compat alias
    public const string MethodRunMacro = "RunMacro";
    public const string MethodListMacros = "ListMacros";
    public const string MethodStopMacro = "StopMacro";
    public const string MethodGetPlayback = "GetPlayback";
    public const string MethodClearAt = "ClearAt";
    public const string MethodSweepPath = "SweepPath";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>True iff the caller's contract version is in the supported 1.x line.</summary>
    public static bool IsSupportedVersion(string? contractVersion)
        => !string.IsNullOrEmpty(contractVersion) && contractVersion.StartsWith("1.", StringComparison.Ordinal);
}
