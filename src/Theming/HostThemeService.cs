using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Labs626.UrTask.Theming;

/// <summary>
/// Keeps Ur Task's application brushes in lockstep with the RoRoRo host's
/// active theme. Reads the host's saved theme from disk at startup (no plugin
/// contract or pipe traffic involved), then watches the host's settings +
/// themes folder so a theme switch in RoRoRo re-paints the plugin live.
///
/// Apply strategy — same as the host's ThemeService: REPLACE the brush
/// instance in Application.Current.Resources. All XAML consumers reference the
/// eight brush keys via {DynamicResource}, which re-binds on dictionary entry
/// replacement. (Mutating the existing brush's Color was tried first and does
/// not propagate: StaticResource consumers capture instances at parse time and
/// BAML-loaded brushes can come back frozen — verified empirically on v0.5.)
/// </summary>
internal sealed class HostThemeService : IDisposable
{
    // Ur Task brush key ← host palette slot. RowHoverBrush is derived, not a host slot.
    private const double HoverTintStrength = 0.04;

    private readonly string _hostFolder;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;

    public HostThemeService() : this(HostThemeReader.DefaultHostFolder()) { }

    public HostThemeService(string hostFolder)
    {
        _hostFolder = hostFolder ?? throw new ArgumentNullException(nameof(hostFolder));
    }

    /// <summary>Apply the host's current theme and start watching for changes. Call on the UI thread.</summary>
    public void Start()
    {
        ApplyCurrent();

        if (!Directory.Exists(_hostFolder))
        {
            // Host not installed (or never ran) — Brand fallback already applied,
            // nothing to watch. The plugin is fully usable standalone.
            return;
        }

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce!.Stop();
            ApplyCurrent();
        };

        try
        {
            // One watcher, subdirectories on: catches settings.json (active id)
            // and themes\*.json (palette edits) in a single subscription.
            _watcher = new FileSystemWatcher(_hostFolder, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            _watcher.Changed += OnHostFileChanged;
            _watcher.Created += OnHostFileChanged;
            _watcher.Renamed += OnHostFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Watching is best-effort; startup apply already happened. The next
            // plugin launch picks up any theme change made while unwatched.
            _watcher = null;
        }
    }

    private void OnHostFileChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher fires on a threadpool thread and fires in bursts
        // (tmp-write + rename per host save) — marshal to the UI thread and
        // debounce so one save produces one re-apply.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_debounce is null) return;
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private void ApplyCurrent()
    {
        var palette = HostThemeReader.ResolveActive(_hostFolder);

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Apply(palette));
            return;
        }
        Apply(palette);
    }

    private static void Apply(HostThemePalette palette)
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
            // Bad hex in a user theme file — keep the current brush rather than
            // painting black. Host builds themes through the same validation,
            // so this only triggers on hand-edited files.
            return;
        }

        // Replacement, not mutation — DynamicResource subscribers re-bind when
        // the dictionary entry changes. Freeze the new brush: it's shared
        // across the UI and never mutated after this point.
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

    public void Dispose()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
        }
        catch (Exception)
        {
            // best-effort teardown
        }
        _watcher = null;
        _debounce?.Stop();
        _debounce = null;
    }
}
