using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Labs626.UrTask.Tests;

/// <summary>
/// The shipped <c>manifest.json</c> and the csproj declare this plugin's version independently, and
/// on 2026-08-11 they were found to have drifted: the manifest said <c>0.7.0</c> while the built
/// assembly reported <c>0.6.0.0</c>.
/// <para>
/// That matters more here than in a normal app. RoRoRo's installer reads the manifest to decide
/// what is installed and whether an update is newer, while anything looking at the binary — a
/// crash report, Task Manager, a support question — sees the assembly. Two answers to "what
/// version is this" is a debugging tax paid by whoever is furthest from the code.
/// </para>
/// </summary>
public class VersionConsistencyTests
{
    [Fact]
    public void ManifestVersion_MatchesTheAssemblyVersion()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        Assert.True(File.Exists(manifestPath),
            $"manifest.json was not copied next to the test binary (looked in {AppContext.BaseDirectory}). "
            + "Without it this test would silently measure nothing.");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestVersion = doc.RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(manifestVersion), "manifest.json has no version.");

        var asm = typeof(Labs626.UrTask.Theming.HostThemeReader).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var assemblyVersion = informational?.Split('+')[0];

        Assert.Equal(manifestVersion, assemblyVersion);
    }

    /// <summary>
    /// minHostVersion is a real gate, not documentation: RoRoRo's PluginInstaller refuses to install
    /// a plugin whose declared minimum exceeds the running host. A malformed value throws there, so
    /// it is worth catching in this repo rather than in someone's install dialog.
    /// </summary>
    [Fact]
    public void MinHostVersion_IsAParseableVersion()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));

        var raw = doc.RootElement.GetProperty("minHostVersion").GetString();
        Assert.True(Version.TryParse(raw, out _), $"minHostVersion '{raw}' is not a parseable version.");
    }
}
