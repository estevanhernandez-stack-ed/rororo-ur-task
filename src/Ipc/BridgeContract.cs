using System.Text.Json;
using System.Text.Json.Serialization;

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

internal static class BridgeContract
{
    public const string Method = "RunMacro";          // back-compat alias
    public const string MethodRunMacro = "RunMacro";
    public const string MethodListMacros = "ListMacros";
    public const string MethodStopMacro = "StopMacro";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>True iff the caller's contract version is in the supported 1.x line.</summary>
    public static bool IsSupportedVersion(string? contractVersion)
        => !string.IsNullOrEmpty(contractVersion) && contractVersion.StartsWith("1.", StringComparison.Ordinal);
}
