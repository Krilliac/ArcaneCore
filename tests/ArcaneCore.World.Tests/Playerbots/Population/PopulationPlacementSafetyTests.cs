using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

public sealed class PopulationPlacementSafetyTests
{
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
