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
    public static RunMacroResponse Accepted(string playbackId) => new(true, playbackId, false, null, null); // Queued=false: contract refuses-when-busy, no server-side queue path
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
