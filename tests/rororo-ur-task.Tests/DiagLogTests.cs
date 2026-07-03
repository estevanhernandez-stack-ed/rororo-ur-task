using System.IO;
using Labs626.UrTask.Diagnostics;

namespace Labs626.UrTask.Tests;

// DiagLog holds static state (Directory + session-disable flag). Every test
// class that touches it joins this collection so xunit serializes them.
[CollectionDefinition("DiagLog")]
public class DiagLogCollection { }

[Collection("DiagLog")]
public class DiagLogTests : IDisposable
{
    private readonly string _dir;

    public DiagLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "urtask-diaglog-" + Guid.NewGuid().ToString("N"));
        DiagLog.Directory = _dir;
        DiagLog.ResetForTests();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Write_CreatesFileWithTimestampPrefixedLine()
    {
        DiagLog.Write("hello diagnostics");

        var lines = File.ReadAllLines(DiagLog.CurrentLogPath);
        var line = Assert.Single(lines);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  hello diagnostics$", line);
    }

    [Fact]
    public void Write_PastThreshold_RollsCurrentToDotOneAndStartsFresh()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(DiagLog.RolledLogPath, "old rolled content");
        File.WriteAllText(DiagLog.CurrentLogPath, new string('x', 1_000_001));

        DiagLog.Write("first line after roll");

        // Previous current file became the new .1; the stale .1 is gone.
        Assert.Equal(new string('x', 1_000_001), File.ReadAllText(DiagLog.RolledLogPath));
        var line = Assert.Single(File.ReadAllLines(DiagLog.CurrentLogPath));
        Assert.EndsWith("first line after roll", line);
    }

    [Fact]
    public void Write_UncreatableDirectory_DisablesForSessionWithoutThrowing()
    {
        // A path nested under a FILE can never be created as a directory.
        var blocker = Path.Combine(Path.GetTempPath(), "urtask-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            DiagLog.Directory = Path.Combine(blocker, "logs");
            DiagLog.Write("lost");            // must not throw; disables the session

            DiagLog.Directory = _dir;         // even a good dir stays dark until reset
            DiagLog.Write("still disabled");

            Assert.False(File.Exists(DiagLog.CurrentLogPath));
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }

    [Fact]
    public void Write_ConcurrentWriters_EveryLineIntact()
    {
        const int threads = 8, perThread = 50;
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++) DiagLog.Write($"t{t} line {i}");
        });

        var lines = File.ReadAllLines(DiagLog.CurrentLogPath);
        Assert.Equal(threads * perThread, lines.Length);
        Assert.All(lines, l =>
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  t\d line \d+$", l));
    }
}
