namespace Labs626.UrTask.Macros;

/// <summary>How a recipe step is played across the selected alts.</summary>
public enum StepIteration
{
    RunOnce,    // play once per alt (position); the step completes when all are done — the barrier
    Loop,       // round-robin the macro across alts forever (AssignmentRunner)
    KeepAlive,  // no macro; round-robin a Space keep-alive forever
}

/// <summary>One step of a recipe: a macro (by id) and how it iterates.
/// KeepAlive carries no macro.</summary>
public sealed record RecipeStep(string? MacroId, StepIteration Iteration);

/// <summary>
/// An ordered position→loop routine run against a selected alt set. Every step
/// but the last is <see cref="StepIteration.RunOnce"/> (position); the last
/// (terminal) step is <see cref="StepIteration.Loop"/> or
/// <see cref="StepIteration.KeepAlive"/> (the sustained state). Macros are
/// referenced by id and resolved against <see cref="MacroStore"/> at run time.
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

    /// <summary>Enforce the shape: non-empty; all-but-last RunOnce with a macro;
    /// last is Loop (with a macro) or KeepAlive (macro optional).</summary>
    public static (bool ok, string? error) ValidateSteps(IReadOnlyList<RecipeStep> steps)
    {
        if (steps is null || steps.Count == 0) return (false, "A recipe needs at least a terminal step.");

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
        return (true, null);
    }
}
