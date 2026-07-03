using System.IO;
using System.Text.Json;

namespace Labs626.UrTask.Theming;

/// <summary>
/// The slice of a RoRoRo theme that Ur Task consumes — the seven brush slots the
/// plugin's XAML references plus the theme id. RowExpired*/Navy host slots are
/// dropped (no Ur Task surface uses them); RowHoverBrush is derived at apply
/// time by tinting RowBg toward White.
/// </summary>
public sealed record HostThemePalette(
    string Id,
    string Bg,
    string Cyan,
    string Magenta,
    string White,
    string MutedText,
    string Divider,
    string RowBg);

/// <summary>
/// Pure disk-shape reader for the host app's theming state. RoRoRo persists the
/// active theme id in <c>%LOCALAPPDATA%\ROROROblox\settings.json</c> (camelCase)
/// and user themes as snake_case JSON files in <c>...\ROROROblox\themes\</c>;
/// built-in themes live in host code, so their palettes are mirrored here.
/// Mirror-drift risk: if RoRoRo's built-ins change, these copies need a matching
/// bump — the slot values come from ROROROblox.Core ThemeStore.BuildBuiltIns.
/// Every failure path falls back to Brand: a malformed host file must never
/// break the plugin.
/// </summary>
public static class HostThemeReader
{
    // Host settings.json is written with JsonNamingPolicy.CamelCase.
    private const string ActiveThemeIdProperty = "activeThemeId";

    // Host theme files are written with JsonNamingPolicy.SnakeCaseLower and
    // tolerate comments + trailing commas (ThemeStore's reader options).
    private static readonly JsonDocumentOptions ThemeFileOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The host's default theme — also Ur Task's XAML fallback palette.</summary>
    public static readonly HostThemePalette Brand = new(
        Id: "brand",
        Bg: "#0F1F31", Cyan: "#17D4FA", Magenta: "#F22F89", White: "#FFFFFF",
        MutedText: "#9AA8B8", Divider: "#1F3149", RowBg: "#15263A");

    private static readonly HostThemePalette Midnight = new(
        Id: "midnight",
        Bg: "#0A1320", Cyan: "#3FB8D9", Magenta: "#C0407E", White: "#E6EDF5",
        MutedText: "#6F7E92", Divider: "#162232", RowBg: "#0F1B2B");

    private static readonly HostThemePalette MagentaHeat = new(
        Id: "magenta-heat",
        Bg: "#1A0F1F", Cyan: "#F22F89", Magenta: "#F22F89", White: "#FFE9F4",
        MutedText: "#B091A2", Divider: "#2D1832", RowBg: "#241432");

    private static readonly HostThemePalette[] BuiltIns = { Brand, Midnight, MagentaHeat };

    public static string DefaultHostFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ROROROblox");

    /// <summary>Extract the active theme id from host settings.json content; null when absent.</summary>
    public static string? ReadActiveThemeId(string settingsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(ActiveThemeIdProperty, out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// Parse a host user-theme file (snake_case slots). Returns null when the
    /// JSON is malformed or any Ur Task-consumed slot is missing — mirroring the
    /// host's drop-don't-throw posture for user themes.
    /// </summary>
    public static HostThemePalette? ParseThemeFile(string id, string themeJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(themeJson, ThemeFileOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Slot(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            var bg = Slot("bg");
            var cyan = Slot("cyan");
            var magenta = Slot("magenta");
            var white = Slot("white");
            var mutedText = Slot("muted_text");
            var divider = Slot("divider");
            var rowBg = Slot("row_bg");

            if (bg is null || cyan is null || magenta is null || white is null
                || mutedText is null || divider is null || rowBg is null)
            {
                return null;
            }

            return new HostThemePalette(id, bg, cyan, magenta, white, mutedText, divider, rowBg);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve the host's currently active palette from disk: read the saved id
    /// from <paramref name="hostFolder"/>/settings.json, match a mirrored
    /// built-in, else load <c>themes\&lt;id&gt;.json</c>. Brand on any miss.
    /// </summary>
    public static HostThemePalette ResolveActive(string hostFolder)
    {
        string? id = null;
        try
        {
            var settingsPath = Path.Combine(hostFolder, "settings.json");
            if (File.Exists(settingsPath))
            {
                id = ReadActiveThemeId(File.ReadAllText(settingsPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Brand;
        }

        if (id is null) return Brand;

        foreach (var builtIn in BuiltIns)
        {
            if (string.Equals(builtIn.Id, id, StringComparison.OrdinalIgnoreCase))
                return builtIn;
        }

        try
        {
            var themePath = Path.Combine(hostFolder, "themes", id + ".json");
            if (File.Exists(themePath))
            {
                return ParseThemeFile(id, File.ReadAllText(themePath)) ?? Brand;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return Brand;
    }

    /// <summary>
    /// Blend <paramref name="baseHex"/> toward <paramref name="towardHex"/> by
    /// <paramref name="t"/> (0..1). Used to derive RowHoverBrush from RowBg +
    /// White so hover tinting stays theme-aware. Returns #RRGGBB; null when
    /// either input isn't a parseable #RRGGBB hex.
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
