using System.IO;
using Labs626.UrTask.Diagnostics;

namespace Labs626.UrTask.Tests;

[Collection("DiagLog")]
public class StartupWatchdogTests : IDisposable
{
    private readonly string _dir;

    public StartupWatchdogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "urtask-watchdog-" + Guid.NewGuid().ToString("N"));
        DiagLog.Directory = _dir;
        DiagLog.ResetForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IncompleteStartup_WritesWatchdogLine()
    {
        var watchdog = new StartupWatchdog(
            threshold: TimeSpan.FromMilliseconds(50),
            repeat: TimeSpan.FromMilliseconds(50));
        try
        {
            // Poll with a generous deadline — thread scheduling, not logic,
            // decides exactly when the first line lands.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(DiagLog.CurrentLogPath) &&
                    File.ReadAllText(DiagLog.CurrentLogPath).Contains("WATCHDOG:"))
                    return; // pass
                Thread.Sleep(20);
            }
            Assert.Fail("watchdog line never appeared");
        }
        finally
        {
            watchdog.MarkComplete(); // stop the thread before the next test repoints DiagLog
            watchdog.JoinForTests(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void CompletedStartup_StaysSilent()
    {
        var watchdog = new StartupWatchdog(
            threshold: TimeSpan.FromMilliseconds(50),
            repeat: TimeSpan.FromMilliseconds(50));
        watchdog.MarkComplete();
        watchdog.JoinForTests(TimeSpan.FromSeconds(5));

        Thread.Sleep(300); // several threshold+repeat periods

        Assert.False(
            File.Exists(DiagLog.CurrentLogPath) &&
            File.ReadAllText(DiagLog.CurrentLogPath).Contains("WATCHDOG:"),
            "watchdog fired after MarkComplete");
    }
}
