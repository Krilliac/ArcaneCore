using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The level rule of the population (<see cref="PopulationLevelRule"/>, the default options): the slack below a graveyard's floor
/// shrinks above the starter floors, and the creature survey keeps bots away from graveyards whose hostile creatures are far above them.
/// The fixture is the live 5875 data the w24 world placed level 38-40 bots with (issues #183, #193, #195): area_template levels of the r5
/// world DB, and the sub-area the 5875 terrain puts each graveyard in (the population atlas probe on the live data):
/// <list type="bullet">
/// <item>The Hinterlands (47, 41-49): 349 "Aerie Peak" (both teams) in Aerie Peak (41), 789 "The Overlook Cliffs" (both) in The Overlook Cliffs (48).</item>
/// <item>Feralas (357, 41-60): 309 "Feathermoon Stronghold" (Alliance) in an unlevelled sub-area (the zone's 41), 310 "Camp Mojache"
/// (Horde) in Camp Mojache (42), 849 "Dire Maul" (both) in Dire Maul (44), beside level 43-46 howlers, bears and screechers.</item>
/// <item>Deadwind Pass (41, 50-60): 36 "Morgan's Plot" (both) in an unlevelled sub-area (the zone's 50), beside level 58-60 undead.</item>
/// <item>Desolace (405, 30-39): 31 "Ghost Walker Post" (both) in Kodo Graveyard (35); Badlands (3, 36-45): 8 (both) in no levelled sub-area.</item>
/// <item>Elwynn Forest (12, Alliance, 5-10): 106 Goldshire (Alliance) unlevelled, 854 Eastvale Logging Camp (both) in a level 6 area;
/// Durotar (14, Horde, 5-10): 32 Razor Hill (both) in Razor Hill (5); Westfall (40, Alliance, 9-18): 4 Sentinel Hill (both) in Sentinel Hill (15).</item>
/// <item>A zone 900 (40-45) whose only graveyard stands in its level 45 sub-area.</item>
/// </list>
/// </summary>
public sealed class PopulationLevelRuleTests
{
    private const uint Hinterlands = 47, Feralas = 357, Deadwind = 41, Desolace = 405, Badlands = 3, Elwynn = 12, Durotar = 14, Westfall = 40, Level45 = 900;

    private static readonly PopulationLevelRule Default = PopulationLevelRule.From(new PlayerbotPopulationOptions());

    private static AreaTemplate Area(uint entry, uint map, uint zone, int level, uint team = 0) => new(entry, map, zone, entry, 0, level, "Area" + entry, team, 0);

    private static List<AreaTemplate> Areas() =>
    [
        Area(Hinterlands, 0, 0, 0), Area(348, 0, Hinterlands, 41), Area(307, 0, Hinterlands, 48), Area(1917, 0, Hinterlands, 49),
        Area(Feralas, 1, 0, 0), Area(1100, 1, Feralas, 41), Area(1116, 1, Feralas, 0), Area(1099, 1, Feralas, 42), Area(2577, 1, Feralas, 44), Area(1113, 1, Feralas, 60),
        Area(Deadwind, 0, 0, 0), Area(2697, 0, Deadwind, 50), Area(2563, 0, Deadwind, 0), Area(2562, 0, Deadwind, 60),
        Area(Desolace, 1, 0, 0), Area(596, 1, Desolace, 35), Area(599, 1, Desolace, 30), Area(602, 1, Desolace, 39),
        Area(Badlands, 0, 0, 0), Area(340, 0, Badlands, 36), Area(341, 0, Badlands, 45),
        Area(Elwynn, 0, 0, 0, AreaTeams.Ally), Area(87, 0, Elwynn, 5), Area(88, 0, Elwynn, 6), Area(89, 0, Elwynn, 10),
        Area(Durotar, 1, 0, 0, AreaTeams.Horde), Area(362, 1, Durotar, 5), Area(817, 1, Durotar, 10),
        Area(Westfall, 0, 0, 0, AreaTeams.Ally), Area(108, 0, Westfall, 15), Area(916, 0, Westfall, 9), Area(917, 0, Westfall, 18),
        Area(Level45, 1, 0, 0), Area(901, 1, Level45, 40), Area(902, 1, Level45, 45),
    ];

