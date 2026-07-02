using System.IO;
using System.Text.Json;
using Labs626.UrTask.Macros;

namespace Labs626.UrTask.Tests;

public class MacroV1MigrationTests
{
    [Fact]
    public void Migrate_V1Json_ProducesV3MacroWithRecordedAgainstFields()
    {
        var json = File.ReadAllText(Path.Combine("fixtures", "macro-v1.json"));

        var macro = MacroV1Migrator.LoadAndMigrate(json);

        Assert.Equal(3, macro.SchemaVersion);
        Assert.Equal("11111111-2222-3333-4444-555555555555", macro.Id);
        Assert.Equal("test-jump-jump", macro.Name);
        Assert.Equal("PerWindow", macro.RecordMode);
        Assert.Equal(47821334L, macro.RecordedAgainstUserId);
        Assert.Equal("Goldnail8", macro.RecordedAgainstDisplayName);
        Assert.Null(macro.InterAltDelayMs);
        Assert.Equal(2, macro.Events.Count);
        Assert.Equal(Macro.CoordSpaceScreen, macro.CoordSpace);
    }

    [Fact]
    public void Migrate_V2Json_UpgradesToV3()
    {
        var v2 = new Macro(
            SchemaVersion: 2,
            Id: "22222222-3333-4444-5555-666666666666",
            Name: "already-v2",
            RecordMode: "PerWindow",
            RecordedAgainstUserId: 99887766L,
            RecordedAgainstDisplayName: "PinkPotatoChip",
            InterAltDelayMs: null,
            RecordedAtUnixMs: 1746000000000,
            Events: new List<MacroEvent>());
        var json = JsonSerializer.Serialize(v2, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var result = MacroV1Migrator.LoadAndMigrate(json);

        Assert.Equal(3, result.SchemaVersion);
        Assert.Equal("already-v2", result.Name);
        Assert.Equal("PinkPotatoChip", result.RecordedAgainstDisplayName);
        Assert.Equal(Macro.CoordSpaceScreen, result.CoordSpace);
    }
}
