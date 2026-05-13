namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure helper for assignment mutations. The pairing model is one-to-many:
/// each ALT pairs with at most one macro, but each MACRO can be paired with
/// multiple alts (the same macro running across the round-robin on alt 1,
/// alt 2, alt 3 simultaneously). Extracted so the rule is testable without
/// spinning up gRPC + HotkeyService side-effects.
/// </summary>
internal static class AssignmentMap
{
    /// <summary>
    /// Apply an assignment to the map. Mutates <paramref name="assignments"/> in place.
    /// </summary>
    /// <remarks>
    /// Behavior:
    /// <list type="bullet">
    /// <item><paramref name="macro"/> is null → clear this alt's slot (alt → keep-alive Space).</item>
    /// <item><paramref name="macro"/> is non-null → set the alt's slot to this macro.
    /// Other alts holding the same macro are NOT affected — the same macro can be
    /// paired with multiple alts.</item>
    /// <item>If the same alt already holds a different macro, the prior macro is
    /// simply overwritten on this alt (the macro itself remains paired with any
    /// other alts that hold it).</item>
    /// </list>
    /// </remarks>
    public static void ApplyAssignment(
        IDictionary<int, Macro?> assignments,
        int altPid,
        Macro? macro)
    {
        if (assignments is null) throw new ArgumentNullException(nameof(assignments));

        if (macro is null)
        {
            assignments.Remove(altPid);
            return;
        }

        assignments[altPid] = macro;
    }
}