    private static GraveyardCatalog Graveyards()
    {
        (uint Id, uint Map, uint Zone, uint Team)[] rows =
        [
            (349, 0, Hinterlands, 0), (789, 0, Hinterlands, 0),
            (309, 1, Feralas, GraveyardCatalog.TeamAlliance), (310, 1, Feralas, GraveyardCatalog.TeamHorde), (849, 1, Feralas, 0),
            (36, 0, Deadwind, 0), (31, 1, Desolace, 0), (8, 0, Badlands, 0),
            (106, 0, Elwynn, GraveyardCatalog.TeamAlliance), (854, 0, Elwynn, 0), (32, 1, Durotar, 0), (4, 0, Westfall, 0), (903, 1, Level45, 0),
        ];
        WorldSafeLoc[] locs = [.. rows.Select(r => new WorldSafeLoc(r.Id, r.Map, r.Id * 10f, r.Id * 20f, 50f, 0f, "GY" + r.Id))];
        GraveyardLink[] links = [.. rows.Select(r => new GraveyardLink(r.Id, r.Zone, r.Team))];
        return GraveyardCatalog.Build(new GraveyardContent(locs, links), null, out _);
    }

    // The sub-area the 5875 terrain puts each graveyard in (its zone is the linked one).
    private static (uint ZoneId, uint AreaId) Terrain(WorldSafeLoc location) => location.Id switch
    {
        349 => (Hinterlands, 348),
        789 => (Hinterlands, 307),
        309 => (Feralas, 1116),
        310 => (Feralas, 1099),
        849 => (Feralas, 2577),
        36 => (Deadwind, 2563),
        31 => (Desolace, 596),
        8 => (Badlands, Badlands),
        106 => (Elwynn, Elwynn),
        854 => (Elwynn, 88),
        32 => (Durotar, 362),
        4 => (Westfall, 108),
        903 => (Level45, 902),
        _ => (0, 0),
    };

    // The creature survey of the live data (lower median MinLevel of the hostile spawns within 100 yards and 15 yards of height): Dire
    // Maul 43 for both teams, Morgan's Plot 59. Aerie Peak 55 for the Horde only is a fixture: the Wildhammer sentries hostile to the
    // Horde stand 17-19 yards below the live graveyard, outside the height bound, so the live survey gives Aerie Peak no level.
    private static int Creatures(WorldSafeLoc location, Team team) => (location.Id, team) switch
    {
        (849, _) => 43,
        (36, _) => 59,
        (349, Team.Horde) => 55,
        _ => 0,
    };

    private static PopulationZoneAtlas Atlas(bool survey = true) => PopulationZoneAtlas.Build(Areas(), Graveyards(), [0, 1], new HashSet<uint>(), Terrain,
        survey ? Creatures : null);

    [Theory]
    [InlineData(Team.Alliance)]
    [InlineData(Team.Horde)]
    public void ALevel38Bot_IsNotPlacedInTheHinterlandsFeralasOrAZoneWhoseGraveyardAreaIs45_Issues183_193_195(Team team)
    {
        PopulationZoneAtlas atlas = Atlas(survey: false); // the area floor alone already keeps it out
        foreach (uint zone in new[] { Hinterlands, Feralas, Level45 })
        {
            Assert.False(PopulationZoneAtlas.Fits(atlas.Find(zone)!, 38, team, Default), $"zone {zone} takes a level 38 {team} bot");
            Assert.True(PopulationZoneAtlas.UnderLevelled(atlas.Find(zone)!, 38, team, Default), $"a level 38 {team} bot in zone {zone} stays");
        }

        // A zone whose graveyard stands in a level 45 area takes nobody below 45 (the slack at floor 45 is 0).
        Assert.False(PopulationZoneAtlas.Fits(atlas.Find(Level45)!, 44, team, Default));
        Assert.True(PopulationZoneAtlas.Fits(atlas.Find(Level45)!, 45, team, Default));

        var random = new Random(38);
        for (int i = 0; i < 200; i++)
        {
            PopulationPlacement placement = atlas.Pick(38, 38, team, Default, new Dictionary<uint, int>(), random)!;
            Assert.Contains(placement.Zone.ZoneId, new[] { Desolace, Badlands });
            Assert.True(placement.LowLevel <= 38);
        }

        // Before: the flat slack of 4 put a level 38 bot at Aerie Peak (41) and Feathermoon/Camp Mojache (41/42).
        Assert.True(PopulationZoneAtlas.Fits(atlas.Find(Hinterlands)!, 38, team, PopulationLevelRule.Flat(4)));
        Assert.True(PopulationZoneAtlas.Fits(atlas.Find(Feralas)!, 38, team, PopulationLevelRule.Flat(4)));
    }

