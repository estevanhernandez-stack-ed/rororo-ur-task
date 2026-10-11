using Labs626.UrTask.Macros;

namespace Labs626.UrTask.UI;

/// <summary>
/// A collapsible group header in the macro library's flat row list. The list
/// mixes these with <see cref="Macro"/> rows (the existing card template) and
/// <see cref="ArchivedMacroRow"/>s; each type gets its own DataTemplate.
/// </summary>
public sealed class MacroGroupHeader
{
    public MacroGroupHeader(string name, string key, int count, bool isCollapsed, bool isArchive = false)
    {
        Name = name;
        Key = key;
        Count = count;
        IsCollapsed = isCollapsed;
        IsArchive = isArchive;
    }

    public string Name { get; }

    /// <summary>The key collapse state is saved under in ui-prefs.json.</summary>
    public string Key { get; }

    public int Count { get; }
    public bool IsCollapsed { get; }
    public bool IsArchive { get; }

    /// <summary>Chevron: right when collapsed, down when open. Geometric shapes, not emoji.</summary>
    public string Chevron => IsCollapsed ? "▸" : "▾";
}

/// <summary>An archived macro, shown greyed out under "Show archived" with a Restore action.</summary>
public sealed class ArchivedMacroRow
{
    public ArchivedMacroRow(Macro macro) => Macro = macro;

    public Macro Macro { get; }
    public string Name => string.IsNullOrWhiteSpace(Macro.Name) ? "(unnamed)" : Macro.Name!;
}
