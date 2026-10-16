using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The dungeon and raid catalogue (<see cref="PlayerbotRunCatalogue"/>) over real classic-db z2815 encounters, creatures and area triggers
/// (see <see cref="RunFixtures"/>): wings, boss order, scripted encounters, level bands, sizes, exclusions and entrances.
/// </summary>
public sealed class PlayerbotRunCatalogueTests
{
    private static PlayerbotRunPlan Plan(PlayerbotRunCatalogue catalogue, uint map, int segment = 0)
        => catalogue.Find(map, segment) ?? throw new Xunit.Sdk.XunitException($"no plan for {map}:{segment}; excluded: {string.Join("; ", catalogue.Exclusions.Select(e => $"{e.MapId}:{e.Segment} {e.Reason}"))}");

    private static string[] Names(PlayerbotRunPlan plan) => [.. plan.Bosses.Select(b => b.Name)];

    [Fact]
    public void Segments_SplitAtLastEncounterMarkers_TrailingNonFinalJoinLastSegment()
    {
        // Scarlet Monastery (189): markers on Thalnos, Doan, Herod and Whitemane (lastEncounterDungeon 18, 165, 163, 164) cut four wings.
        InstanceEncounter[] sm = [.. Encounters().Where(e => e.MapId == ScarletMonastery)];
        List<InstanceEncounter[]> wings = PlayerbotRunCatalogue.Segments(sm);
        Assert.Equal(4, wings.Count);
        Assert.Equal([["Interrogator Vishas", "Bloodmage Thalnos"], ["Houndmaster Loksey", "Arcanist Doan"], ["Herod"], ["High Inquisitor Fairbanks", "High Inquisitor Whitemane"]],
            wings.Select(w => w.Select(e => e.Name).ToArray()).ToArray());

        // Razorfen Kraul (47): Charlga carries the marker (16); Agathelos the Raging comes after it and joins the only wing.
        List<InstanceEncounter[]> kraul = PlayerbotRunCatalogue.Segments([.. Encounters().Where(e => e.MapId == RazorfenKraul)]);
        InstanceEncounter[] wing = Assert.Single(kraul);
        Assert.Equal("Agathelos the Raging", wing[^1].Name);
        Assert.Equal(6, wing.Length);

        // Ragefire Chasm (389): Taragaman carries the marker but is second; Jergosh and Bazzalan trail it and join its wing.
        InstanceEncounter[] chasm = Assert.Single(PlayerbotRunCatalogue.Segments([.. Encounters().Where(e => e.MapId == RagefireChasm)]));
        Assert.Equal(["Oggleflint", "Taragaman the Hungerer", "Jergosh the Invoker", "Bazzalan"], chasm.Select(e => e.Name));

        // No marker at all (the Naxxramas fixture): one wing. Nothing: no wing.
        Assert.Single(PlayerbotRunCatalogue.Segments([.. Encounters().Where(e => e.MapId == Naxxramas)]));
        Assert.Empty(PlayerbotRunCatalogue.Segments([]));
    }

    [Fact]
    public void Segments_ThroughTheCatalogue_GiveScarletMonasteryFourWingsWithTheirOwnBosses()
    {
        PlayerbotRunCatalogue catalogue = Build();

        PlayerbotRunPlan[] wings = [.. catalogue.Plans.Where(p => p.MapId == ScarletMonastery)];
        Assert.Equal([0, 1, 2, 3], wings.Select(w => w.Segment));
        Assert.All(wings, w => Assert.Equal(4, w.SegmentCount));
        Assert.Equal(["Interrogator Vishas", "Bloodmage Thalnos"], Names(wings[0]));
        Assert.Equal(["Houndmaster Loksey", "Arcanist Doan"], Names(wings[1]));
        Assert.Equal(["Herod"], Names(wings[2]));
        Assert.Equal(["High Inquisitor Fairbanks", "High Inquisitor Whitemane"], Names(wings[3]));
        Assert.Equal("Scarlet Monastery:2", wings[2].Label);
        Assert.Equal("The Deadmines", Plan(catalogue, Deadmines).Label);
    }

