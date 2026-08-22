namespace Labs626.UrTask.Ipc;

/// <summary>
/// Seam between the bridge transport and macro playback. The server owns
/// pipes + framing + validation; the invoker owns "resolve the macro + targets
/// and play them." Split so the transport is unit-testable with a fake.
/// </summary>
internal interface IMacroRunInvoker
{
    Task<RunMacroResponse> RunAsync(RunMacroRequest request, CancellationToken ct);

    /// <summary>Enumerate the macro library (id + display name) for name resolution.</summary>
    IReadOnlyList<MacroSummary> ListMacros();

    /// <summary>Cancel a playback by id, or all active playbacks when the id is null.</summary>
    StopMacroResponse StopMacro(StopMacroRequest request);
}
