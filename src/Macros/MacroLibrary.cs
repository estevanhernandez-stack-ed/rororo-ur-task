namespace Labs626.UrTask.Macros;

/// <summary>
/// Live search for the macro library. Pure and WPF-free, next to
/// <see cref="MacroGameFilter"/>; the view model only binds to it.
/// </summary>
public static class MacroSearch
{
    /// <summary>
    /// Every whitespace-separated word of <paramref name="query"/> has to appear,
    /// case-insensitively and in any order, in the macro's name, recorded game
    /// name or <c>[account]</c> tag. A blank query passes everything.
    /// </summary>
    public static bool Matches(Macro macro, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var haystack = string.Join(' ',
            macro.Name ?? string.Empty,
            macro.RecordedGameName ?? string.Empty,
            MacroName.Parse(macro.Name).Account ?? string.Empty);

        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!haystack.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static IReadOnlyList<Macro> Filter(IEnumerable<Macro> macros, string? query)
        => string.IsNullOrWhiteSpace(query)
            ? macros.ToList()
            : macros.Where(m => Matches(m, query)).ToList();
}

/// <summary>One library group: the WHERE part of the names, and its macros in display order.</summary>
public sealed record MacroGroup(string Name, IReadOnlyList<Macro> Macros);

/// <summary>
/// Library ordering, built from the naming convention (see <see cref="MacroName"/>).
/// Groups run alphabetically with "Ungrouped" last. Inside a group, route steps
/// come first (by route, then step number, so <c>booth 1/3, 2/3, 3/3</c> read in run
/// order), then the standalone macros by name. Recency plays no part.
/// </summary>
public static class MacroLibrary
{
    public static IReadOnlyList<MacroGroup> Group(IEnumerable<Macro> macros)
    {
        var parsed = macros.Select(m => (Macro: m, Parts: MacroName.Parse(m.Name)));

        return parsed
            .GroupBy(x => x.Parts.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => string.Equals(g.Key, MacroName.UngroupedGroup, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MacroGroup(
                g.Key,
                g.OrderBy(x => x.Parts.HasRoute ? 0 : 1)
                 .ThenBy(x => x.Parts.Route ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(x => x.Parts.StepN ?? int.MaxValue)
                 .ThenBy(x => x.Macro.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(x => x.Macro.Id, StringComparer.Ordinal)
                 .Select(x => x.Macro)
                 .ToList()))
            .ToList();
    }
}

/// <summary>Where a macro is in use: alts it is paired with and recipes that reference it.</summary>
public sealed record MacroUsageInfo(IReadOnlyList<string> PairedAlts, IReadOnlyList<string> Recipes)
{
    public bool IsUsed => PairedAlts.Count > 0 || Recipes.Count > 0;
}

public static class MacroUsage
{
    /// <summary>
    /// Find the alts paired with <paramref name="macroId"/> and the recipes that use it.
    /// Both lists come back sorted, recipe names default to "(unnamed)".
    /// </summary>
    public static MacroUsageInfo Find(
        string macroId,
        IEnumerable<(string AltName, string? MacroId)> pairings,
        IEnumerable<Recipe> recipes)
    {
        var alts = pairings
            .Where(p => string.Equals(p.MacroId, macroId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.AltName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usedBy = recipes
            .Where(r => r.Steps.Any(s => string.Equals(s.MacroId, macroId, StringComparison.OrdinalIgnoreCase)))
            .Select(r => string.IsNullOrWhiteSpace(r.Name) ? "(unnamed)" : r.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return new MacroUsageInfo(alts, usedBy);
    }
}
