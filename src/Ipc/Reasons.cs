// src/Ipc/Reasons.cs
namespace Labs626.UrTask.Ipc;

/// <summary>
/// Every bridge reason code, in one place so they cannot drift. Existing codes keep their exact
/// strings and meanings (<see cref="Refused"/> included: Ur OCR reruns on it). New in 0.13:
/// <see cref="MacroUnreadable"/> and <see cref="KeepAlivePreempted"/>.
/// </summary>
internal static class Reasons
{
    public const string Busy = "busy";
    public const string Refused = "refused";
    public const string UnknownMacro = "unknown-macro";
    public const string NoTargetsResolved = "no-targets-resolved";
    public const string UnknownPlayback = "unknown-playback";
    public const string VersionMismatch = "version-mismatch";
    public const string Breakglass = "breakglass";
    public const string MacroUnreadable = "macro-unreadable";
    public const string KeepAlivePreempted = "keep-alive-preempted";
    public const string CheckFailed = "check-failed";
    public const string Aborted = "aborted";
    public const string Error = "error";
    public const string Skipped = "skipped";

    /// <summary>Advice for a macro whose file was written by a newer Ur Task.</summary>
    public const string UpdateUrTaskAdvice = "Update Ur Task to use it.";

    /// <summary>Advice for a playback cut short so keep-alive could take its turn.</summary>
    public const string KeepAlivePreemptedAdvice = "Give long holds a max so keep-alive can take its turn.";
}

/// <summary>Who caused a refusal or failure, when it is known.</summary>
internal static class Actors
{
    public const string User = "user";
    public const string KeepAlive = "keep-alive";
    public const string Host = "host";
    public const string UrTask = "ur-task";

    public static string Agent(string callerPluginId) => $"agent:{callerPluginId}";

    /// <summary>The agent actor when the caller is known, else null.</summary>
    public static string? AgentOrNull(string? callerPluginId)
        => string.IsNullOrWhiteSpace(callerPluginId) ? null : Agent(callerPluginId);
}
