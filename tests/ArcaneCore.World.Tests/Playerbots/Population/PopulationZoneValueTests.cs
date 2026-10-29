using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The zone content value (zone-value spec, RED tests 1-6): which zones a level-capped bot is sent to. Zone ids: 100/101/102 are
/// valued zones (quests, a rep faction, dungeon gear), 41 is "Deadwind-like" (level 55-61 elites, no quests).
/// </summary>
public sealed class PopulationZoneValueTests
{
    private const uint ValuedZone = 100;
    private const uint Deadwind = 41;
    private const int Capped = PlayerbotPopulationOptions.MaxLevel;

    private static AreaTemplate Area(uint entry, uint map, uint zone, uint flags, int level, string name, uint team)
        => new(entry, map, zone, entry, flags, level, name, team, 0);

    // Each zone gets two leveled sub-areas [low, high]. The graveyard of a zone is linked to the zone and stands on map 0.
    private static (List<AreaTemplate> Areas, GraveyardCatalog Graveyards) World(params (uint Zone, int Low, int High)[] zones)
    {
        var areas = new List<AreaTemplate>();
        var rows = new List<(uint Id, uint Map, uint Link, uint Team)>();
        foreach ((uint zone, int low, int high) in zones)
        {
            areas.Add(Area(zone, 0, 0, 0, 0, "Zone" + zone, AreaTeams.None));
            areas.Add(Area(zone + 1000, 0, zone, 0, low, "Low" + zone, 0));
            areas.Add(Area(zone + 2000, 0, zone, 0, high, "High" + zone, 0));
            rows.Add((zone, 0, zone, 0));
        }

        WorldSafeLoc[] locs = [.. rows.Select(r => new WorldSafeLoc(r.Id, r.Map, r.Id * 10f, r.Id * 20f, 50f, 0f, "GY" + r.Id))];
        GraveyardLink[] links = [.. rows.Select(r => new GraveyardLink(r.Id, r.Link, r.Team))];
        return (areas, GraveyardCatalog.Build(new GraveyardContent(locs, links), null, out _));
    }

    private static PopulationZoneAtlas Atlas(params (uint Zone, int Low, int High)[] zones)
    {
        (List<AreaTemplate> areas, GraveyardCatalog graveyards) = World(zones);
        return PopulationZoneAtlas.Build(areas, graveyards, [0], new HashSet<uint>());
    }

    private static PopulationLevelRule Rule => PopulationLevelRule.Flat(4);

    private static ZoneContentFacts Valued(uint zone)
    {
        ZoneQuestFact[] quests = [.. Enumerable.Range(0, 6).Select(i => new ZoneQuestFact((uint)(zone * 100 + i), 50, 58 + i % 3, 0, 0, i == 0 ? [529u] : []))];
        // One piece per slot (chest 5, head 1, legs 7, feet 8), all dungeon drops that beat an empty slot.
        ZoneGearFact[] gear = [.. new uint[] { 5, 1, 7, 8 }.Select(t => new ZoneGearFact(zone * 10 + t, t, 62, 55, uint.MaxValue, 0, ReputationRank.Neutral, 0, false))];
        return new ZoneContentFacts(zone) { Quests = quests, Gear = gear };
    }

    private static ZoneContentFacts DeadwindLike()
    {
        int[] levels = [.. Enumerable.Repeat(58, 30).Concat(Enumerable.Repeat(60, 40)).Concat(Enumerable.Repeat(61, 30))];
        int[] elites = [.. levels.Skip(10)]; // 90 of the 100 spawns are elite
        return new ZoneContentFacts(Deadwind)
        {
            HostileLevels = new Dictionary<Team, int[]> { [Team.Alliance] = levels, [Team.Horde] = levels },
            EliteLevels = new Dictionary<Team, int[]> { [Team.Alliance] = elites, [Team.Horde] = elites },
        };
    }

    private static PopulationZoneValue Value(params ZoneContentFacts[] facts) => new(facts.ToDictionary(f => f.ZoneId));