    [Fact]
    public void Segments_RouteTheFinalBossLast_WhateverItsEncounterIndex()
    {
        // Ragefire Chasm: the marked final boss (Taragaman, index 1) is routed after the later-indexed Jergosh and Bazzalan.
        PlayerbotRunPlan chasm = Plan(Build(), RagefireChasm);

        Assert.Equal(["Oggleflint", "Jergosh the Invoker", "Bazzalan", "Taragaman the Hungerer"], Names(chasm));
        Assert.True(chasm.Bosses[^1].Final);
        Assert.Single(chasm.Bosses, b => b.Final);

        // Razorfen Kraul: the trailing, unmarked Agathelos is routed before the marked Charlga.
        PlayerbotRunPlan kraul = Plan(Build(), RazorfenKraul);
        Assert.Equal(["Roogug", "Aggem Thorncurse", "Death Speaker Jargba", "Overlord Ramtusk", "Agathelos the Raging", "Charlga Razorflank"], Names(kraul));
    }

    [Fact]
    public void Bosses_OrderedByEncounterIndex_FinalLast_UnspawnedAreScripted()
    {
        PlayerbotRunPlan deadmines = Plan(Build(), Deadmines);

        // Sneed (creature 643) has a template but no static spawn: he is scripted, not routed. VanCleef carries the marker and comes last.
        Assert.Equal(["Rhahk'zor", "Gilnid", "Mr. Smite", "Cookie", "Captain Greenskin", "Edwin VanCleef"], Names(deadmines));
        Assert.Equal(["Sneed"], deadmines.Scripted);
        Assert.Equal([0u, 2u, 3u, 4u, 5u, 6u], deadmines.Bosses.Select(b => b.EncounterIndex));
        Assert.Equal([644u, 1763u, 646u, 645u, 647u, 639u], deadmines.Bosses.Select(b => b.Entry));
        Assert.Equal([161u, 163u, 164u, 165u, 166u, 167u], deadmines.Bosses.Select(b => b.EncounterId));
        Assert.Equal(new System.Numerics.Vector3(-87.369f, -819.895f, 39.3004f), deadmines.Bosses[^1].Position);

        // Molten Core: Majordomo and Ragnaros are only summoned; the eight spawned bosses are routed in order and none is marked final.
        PlayerbotRunPlan core = Plan(Build(), MoltenCore);
        Assert.Equal(["Lucifron", "Magmadar", "Gehennas", "Garr", "Shazzrah", "Baron Geddon", "Sulfuron Harbinger", "Golemagg the Incinerator"], Names(core));
        Assert.Equal(["Majordomo Executus", "Ragnaros"], core.Scripted);
        Assert.DoesNotContain(core.Bosses, b => b.Final);
    }

    [Fact]
    public void CreditTypeSpell_IsScripted_NotRouted()
    {
        // Naxxramas: The Four Horsemen is credited by spell 59450 (credit type 1). A creature that happens to have entry 59450 must not
        // make it a routed boss. The entrance trigger of the fixture is moved to the continent so the wing is offered at all (the real
        // one stands inside Naxxramas, see Excluded_WithReason).
        (var template, var spawn) = Creature(Naxxramas, FourHorsemenSpell, "Impostor with the spell id as entry", 63, 3000f, -3400f, 290f, 5339999);
        AreaTriggerTemplate[] triggers = [.. Triggers().Select(t => t.Id == 4156 ? t with { MapId = 0 } : t)];

        PlayerbotRunPlan naxx = Plan(Build(triggers: triggers, creatures: RunFixtures.Creatures((template, spawn))), Naxxramas);

        Assert.Equal(["Anub'Rekhan"], Names(naxx));
        Assert.Equal(["The Four Horsemen"], naxx.Scripted);
        Assert.DoesNotContain(naxx.Bosses, b => b.Entry == FourHorsemenSpell);
    }

