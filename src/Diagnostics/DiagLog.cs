using System.IO;

namespace Labs626.UrTask.Diagnostics;

/// <summary>
/// Append-only diagnostics sink under %LOCALAPPDATA%\626Labs\RoRoRoUrTask\logs.
/// Open-append-close per write — no held handle, so users can copy the file
/// while the plugin runs and a crash never truncates it. Rolls at 1 MB into
/// ur-task.1.log (2 MB worst case on disk). Never throws: a failed write is
/// lost and the next one tries again; if the directory itself can't be
/// created, the sink disables itself for the session. Static because it must
/// be callable from App.OnStartup before any object exists and from exception
/// handlers when everything is broken.
/// </summary>
internal static class DiagLog
{
    private const long RollThresholdBytes = 1_000_000;
    private static readonly object Gate = new();
    private static bool _disabled;

    /// <summary>Log directory. Settable so tests repoint at a temp dir.</summary>
    public static string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "626Labs", "RoRoRoUrTask", "logs");

    public static string CurrentLogPath => Path.Combine(Directory, "ur-task.log");
    public static string RolledLogPath => Path.Combine(Directory, "ur-task.1.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_disabled) return;
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                RollIfNeeded();
                File.AppendAllText(CurrentLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                    + "  " + message + Environment.NewLine);
            }
            catch
            {
                // A failed write is lost — the next write tries again. If the
                // directory itself is uncreatable, disable for the session.
                if (!System.IO.Directory.Exists(Directory)) _disabled = true;
            }
        }
    }

    private static void RollIfNeeded()
    {
        var current = new FileInfo(CurrentLogPath);
        if (!current.Exists || current.Length <= RollThresholdBytes) return;
        File.Delete(RolledLogPath); // no-op when absent
        File.Move(CurrentLogPath, RolledLogPath);
    }

    /// <summary>Test seam — clears session-disable after tests repoint Directory.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) { _disabled = false; }
    }
}
