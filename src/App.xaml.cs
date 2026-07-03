using System.Windows;
using System.Windows.Threading;
using Labs626.UrTask.Diagnostics;
using Labs626.UrTask.Theming;
using Labs626.UrTask.UI;

namespace Labs626.UrTask;

public partial class App : Application
{
    private PluginRuntime? _runtime;
    private RecorderWindow? _window;
    private TrayService? _tray;
    private HostThemeService? _theme;
    private StartupWatchdog? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Evidence layer first — handlers, session header, and watchdog exist
        // before any construction step can crash or hang (2026-07-03 spec).
        RegisterExceptionEvidence();

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?";
        DiagLog.Write($"=== RoRoRo Ur Task v{version} starting — pid {Environment.ProcessId}, " +
                      $"{Environment.OSVersion.VersionString}, .NET {Environment.Version} ===");
        _watchdog = new StartupWatchdog();

        // Manual-verify hook (spec §7). Checked only when the variable is set.
        var testCrash = Environment.GetEnvironmentVariable("URTASK_TEST_CRASH");
        if (testCrash == "hang")
        {
            DiagLog.Write("URTASK_TEST_CRASH=hang — blocking OnStartup deliberately");
            Thread.Sleep(Timeout.Infinite); // windowless hang; the watchdog reports it
        }

        // Sync brushes to the RoRoRo host's active theme before any window
        // resolves resources, then keep following theme switches live.
        DiagLog.Write("startup: theme sync");
        _theme = new HostThemeService();
        _theme.Start();

        DiagLog.Write("startup: runtime");
        _runtime = new PluginRuntime();
        DiagLog.Write("startup: view model");
        var vm = new RecorderViewModel(_runtime);
        DiagLog.Write("startup: window");
        _window = new RecorderWindow { DataContext = vm };
        DiagLog.Write("startup: tray");
        _tray = new TrayService(_window);
        _runtime.StateChanged += () => _tray.UpdateState(_runtime.State);

        DiagLog.Write("startup: window shown");
        _window.Show();

        // Connect to RoRoRo after the window is visible. If RoRoRo isn't running,
        // the UI surfaces the failure rather than blocking startup on it.
        DiagLog.Write("startup: StartAsync dispatched");
        _ = _runtime.StartAsync();

        if (testCrash == "dispatcher")
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
                throw new InvalidOperationException("URTASK_TEST_CRASH=dispatcher — deliberate test crash");
            timer.Start();
        }

        _watchdog.MarkComplete();
    }

    /// <summary>
    /// Log-then-crash-loud evidence handlers (host philosophy: silent crash is
    /// worse than loud crash — never set Handled, just leave a trace). Handlers
    /// alone can't see liveness bugs (#20) — that's the StartupWatchdog's job.
    /// </summary>
    private void RegisterExceptionEvidence()
    {
        DispatcherUnhandledException += (_, args) =>
            DiagLog.Write($"FATAL (dispatcher): {args.Exception}");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagLog.Write($"FATAL (appdomain, terminating={args.IsTerminating}): {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagLog.Write($"UNOBSERVED task exception: {args.Exception}");
            args.SetObserved(); // behavior-preserving; evidence only
        };
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
        // Absence of this line at the end of a session = crash or hang, not exit.
        DiagLog.Write($"exiting cleanly (code {e.ApplicationExitCode})");
        base.OnExit(e);
    }
}