    [Fact]
    public void Band_IsEntranceLevelFloored_BossLevelsMinusBelow_PlusAbove()
    {
        var options = new PlayerbotRunOptions();
        PlayerbotRunCatalogue catalogue = Build();

        // Deadmines: bosses 19..21, entrance 10 -> 19 - 3 .. 21 + 3.
        Assert.Equal(((byte)16, (byte)24), Plan(catalogue, Deadmines).Band(options));
        // Ragefire Chasm: bosses all 16, entrance 8 -> 13..19.
        Assert.Equal(((byte)13, (byte)19), Plan(catalogue, RagefireChasm).Band(options));
        // Raids: bosses 63, entrance 50 -> 60..66.
        Assert.Equal(((byte)60, (byte)66), Plan(catalogue, MoltenCore).Band(options));
        Assert.Equal(((byte)60, (byte)66), Plan(catalogue, OnyxiasLair).Band(options));
        // Scarlet Monastery wing 0: bosses 32 and 34, entrance 20 -> 29..37.
        Assert.Equal(((byte)29, (byte)37), Plan(catalogue, ScarletMonastery).Band(options));

        // The entrance level floors the band: with a wide margin below the bosses the lower end stays at the entrance's 10.
        Assert.Equal(((byte)10, (byte)24), Plan(catalogue, Deadmines).Band(new PlayerbotRunOptions { BandBelowBoss = 20 }));
        // Zero margins: exactly the bosses' levels.
        Assert.Equal(((byte)19, (byte)21), Plan(catalogue, Deadmines).Band(new PlayerbotRunOptions { BandBelowBoss = 0, BandAboveBoss = 0 }));
    }

    [Fact]
    public void LevelBandOverrides_ReplaceMapOrSegmentBand()
    {
        PlayerbotRunCatalogue catalogue = Build();
        var options = new PlayerbotRunOptions
        {
            LevelBandOverrides = new Dictionary<string, string> { ["36"] = "15-22", ["189"] = "30-40", ["189:2"] = "38-45", ["47:3"] = "1-5" },
        };

        // A map entry replaces the derived band of the map.
        Assert.Equal(((byte)15, (byte)22), Plan(catalogue, Deadmines).Band(options));
        // A wing entry beats the map entry for that wing only.
        Assert.Equal(((byte)38, (byte)45), Plan(catalogue, ScarletMonastery, 2).Band(options));
        Assert.Equal(((byte)30, (byte)40), Plan(catalogue, ScarletMonastery, 0).Band(options));
        Assert.Equal(((byte)30, (byte)40), Plan(catalogue, ScarletMonastery, 3).Band(options));
        // An entry for another map or a wing that does not exist changes nothing.
        Assert.Equal(((byte)13, (byte)19), Plan(catalogue, RagefireChasm).Band(options));
        Assert.Equal(((byte)30, (byte)40), Plan(catalogue, RazorfenKraul).Band(new PlayerbotRunOptions { LevelBandOverrides = new() { ["47"] = "30-40", ["47:3"] = "1-5" } }));

        // Entries that do not parse or are out of order are ignored: the derived band stands.
        var broken = new PlayerbotRunOptions
        {
            LevelBandOverrides = new Dictionary<string, string> { ["36"] = "30-10", ["abc"] = "1-5", ["36:x"] = "1-5", ["36:0"] = "0-5", ["36:1"] = "10-64" },
        };
        Assert.Equal(((byte)16, (byte)24), Plan(catalogue, Deadmines).Band(broken));
    }

