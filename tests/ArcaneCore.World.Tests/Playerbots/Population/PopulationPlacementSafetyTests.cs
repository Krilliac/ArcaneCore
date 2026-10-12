using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Tests.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

public sealed class PopulationPlacementSafetyTests
{
    [RealAreaTerrainFact]
    public void RealTerrain_EachRaceStarterGraveyardAcceptsALevelOneBot()
    {
        string dbc = Environment.GetEnvironmentVariable("ARCANECORE_TEST_DBC_DIR")!;
        string root = Environment.GetEnvironmentVariable("ARCANECORE_TEST_TERRAIN_DIR")!;
        var terrain = new TerrainManager(root)
        {
            Areas = new AreaTable(AreaTableDbcReader.Read(Path.Combine(dbc, "AreaTable.dbc")).Select(r => new AreaTemplate(
                r.Entry, r.MapId, r.ZoneId, r.ExploreFlag, r.Flags, r.AreaLevel, r.Name, r.Team, r.LiquidTypeId)), MapRegistry.Default),
        };
        // world_safe_locs rows linked to the six 1.12.1 starting zones; one for each race's starting area.
        WorldSafeLoc[] starters =
        [
            new(105, 0, -8935.33f, -188.65f, 80.42f, 2.72f, "Northshire"),
            new(94, 0, 1882.94f, 1629.11f, 94.42f, 4.64f, "Deathknell"),
            new(100, 0, -6164.23f, 336.32f, 399.79f, 2.06f, "Coldridge"),
            new(709, 1, -634.64f, -4296.03f, 40.53f, 4.12f, "Valley of Trials"),
            new(93, 1, 10384.80f, 811.53f, 1317.54f, 1.12f, "Shadowglen"),
            new(34, 1, -2944.56f, -153.21f, 65.79f, 1.88f, "Camp Narache"),
        ];
        uint[] factions = [1, 5, 3, 2, 4, 6];
        for (int i = 0; i < starters.Length; i++)
        {
            WorldSafeLoc at = starters[i];
            PlayerbotAreaLevelLookup lookup = PlayerbotAreaLevelLookup.FromTerrain(terrain, at.MapId);
            int area = lookup.LevelAt(at.X, at.Y, at.Z);
            Assert.NotEqual((ushort)0, terrain.For(at.MapId).GetAreaFlag(at.X, at.Y, at.Z)); // the lookup resolved a real area
            Assert.True(PopulationPlacementSafety.Choose(at, [at], 1, factions[i],
                loc => lookup.LevelAt(loc.X, loc.Y, loc.Z), CreatureContent.Empty, FactionTemplateCatalog.Empty) is not null,
                $"{at.Name} at area level {area} was refused for level one");
            Console.WriteLine($"starter {at.Name}: area level {area} flag {terrain.For(at.MapId).GetAreaFlag(at.X, at.Y, at.Z)} zone {terrain.For(at.MapId).GetZoneAndAreaId(at.X, at.Y, at.Z)}");
        }

        var goldshire = new WorldSafeLoc(106, 0, -9339.46f, 171.41f, 61.56f, 4.57f, "Goldshire");
        PlayerbotAreaLevelLookup elwynn = PlayerbotAreaLevelLookup.FromTerrain(terrain, 0);
        Console.WriteLine($"Goldshire graveyard: area level {elwynn.LevelAt(goldshire.X, goldshire.Y, goldshire.Z)}");
    }

    private static readonly WorldSafeLoc DireMaul = new(849, 1, -4590.41f, 1632.08f, 93.97f, 0, "Dire Maul");
    private static readonly WorldSafeLoc CampMojache = new(310, 1, -4439.97f, 370.15f, 51.36f, 3.11f, "Camp Mojache");
    private static readonly WorldSafeLoc AeriePeak = new(349, 0, 323.51f, -2227.20f, 137.62f, 2.58f, "Aerie Peak");
    private static readonly WorldSafeLoc OverlookCliffs = new(789, 0, -291, -4374, 107, 0, "Overlook Cliffs");
    private static readonly WorldSafeLoc Arathi = new(99, 0, -1472.29f, -2617.96f, 49.28f, 5.57f, "Arathi Highlands");

