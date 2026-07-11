namespace Labs626.UrTask.Macros;

/// <summary>How a recipe step is played across the selected alts.</summary>
public enum StepIteration
{
    RunOnce,    // play once per alt (position); the step completes when all are done — the barrier
    Loop,       // round-robin the macro across alts forever (AssignmentRunner)
    KeepAlive,  // no macro; round-robin a Space keep-alive forever
    /// <summary>terminal marker — run the prior position steps once, then stop (a loadout).</summary>
    Done,
}

/// <summary>One step of a recipe: a macro (by id) and how it iterates.
/// KeepAlive carries no macro.</summary>
public sealed record RecipeStep(string? MacroId, StepIteration Iteration);

/// <summary>
/// An ordered position→loop routine run against a selected alt set. Every step
/// but the last is <see cref="StepIteration.RunOnce"/> (position); the last
/// (terminal) step is <see cref="StepIteration.Loop"/>, <see cref="StepIteration.KeepAlive"/>
/// (the sustained state), or <see cref="StepIteration.Done"/> (a loadout — run once, then
/// stop). Macros are referenced by id and resolved against <see cref="MacroStore"/> at run time.
/// </summary>
public sealed record Recipe(
    int SchemaVersion,
    string Id,
    string? Name,
    IReadOnlyList<RecipeStep> Steps,
    long RecordedAtUnixMs,
    long? RecordedPlaceId = null,
    string? RecordedGameName = null)
{
    public const int CurrentSchemaVersion = 1;

    public RecipeStep Terminal => Steps[^1];
    public IEnumerable<RecipeStep> PositionSteps => Steps.Take(Steps.Count - 1);

    /// <summary>True when the terminal step is <see cref="StepIteration.Done"/> — a
    /// loadout: position steps run once, then the routine stops (no loop, no keep-alive).</summary>
    public bool IsLoadout => Terminal.Iteration == StepIteration.Done;

    /// <summary>Enforce the shape: non-empty; all-but-last RunOnce with a macro;
    /// last is Loop (with a macro), KeepAlive (macro optional), or Done (no macro —
    /// a loadout, which needs at least one position step before it).</summary>
    public static (bool ok, string? error) ValidateSteps(IReadOnlyList<RecipeStep> steps)
    {
        if (steps is null || steps.Count == 0) return (false, "Add a position step, then set a loop or keep-alive.");

        for (int i = 0; i < steps.Count - 1; i++)
        {
            if (steps[i].Iteration != StepIteration.RunOnce)
                return (false, $"Step {i + 1} must be a run-once position step.");
            if (string.IsNullOrEmpty(steps[i].MacroId))
                return (false, $"Position step {i + 1} needs a macro.");
        }

        var last = steps[^1];
        if (last.Iteration == StepIteration.RunOnce)
            return (false, "The last step must be a loop or keep-alive.");
        if (last.Iteration == StepIteration.Loop && string.IsNullOrEmpty(last.MacroId))
            return (false, "The loop step needs a macro.");
        if (last.Iteration == StepIteration.Done && steps.Count < 2)
            return (false, "A loadout needs at least one step to run.");
        return (true, null);
    }
}