    [Fact]
    public void Size_FromMapTemplate_DungeonCappedAtFive_Raid10_20_40()
    {
        MapTemplate Dungeon(uint limit) => new(36, 0, MapType.Instance, 0, limit, 0, 0, 0, 0, "d", "");
        MapTemplate Raid(uint limit) => new(409, 0, MapType.Raid, 0, limit, 0, 0, 0, 0, "r", "");

        // classic-db instance_template gives the Deadmines maxPlayers 10, the party is still five.
        Assert.Equal(5, PlayerbotGroupContent.RunGroupSize(Dungeon(10)));
        Assert.Equal(5, PlayerbotGroupContent.RunGroupSize(Dungeon(5)));
        Assert.Equal(5, PlayerbotGroupContent.RunGroupSize(Dungeon(0)));
        Assert.Equal(3, PlayerbotGroupContent.RunGroupSize(Dungeon(3)));
        Assert.Equal(10, PlayerbotGroupContent.RunGroupSize(Raid(10)));
        Assert.Equal(20, PlayerbotGroupContent.RunGroupSize(Raid(20)));
        Assert.Equal(40, PlayerbotGroupContent.RunGroupSize(Raid(40)));

        // Through the catalogue: Deadmines 5, Molten Core and Onyxia (maxPlayers 40) 40.
        PlayerbotRunCatalogue catalogue = Build();
        Assert.Equal((5, false), (Plan(catalogue, Deadmines).Size, Plan(catalogue, Deadmines).Raid));
        Assert.Equal((40, true), (Plan(catalogue, MoltenCore).Size, Plan(catalogue, MoltenCore).Raid));
        Assert.Equal((40, true), (Plan(catalogue, OnyxiasLair).Size, Plan(catalogue, OnyxiasLair).Raid));
    }

    [Fact]
    public void Excluded_WithReason_NoEntranceOnContinent_NoSpawnedBoss_NoEncounterData_MapNotInstance()
    {
        PlayerbotRunCatalogue catalogue = Build(encounters: [.. Encounters(), new(1, 0, 9000, 0, 1, false, "Not a dungeon"), new(9999, 0, 9001, 0, 1, false, "Unknown map")]);

        PlayerbotRunExclusion Exclusion(uint map) => Assert.Single(catalogue.Exclusions, e => e.MapId == map);

        // Blackwing Lair: its only trigger (3726) stands on map 229, inside Blackrock Spire.
        Assert.Equal((0, PlayerbotRunCatalogue.NoEntrance, "Blackwing Lair"), (Exclusion(BlackwingLair).Segment, Exclusion(BlackwingLair).Reason, Exclusion(BlackwingLair).MapName));
        Assert.DoesNotContain(catalogue.Plans, p => p.MapId == BlackwingLair);
        // Naxxramas' entrance trigger (4156) stands on map 533 itself.
        Assert.Equal(PlayerbotRunCatalogue.NoEntrance, Exclusion(Naxxramas).Reason);
        // No encounter rows: Blackrock Spire and Blackrock Depths in this fixture.
        Assert.Equal(PlayerbotRunCatalogue.NoEncounterData, Exclusion(BlackrockSpire).Reason);
        Assert.Equal(PlayerbotRunCatalogue.NoEncounterData, Exclusion(230).Reason);
        // Encounters that name a continent, or a map that is not known at all.
        Assert.Equal(PlayerbotRunCatalogue.MapNotInstance, Exclusion(1).Reason);
        Assert.Equal(("9999", PlayerbotRunCatalogue.MapNotInstance), (Exclusion(9999).MapName, Exclusion(9999).Reason));

        // No creature has a static spawn: every wing with encounters has no spawned boss.
        PlayerbotRunCatalogue unspawned = Build(creatures: ArcaneCore.Kernel.WorldData.Creatures.CreatureContent.Empty);
        Assert.Empty(unspawned.Plans);
        Assert.Equal(PlayerbotRunCatalogue.NoSpawnedBoss, Assert.Single(unspawned.Exclusions, e => e.MapId == Deadmines).Reason);
        Assert.Equal(4, unspawned.Exclusions.Count(e => e.MapId == ScarletMonastery && e.Reason == PlayerbotRunCatalogue.NoSpawnedBoss));

        // Exclusions are ordered by map then wing.
        Assert.Equal(catalogue.Exclusions.OrderBy(e => e.MapId).ThenBy(e => e.Segment).ToArray(), catalogue.Exclusions);
        Assert.Equal(catalogue.Plans.OrderBy(p => p.MapId).ThenBy(p => p.Segment).ToArray(), catalogue.Plans);
    }

