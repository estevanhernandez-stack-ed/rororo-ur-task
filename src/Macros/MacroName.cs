using System.Text.RegularExpressions;

namespace Labs626.UrTask.Macros;

/// <summary>
/// The parts of a macro name written to the library convention
/// <c>&lt;WHERE&gt; | &lt;ROUTE&gt; n/N | &lt;what&gt; [account] (notes)</c>
/// (ps99-ops/macros/NAMING.md). <see cref="Group"/> is never null: names that
/// don't follow the convention land in <see cref="MacroName.UngroupedGroup"/>.
/// </summary>
public sealed record MacroNameParts(
    string Group,
    string? Where,
    string? Route,
    int? StepN,
    int? StepOf,
    string What,
    string? Account)
{
    public bool HasRoute => Route is not null;
}

/// <summary>
/// Pure parser for the naming convention. The library's groups and run order
/// come from here, so the convention needs no schema field: old files, shared
/// bundles and the MCP keep working unchanged.
/// </summary>
public static partial class MacroName
{
    public const string UngroupedGroup = "Ungrouped";

    private const string Separator = " | ";

    public static MacroNameParts Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new MacroNameParts(UngroupedGroup, null, null, null, null, string.Empty, null);

        var segments = name.Split(Separator);
        var where = segments[0].Trim();

        if (segments.Length < 2 || where.Length == 0)
        {
            var whole = name.Trim();
            return new MacroNameParts(UngroupedGroup, null, null, null, null, whole, AccountIn(whole));
        }

        if (segments.Length == 2)
        {
            var what2 = segments[1].Trim();
            return new MacroNameParts(where, where, null, null, null, what2, AccountIn(what2));
        }

        // 3+ segments: the middle one is the route; anything past it belongs to what.
        var routeText = segments[1].Trim();
        var what = string.Join(Separator, segments.Skip(2)).Trim();

        string route = routeText;
        int? stepN = null, stepOf = null;
        var step = StepPattern().Match(routeText);
        if (step.Success)
        {
            route = step.Groups["route"].Value.Trim();
            stepN = int.Parse(step.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            stepOf = int.Parse(step.Groups["of"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return new MacroNameParts(where, where, route, stepN, stepOf, what, AccountIn(what));
    }

    private static string? AccountIn(string text)
    {
        var m = AccountPattern().Match(text);
        if (!m.Success) return null;
        var account = m.Groups[1].Value.Trim();
        return account.Length == 0 ? null : account;
    }

    // First "n/N" preceded by whitespace; "egg 2/6 + 4/6" takes 2/6.
    [GeneratedRegex(@"^(?<route>.*?)\s+(?<n>\d{1,6})/(?<of>\d{1,6})(?!\d)")]
    private static partial Regex StepPattern();

    [GeneratedRegex(@"\[([^\[\]]+)\]")]
    private static partial Regex AccountPattern();
}
