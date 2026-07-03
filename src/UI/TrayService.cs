using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using Labs626.UrTask.Diagnostics;

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

    public TrayService(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/icon.png")),
            ToolTipText = "RoRoRo Ur Task — idle",
            ContextMenu = BuildMenu(),
        };
        _icon.TrayLeftMouseUp += (_, _) => SurfaceWindow();
        _icon.TrayMouseDoubleClick += (_, _) => SurfaceWindow();
    }

    public void UpdateState(PluginState state)
    {
        var label = state switch
        {
            PluginState.Recording => "recording",
            PluginState.Playing => "playing",
            _ => "idle",
        };
        _icon.ToolTipText = $"RoRoRo Ur Task — {label}";
    }

    public void Dispose() => _icon.Dispose();

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var show = new MenuItem { Header = "Show recorder" };
        show.Click += (_, _) => SurfaceWindow();
        menu.Items.Add(show);

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
