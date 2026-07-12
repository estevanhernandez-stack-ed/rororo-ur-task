using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Macros;

public enum RecipeRunPhase { Positioning, Looping, KeepAlive, AllAltsFailed, MacroMissing, Done }

public sealed record RecipePhaseEvent(
    string StepLabel, int StepIndex, int TotalSteps,
    IReadOnlyList<AccountRegistry.AccountInfo> LiveAlts, RecipeRunPhase Phase);

/// <summary>
/// Thin orchestrator: run each RunOnce (position) step once-per-alt and BARRIER on
/// its completion, carrying only the alts that positioned forward
/// (proceed-with-successes), then start the terminal Loop/KeepAlive step across the
/// survivors — or, for a loadout (terminal <see cref="StepIteration.Done"/>), stop
/// after positioning with no loop at all. Owns no Win32 — it drives two injected
/// delegates that in production wrap SequencePlayer (run-once) and AssignmentRunner
/// (loop), so orchestration is unit-testable with fakes.
/// </summary>
internal sealed class RecipeRunner
{
    public delegate Task<SequenceResult> RunOnceDelegate(
        Macro macro, IReadOnlyList<AccountRegistry.AccountInfo> alts, CancellationToken ct);
    public delegate Task RunLoopDelegate(
        IReadOnlyList<Assignment> assignments, CancellationToken ct);

    private readonly RunOnceDelegate _runOnce;
    private readonly RunLoopDelegate _runLoop;
    private readonly Func<string, Macro?> _resolveMacro;
    private CancellationTokenSource? _activeCts;

    public RecipeRunner(RunOnceDelegate runOnce, RunLoopDelegate runLoop, Func<string, Macro?> resolveMacro)
    {
        _runOnce = runOnce ?? throw new ArgumentNullException(nameof(runOnce));
        _runLoop = runLoop ?? throw new ArgumentNullException(nameof(runLoop));
        _resolveMacro = resolveMacro ?? throw new ArgumentNullException(nameof(resolveMacro));
    }

    public event EventHandler<RecipePhaseEvent>? Progress;

    public bool IsRunning => _activeCts is not null;

    public async Task RunAsync(
        Recipe recipe,
        IReadOnlyList<AccountRegistry.AccountInfo> selected,
        CancellationToken external = default)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        if (selected is null || selected.Count == 0) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        if (Interlocked.CompareExchange(ref _activeCts, cts, null) is not null) { cts.Dispose(); return; }
        var ct = cts.Token;

        try
        {
            var live = selected.ToList();
            var positionSteps = recipe.PositionSteps.ToList();

            for (int i = 0; i < positionSteps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var macro = _resolveMacro(positionSteps[i].MacroId!);
                if (macro is null)
                {
                    // unresolved macro id — recipe references a deleted/renamed macro. Stop and
                    // surface it rather than running the loop over un-positioned alts.
                    Emit(new RecipePhaseEvent($"Missing macro ({i + 1})", i, recipe.Steps.Count, live, RecipeRunPhase.MacroMissing));
                    return;
                }

                Emit(new RecipePhaseEvent($"Positioning ({i + 1})", i, recipe.Steps.Count, live, RecipeRunPhase.Positioning));
                var result = await _runOnce(macro, live, ct).ConfigureAwait(false);

                // proceed-with-successes: only alts that Completed carry forward
                live = result.PerAlt
                    .Where(o => o.Outcome == PlaybackOutcome.Completed)
                    .Select(o => o.Alt)
                    .ToList();

                if (live.Count == 0)
                {
                    Emit(new RecipePhaseEvent("All alts failed to position", i, recipe.Steps.Count, live, RecipeRunPhase.AllAltsFailed));
                    return;
                }
            }

            ct.ThrowIfCancellationRequested();
            var terminal = recipe.Terminal;

            if (terminal.Iteration == StepIteration.Done)
            {
                // a loadout: position steps already ran once above — stop here, no
                // AssignmentRunner loop. No Assignments to build either.
                Emit(new RecipePhaseEvent("Done", recipe.Steps.Count - 1, recipe.Steps.Count, live, RecipeRunPhase.Done));
                return;
            }

            var terminalMacro = terminal.Iteration == StepIteration.Loop
                ? _resolveMacro(terminal.MacroId!)
                : null; // KeepAlive → null macro → AssignmentRunner sends Space

            if (terminal.Iteration == StepIteration.Loop && terminalMacro is null)
            {
                // unresolved terminal macro id — do not degrade to keep-alive silently.
                Emit(new RecipePhaseEvent(
                    "Missing macro (terminal)", recipe.Steps.Count - 1, recipe.Steps.Count, live, RecipeRunPhase.MacroMissing));
                return;
            }

            var assignments = live.Select(a => new Assignment(a, terminalMacro)).ToList();

            Emit(new RecipePhaseEvent(
                terminal.Iteration == StepIteration.Loop ? "Looping" : "Keep-alive",
                recipe.Steps.Count - 1, recipe.Steps.Count, live,
                terminal.Iteration == StepIteration.Loop ? RecipeRunPhase.Looping : RecipeRunPhase.KeepAlive));

            await _runLoop(assignments, ct).ConfigureAwait(false); // runs until cancelled
            Emit(new RecipePhaseEvent("Done", recipe.Steps.Count - 1, recipe.Steps.Count, live, RecipeRunPhase.Done));
        }
        catch (OperationCanceledException) { /* aborted — expected */ }
        finally
        {
            cts.Dispose();
            _activeCts = null;
        }
    }

    public bool Abort()
    {
        var cts = _activeCts;
        if (cts is null) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return true;
    }

    private void Emit(RecipePhaseEvent e)
    {
        try { Progress?.Invoke(this, e); } catch { /* subscriber bugs don't kill the run */ }
    }
}
