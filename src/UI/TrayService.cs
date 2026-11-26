using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using Labs626.UrTask.Diagnostics;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.UI;

/// <summary>
/// Plugin's own system-tray icon — separate from RoRoRo's tray. Click surfaces
/// the recorder window; right-click context menu offers Show / Quit. Tooltip
/// reflects the current plugin state (Idle / Recording / Playing) so the user
/// can see what's happening without opening the window.
/// </summary>
internal sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Window _window;
    private readonly PluginRuntime _runtime;

    public TrayService(Window window, PluginRuntime runtime)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/icon.png")),
            ToolTipText = TooltipFor(PluginState.Idle, loopRunning: false),
            ContextMenu = BuildMenu(),
        };
        _icon.TrayLeftMouseUp += (_, _) => SurfaceWindow();
        _icon.TrayMouseDoubleClick += (_, _) => SurfaceWindow();

        // The keep-alive loop never changes PluginState (only the macro player does), so follow its own
        // progress events, the same ones the recorder window's PLAY/STOP button follows. They arrive on the UI thread.
        _runtime.AssignmentProgressed += p =>
        {
            _loopRunning = p.Phase != AssignmentPhase.Stopped;
            Refresh();
        };
    }

    private PluginState _state = PluginState.Idle;
    private bool _loopRunning;

    public void UpdateState(PluginState state)
    {
        _state = state;
        Refresh();
    }

    private void Refresh() => _icon.ToolTipText = TooltipFor(_state, _loopRunning);

    /// <summary>Recording and playing win; otherwise say whether keep-alive is on, because "idle" read the same
    /// whether it was running or had been stopped by accident.</summary>
    internal static string TooltipFor(PluginState state, bool loopRunning) => state switch
    {
        PluginState.Recording => "RoRoRo Ur Task: recording.",
        PluginState.Playing => "RoRoRo Ur Task: playing.",
        _ => loopRunning ? "RoRoRo Ur Task: keep-alive is on." : "RoRoRo Ur Task: keep-alive is off.",
    };

    public void Dispose() => _icon.Dispose();

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show recorder" };
        show.Click += (_, _) => SurfaceWindow();
        menu.Items.Add(show);

        var recipes = new MenuItem { Header = "Recipes" };
        recipes.Click += (_, _) => OpenRecipesLibrary();
        menu.Items.Add(recipes);

        var openLogs = new MenuItem { Header = "Open log folder" };
        openLogs.Click += (_, _) => OpenLogFolder();
        menu.Items.Add(openLogs);

        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = "Quit RoRoRo Ur Task" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        return menu;
    }

    private void SurfaceWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Focus();
    }

    /// <summary>
    /// "Recipes" menu item: delegates to <see cref="PluginRuntime.OpenRecipesLibrary"/>,
    /// the shared wiring the main window's RECIPES button also uses.
    /// </summary>
    private void OpenRecipesLibrary() => _runtime.OpenRecipesLibrary(_window);

    private static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(DiagLog.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DiagLog.Directory,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Menu click — nowhere to report; the folder just doesn't open.
        }
    }
}