    [Fact]
    public void ALevel40Bot_NeverLandsAtTheDireMaulGraveyard_AndALevel49BotNotInDeadwindPass()
    {
        PopulationZoneAtlas atlas = Atlas();
        PopulationZone feralas = atlas.Find(Feralas)!;
        WorldSafeLoc direMaul = feralas.AllianceLocations.Single(l => l.Id == 849);
        Assert.Equal(44, feralas.LowestLevelAt(direMaul, Team.Alliance, Default)); // the area (44), above the creatures (43) minus 3
        Assert.Equal(40, feralas.LowestLevelAt(direMaul, Team.Alliance, PopulationLevelRule.Flat(4))); // before: 44 - 4

        var random = new Random(40);
        for (int i = 0; i < 200; i++)
        {
            Assert.NotEqual(849u, atlas.Pick(40, 43, Team.Alliance, Default, new Dictionary<uint, int> { [Badlands] = 9, [Desolace] = 9 }, random)?.Location.Id ?? 0);
            Assert.NotEqual(Deadwind, atlas.Pick(46, 55, Team.Horde, Default, new Dictionary<uint, int>(), random)?.Zone.ZoneId ?? 0);
        }

        // Morgan's Plot: the area floor is 50, but most of the undead beside it are 58-60: nobody below 56 is placed there (Grosan was 49).
        PopulationZone deadwind = atlas.Find(Deadwind)!;
        Assert.Equal(56, deadwind.LowestLevelFor(Team.Horde, Default));
        Assert.False(PopulationZoneAtlas.Fits(deadwind, 49, Team.Horde, Default));
        Assert.False(PopulationZoneAtlas.Fits(deadwind, 55, Team.Alliance, Default));
        Assert.True(PopulationZoneAtlas.Fits(deadwind, 56, Team.Alliance, Default));
        Assert.True(PopulationZoneAtlas.UnderLevelled(deadwind, 49, Team.Horde, Default));
    }

    [Fact]
    public void TheCreatureSurvey_IsPerTeam_AHordeBotIsNotPlacedBesideWildhammerSentries()
    {
        PopulationZone hinterlands = Atlas().Find(Hinterlands)!;
        WorldSafeLoc aeriePeak = hinterlands.HordeLocations.Single(l => l.Id == 349);
        Assert.Equal(41, hinterlands.LowestLevelAt(aeriePeak, Team.Alliance, Default));
        Assert.Equal(52, hinterlands.LowestLevelAt(aeriePeak, Team.Horde, Default));
        Assert.True(PopulationZoneAtlas.Fits(hinterlands, 45, Team.Alliance, Default));
        Assert.False(PopulationZoneAtlas.Fits(hinterlands, 45, Team.Horde, Default)); // Overlook Cliffs is 48, Aerie Peak 52 for the Horde
        Assert.True(PopulationZoneAtlas.Fits(hinterlands, 48, Team.Horde, Default));

        // A null margin ignores the survey.
        Assert.Equal(41, hinterlands.LowestLevelAt(aeriePeak, Team.Horde, Default with { CreatureMargin = null }));
    }

    [Fact]
    public void TheSlack_IsWholeUpToTheStarterFloors_ThenShrinksToZero()
    {
        int[] floors = [1, 4, 7, 10, 12, 13, 14, 15, 16, 17, 18, 19, 20, 30, 41, 60];
        int[] slack = [4, 4, 4, 4, 4, 3, 3, 2, 2, 1, 1, 0, 0, 0, 0, 0];
        Assert.Equal(slack, floors.Select(Default.SlackAt).ToArray());
        Assert.All(floors, f => Assert.Equal(4, PopulationLevelRule.Flat(4).SlackAt(f)));
        Assert.All(floors, f => Assert.Equal(4, (Default with { FullSlackMaxFloor = 60 }).SlackAt(f))); // ZoneLevelSlackMaxFloor 60: the old rule
        Assert.Equal(new PopulationLevelRule(4, 12, 3), Default);
    }

    [Fact]
    public void LowBands_StillFitTheStarterZones()
    {
        PopulationZoneAtlas atlas = Atlas();
        var random = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            PopulationPlacement alliance = atlas.Pick(1, 10, Team.Alliance, Default, new Dictionary<uint, int>(), random)!;
            Assert.Equal(Elwynn, alliance.Zone.ZoneId);
            Assert.Equal(alliance.Location.Id == 106 ? 1 : 2, alliance.LowLevel); // floors 5 (Goldshire) and 6 (Eastvale), minus the whole slack
            PopulationPlacement horde = atlas.Pick(1, 1, Team.Horde, Default, new Dictionary<uint, int>(), random)!;
            Assert.Equal(Durotar, horde.Zone.ZoneId);
        }

