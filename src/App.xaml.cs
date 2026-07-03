using System.Windows;
using Labs626.UrTask.Theming;
using Labs626.UrTask.UI;

namespace Labs626.UrTask;

public partial class App : Application
{
    private PluginRuntime? _runtime;
    private RecorderWindow? _window;
    private TrayService? _tray;
    private HostThemeService? _theme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Sync brushes to the RoRoRo host's active theme before any window
        // resolves resources, then keep following theme switches live.
        _theme = new HostThemeService();
        _theme.Start();

        _runtime = new PluginRuntime();
        var vm = new RecorderViewModel(_runtime);
        _window = new RecorderWindow { DataContext = vm };

        _tray = new TrayService(_window);
        _runtime.StateChanged += () => _tray.UpdateState(_runtime.State);

        _window.Show();

        // Connect to RoRoRo after the window is visible. If RoRoRo isn't running,
        // the UI surfaces the failure rather than blocking startup on it.
        _ = _runtime.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _theme?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try
        {
            _runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // best-effort shutdown — don't let cleanup throw on exit
        }
        base.OnExit(e);
    }
}