    private static PopulationBotProfile Fresh60() => PopulationBotProfile.Fresh(Capped, Team.Alliance);

    [Fact]
    public void ValuedZoneOutranksMobOnlyZone()
    {
        PopulationZoneAtlas atlas = Atlas((ValuedZone, 55, 60), (Deadwind, 55, 61));
        PopulationValueWeights w = PopulationValueWeights.Default;
        PopulationZoneValue value = Value(Valued(ValuedZone), DeadwindLike());

        IReadOnlyList<ZoneScore> ranking = atlas.Rank(Fresh60(), Rule, value, w, new Dictionary<uint, int>());

        Assert.NotEmpty(ranking);
        Assert.Equal(ValuedZone, ranking[0].ZoneId);
        int deadwindAt = ranking.ToList().FindIndex(s => s.ZoneId == Deadwind);
        Assert.True(deadwindAt == -1 || deadwindAt > 0, "the mob-only zone must not outrank the valued zone");

        ZoneScore deadwind = PopulationZoneScoring.Score(atlas.Find(Deadwind)!, value.Find(Deadwind), Fresh60(), w);
        Assert.False(deadwind.Valued);
        Assert.Equal(0, deadwind.Quest);
        Assert.Equal(0, deadwind.Reputation);
        Assert.Equal(0, deadwind.Gear);
        Assert.True(deadwind.Elite > 0, "a mostly elite zone is penalised");

        ZoneScore good = PopulationZoneScoring.Score(atlas.Find(ValuedZone)!, value.Find(ValuedZone), Fresh60(), w);
        Assert.True(good.Valued);
        Assert.True(good.Quest > 0 && good.Reputation > 0 && good.Gear > 0);
        Assert.True(good.Total > deadwind.Total);
    }

    [Fact]
    public void ExaltedFullyGearedBotDropsRepAndGear()
    {
        PopulationZoneAtlas atlas = Atlas((ValuedZone, 55, 60));
        PopulationValueWeights w = PopulationValueWeights.Default;
        PopulationZoneValue value = Value(Valued(ValuedZone));
        PopulationZone zone = atlas.Find(ValuedZone)!;

        ZoneScore fresh = PopulationZoneScoring.Score(zone, value.Find(ValuedZone), Fresh60(), w);
        PopulationBotProfile maxed = Fresh60() with { RankOf = _ => ReputationRank.Exalted, EquippedItemLevel = _ => 100 };
        ZoneScore done = PopulationZoneScoring.Score(zone, value.Find(ValuedZone), maxed, w);

        Assert.True(fresh.Reputation > 0 && fresh.Gear > 0);
        Assert.Equal(0, done.Reputation);
        Assert.Equal(0, done.Gear);
        Assert.True(done.Quest > 0);
        Assert.Equal(fresh.Quest, done.Quest);
        Assert.True(done.Valued, "the quests alone still make the zone worth going to");

        // Each term is capped on its own: exalted but naked still wants the gear, geared but neutral still wants the reputation.
        ZoneScore exaltedOnly = PopulationZoneScoring.Score(zone, value.Find(ValuedZone), Fresh60() with { RankOf = _ => ReputationRank.Exalted }, w);
        Assert.Equal(0, exaltedOnly.Reputation);
        Assert.Equal(fresh.Gear, exaltedOnly.Gear);
        ZoneScore gearedOnly = PopulationZoneScoring.Score(zone, value.Find(ValuedZone), Fresh60() with { EquippedItemLevel = _ => 100 }, w);
        Assert.Equal(0, gearedOnly.Gear);
        Assert.Equal(fresh.Reputation, gearedOnly.Reputation);

        // A completed quest is not counted: with every quest done the zone has no quest value.
        ZoneScore questsDone = PopulationZoneScoring.Score(zone, value.Find(ValuedZone), Fresh60() with { QuestDone = _ => true }, w);
        Assert.Equal(0, questsDone.Quest);
    }

