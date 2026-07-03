using System;
using System.IO;
using Labs626.UrTask.Theming;

namespace Labs626.UrTask.Tests;

public class HostThemeReaderTests
{
    // Host settings.json shape (camelCase, extra fields present).
    private const string SettingsJson = """
    {
      "version": 1,
      "defaultPlaceUrl": "https://www.roblox.com/games/606849621",
      "launchMainOnStartup": false,
      "activeThemeId": "midnight"
    }
    """;

    // Host user-theme file shape (snake_case, comments + trailing commas allowed).
    private const string UserThemeJson = """
    {
      // hand-rolled theme
      "name": "Lava",
      "bg": "#200A0A",
      "cyan": "#FF9430",
      "magenta": "#FF3355",
      "white": "#FFF4E9",
      "muted_text": "#B09A91",
      "divider": "#321616",
      "row_bg": "#2B0F0F",
      "row_expired_bg": "#3A2D14",
      "row_expired_accent": "#F1B232",
      "navy": "#200A0A",
    }
    """;

    [Fact]
    public void ReadActiveThemeId_ParsesCamelCaseSettings()
    {
        Assert.Equal("midnight", HostThemeReader.ReadActiveThemeId(SettingsJson));
    }

    [Fact]
    public void ReadActiveThemeId_MissingField_ReturnsNull()
    {
        Assert.Null(HostThemeReader.ReadActiveThemeId("""{ "version": 1 }"""));
        Assert.Null(HostThemeReader.ReadActiveThemeId("not json"));
    }

    [Fact]
    public void ParseThemeFile_ReadsSnakeCaseSlots()
    {
        var palette = HostThemeReader.ParseThemeFile("lava", UserThemeJson);

        Assert.NotNull(palette);
        Assert.Equal("lava", palette!.Id);
        Assert.Equal("#200A0A", palette.Bg);
        Assert.Equal("#FF9430", palette.Cyan);
        Assert.Equal("#B09A91", palette.MutedText);
        Assert.Equal("#2B0F0F", palette.RowBg);
    }

    [Fact]
    public void ParseThemeFile_MissingSlot_ReturnsNull()
    {
        Assert.Null(HostThemeReader.ParseThemeFile("x", """{ "bg": "#000000" }"""));
        Assert.Null(HostThemeReader.ParseThemeFile("x", "not json"));
    }

    [Fact]
    public void ResolveActive_BuiltInId_ReturnsMirroredPalette()
    {
        var dir = MakeHostDir(activeThemeId: "midnight");
        try
        {
            var palette = HostThemeReader.ResolveActive(dir);
            Assert.Equal("midnight", palette.Id);
            Assert.Equal("#0A1320", palette.Bg);
            Assert.Equal("#3FB8D9", palette.Cyan);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveActive_UserThemeFile_Wins()
    {
        var dir = MakeHostDir(activeThemeId: "lava");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "themes"));
            File.WriteAllText(Path.Combine(dir, "themes", "lava.json"), UserThemeJson);

            var palette = HostThemeReader.ResolveActive(dir);
            Assert.Equal("lava", palette.Id);
            Assert.Equal("#200A0A", palette.Bg);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveActive_UnknownIdOrMissingSettings_FallsBackToBrand()
    {
        var noSettings = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(noSettings);
        var unknownId = MakeHostDir(activeThemeId: "ghost-theme");
        try
        {
            Assert.Equal("brand", HostThemeReader.ResolveActive(noSettings).Id);
            Assert.Equal("brand", HostThemeReader.ResolveActive(unknownId).Id);
            Assert.Equal("brand", HostThemeReader.ResolveActive(Path.Combine(noSettings, "does-not-exist")).Id);
        }
        finally
        {
            Directory.Delete(noSettings, recursive: true);
            Directory.Delete(unknownId, recursive: true);
        }
    }

    [Fact]
    public void BlendTowards_TintsDeterministically()
    {
        // Brand RowBg toward brand White at 4% — the RowHoverBrush derivation.
        var hover = HostThemeReader.BlendTowards("#15263A", "#FFFFFF", 0.04);
        Assert.Equal("#1E2F42", hover);

        Assert.Null(HostThemeReader.BlendTowards("nope", "#FFFFFF", 0.04));
        Assert.Null(HostThemeReader.BlendTowards("#15263A", "junk", 0.04));
    }

    private static string MakeHostDir(string activeThemeId)
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), $$"""
        { "version": 1, "activeThemeId": "{{activeThemeId}}" }
        """);
        return dir;
    }
}