    [Fact]
    public void LowLevelFeralasBot_DoesNotSpawnAtTheHighLevelDireMaulGraveyard()
    {
        // Feralas's zone range is 41-60, so ZoneLevelSlack=4 admitted the level-38 bot in QA #183/#193/#197.
        // The Dire Maul graveyard is beside level-44-46 bears, wolves and screechers in the live world DB.
        CreatureContent content = new(
            [new CreatureTemplate { Entry = 5308, Name = "Rogue Vale Screecher", MinLevel = 44, MaxLevel = 46, Faction = 14 }],
            [new CreatureSpawn { Guid = 51215, Entry = 5308, MapId = 1, X = -4543.16f, Y = 1572.12f, Z = 102.69f, WanderDistance = 15 }],
            [], [], []);
        WorldSafeLoc? chosen = PopulationPlacementSafety.Choose(DireMaul, [DireMaul, CampMojache], 38, 2,
            _ => 0, content, Factions());
        Assert.Equal(CampMojache.Id, chosen?.Id);
    }

    [Fact]
    public void LocalAreaLevel_IsCheckedEvenWhenNoCreatureSpawnIsKnown()
    {
        WorldSafeLoc? chosen = PopulationPlacementSafety.Choose(DireMaul, [DireMaul, CampMojache], 38, 2,
            loc => loc.Id == DireMaul.Id ? 45 : 41, CreatureContent.Empty, Factions());
        Assert.Equal(CampMojache.Id, chosen?.Id);
    }

    [Fact]
    public void FeralasLevel38_RefusesBothHordeGraveyards_Level40CanUseCampMojache()
    {
        // Complete nearby-spawn check: Camp Mojache also has level-41/42 Ironfur Bears, so a level-38 bot
        // should not be sent there merely because Dire Maul is worse. A level-40 bot has a safe alternative.
        CreatureContent content = new(
            [
                new CreatureTemplate { Entry = 5308, Name = "Rogue Vale Screecher", MinLevel = 44, MaxLevel = 46, Faction = 14 },
                new CreatureTemplate { Entry = 5268, Name = "Ironfur Bear", MinLevel = 41, MaxLevel = 42, Faction = 44 },
            ],
            [
                new CreatureSpawn { Guid = 51215, Entry = 5308, MapId = 1, X = -4543.16f, Y = 1572.12f, Z = 102.69f, WanderDistance = 15 },
                new CreatureSpawn { Guid = 50698, Entry = 5268, MapId = 1, X = -4379.46f, Y = 395.76f, Z = 47.24f, WanderDistance = 15 },
                new CreatureSpawn { Guid = 50704, Entry = 5268, MapId = 1, X = -4495.67f, Y = 424.66f, Z = 49.34f, WanderDistance = 15 },
            ], [], [], []);
        Assert.Null(PopulationPlacementSafety.Choose(DireMaul, [DireMaul, CampMojache], 38, 2,
            _ => 0, content, Factions()));
        Assert.Equal(CampMojache.Id, PopulationPlacementSafety.Choose(DireMaul, [DireMaul, CampMojache], 40, 5,
            _ => 0, content, Factions())?.Id);
    }

    [Fact]
    public void HordeHinterlandsBot_DoesNotSpawnBesideHostileWildhammerSentries()
    {
        // QA #211: the Aerie Peak graveyard link is team 0, yet its level-55 Wildhammer Sentries are hostile to Horde.
        CreatureContent content = new(
            [new CreatureTemplate { Entry = 7865, Name = "Wildhammer Sentry", MinLevel = 55, MaxLevel = 55, Faction = 1054 }],
            [new CreatureSpawn { Guid = 92917, Entry = 7865, MapId = 0, X = 271.74f, Y = -2176.42f, Z = 120.05f }],
            [], [], []);
        WorldSafeLoc? chosen = PopulationPlacementSafety.Choose(AeriePeak, [AeriePeak, OverlookCliffs], 40, 2,
            _ => 41, content, Factions());
        Assert.Equal(OverlookCliffs.Id, chosen?.Id);
        Assert.Equal(AeriePeak.Id, PopulationPlacementSafety.Choose(AeriePeak, [AeriePeak, OverlookCliffs], 40, 1,
            _ => 41, content, Factions())?.Id); // the same sentry is friendly to Alliance
    }