        Assert.True(PopulationZoneAtlas.Fits(atlas.Find(Elwynn)!, 1, Team.Alliance, Default));
        Assert.True(PopulationZoneAtlas.Fits(atlas.Find(Durotar)!, 1, Team.Horde, Default));
        // Sentinel Hill (15) keeps a slack of 2: a level 13 bot fits Westfall, a level 12 one does not.
        Assert.True(PopulationZoneAtlas.Fits(atlas.Find(Westfall)!, 13, Team.Alliance, Default));
        Assert.False(PopulationZoneAtlas.Fits(atlas.Find(Westfall)!, 12, Team.Alliance, Default));
    }

    [Theory]
    [InlineData(Team.Alliance)]
    [InlineData(Team.Horde)]
    public void EveryPlacement_IsNeitherUnderLevelledNorOutLevelledWhereItLands_SoRelocationDoesNotPingPong(Team team)
    {
        PopulationZoneAtlas atlas = Atlas();
        var random = new Random(7);
        for (int level = 1; level <= 60; level++)
        {
            for (int i = 0; i < 20; i++)
            {
                if (atlas.Pick(level, level, team, Default, new Dictionary<uint, int>(), random) is not { } placement) continue;
                Assert.True(PopulationZoneAtlas.Fits(placement.Zone, level, team, Default));
                Assert.False(PopulationZoneAtlas.UnderLevelled(placement.Zone, level, team, Default), $"level {level} placed in {placement.Zone.ZoneId} is under-levelled there");
                Assert.True(level <= placement.Zone.MaxLevel);
                Assert.InRange(level, placement.LowLevel, placement.HighLevel);
            }
        }
    }

    [Fact]
    public void UncoveredBands_IsEmptyWhenEveryBandHasAZoneForBothTeams_AndNamesTheOnesThatDoNot()
    {
        PopulationBand[] bands =
        [
            new() { MinLevel = 1, MaxLevel = 10, Weight = 1 }, new() { MinLevel = 31, MaxLevel = 40, Weight = 1 },
            new() { MinLevel = 41, MaxLevel = 50, Weight = 1 }, new() { MinLevel = 51, MaxLevel = 60, Weight = 1 },
            new() { MinLevel = 11, MaxLevel = 20, Weight = 0 }, // no weight: never checked
        ];
        PopulationZoneAtlas atlas = Atlas();
        Assert.Empty(atlas.UncoveredBands(bands, [Team.Alliance, Team.Horde], Default));

        // Without Feralas, Deadwind Pass and the level 45 zone, only Morgan's-Plot-free zones are left: nothing takes 51-60.
        PopulationZoneAtlas small = PopulationZoneAtlas.Build(Areas(), Graveyards(), [0, 1], new HashSet<uint> { Feralas, Deadwind, Level45, Hinterlands }, Terrain, Creatures);
        Assert.Equal(
            [(51, 60, Team.Alliance), (51, 60, Team.Horde)],
            small.UncoveredBands(bands, [Team.Alliance, Team.Horde], Default).Select(u => (u.Band.MinLevel, u.Band.MaxLevel, u.Team)).ToArray());
        Assert.Equal([(51, 60, Team.Horde)], small.UncoveredBands(bands, [Team.Horde], Default).Select(u => (u.Band.MinLevel, u.Band.MaxLevel, u.Team)).ToArray());

        // The check follows the rule: a band whose only zone needs the slack is covered by the flat rule and not by the default one.
        PopulationBand[] band4244 = [new() { MinLevel = 42, MaxLevel = 44, Weight = 1 }];
        PopulationZoneAtlas level45Only = PopulationZoneAtlas.Build(Areas(), Graveyards(), [1], new HashSet<uint> { Feralas, Desolace, Durotar }, Terrain, Creatures);
        Assert.Single(level45Only.UncoveredBands(band4244, [Team.Alliance], Default));
        Assert.Empty(level45Only.UncoveredBands(band4244, [Team.Alliance], PopulationLevelRule.Flat(4)));
    }

    // FactionTemplate.dbc-shaped rows: 1 Human and 2 Orc (players), 14 Monster (hostile to every player), 694 a Wildhammer-like guard
    // (friendly to the Alliance, hostile to the Horde), 35 friendly to all.
    private static FactionTemplateCatalog Factions() => new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
        new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
        new FactionTemplateRecord(14, 7, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
        new FactionTemplateRecord(694, 471, 0, OwnMask: 0, FriendlyMask: 2, HostileMask: 4),
        new FactionTemplateRecord(35, 35, 0, OwnMask: 0, FriendlyMask: 15, HostileMask: 0),
    ]);

    private static CreatureSpawn Spawn(uint guid, uint entry, float x, float y, uint map = 1) => new() { Guid = guid, Entry = entry, MapId = map, X = x, Y = y, Z = 0 };

    [Fact]
    public void TheSurvey_IsTheLowerMedianLevelOfTheHostileCreaturesInTheRadius()
    {
        var templates = new Dictionary<uint, CreatureTemplate>
        {
            [1] = new() { Entry = 1, Name = "Howler", MinLevel = 43, MaxLevel = 44, Faction = 14 },
            [2] = new() { Entry = 2, Name = "Bear", MinLevel = 44, MaxLevel = 45, Faction = 14 },
            [3] = new() { Entry = 3, Name = "Screecher", MinLevel = 46, MaxLevel = 46, Faction = 14 },
            [4] = new() { Entry = 4, Name = "Sentry", MinLevel = 55, MaxLevel = 55, Faction = 694 },
            [5] = new() { Entry = 5, Name = "Spirit Healer", MinLevel = 60, MaxLevel = 60, Faction = 35 },
            [6] = new() { Entry = 6, Name = "Peasant", MinLevel = 60, MaxLevel = 60, Faction = 14, Civilian = true },
            [7] = new() { Entry = 7, Name = "Silithid", MinLevel = 57, MaxLevel = 57, Faction = 14 },
            [8] = new() { Entry = 8, Name = "Trigger", MinLevel = 60, MaxLevel = 60, Faction = 14, UnitFlags = 0x02000000 }, // not selectable
        };
        var at = new WorldSafeLoc(849, 1, 0f, 0f, 0f, 0f, "Dire Maul");
        List<CreatureSpawn> spawns =
        [
            Spawn(1, 1, 10, 0), Spawn(2, 1, 0, 20), Spawn(3, 2, 30, 30), Spawn(4, 3, -50, 0), Spawn(5, 5, 5, 5), Spawn(6, 6, 6, 6),
            Spawn(7, 7, 7, 7), Spawn(8, 7, 8, 8), Spawn(9, 7, 9, 9), // gated by an event
            Spawn(10, 3, 150, 0), // outside the radius
            Spawn(11, 3, 1, 1, map: 0), // another map
            Spawn(12, 8, 2, 2), Spawn(13, 8, 3, 3), Spawn(14, 8, 4, 4), // not selectable
            Spawn(15, 3, 1, 2) with { Z = 16 }, Spawn(16, 3, 2, 1) with { Z = -16 }, Spawn(17, 3, 2, 3) with { Z = -16 }, // another floor (|dz| > 15)
            Spawn(18, 1, 3, 2) with { Z = 15 }, // at the bound: counts
        ];
        HashSet<uint> gated = [7, 8, 9];
        FactionTemplateCatalog factions = Factions();
        // Hostile in the radius and height: 43, 43, 43, 44, 46 -> the lower median is 43.
        Assert.Equal(43, PopulationCreatureSurvey.LevelNear(at, Team.Alliance, 100, spawns, e => templates.GetValueOrDefault(e), factions, gated));
        Assert.Equal(44, PopulationCreatureSurvey.LevelNear(at, Team.Alliance, 100, spawns, e => templates.GetValueOrDefault(e), factions, new HashSet<uint>())); // 43 x3, 44, 46, 57 x3

        // Four sentries hostile to the Horde only: the Horde sees 43 x3, 44, 46, 55 x4 -> 46; the Alliance still 43.
        spawns.AddRange([Spawn(20, 4, 3, 0), Spawn(21, 4, 4, 0), Spawn(22, 4, 5, 0), Spawn(23, 4, 6, 0)]);
        Assert.Equal(46, PopulationCreatureSurvey.LevelNear(at, Team.Horde, 100, spawns, e => templates.GetValueOrDefault(e), factions, gated));
        Assert.Equal(43, PopulationCreatureSurvey.LevelNear(at, Team.Alliance, 100, spawns, e => templates.GetValueOrDefault(e), factions, gated));

        // Fewer than three hostile creatures, a zero radius or no player template give no level.
        Assert.Equal(0, PopulationCreatureSurvey.LevelNear(at, Team.Alliance, 5, spawns, e => templates.GetValueOrDefault(e), factions, gated));
        Assert.Equal(0, PopulationCreatureSurvey.LevelNear(at, Team.Alliance, 0, spawns, e => templates.GetValueOrDefault(e), factions, gated));
        Assert.Equal(0, PopulationCreatureSurvey.LevelNear(at, Team.Alliance, 100, spawns, e => templates.GetValueOrDefault(e), FactionTemplateCatalog.Empty, gated));
    }
}