    [Fact]
    public void SoftCapSpreadsBots()
    {
        PopulationZoneAtlas atlas = Atlas((100, 55, 60), (101, 55, 60), (102, 55, 60));
        PopulationValueWeights w = PopulationValueWeights.Default with { ZoneSoftCap = 10, TopZones = 3 };
        PopulationZoneValue value = Value(Valued(100), Valued(101), Valued(102));

        Dictionary<uint, int> Fill(int calls, out int placed)
        {
            var assigned = new Dictionary<uint, int>();
            var random = new Random(12345);
            placed = 0;
            for (int i = 0; i < calls; i++)
            {
                PopulationSafeSelection? pick = atlas.PickValued(Fresh60(), Rule, value, w, assigned, random, (_, _) => true);
                if (pick is null) continue;
                placed++;
                uint zone = pick.Placement.Zone.ZoneId;
                assigned[zone] = assigned.GetValueOrDefault(zone) + 1;
            }

            return assigned;
        }

        Dictionary<uint, int> thirty = Fill(30, out int placed30);
        Assert.Equal(30, placed30); // 3 zones x cap 10 hold all 30
        Assert.All(new uint[] { 100, 101, 102 }, zone => Assert.InRange(thirty.GetValueOrDefault(zone), 1, 10));

        Dictionary<uint, int> sixty = Fill(60, out int placed60);
        Assert.All(sixty.Values, count => Assert.True(count <= 10, "no zone takes more than the soft cap"));
        Assert.Equal(30, placed60); // the other 30 get null: every valued zone is full
        Assert.All(new uint[] { 100, 101, 102 }, zone => Assert.True(sixty.GetValueOrDefault(zone) >= 1));
    }

    [Fact]
    public void FallbackDoesNotFireWhenValuedZoneExists()
    {
        // No zone fits a level 60 bot (every MaxLevel is 58): the old pick has nothing, the valued pick still finds the quest zone.
        PopulationZoneAtlas atlas = Atlas((ValuedZone, 50, 58), (101, 50, 58), (Deadwind, 50, 58));
        PopulationZoneValue value = Value(Valued(ValuedZone), DeadwindLike());
        PopulationValueWeights w = PopulationValueWeights.Default; // CapReach 6
        var assigned = new Dictionary<uint, int>();

        Assert.Null(atlas.PickSafe(Capped, Capped, Team.Alliance, Rule, assigned, new Random(7), (_, _) => true));

        var ranked = new List<ZoneScore>();
        PopulationSafeSelection? pick = atlas.PickValued(Fresh60(), Rule, value, w, assigned, new Random(7), (_, _) => true, ranked: ranked);
        Assert.NotNull(pick);
        Assert.Equal(ValuedZone, pick.Placement.Zone.ZoneId);
        Assert.Equal(Capped, pick.Level);
        Assert.Equal(Capped, pick.Placement.HighLevel);
        Assert.Equal([ValuedZone], ranked.Select(s => s.ZoneId));

        // A refused location leaves nothing valued and safe: null (the caller then falls back and logs).
        Assert.Null(atlas.PickValued(Fresh60(), Rule, value, w, assigned, new Random(7), (_, _) => false));

        // The reach boundary: 58 >= 60 - 2 reaches, 58 >= 60 - 1 does not.
        Assert.NotNull(atlas.PickValued(Fresh60(), Rule, value, w with { CapReach = 2 }, assigned, new Random(7), (_, _) => true));
        Assert.Null(atlas.PickValued(Fresh60(), Rule, value, w with { CapReach = 1 }, assigned, new Random(7), (_, _) => true));

        // Only a level-capped bot reaches: a level 59 bot is not capped and no zone fits it.
        PopulationBotProfile level59 = PopulationBotProfile.Fresh(59, Team.Alliance);
        Assert.Empty(atlas.Rank(level59, Rule, value, w, assigned));
        Assert.Null(atlas.PickValued(level59, Rule, value, w, assigned, new Random(7), (_, _) => true));
    }

