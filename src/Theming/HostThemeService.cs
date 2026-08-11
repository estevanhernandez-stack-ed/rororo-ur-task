using System.Windows;
using System.Windows.Media;
using ROROROblox.PluginContract;

namespace Labs626.UrTask.Theming;

/// <summary>
/// Keeps Ur Task's application brushes in lockstep with the RoRoRo host's active theme.
/// <para>
/// <b>The host tells us; we no longer go looking.</b> This used to read RoRoRo's <c>settings.json</c>
/// for an active theme id, match it against a hand-copied table of built-in palettes, and otherwise
/// load <c>themes\&lt;id&gt;.json</c> off disk — watching the whole folder with a
/// <c>FileSystemWatcher</c> to notice changes. That worked for user themes and could never work for
/// built-in themes, which live in RoRoRo's code and never touch the disk: flatline, added after the
/// mirror was written, silently fell through to Brand. F-091.
/// </para>
/// <para>
/// Now <see cref="Start"/> paints the fallback so windows have brushes before they render, and
/// <see cref="Apply(ThemePalette)"/> is fed by <c>PluginClient</c> — once on connect, then on every
/// theme switch. No watcher, no file reads, no knowledge of where RoRoRo keeps anything.
/// </para>
/// <para>
/// Apply strategy is unchanged and still load-bearing: REPLACE the brush instance in
/// <c>Application.Current.Resources</c>. All XAML consumers reference the eight brush keys via
/// <c>{DynamicResource}</c>, which re-binds on dictionary entry replacement. Mutating the existing
/// brush's Color was tried first and does not propagate — StaticResource consumers capture instances
/// at parse time and BAML-loaded brushes can come back frozen. Verified empirically on v0.5.
/// </para>
/// </summary>
internal sealed class HostThemeService
{
    private const double HoverTintStrength = 0.04;

    /// <summary>
    /// Paint the fallback. Called on the UI thread at startup, before any window resolves
    /// resources and long before the host connection exists — Ur Task is fully usable standalone,
    /// so "no host" is a supported state rather than an error path.
    /// </summary>
    public void Start() => Apply(HostThemeReader.Brand);

    /// <summary>
    /// Apply a palette pushed by the host. Safe to call from the gRPC consumer thread; marshals to
    /// the UI thread itself.
    /// </summary>
    public void Apply(ThemePalette palette)
    {
        if (palette is null) return;
        Apply(new HostThemePalette(
            Bg: palette.Bg,
            Cyan: palette.Cyan,
            Magenta: palette.Magenta,
            White: palette.White,
            MutedText: palette.MutedText,
            Divider: palette.Divider,
            RowBg: palette.RowBg));
    }

    private void Apply(HostThemePalette palette)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ApplyOnUiThread(palette));
            return;
        }
        ApplyOnUiThread(palette);
    }

    private static void ApplyOnUiThread(HostThemePalette palette)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        ApplySlot(resources, "BgBrush", palette.Bg);
        ApplySlot(resources, "CyanBrush", palette.Cyan);
        ApplySlot(resources, "MagentaBrush", palette.Magenta);
        ApplySlot(resources, "WhiteBrush", palette.White);
        ApplySlot(resources, "MutedTextBrush", palette.MutedText);
        ApplySlot(resources, "DividerBrush", palette.Divider);
        ApplySlot(resources, "RowBgBrush", palette.RowBg);

        var hover = HostThemeReader.BlendTowards(palette.RowBg, palette.White, HoverTintStrength);
        if (hover is not null)
        {
            ApplySlot(resources, "RowHoverBrush", hover);
        }
    }

    private static void ApplySlot(ResourceDictionary resources, string key, string hex)
    {
        if (!TryParseColor(hex, out var color))
        {
            // Keep the current brush rather than painting black. The host validates themes before
            // applying them, so this only fires on something genuinely malformed upstream.
            return;
        }

        // Replacement, not mutation — DynamicResource subscribers re-bind when the dictionary
        // entry changes. Freeze the new brush: it's shared across the UI and never mutated after.
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrEmpty(hex)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch (FormatException)
        {
        }
        return false;
    }
}
