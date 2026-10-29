using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Graveyards;
using ArcaneCore.Data.World.GameObjects;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Reputation;
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
            Assert.True(PopulationZoneAtlas.Fits(zone, 1, team, PopulationLevelRule.Flat(4)), $"race {race} has no fitting starter zone {zoneId}");
            Assert.True(zone.LocationsFor(team).Any(loc => PopulationPlacementSafety.IsSafe(loc, 1, raceFactions[race], AreaAt(loc), index, factions)),
                $"race {race} has no safe starter graveyard in zone {zoneId}");
        }

        int eligible = 0, unavailable = 0, allianceUnavailable = 0, hordeUnavailable = 0;
        var examples = new List<string>();
        foreach (PopulationZone zone in atlas.Zones)
            foreach (Team team in new[] { Team.Alliance, Team.Horde })
                for (byte level = 1; level <= 60; level++)
                {
                    if (!PopulationZoneAtlas.Fits(zone, level, team, PopulationLevelRule.Flat(4))) continue;
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

    [RealPlacementWorldFact]
    public async Task RealWorld_ZoneValue_RanksQuestHubsAboveDeadwindForAFreshLevel60Bot()
    {
        string path = Environment.GetEnvironmentVariable("ARCANECORE_TEST_WORLD_DB")!;
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        string root = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR")!;
        var options = new DbContextOptionsBuilder<WorldDbContext>()
            .UseSqlite($"Data Source={path};Mode=ReadOnly;Pooling=False").Options;
        CreatureContent creatures;
        GraveyardContent graveyardRows;
        QuestStore quests;
        NpcStore npcs;
        LootContent loot;
        ItemTemplateStore items;
        MapContent mapContent;
        IReadOnlyList<ReputationOnKillEntry> onKill;
        await using (var db = new WorldDbContext(options))
        {
            creatures = await new EfCreatureDataStore(db).LoadAsync();
            graveyardRows = await new EfGraveyardDataStore(db).LoadAsync();
            quests = new QuestStore(await new EfQuestContentStore(db).LoadAsync());
            npcs = new NpcStore(await new EfNpcContentStore(db).LoadAsync());
            loot = await new EfLootDataStore(db).LoadAsync();
            items = new ItemTemplateStore(await new EfItemTemplateSource(db).LoadTemplatesAsync());
            mapContent = await new EfMapDataStore(db).LoadAsync();
            onKill = await new EfReputationOnKillSource(db).LoadAsync();
        }

        AreaTemplate[] areas = [.. AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
            r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId))];
        GraveyardCatalog graveyards = GraveyardCatalog.Build(graveyardRows, null, out _);
        PopulationZoneAtlas atlas = PopulationZoneAtlas.Build(areas, graveyards, [0u, 1u], new HashSet<uint>());
        var registry = new MapRegistry(mapContent.Maps);
        var areaTable = new AreaTable(areas, registry);
        var terrain = new TerrainManager(root) { Areas = areaTable };
        FactionTemplateCatalog factions = FactionTemplateDbcReader.Load(Path.Combine(dbc, "FactionTemplate.dbc"));

        // Mirrors PlayerbotPopulationFeature.EnsureZoneValue: triggers with a teleport, and the map templates they lead to.
        AreaTriggerTemplate[] triggers = [.. mapContent.AreaTriggers];
        Dictionary<uint, AreaTriggerTeleport> teleportById = mapContent.AreaTriggerTeleports.ToDictionary(t => t.Id);
        var teleports = new Dictionary<uint, AreaTriggerTeleport>();
        var mapTemplates = new Dictionary<uint, MapTemplate>();
        foreach (AreaTriggerTemplate trigger in triggers)
        {
            if (!teleportById.TryGetValue(trigger.Id, out AreaTriggerTeleport? teleport)) continue;
            teleports[trigger.Id] = teleport;
            if (registry.Find(teleport.TargetMap) is { } target) mapTemplates[target.Entry] = target;
        }

        var sources = new PopulationZoneValueSources(
            [0u, 1u],
            creatures.GetSpawns,
            creatures.FindTemplate,
            id => quests.Get(id)?.Template,
            quests.StartersOf,
            loot,
            items.Find,
            npcs.VendorItems,
            triggers,
            id => teleports.GetValueOrDefault(id),
            id => mapTemplates.GetValueOrDefault(id),
            [.. onKill],
            factions,
            new HashSet<uint>());

        (uint Map, int X, int Y) loaded = (uint.MaxValue, 0, 0);
        TerrainTile tile = TerrainTile.Empty;
        PopulationZoneValue value = PopulationZoneValue.Build(sources, (map, x, y, _) =>
        {
            if (TerrainTile.TileOf(x, y) is not { } at) return 0;
            if (loaded != (map, at.X, at.Y))
            {
                tile = terrain.ReadTileDetached(map, at.X, at.Y);
                loaded = (map, at.X, at.Y);
            }

            ushort flag = tile.GetAreaFlag(x, y);
            return flag == 0 ? 0 : areaTable.GetZoneAndAreaId(flag, map).ZoneId;
        }, CancellationToken.None);

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"zones with value: {value.Zones.Count}"));
        PopulationValueWeights weights = PopulationValueWeights.Default;
        PopulationLevelRule rule = PopulationLevelRule.From(new PlayerbotPopulationOptions());
        var none = new Dictionary<uint, int>();
        string Line(int rank, ZoneScore s)
        {
            ZoneContentFacts? f = value.Find(s.ZoneId);
            return string.Create(inv, $"#{rank} {atlas.Find(s.ZoneId)?.Name ?? "?"} ({s.ZoneId}) total {s.Total:0.0} = Quest {s.Quest:0.0} Rep {s.Reputation:0.0} Gear {s.Gear:0.0} Farm {s.Farm:0.0} Elite -{s.Elite:0.0} Trouble -{s.Trouble:0.0} Distance -{s.Distance:0.0}"
                + $" | quests {f?.Quests.Count ?? 0} repSources {f?.RepSources.Count ?? 0} gearFacts {f?.Gear.Count ?? 0} skinnable {f?.Skinnable ?? 0} dungeonMaps [{string.Join(',', f?.DungeonMaps ?? [])}]");
        }

        foreach (Team team in new[] { Team.Alliance, Team.Horde })
        {
            IReadOnlyList<ZoneScore> ranking = atlas.Rank(PopulationBotProfile.Fresh(60, team), rule, value, weights, none);
            output.WriteLine($"--- {team}: {ranking.Count} ranked zones, top 12");
            for (int i = 0; i < Math.Min(12, ranking.Count); i++) output.WriteLine(Line(i + 1, ranking[i]));
            int deadwind = -1;
            for (int i = 0; i < ranking.Count; i++) if (ranking[i].ZoneId == 41) deadwind = i;
            output.WriteLine(deadwind < 0 ? $"{team}: Deadwind Pass (41) is not ranked" : $"{team}: Deadwind Pass at {Line(deadwind + 1, ranking[deadwind])}");

            Assert.NotEmpty(ranking);
            Assert.NotEqual(41u, ranking[0].ZoneId);
        }
    }
}