    [Fact]
    public void MinTotalKeepsLessValuableZonesOutOfTheRankingAndTheDraw()
    {
        // Valued() zones are equal; a zone with fewer quests is worth less.
        PopulationZoneAtlas atlas = Atlas((100, 55, 60), (101, 55, 60), (102, 55, 60));
        PopulationZoneValue value = Value(Valued(100), Valued(101) with { Quests = [.. Valued(101).Quests.Take(2)], Gear = [] }, Valued(102) with { Quests = [.. Valued(102).Quests.Take(4)], Gear = [] });
        PopulationValueWeights w = PopulationValueWeights.Default;
        var assigned = new Dictionary<uint, int>();
        IReadOnlyList<ZoneScore> all = atlas.Rank(Fresh60(), Rule, value, w, assigned);
        Assert.Equal([100u, 102u, 101u], all.Select(s => s.ZoneId));

        // The floor is inclusive and only drops what is below it.
        Assert.Equal([100u, 102u], atlas.Rank(Fresh60(), Rule, value, w, assigned, minTotal: all[1].Total).Select(s => s.ZoneId));
        Assert.Equal([100u], atlas.Rank(Fresh60(), Rule, value, w, assigned, minTotal: all[1].Total + 0.001).Select(s => s.ZoneId));
        Assert.Empty(atlas.Rank(Fresh60(), Rule, value, w, assigned, minTotal: all[0].Total + 0.001));
        Assert.Equal(all.Select(s => s.ZoneId), atlas.Rank(Fresh60(), Rule, value, w, assigned, minTotal: double.NegativeInfinity).Select(s => s.ZoneId));

        // The draw never leaves the floor, across many seeds, and reports the filtered ranking.
        for (int seed = 0; seed < 200; seed++)
        {
            var ranked = new List<ZoneScore>();
            PopulationSafeSelection? pick = atlas.PickValued(Fresh60(), Rule, value, w, assigned, new Random(seed), (_, _) => true, ranked: ranked, minTotal: all[1].Total);
            Assert.NotNull(pick);
            Assert.NotEqual(101u, pick.Placement.Zone.ZoneId);
            Assert.Equal([100u, 102u], ranked.Select(s => s.ZoneId));
        }

        // Nothing at or above the floor and safe: null, the zones below it are not a fallback.
        Assert.Null(atlas.PickValued(Fresh60(), Rule, value, w, assigned, new Random(1), (location, _) => location.Id == 101, minTotal: all[1].Total));

        // A precomputed ranking is used as given (no second Rank).
        PopulationSafeSelection? viaCache = atlas.PickValued(Fresh60(), Rule, value, w, assigned, new Random(1), (_, _) => true, precomputed: [all[2]]);
        Assert.Equal(101u, viaCache!.Placement.Zone.ZoneId);
    }

    // ----- builder -----

    private const uint Human = 1, Orc = 2, Friendly = 35, Hostile = 14;

    private static CreatureTemplate Creature(uint entry, uint faction, byte level, uint rank = 0)
        => new() { Entry = entry, Name = "C" + entry, Faction = faction, MinLevel = level, MaxLevel = level, Rank = rank };

    private static CreatureSpawn Spawn(uint guid, uint entry, uint map, float x, float y = 0f)
        => new() { Guid = guid, Entry = entry, MapId = map, X = x, Y = y, Z = 0f };

    private static ItemTemplate Item(uint entry, uint quality, uint inventoryType, uint itemLevel = 60, uint requiredLevel = 55)
        => new() { Entry = entry, Quality = quality, InventoryType = inventoryType, ItemLevel = itemLevel, RequiredLevel = requiredLevel };

    // Points on map 0 with x in [0, 100) lie in zone 100, [100, 200) in zone 101, anything else is unknown. The dungeon trigger sits in zone 100.
    private static uint ZoneOf(uint map, float x, float y, float z) => map != 0 ? 0u : x switch { >= 0 and < 100 => 100u, >= 100 and < 200 => 101u, _ => 0u };

