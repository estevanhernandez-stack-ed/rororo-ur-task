namespace Labs626.UrTask.Theming;

/// <summary>
/// The slice of a RoRoRo palette that Ur Task consumes — the seven brush slots the plugin's XAML
/// references. RoRoRo's expired-row and navy slots are dropped (no Ur Task surface uses them), and
/// RowHoverBrush is derived at apply time by tinting RowBg toward White.
/// </summary>
public sealed record HostThemePalette(
    string Bg,
    string Cyan,
    string Magenta,
    string White,
    string MutedText,
    string Divider,
    string RowBg);

/// <summary>
/// The fallback palette, and the colour maths that goes with it.
/// <para>
/// <b>This class used to read RoRoRo's storage directly</b> — the active theme id out of its
/// <c>settings.json</c>, then the palette out of <c>themes\&lt;id&gt;.json</c>, with a hand-copied
/// mirror of the built-in themes for the ids that were never written to disk at all. It worked for
/// user themes, because those are files. It could never work for built-in themes, because those are
/// records in RoRoRo's own code — so flatline, which shipped after the mirror was written, silently
/// fell through to Brand. That was F-091.
/// </para>
/// <para>
/// The host now sends its resolved palette over the plugin contract (<c>GetTheme</c> plus
/// <c>SubscribeThemeChanged</c>, contract package 0.8.0), so none of that machinery survives. Gone
/// with it: knowledge of RoRoRo's settings filename, its camelCase key, its themes folder layout,
/// its per-file snake_case naming policy, and its reader's comment tolerance. Five internal storage
/// details this plugin had no business knowing, any of which could have changed in a RoRoRo release
/// and quietly turned the window the wrong colour.
/// </para>
/// <para>
/// What is left is the fallback for when there is no host to ask, which is a normal and supported
/// state: Ur Task runs standalone.
/// </para>
/// </summary>
public static class HostThemeReader
{
    /// <summary>
    /// RoRoRo's default theme, and Ur Task's palette when no host is answering — RoRoRo not
    /// running, or a host too old to have the theme feed. Deliberately still hardcoded: this is a
    /// starting colour for a window that has to render before any connection exists, not a mirror
    /// of anything. It has one value and it never needs to track RoRoRo's built-ins again.
    /// </summary>
    public static readonly HostThemePalette Brand = new(
        Bg: "#0F1F31", Cyan: "#17D4FA", Magenta: "#F22F89", White: "#FFFFFF",
        MutedText: "#9AA8B8", Divider: "#1F3149", RowBg: "#15263A");

    /// <summary>
    /// Blend <paramref name="baseHex"/> toward <paramref name="towardHex"/> by
    /// <paramref name="t"/> (0..1). Used to derive RowHoverBrush from RowBg + White so hover
    /// tinting stays theme-aware. Returns #RRGGBB; null when either input isn't a parseable
    /// #RRGGBB hex.
    /// <para>
    /// Survives the feed because hover is <i>derived from</i> the palette rather than carried in
    /// it — the host has no hover slot to send.
    /// </para>
    /// </summary>
    public static string? BlendTowards(string baseHex, string towardHex, double t)
    {
        if (!TryParseRgb(baseHex, out var br, out var bg, out var bb)) return null;
        if (!TryParseRgb(towardHex, out var tr, out var tg, out var tb)) return null;
        t = Math.Clamp(t, 0.0, 1.0);
        var r = (int)Math.Round(br + (tr - br) * t);
        var g = (int)Math.Round(bg + (tg - bg) * t);
        var b = (int)Math.Round(bb + (tb - bb) * t);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static bool TryParseRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#') return false;
        try
        {
            r = Convert.ToInt32(hex.Substring(1, 2), 16);
            g = Convert.ToInt32(hex.Substring(3, 2), 16);
            b = Convert.ToInt32(hex.Substring(5, 2), 16);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
