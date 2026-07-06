using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Labs626.UrTask.Macros;
using Labs626.UrTask.PluginHost;

namespace Labs626.UrTask.Tests;

public class GameAwareLibraryTests
{
    private static Macro MakeMacro(string name, long? placeId = null, string? gameName = null,
        bool allGames = false, long recordedAt = 1) => new(
        SchemaVersion: Macro.CurrentSchemaVersion,
        Id: Guid.NewGuid().ToString(),
        Name: name,
        RecordMode: "PerWindow",
        RecordedAgainstUserId: null,
        RecordedAgainstDisplayName: null,
        InterAltDelayMs: null,
        RecordedAtUnixMs: recordedAt,
        Events: new List<MacroEvent>(),
        CoordSpace: Macro.CoordSpaceScreen,
        RecordedPlaceId: placeId,
        RecordedGameName: gameName,
        AllGames: allGames);

    // ---------- schema ----------

    [Fact]
    public void GameFields_RoundTripThroughStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urtask-tests", Guid.NewGuid().ToString("N"));
        var store = new MacroStore(dir);
        store.Save(MakeMacro("farm", placeId: 606849621, gameName: "Pet Simulator"));

        var loaded = store.LoadAll();
        Assert.Empty(loaded.Failures);
        var back = Assert.Single(loaded.Macros);
        Assert.Equal(606849621L, back.RecordedPlaceId);
        Assert.Equal("Pet Simulator", back.RecordedGameName);
        Assert.False(back.AllGames);
        Assert.True(back.IsGameScoped);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void UnstampedMacro_OmitsGameFieldsFromJson_AndIsNotScoped()
    {
        // Schema stays v3: unstamped macros serialize without the new members,
        // byte-compatible with what v0.5 wrote.
        var macro = MakeMacro("legacy");
        var json = JsonSerializer.Serialize(macro, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });
        Assert.DoesNotContain("recordedPlaceId", json);
        Assert.DoesNotContain("recordedGameName", json);
        Assert.False(macro.IsGameScoped);
    }

    [Fact]
    public void V05ShapedReader_IgnoresGameFields()
    {
        // A v0.5 build deserializes into the pre-v0.6 record shape — unknown
        // JSON members are ignored by System.Text.Json, so v0.6 bundles still
        // import on v0.5. This locks the cross-version sharing decision.
        const string v06Json = """
        {
          "schemaVersion": 3,
          "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
          "name": "stamped",
          "recordMode": "PerWindow",
          "recordedAtUnixMs": 1,
          "events": [],
          "coordSpace": "screen",
          "recordedPlaceId": 606849621,
          "recordedGameName": "Pet Simulator",
          "allGames": false
        }
        """;
        var back = JsonSerializer.Deserialize<V05Macro>(v06Json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        Assert.NotNull(back);
        Assert.Equal("stamped", back!.Name);
    }

    private sealed record V05Macro(int SchemaVersion, string Id, string? Name, string? RecordMode,
        long? RecordedAgainstUserId, string? RecordedAgainstDisplayName, int? InterAltDelayMs,
        long RecordedAtUnixMs, IReadOnlyList<MacroEvent> Events, string? CoordSpace = null,
        int? RecordedClientW = null, int? RecordedClientH = null);

    [Fact]
    public void AllGamesOverride_DisablesScoping_AndImportsPreserveStamp()
    {
        var macro = MakeMacro("farm", placeId: 42, gameName: "X", allGames: true);
        Assert.False(macro.IsGameScoped);

        // Bundle round-trip keeps the stamp and the override.
        var parsed = MacroBundle.Parse(MacroBundle.Serialize(new[] { macro }, 1)).Macros.Single();
        Assert.Equal(42L, parsed.RecordedPlaceId);
        Assert.True(parsed.AllGames);
    }

    // ---------- registry ----------

    [Fact]
    public void Registry_CarriesPlaceInfo_AndRefreshOverwrites()
    {
        var registry = new AccountRegistry();
        registry.OnLaunched(100, 1, "Alice", "acc-1"); // launch event: presence not filled yet
        Assert.Equal(0, registry.ResolveByPid(100)!.PlaceId);

        // Snapshot refresh arrives with game identity — same pid, richer info.
        registry.OnLaunched(100, 1, "Alice", "acc-1", 606849621, "Pet Simulator");
        var info = registry.ResolveByPid(100)!;
        Assert.Equal(606849621L, info.PlaceId);
        Assert.Equal("Pet Simulator", info.PlaceName);
    }

    // ---------- sort / filter / mismatch ----------

    private static readonly IReadOnlySet<long> Playing = new HashSet<long> { 10, 20 };

    [Fact]
    public void Sort_CurrentGameFirst_ThenAgnostic_ThenOtherGames_RecencyWithin()
    {
        var macros = new[]
        {
            MakeMacro("other-game", placeId: 99, recordedAt: 900),
            MakeMacro("agnostic-old", recordedAt: 100),
            MakeMacro("current-old", placeId: 10, recordedAt: 200),
            MakeMacro("all-games", placeId: 99, allGames: true, recordedAt: 300),
            MakeMacro("current-new", placeId: 20, recordedAt: 800),
        };

        var sorted = MacroGameFilter.Sort(macros, Playing).Select(m => m.Name).ToArray();

        Assert.Equal(new[] { "current-new", "current-old", "all-games", "agnostic-old", "other-game" }, sorted);
    }

    [Fact]
    public void FilterPlayingNow_HidesOnlyMismatchedGameScoped()
    {
        var macros = new[]
        {
            MakeMacro("current", placeId: 10),
            MakeMacro("other", placeId: 99),
            MakeMacro("agnostic"),
            MakeMacro("all-games", placeId: 99, allGames: true),
        };

        var visible = MacroGameFilter.FilterPlayingNow(macros, Playing).Select(m => m.Name).ToArray();

        Assert.Equal(new[] { "current", "agnostic", "all-games" }, visible);
    }

    [Fact]
    public void FilterPlayingNow_NoGamesRunning_IsANoOp()
    {
        var macros = new[] { MakeMacro("scoped", placeId: 99), MakeMacro("agnostic") };
        var visible = MacroGameFilter.FilterPlayingNow(macros, new HashSet<long>());
        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public void IsMismatch_OnlyWhenBothSidesKnown_AndDifferent()
    {
        var scoped = MakeMacro("scoped", placeId: 10);
        Assert.False(MacroGameFilter.IsMismatch(scoped, 10));   // same game
        Assert.True(MacroGameFilter.IsMismatch(scoped, 99));    // different game
        Assert.False(MacroGameFilter.IsMismatch(scoped, 0));    // alt game unknown — advisory stays quiet
        Assert.False(MacroGameFilter.IsMismatch(MakeMacro("agnostic"), 99));           // no stamp
        Assert.False(MacroGameFilter.IsMismatch(MakeMacro("x", placeId: 10, allGames: true), 99)); // override
        Assert.False(MacroGameFilter.IsMismatch(null, 99));
    }
}