    private static PopulationZoneValueSources Sources()
    {
        FactionTemplateCatalog factions = new(
        [
            new FactionTemplateRecord(Human, 1, 0, OwnMask: 1, FriendlyMask: 0, HostileMask: 0),
            new FactionTemplateRecord(Orc, 2, 0, OwnMask: 2, FriendlyMask: 0, HostileMask: 0),
            new FactionTemplateRecord(Friendly, 0, 0, OwnMask: 4, FriendlyMask: 3, HostileMask: 0),
            new FactionTemplateRecord(Hostile, 0, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 3),
        ]);

        Dictionary<uint, CreatureTemplate> templates = new[]
        {
            Creature(1000, Friendly, 60), Creature(1001, Friendly, 60), Creature(4000, Friendly, 60),
            Creature(2000, Hostile, 58), Creature(2001, Hostile, 60, rank: 1),
            Creature(3000, Hostile, 60, rank: 1), Creature(3001, Hostile, 60), Creature(3100, Hostile, 60),
        }.ToDictionary(t => t.Entry);

        Dictionary<uint, CreatureSpawn[]> spawns = new()
        {
            [0] =
            [
                Spawn(1, 1000, 0, 10),   // quest 1 starter, zone 100
                Spawn(2, 1000, 0, 500),  // zone unknown: dropped
                Spawn(3, 1001, 0, 150),  // quest 2 starter, zone 101
                Spawn(4, 2000, 0, 20), Spawn(5, 2000, 0, 21), Spawn(6, 2001, 0, 22), // zone 100: two normal, one elite
                Spawn(7, 4000, 0, 30),   // reputation vendor, zone 100
                Spawn(8, 2000, 0, 600),  // hostile, zone unknown
            ],
            [36] = [Spawn(100, 3000, 36, 5), Spawn(101, 3001, 36, 6)],
            [43] = [Spawn(102, 3100, 43, 5)],
        };

        Dictionary<uint, QuestTemplate> quests = new()
        {
            [1] = new QuestTemplate { Entry = 1, MinLevel = 50, QuestLevel = 58, RewRepFaction1 = 529, RewRepValue1 = 250, RewRepFaction2 = 530, RewRepValue2 = -100 },
            [2] = new QuestTemplate { Entry = 2, MinLevel = 40, QuestLevel = 45, RequiredRaces = 1, RequiredClasses = 2 },
        };
        Dictionary<uint, uint[]> starters = new() { [1000] = [1], [1001] = [2] };

        LootContent loot = new(
        [
            (LootTableKind.Creature, new LootStoreRow(300, 5001, 20f, 0, 1, 1)),      // rare chest piece: kept
            (LootTableKind.Creature, new LootStoreRow(300, 5002, 20f, 0, 1, 1)),      // uncommon: dropped
            (LootTableKind.Creature, new LootStoreRow(300, 5003, 20f, 0, 1, 1)),      // rare but not equippable: dropped
            (LootTableKind.Creature, new LootStoreRow(300, 5004, -50f, 0, 1, 1)),     // quest item: dropped
            (LootTableKind.Creature, new LootStoreRow(300, 0, 100f, 0, -700, 1)),     // reference to reference table 700
            (LootTableKind.Reference, new LootStoreRow(700, 5005, 10f, 0, 1, 1)),     // rare legs behind the reference: kept
            (LootTableKind.Creature, new LootStoreRow(301, 5001, 20f, 0, 1, 1)),      // the same item from another boss: deduped
            (LootTableKind.Creature, new LootStoreRow(310, 5006, 20f, 0, 1, 1)),      // dungeon 43 (trigger outside every zone): dropped
            (LootTableKind.Skinning, new LootStoreRow(900, 7001, 100f, 0, 1, 1)),
        ],
        [
            new CreatureLootInfo(3000, 300, 0, 0, 0), new CreatureLootInfo(3001, 301, 0, 0, 0), new CreatureLootInfo(3100, 310, 0, 0, 0),
            new CreatureLootInfo(2000, 0, 900, 0, 0),
        ]);

        Dictionary<uint, ItemTemplate> items = new[]
        {
            Item(5001, 3, 5, 62), Item(5002, 2, 5), Item(5003, 3, 0), Item(5004, 3, 13), Item(5005, 4, 7, 66, 58), Item(5006, 3, 1),
            new ItemTemplate { Entry = 6001, Quality = 2, InventoryType = 8, ItemLevel = 55, RequiredLevel = 50, BuyPrice = 1000,
                RequiredReputationFaction = 529, RequiredReputationRank = (uint)ReputationRank.Honored },
            Item(6002, 2, 8),                                                                                          // no reputation gate: dropped
            new ItemTemplate { Entry = 6003, Quality = 2, InventoryType = 0, RequiredReputationFaction = 529 },        // not equippable: dropped
        }.ToDictionary(i => i.Entry);

        VendorItem Sells(uint item) => new() { Entry = 4000, Item = item };
        AreaTriggerTemplate[] triggers =
        [
            new(50, 0, 50f, 50f, 0f, 5f, 0f, 0f, 0f, 0f, "dungeon entrance in zone 100"),
            new(51, 0, 150f, 50f, 0f, 5f, 0f, 0f, 0f, 0f, "raid entrance in zone 101"),
            new(52, 0, 900f, 900f, 0f, 5f, 0f, 0f, 0f, 0f, "dungeon entrance in no known zone"),
        ];
        Dictionary<uint, AreaTriggerTeleport> teleports = new()
        {
            [50] = new(50, "dungeon", "", 20, 36, 0f, 0f, 0f, 0f),
            [51] = new(51, "raid", "", 60, 409, 0f, 0f, 0f, 0f),
            [52] = new(52, "dungeon 43", "", 20, 43, 0f, 0f, 0f, 0f),
        };
        Dictionary<uint, MapTemplate> maps = new()
        {
            [36] = new(36, 0, MapType.Instance, 0, 10, 0, -1, 0f, 0f, "dungeon", ""),
            [43] = new(43, 0, MapType.Instance, 0, 10, 0, -1, 0f, 0f, "dungeon 43", ""),
            [409] = new(409, 0, MapType.Raid, 0, 40, 0, -1, 0f, 0f, "raid", ""),
        };

        return new PopulationZoneValueSources(
            [0],
            map => spawns.GetValueOrDefault(map) ?? [],
            entry => templates.GetValueOrDefault(entry),
            id => quests.GetValueOrDefault(id),
            entry => starters.GetValueOrDefault(entry) ?? [],
            loot,
            id => items.GetValueOrDefault(id),
            entry => entry == 4000 ? [Sells(6001), Sells(6002), Sells(6003)] : [],
            triggers,
            id => teleports.GetValueOrDefault(id),
            id => maps.GetValueOrDefault(id),
            [new ReputationOnKillEntry(2000, 529, 0, 5, false, 10, 0, false, 0, false)],
            factions,
            new HashSet<uint>());
    }

