using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Stores;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Population;

internal sealed class RealPlacementWorldFactAttribute : FactAttribute
{
    public RealPlacementWorldFactAttribute()
    {
        string? db = Environment.GetEnvironmentVariable("ARCANECORE_TEST_WORLD_DB");
        string? dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR");
        string? terrain = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR");
        if (!File.Exists(db) || !File.Exists(Path.Combine(dbc ?? "", "AreaTable.dbc"))
            || !File.Exists(Path.Combine(dbc ?? "", "FactionTemplate.dbc"))
            || !Directory.Exists(Path.Combine(terrain ?? "", "maps")))
            Skip = "ARCANECORE_TEST_WORLD_DB, ARCANECORE_TEST_DBC_DIR and ARCANECORE_TEST_TERRAIN_DIR are needed; real placement coverage did not run.";
    }
}

/// <summary>Read-only imported-world placement coverage; no server, DB copy, or DB write.</summary>
public sealed class PopulationRealWorldCoverageTests(ITestOutputHelper output)
{
    [RealPlacementWorldFact]
    public async Task RealWorld_AllStarterRacesHaveASafeLevelOneLocation_AndCountsUnavailableChoices()
    {
        string path = Environment.GetEnvironmentVariable("ARCANECORE_TEST_WORLD_DB")!;
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        string root = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR")!;
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=False").Options;
        CreatureContent creatures;
        GraveyardContent graveyardRows;
        var raceFactions = new Dictionary<byte, uint>();
        await using (var db = new WorldDbContext(options))
        {
            creatures = await new EfCreatureDataStore(db).LoadAsync();
            graveyardRows = await new EfGraveyardDataStore(db).LoadAsync();
            var races = new EfWorldDataStore(db);
            foreach (byte race in PopulationRaceClassCache.Races)
                if (await races.GetRaceInfoAsync(race, 0) is { } info) raceFactions[race] = info.FactionTemplate;
        }

        GraveyardCatalog graveyards = GraveyardCatalog.Build(graveyardRows, null, out _);
        AreaTemplate[] areas = [.. AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
            r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId))];
        PopulationZoneAtlas atlas = PopulationZoneAtlas.Build(areas, graveyards, [0u, 1u], new HashSet<uint>());
        var terrain = new TerrainManager(root) { Areas = new AreaTable(areas, MapRegistry.Default) };
        var index = new PopulationSpawnIndex(creatures);
        var factions = FactionTemplateDbcReader.Load(Path.Combine(dbc, "FactionTemplate.dbc"));
        var lookups = new Dictionary<uint, PlayerbotAreaLevelLookup>();
        int AreaAt(WorldSafeLoc location)
        {
            if (!lookups.TryGetValue(location.MapId, out PlayerbotAreaLevelLookup? lookup))
                lookups[location.MapId] = lookup = PlayerbotAreaLevelLookup.FromTerrain(terrain, location.MapId);
            return lookup.LevelAt(location.X, location.Y, location.Z);
        }

        bool Safe(PopulationZone zone, Team team, byte level)
            => zone.LocationsFor(team).Any(loc => raceFactions.Any(pair => PopulationRaceClassCache.TeamOf(pair.Key) == team
                && PopulationPlacementSafety.IsSafe(loc, level, pair.Value, AreaAt(loc), index, factions)));

        (uint Zone, byte Race)[] starters = [(12, 1), (14, 2), (1, 3), (141, 4), (85, 5), (215, 6), (1, 7), (14, 8)];
        foreach ((uint zoneId, byte race) in starters)
        {
            PopulationZone zone = Assert.IsType<PopulationZone>(atlas.Find(zoneId));
            Team team = PopulationRaceClassCache.TeamOf(race);
            Assert.True(PopulationZoneAtlas.Fits(zone, 1, team, 4), $"race {race} has no fitting starter zone {zoneId}");
            Assert.True(zone.LocationsFor(team).Any(loc => PopulationPlacementSafety.IsSafe(loc, 1, raceFactions[race], AreaAt(loc), index, factions)),
                $"race {race} has no safe starter graveyard in zone {zoneId}");
        }

        int eligible = 0, unavailable = 0, allianceUnavailable = 0, hordeUnavailable = 0;
        var examples = new List<string>();
        foreach (PopulationZone zone in atlas.Zones)
            foreach (Team team in new[] { Team.Alliance, Team.Horde })
                for (byte level = 1; level <= 60; level++)
                {
                    if (!PopulationZoneAtlas.Fits(zone, level, team, 4)) continue;
                    eligible++;
                    if (Safe(zone, team, level)) continue;
                    unavailable++;
                    if (team == Team.Alliance) allianceUnavailable++; else hordeUnavailable++;
                    if (examples.Count < 18) examples.Add($"{zone.Name}/{team}/{level}");
                }

        string summary = $"placement combinations: {unavailable} unavailable / {eligible} eligible; Alliance {allianceUnavailable}, Horde {hordeUnavailable}";
        output.WriteLine(summary);
        Console.WriteLine(summary);
        output.WriteLine("first unavailable: " + string.Join(", ", examples));
        Assert.True(eligible > 0);
    }
}