    [Fact]
    public void LowLevelArathiBot_RefusesTheOnlyGraveyardBesideThunderingExiles()
    {
        // QA #208 includes a level-26 bot. The level-38/39 exile spawn is 89 yd from the zone's only graveyard.
        CreatureContent content = new(
            [new CreatureTemplate { Entry = 2762, Name = "Thundering Exile", MinLevel = 38, MaxLevel = 39, Faction = 16 }],
            [new CreatureSpawn { Guid = 14657, Entry = 2762, MapId = 0, X = -1413.11f, Y = -2684.88f, Z = 51.43f, WanderDistance = 10 }],
            [], [], []);
        Assert.Null(PopulationPlacementSafety.Choose(Arathi, [Arathi], 26, 2, _ => 30, content, Factions()));
    }

    [Fact]
    public void NoSafeGraveyard_RefusesPlacement()
    {
        Assert.Null(PopulationPlacementSafety.Choose(DireMaul, [DireMaul], 38, 2,
            _ => 45, CreatureContent.Empty, Factions()));
    }

    [Fact]
    public void UnknownFactionAndEntryZeroPool_AreUnsafeUnlessEveryPossibleEntryIsKnownSafe()
    {
        CreatureTemplate unknown = new() { Entry = 990011, Name = "unknown faction", Faction = 990099, MinLevel = 20, MaxLevel = 20 };
        CreatureContent ordinary = new([unknown],
            [new CreatureSpawn { Guid = 990011, Entry = unknown.Entry, MapId = 0, X = Arathi.X + 20, Y = Arathi.Y, Z = Arathi.Z }],
            [], [], []);
        Assert.Null(PopulationPlacementSafety.Choose(Arathi, [Arathi], 1, 2, _ => 0, ordinary, Factions()));

        CreatureContent pooled = new([unknown],
            [new CreatureSpawn { Guid = 990012, Entry = 0, MapId = 0, X = Arathi.X + 20, Y = Arathi.Y, Z = Arathi.Z }],
            [], [], [], spawnEntries: [(990012u, unknown.Entry)]);
        Assert.Null(PopulationPlacementSafety.Choose(Arathi, [Arathi], 1, 2, _ => 0, pooled, Factions()));
        Assert.Null(PopulationPlacementSafety.Choose(Arathi, [Arathi], 1, 2, _ => 0, ordinary, FactionTemplateCatalog.Empty));
    }

    [Fact]
    public void KnownFriendlyGuardAndPassiveSpiritHealer_DoNotVetoAStarterGraveyard()
    {
        CreatureContent content = new(
            [
                new CreatureTemplate { Entry = 7865, Name = "Wildhammer Sentry", Faction = 1054, MinLevel = 55, MaxLevel = 55 },
                new CreatureTemplate { Entry = 6491, Name = "Spirit Healer", Faction = 35, MinLevel = 60, MaxLevel = 60,
                    ExtraFlags = 2 },
            ],
            [
                new CreatureSpawn { Guid = 990021, Entry = 7865, MapId = 0, X = AeriePeak.X + 20, Y = AeriePeak.Y, Z = AeriePeak.Z },
                new CreatureSpawn { Guid = 990022, Entry = 6491, MapId = 0, X = AeriePeak.X + 2, Y = AeriePeak.Y, Z = AeriePeak.Z },
            ], [], [], []);
        Assert.Equal(AeriePeak.Id, PopulationPlacementSafety.Choose(AeriePeak, [AeriePeak], 1, 1,
            _ => 5, content, Factions())?.Id);
    }

    // Exact build-5875 FactionTemplate.dbc fields 0-5, plus the one nonzero explicit friend of 16/1054.
    private static FactionTemplateCatalog Factions() => new([
        new FactionTemplateRecord(1, 1, 72, 3, 2, 12),
        new FactionTemplateRecord(2, 2, 72, 5, 4, 10),
        new FactionTemplateRecord(5, 5, 72, 5, 4, 10),
        new FactionTemplateRecord(14, 14, 0, 8, 0, 1),
        new FactionTemplateRecord(16, 14, 1, 8, 0, 1, Friend1: 14),
        new FactionTemplateRecord(44, 37, 1, 8, 0, 1, Friend1: 37),
        new FactionTemplateRecord(1054, 471, 2081, 3, 2, 12, Friend1: 471),
    ]);
}