    [Fact]
    public void Build_PutsQuestsGearAndMobsInTheirZone_AndDropsUnknownZones()
    {
        PopulationZoneValue value = PopulationZoneValue.Build(Sources(), ZoneOf, CancellationToken.None);

        ZoneContentFacts a = value.Find(100)!;
        Assert.NotNull(a);

        // Quest: the starter's spawn in zone 100 puts quest 1 there; its spawn with no zone adds nothing; zone 101 gets only quest 2.
        ZoneQuestFact quest = Assert.Single(a.Quests);
        Assert.Equal((1u, 50, 58, 0u, 0u), (quest.QuestId, quest.MinLevel, quest.QuestLevel, quest.RequiredRaces, quest.RequiredClasses));
        Assert.Equal([529u], quest.RepFactions); // the negative 530 reward is not a reputation source
        ZoneQuestFact other = Assert.Single(value.Find(101)!.Quests);
        Assert.Equal((2u, 1u, 2u), (other.QuestId, other.RequiredRaces, other.RequiredClasses));
        Assert.Null(value.Find(0));

        // Gear: only the 5-man dungeon loot (rare+, equippable, not quest, reference rows followed once, deduped), only for the trigger in the zone.
        uint[] dungeonItems = [.. a.Gear.Where(g => !g.FromVendor).Select(g => g.ItemId).Order()];
        Assert.Equal([5001u, 5005u], dungeonItems);
        ZoneGearFact chest = a.Gear.Single(g => g.ItemId == 5001);
        Assert.Equal((5u, 62, 55), (chest.InventoryType, chest.ItemLevel, chest.RequiredLevel));
        Assert.Equal([36u], a.DungeonMaps); // not the raid (409), not the trigger outside every zone (43)
        Assert.DoesNotContain(value.Find(101)!.Gear, g => !g.FromVendor);

        // Vendor gear: the reputation-gated equippable item of a creature spawned in the zone.
        ZoneGearFact vendor = Assert.Single(a.Gear, g => g.FromVendor);
        Assert.Equal((6001u, 8u, 529u, ReputationRank.Honored, 1000u), (vendor.ItemId, vendor.InventoryType, vendor.RequiredFaction, vendor.RequiredRank, vendor.BuyPrice));

        // On-kill reputation: creature 2000 pays faction 529 up to Honored; the empty second slot is no source.
        Assert.Contains(new ZoneRepSource(529, ReputationRank.Honored), a.RepSources);
        Assert.DoesNotContain(a.RepSources, s => s.FactionId == 0);

        // Mobs: friendly quest givers and the vendor do not count; the spawn without a zone does not count; elites are listed apart.
        foreach (Team team in new[] { Team.Alliance, Team.Horde })
        {
            Assert.Equal([58, 58, 60], a.HostileLevels[team]);
            Assert.Equal([60], a.EliteLevels[team]);
        }

        Assert.Equal(2, a.Skinnable); // the two creature 2000 spawns in the zone have skinning loot
    }

