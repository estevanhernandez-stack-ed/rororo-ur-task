using System.Windows;
using System.Windows.Threading;
using Labs626.UrTask.Diagnostics;
using Labs626.UrTask.Theming;
using Labs626.UrTask.UI;

namespace Labs626.UrTask;

public partial class App : Application
{
    /// <summary>
    /// A VERBATIM literal on purpose, and no interpolation at all: the host's author guide
    /// records that in an ordinary interpolated string <c>\r</c> is a carriage return, so a name
    /// built as <c>$"Local\rororo-…"</c> compiles, never collides, and therefore never guards
    /// anything. Keyed on <see cref="PluginRuntime.PluginId"/> so two DIFFERENT plugins never
    /// collide; pinned by a test that asserts the two stay in agreement and the name carries no
    /// control characters.
    /// </summary>
    internal const string SingleInstanceMutexName = @"Local\rororo-plugin-626labs.ur-task";

    private PluginRuntime? _runtime;
    private RecorderWindow? _window;
    private TrayService? _tray;
    private HostThemeService? _theme;
    private StartupWatchdog? _watchdog;
    private Mutex? _singleInstance;

    /// <summary>False in a second copy — it must not touch the shared log, even on the way out.</summary>
    private bool _isFirstInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Run one copy — before ANYTHING else takes a global resource. This app shipped without
        // it and produced the bug nobody could diagnose from the symptom: a manual launch raced
        // the host's autostart, the loser could not register Ctrl+Shift+R, showed its full window
        // and reported "Not connected to RoRoRo" with 0 macros — indistinguishable from a genuine
        // host failure, so the user debugged the wrong end. Hotkeys, the action-bridge pipe, and
        // DiagLog's single file are all first-come-first-served; the guard has to precede every
        // one of them, which is why it even precedes the evidence layer — a second copy writing
        // the shared log IS one of the races.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            // Exit quietly, per the host's author guide: the host is fine and the first copy owns
            // everything. Opening a window or reporting an error here sends the user to debug the
            // wrong end; writing the shared log file is the race itself.
            Shutdown(0);
            return;
        }

        _isFirstInstance = true;

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
        // The host is the source of truth for colour from here on. Start() above painted the
        // fallback so windows have brushes before they render; this takes over the moment the
        // connection lands, and keeps up with every switch after.
        _runtime.ThemeChanged += p => _theme?.Apply(p);
        DiagLog.Write("startup: view model");
        var vm = new RecorderViewModel(_runtime);
        DiagLog.Write("startup: window");
        _window = new RecorderWindow { DataContext = vm };
        _window.RecipesRequested = () => _runtime.OpenRecipesLibrary(_window);
        DiagLog.Write("startup: tray");
        _tray = new TrayService(_window, _runtime);
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
        try { _tray?.Dispose(); } catch { }
        try
        {
            _runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // best-effort shutdown — don't let cleanup throw on exit
        }
        // Absence of this line at the end of a session = crash or hang, not exit. A losing second
        // copy skips it: the log file belongs to the first instance, exit included.
        if (_isFirstInstance)
        {
            DiagLog.Write($"exiting cleanly (code {e.ApplicationExitCode})");
        }

        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
