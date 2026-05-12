namespace Labs626.UrTask.Macros;

/// <summary>
/// Pure helper for assignment mutations. Enforces the 1:1 rule — each macro
/// pairs with at most one alt; each alt pairs with at most one macro.
/// Extracted from PluginRuntime so the rule is testable without spinning up
/// gRPC + HotkeyService side-effects.
/// </summary>
internal static class AssignmentMap
{
    /// <summary>
    /// Apply an assignment to the map. Mutates <paramref name="assignments"/> in place.
    /// Returns the list of OTHER alt PIDs that lost their pairing as a result
    /// (caller should fire AssignmentChanged events for each).
    /// </summary>
    /// <remarks>
    /// Behavior:
    /// <list type="bullet">
    /// <item><paramref name="macro"/> is null → clear this alt's slot (alt → keep-alive Space). No displacement.</item>
    /// <item><paramref name="macro"/> is non-null → assign macro to this alt, AND clear any other alt
    /// that was holding the same macro (matched by <c>Macro.Id</c>). Displaced alts are returned.</item>
    /// <item>If the same alt already holds the same macro, this is a no-op (still returns empty list).</item>
    /// </list>
    /// </remarks>
    public static IReadOnlyList<int> ApplyAssignment(
        IDictionary<int, Macro?> assignments,
        int altPid,
        Macro? macro)
    {
        if (assignments is null) throw new ArgumentNullException(nameof(assignments));

        if (macro is null)
        {
            assignments.Remove(altPid);
            return Array.Empty<int>();
        }

        var displaced = new List<int>();
        // Snapshot keys first — can't mutate dict while enumerating.
        var displacedKeys = assignments
            .Where(kv => kv.Key != altPid && kv.Value?.Id == macro.Id)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var otherPid in displacedKeys)
        {
            assignments.Remove(otherPid);
            displaced.Add(otherPid);
        }

        assignments[altPid] = macro;
        return displaced;
    }
}
