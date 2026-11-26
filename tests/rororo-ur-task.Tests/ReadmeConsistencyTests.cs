using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The README repeats facts the manifest owns, and it is the page a non-technical user reads
/// before installing. It went on saying "RoRoRo v1.4.3 or later" from v0.7.0, when the manifest's
/// minHostVersion moved to 1.19.0, until 0.13.1: the README promised an install the host refused.
/// </summary>
public class ReadmeConsistencyTests
{
    [Fact]
    public void InstallSection_NamesTheManifestsMinimumHostVersion()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        Assert.True(File.Exists(manifestPath),
            $"manifest.json was not copied next to the test binary (looked in {AppContext.BaseDirectory}).");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var raw = doc.RootElement.GetProperty("minHostVersion").GetString();
        Assert.True(Version.TryParse(raw, out var min), $"minHostVersion '{raw}' is not a parseable version.");
        var majorMinor = $"{min!.Major}.{min.Minor}";

        // Only the Install section counts: the same number in a changelog note or a capability
        // row would not tell anyone what they need before they install.
        var install = Section(File.ReadAllText(RepoFile("README.md")), "## Install");

        // A whole version token: "1.19" must not be satisfied by "1.190" or "11.19".
        var token = new Regex($@"(?<![\d.])v?{Regex.Escape(majorMinor)}(?!\d)");
        Assert.True(token.IsMatch(install),
            $"README.md's Install section does not mention RoRoRo {majorMinor}, which is manifest.json's "
            + $"minHostVersion ({raw}). Update the README when the minimum host moves.");
    }

    /// <summary>The text from a "## " heading up to the next one.</summary>
    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"README.md has no '{heading}' section. Renamed? Point this test at the new one.");
        var next = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? markdown[start..] : markdown[start..next];
    }

    /// <summary>
    /// Reads from the repo root rather than a copy beside the test binary, so the assertion is on
    /// the file a human edits (the same walk ModalDefaultButtonSafetyTests does).
    /// </summary>
    private static string RepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "rororo-ur-task.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.True(dir is not null,
            "Could not find the repo root (walked up from the test binary looking for rororo-ur-task.csproj).");

        var path = Path.Combine(dir!.FullName, name);
        Assert.True(File.Exists(path), $"{name} not found at {path}.");
        return path;
    }
}
