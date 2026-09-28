namespace Labs626.UrTask.Ipc;

/// <summary>
/// Seam between the bridge transport and macro playback. The server owns
/// pipes + framing + validation; the invoker owns "resolve the macro + targets
/// and play them." Split so the transport is unit-testable with a fake.
/// </summary>
internal interface IMacroRunInvoker
{
    Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct);

    /// <summary>Play an ordered list of points on one account as reach holds, as ONE playback
    /// (bridge 1.x, additive). Same single-flight rule, playback id, StopMacro and GetPlayback as
    /// <see cref="RunAsync"/>; the macro is built in memory and never saved.</summary>
    Task<RunMacroResponse> ClearAtAsync(ClearAtRequest request, CancellationToken ct);

    /// <summary>Enumerate the macro library (id + display name) for name resolution.</summary>
    IReadOnlyList<MacroSummary> ListMacros();

    /// <summary>Cancel a playback by id, or all active playbacks when the id is null.</summary>
    StopMacroResponse StopMacro(StopMacroRequest request);

    /// <summary>How a playback is going or how it ended (kept 10 minutes after it ends).</summary>
    GetPlaybackResponse GetPlayback(GetPlaybackRequest request);
}
