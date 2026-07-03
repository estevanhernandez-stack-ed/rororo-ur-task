namespace Labs626.UrTask.Diagnostics;

/// <summary>
/// Affirmative evidence for exception-free startup hangs (the #20 class: a
/// synchronous spin inside the OnStartup ctor chain hangs the process
/// windowless with zero exceptions at process scope — exception handlers
/// never fire). A background thread sleeps past the threshold and, until
/// MarkComplete() is called, writes a WATCHDOG line every repeat interval so
/// the log self-reports "hung after last breadcrumb" instead of just
/// stopping. Background thread — dies with the process, nothing to dispose.
/// </summary>
internal sealed class StartupWatchdog
{
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _repeat;
    private volatile bool _complete;

    public StartupWatchdog(TimeSpan? threshold = null, TimeSpan? repeat = null)
    {
        _threshold = threshold ?? TimeSpan.FromSeconds(30);
        _repeat = repeat ?? TimeSpan.FromMinutes(1);
        new Thread(Run) { IsBackground = true, Name = "RoRoRoUrTask-StartupWatchdog" }.Start();
    }

    /// <summary>Call at the end of App.OnStartup. Silences the watchdog.</summary>
    public void MarkComplete() => _complete = true;

    private void Run()
    {
        Thread.Sleep(_threshold);
        while (!_complete)
        {
            DiagLog.Write(
                $"WATCHDOG: startup not complete after {_threshold.TotalSeconds:0}s — hung after last breadcrumb above");
            Thread.Sleep(_repeat);
        }
    }
}