    [Fact]
    public void NoEncounterRows_IsAnEmptyCatalogue_EveryDungeonExcluded()
    {
        PlayerbotRunCatalogue catalogue = Build(encounters: []);

        Assert.Empty(catalogue.Plans);
        Assert.All(catalogue.Exclusions, e => Assert.Equal(PlayerbotRunCatalogue.NoEncounterData, e.Reason));
        Assert.Contains(catalogue.Exclusions, e => e.MapId == Deadmines);
        Assert.Empty(PlayerbotRunCatalogue.Empty.Plans);
        Assert.Null(PlayerbotRunCatalogue.Empty.Find(Deadmines, 0));
    }

    [Fact]
    public void Entrance_IsNearestContinentTriggerToSegmentFirstBoss()
    {
        PlayerbotRunCatalogue catalogue = Build();

        // Scarlet Monastery: each wing's teleport lands nearest its first routed boss (45 graveyard, 614 library, 612 armory, 610 cathedral).
        Assert.Equal([45u, 614u, 612u, 610u], Enumerable.Range(0, 4).Select(w => Plan(catalogue, ScarletMonastery, w).EntranceTrigger));
        PlayerbotRunPlan graveyard = Plan(catalogue, ScarletMonastery, 0);
        Assert.Equal((0u, 20, new System.Numerics.Vector3(2924.38f, -798.429f, 161.611f), new System.Numerics.Vector3(1687.27f, 1050.09f, 18.6773f)),
            (graveyard.EntranceMap, (int)graveyard.EntranceLevel, graveyard.Entrance, graveyard.InsideStart));

        // Deadmines: trigger 78 on Eastern Kingdoms, level 10; Onyxia: trigger 2848 on Kalimdor.
        PlayerbotRunPlan deadmines = Plan(catalogue, Deadmines);
        Assert.Equal((78u, 0u, (byte)10), (deadmines.EntranceTrigger, deadmines.EntranceMap, deadmines.EntranceLevel));
        PlayerbotRunPlan onyxia = Plan(catalogue, OnyxiasLair);
        Assert.Equal((2848u, 1u, (byte)50), (onyxia.EntranceTrigger, onyxia.EntranceMap, onyxia.EntranceLevel));

        // Molten Core: 2886 also leads in but stands on map 230 (Blackrock Depths); the two continent windows land at the same spot and the
        // lower id wins.
        Assert.Equal(3528u, Plan(catalogue, MoltenCore).EntranceTrigger);
    }

    [Fact]
    public void CreatureContentNotYetInstalled_IsNotACatalogueInput_ElseEveryWingIsLeftOutAsNoSpawnedBoss()
    {
        // The live startup bug: the coordinator built the catalogue before CreatureWorldFeature's posted Install ran, from the
        // CreatureContent.Empty placeholder, and logged every wing (Deadmines, Scarlet Monastery 0-3, ...) as no-spawned-boss.
        PlayerbotRunCatalogue fromPlaceholder = Build(creatures: CreatureContent.Empty);
        Assert.Empty(fromPlaceholder.Plans);
        Assert.Contains(fromPlaceholder.Exclusions, e => e.MapId == Deadmines && e.Reason == PlayerbotRunCatalogue.NoSpawnedBoss);

        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var feature = new CreatureWorldFeature(services, NullLogger<CreatureWorldFeature>.Instance);
        Assert.False(feature.ContentInstalled);
        Assert.Null(PlayerbotGroupCoordinator.CatalogueCreatures(feature));
        Assert.Null(PlayerbotGroupCoordinator.CatalogueCreatures(null));

        // Once installed the real content is the input (no spawns here: Install needs an attached world to start map systems).
        var installed = new CreatureContent([], [], [], [], []);
        feature.Install(installed);
        Assert.Same(installed, PlayerbotGroupCoordinator.CatalogueCreatures(feature));

        // And the real content makes Deadmines runnable.
        Assert.NotNull(Build().Find(Deadmines, 0));
    }
}