    [Fact]
    public void Build_ZoneOfZeroDropsEverything_AndCancellationThrows()
    {
        PopulationZoneValue none = PopulationZoneValue.Build(Sources(), (_, _, _, _) => 0u, CancellationToken.None);
        Assert.Empty(none.Zones);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PopulationZoneValue.Build(Sources(), ZoneOf, cts.Token));
    }

    // ----- options -----

    [Fact]
    public void ValueOptions_DefaultsValidate_AndAnOutOfRangeValueNamesItself()
    {
        var defaults = new PlayerbotPopulationOptions();
        defaults.Validate();
        Assert.Equal((10, 5, 8, 1, 6, 5, 1), (defaults.ValueQuestWeight, defaults.ValueReputationWeight, defaults.ValueGearWeight,
            defaults.ValueFarmWeight, defaults.ValueEliteWeight, defaults.ValueTroubleWeight, defaults.ValueDistanceWeight));
        Assert.Equal((6, 20, 5, 120, 6), (defaults.ValueQuestGrayMargin, defaults.ValueEliteSoloPct, defaults.ValueTopZones, defaults.ValueZoneSoftCap, defaults.ValueCapReach));
        Assert.Equal((true, 30, 50, 8), (defaults.ValueRouting, defaults.ValueRelocateMinStayMinutes, defaults.ValueRelocateBelowPct, defaults.ValueChecksPerPass));
        Assert.Equal(PopulationValueWeights.From(defaults), PopulationValueWeights.Default);

        (Action<PlayerbotPopulationOptions> Bad, string Key)[] cases =
        [
            (o => o.ValueQuestWeight = 101, "ValueQuestWeight"), (o => o.ValueReputationWeight = -1, "ValueReputationWeight"),
            (o => o.ValueGearWeight = 101, "ValueGearWeight"), (o => o.ValueFarmWeight = 101, "ValueFarmWeight"),
            (o => o.ValueEliteWeight = 101, "ValueEliteWeight"), (o => o.ValueTroubleWeight = 101, "ValueTroubleWeight"),
            (o => o.ValueDistanceWeight = 101, "ValueDistanceWeight"), (o => o.ValueQuestGrayMargin = 21, "ValueQuestGrayMargin"),
            (o => o.ValueEliteSoloPct = 101, "ValueEliteSoloPct"), (o => o.ValueTopZones = 0, "ValueTopZones"),
            (o => o.ValueTopZones = 21, "ValueTopZones"), (o => o.ValueZoneSoftCap = 1001, "ValueZoneSoftCap"),
            (o => o.ValueCapReach = 21, "ValueCapReach"), (o => o.ValueRelocateMinStayMinutes = 1441, "ValueRelocateMinStayMinutes"),
            (o => o.ValueRelocateBelowPct = 101, "ValueRelocateBelowPct"), (o => o.ValueChecksPerPass = 201, "ValueChecksPerPass"),
        ];
        foreach ((Action<PlayerbotPopulationOptions> bad, string key) in cases)
        {
            var options = new PlayerbotPopulationOptions();
            bad(options);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
        }

        // Boundaries are accepted: 0 turns the soft cap off, 100 is the top weight.
        new PlayerbotPopulationOptions { ValueZoneSoftCap = 0, ValueQuestWeight = 100, ValueTopZones = 1, ValueChecksPerPass = 0 }.Validate();
    }

    private static PopulationZone ZoneOne() => Atlas((ValuedZone, 55, 62)).Find(ValuedZone)!;

    private static ZoneScore ScoreOf(ZoneContentFacts facts, PopulationBotProfile bot)
        => PopulationZoneScoring.Score(ZoneOne(), facts, bot, PopulationValueWeights.Default);

    [Fact]
    public void FreshProfileHasNoPosition_SoDistanceIsZero_WhileAPositionedProfileOnAnotherMapPaysIt()
    {
        PopulationValueWeights w = PopulationValueWeights.Default;
        Assert.Equal(0.0, PopulationZoneScoring.Score(ZoneOne(), null, Fresh60(), w).Distance);
        var elsewhere = new PopulationBotProfile(Capped, Team.Alliance, 1, 1, 0, 1, 0f, 0f);
        Assert.Equal((double)w.Distance, PopulationZoneScoring.Score(ZoneOne(), null, elsewhere, w).Distance);
    }

    [Fact]
    public void UnknownRace_UsesTheRacesOfTheBotsTeam()
    {
        const uint hordeOnly = (1u << 1) | (1u << 4) | (1u << 5) | (1u << 7);
        var facts = new ZoneContentFacts(ValuedZone) { Quests = [new ZoneQuestFact(1, 50, 58, hordeOnly, 0, [])] };
        Assert.Equal(0.0, ScoreOf(facts, PopulationBotProfile.Fresh(Capped, Team.Alliance)).Quest);
        Assert.True(ScoreOf(facts, PopulationBotProfile.Fresh(Capped, Team.Horde)).Quest > 0);
    }

    [Fact]
    public void AQuestWhoseGiverIsHostileToTheTeam_IsNotCountedForIt()
    {
        var facts = new ZoneContentFacts(ValuedZone) { Quests = [new ZoneQuestFact(1, 50, 58, 0, 0, []) { HordeGiver = false }] };
        Assert.True(ScoreOf(facts, PopulationBotProfile.Fresh(Capped, Team.Alliance)).Quest > 0);
        Assert.Equal(0.0, ScoreOf(facts, PopulationBotProfile.Fresh(Capped, Team.Horde)).Quest);
    }

    [Fact]
    public void ALevelScaledQuest_CountsAsTheBotsLevel_NotGrey()
    {
        var facts = new ZoneContentFacts(ValuedZone) { Quests = [new ZoneQuestFact(1, 1, 0, 0, 0, [])] };
        Assert.True(ScoreOf(facts, Fresh60()).Quest > 0);
    }

    [Fact]
    public void GearTheBotCannotUse_IsNotAnUpgrade()
    {
        ZoneContentFacts facts = Valued(ValuedZone);
        Assert.True(ScoreOf(facts, Fresh60()).Gear > 0);
        Assert.Equal(0.0, ScoreOf(facts, Fresh60() with { CanUse = _ => false }).Gear);
    }
}
