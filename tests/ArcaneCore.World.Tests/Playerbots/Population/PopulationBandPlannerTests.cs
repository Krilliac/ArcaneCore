using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>The pure planning of the bots the population creates: band shares, faction split, race and class, level and zone.</summary>
public sealed class PopulationBandPlannerTests
{
    // The 1.12 race and class table (the pairs vmangos playercreateinfo and CMSG_CHAR_CREATE accept): race id -> class ids.
    // 1 human, 2 orc, 3 dwarf, 4 night elf, 5 undead, 6 tauren, 7 gnome, 8 troll; 1 warrior, 2 paladin, 3 hunter, 4 rogue, 5 priest,
    // 7 shaman, 8 mage, 9 warlock, 11 druid.
    private static readonly Dictionary<byte, byte[]> Vanilla = new()
    {
        [1] = [1, 2, 4, 5, 8, 9],
        [2] = [1, 3, 4, 7, 9],
        [3] = [1, 2, 3, 4, 5],
        [4] = [1, 3, 4, 5, 11],
        [5] = [1, 4, 5, 8, 9],
        [6] = [1, 3, 7, 11],
        [7] = [1, 4, 8, 9],
        [8] = [1, 3, 4, 5, 7, 8],
    };

    private static PopulationRaceClassCache Cache() => new(Vanilla.SelectMany(kv => kv.Value.Select(c => (kv.Key, c))));

    private static PopulationBand[] Bands(params (int Min, int Max, int Weight)[] bands)
        => [.. bands.Select(b => new PopulationBand { MinLevel = b.Min, MaxLevel = b.Max, Weight = b.Weight })];

    private static AreaTemplate Area(uint entry, uint map, uint zone, int level, uint team)
        => new(entry, map, zone, entry, 0, level, "Area" + entry, team, 0);

    // Elwynn (Alliance, 5-10), Durotar (Horde, 5-10), Duskwood (contested, 10-30), Stranglethorn (contested, 30-50). One graveyard each.
    private static PopulationZoneAtlas Atlas()
    {
        AreaTemplate[] areas =
        [
            Area(12, 0, 0, 0, AreaTeams.Ally), Area(24, 0, 12, 5, 0), Area(25, 0, 12, 10, 0),
            Area(14, 1, 0, 0, AreaTeams.Horde), Area(34, 1, 14, 5, 0), Area(35, 1, 14, 10, 0),
            Area(10, 0, 0, 0, AreaTeams.None), Area(42, 0, 10, 10, 0), Area(43, 0, 10, 30, 0),
            Area(33, 0, 0, 0, AreaTeams.None), Area(44, 0, 33, 30, 0), Area(45, 0, 33, 50, 0),
        ];
        (uint Id, uint Map, uint Link)[] rows = [(1, 0, 12), (2, 1, 14), (3, 0, 10), (4, 0, 33), (5, 1, 33)];
        WorldSafeLoc[] locs = [.. rows.Select(r => new WorldSafeLoc(r.Id, r.Map, r.Id * 10f, r.Id * 20f, 50f, 0f, "GY" + r.Id))];
        GraveyardLink[] links = [.. rows.Select(r => new GraveyardLink(r.Id, r.Link, 0))];
        GraveyardCatalog graveyards = GraveyardCatalog.Build(new GraveyardContent(locs, links), null, out _);
        return PopulationZoneAtlas.Build(areas, graveyards, [0, 1], new HashSet<uint>());
    }

    [Fact]
    public void DesiredPerBand_FollowsTheWeights_LargestRemainder()
    {
        // Weights 1:2:3 of 10 bots: 1.67, 3.33, 5 -> floors 1, 3, 5 (9), the spare bot goes to the largest remainder (band 0, 4/6).
        Assert.Equal([2, 3, 5], PopulationBandPlanner.DesiredPerBand(10, Bands((1, 10, 1), (11, 20, 2), (21, 30, 3))));
        // Equal remainders: the lower band first.
        Assert.Equal([4, 3, 3], PopulationBandPlanner.DesiredPerBand(10, Bands((1, 10, 1), (11, 20, 1), (21, 30, 1))));
        // The six default bands of 100 bots: 16 each and four spare, the first four bands take them.
        Assert.Equal([17, 17, 17, 17, 16, 16], PopulationBandPlanner.DesiredPerBand(100, new PlayerbotPopulationOptions().Bands));
        // A band with weight 0 holds none, whatever the remainders; the others share the target.
        Assert.Equal([0, 5, 5], PopulationBandPlanner.DesiredPerBand(10, Bands((1, 10, 0), (11, 20, 1), (21, 30, 1))));
        Assert.Equal([0, 1, 0], PopulationBandPlanner.DesiredPerBand(1, Bands((1, 10, 0), (11, 20, 1), (21, 30, 0))));
    }

    [Fact]
    public void DesiredPerBand_AlwaysAddsUpToTheTarget_AndHandlesEmptyInput()
    {
        PopulationBand[] bands = Bands((1, 10, 7), (11, 20, 3), (21, 30, 11), (31, 40, 1), (41, 60, 5));
        for (int target = 0; target <= 200; target++)
            Assert.Equal(target, PopulationBandPlanner.DesiredPerBand(target, bands).Sum());
        Assert.Equal([0, 0, 0], PopulationBandPlanner.DesiredPerBand(0, Bands((1, 10, 1), (11, 20, 1), (21, 30, 1))));
        Assert.Equal([0, 0], PopulationBandPlanner.DesiredPerBand(10, Bands((1, 10, 0), (11, 20, 0)))); // no weight: nothing wanted
        Assert.Equal([0, 0], PopulationBandPlanner.DesiredPerBand(-5, Bands((1, 10, 1), (11, 20, 1))));
        Assert.Empty(PopulationBandPlanner.DesiredPerBand(10, []));
        Assert.Equal([1000], PopulationBandPlanner.DesiredPerBand(1000, Bands((1, 60, 1000))));
    }

    [Fact]
    public void NextBand_IsTheLargestDeficit()
    {
        Assert.Equal(2, PopulationBandPlanner.NextBand([3, 3, 5], [2, 3, 0]));
        Assert.Equal(0, PopulationBandPlanner.NextBand([4, 4], [1, 1])); // a tie: the lower band
        Assert.Equal(-1, PopulationBandPlanner.NextBand([3, 3], [3, 4])); // none below its share (a surplus is not a deficit)
        Assert.Equal(-1, PopulationBandPlanner.NextBand([], []));
        Assert.Equal(0, PopulationBandPlanner.NextBand([5, 5, 5], [0, 1, 2], band => band != 2)); // the filter skips ineligible bands
        Assert.Equal(1, PopulationBandPlanner.NextBand([5, 5, 5], [0, 1, 2], band => band != 0));
        Assert.Equal(-1, PopulationBandPlanner.NextBand([5, 5], [0, 0], _ => false));
        Assert.Equal([0, 0, 5], PopulationBandPlanner.Deficits([3, 3, 5], [4, 3, 0]));
    }

    [Fact]
    public void RaceAndClass_AreOnlyValidPairsOfTheChosenFaction()
    {
        PopulationRaceClassCache cache = Cache();
        uint[] all = [.. PlayerbotPopulationOptions.ValidClasses];
        var random = new Random(11);
        var seenAlliance = new HashSet<(byte, byte)>();
        var seenHorde = new HashSet<(byte, byte)>();
        for (int i = 0; i < 4000; i++)
        {
            foreach (Team team in new[] { Team.Alliance, Team.Horde })
            {
                (byte race, byte cls) = PopulationBandPlanner.ChooseRaceClass(cache, team, all, random)!.Value;
                Assert.Contains(cls, Vanilla[race]); // a pair CMSG_CHAR_CREATE accepts
                Assert.Equal(team, PopulationRaceClassCache.TeamOf(race)); // of the faction asked for
                (team == Team.Alliance ? seenAlliance : seenHorde).Add((race, cls));
            }
        }

        // Every valid pair of each faction is reachable, and no other is.
        Assert.Equal(Vanilla.Where(kv => kv.Key is 1 or 3 or 4 or 7).SelectMany(kv => kv.Value.Select(c => (kv.Key, c))).Order(), seenAlliance.Order());
        Assert.Equal(Vanilla.Where(kv => kv.Key is 2 or 5 or 6 or 8).SelectMany(kv => kv.Value.Select(c => (kv.Key, c))).Order(), seenHorde.Order());
        Assert.DoesNotContain(seenHorde, p => p.Item2 == 2); // no Horde paladin in 1.12
        Assert.DoesNotContain(seenAlliance, p => p.Item2 == 7); // no Alliance shaman
        Assert.Equal(new byte[] { 1, 3, 4, 7 }, Races(Team.Alliance));
        Assert.Equal(new byte[] { 2, 5, 6, 8 }, Races(Team.Horde));
    }

    private static byte[] Races(Team team) => [.. PopulationRaceClassCache.Races.Where(r => PopulationRaceClassCache.TeamOf(r) == team)];

    [Fact]
    public void RaceAndClass_RespectTheConfiguredClasses_AndAreNullWhenNoneFits()
    {
        PopulationRaceClassCache cache = Cache();
        var random = new Random(12);
        for (int i = 0; i < 300; i++)
        {
            (byte race, byte cls) = PopulationBandPlanner.ChooseRaceClass(cache, Team.Horde, [3u, 7u], random)!.Value;
            Assert.Contains(cls, new byte[] { 3, 7 });
            Assert.Contains(cls, Vanilla[race]);
        }

        // Paladin (2) and druid (11) exist for one faction each: the other faction has no usable class, so nothing is chosen.
        Assert.Null(PopulationBandPlanner.ChooseRaceClass(cache, Team.Horde, [2u], random));
        Assert.Null(PopulationBandPlanner.ChooseRaceClass(cache, Team.Alliance, [7u], random));
        Assert.Null(PopulationBandPlanner.ChooseRaceClass(cache, Team.Alliance, [], random));
        Assert.Null(PopulationBandPlanner.ChooseRaceClass(new PopulationRaceClassCache([]), Team.Alliance, [1u], random));
        // A class with a single race of the faction always gets that race: only the night elf is a druid among the Alliance.
        for (int i = 0; i < 50; i++) Assert.Equal((byte)4, PopulationBandPlanner.ChooseRaceClass(cache, Team.Alliance, [11u], random)!.Value.Race);
    }

    [Fact]
    public void FactionShare_AndSingleFactionSetting_AreRespected()
    {
        // One faction configured: that one, whatever the running split.
        var alliance = new PlayerbotPopulationOptions { Factions = PopulationFactions.Alliance };
        var horde = new PlayerbotPopulationOptions { Factions = PopulationFactions.Horde };
        Assert.Equal(Team.Alliance, PopulationBandPlanner.ChooseTeam(alliance, 0, 50));
        Assert.Equal(Team.Horde, PopulationBandPlanner.ChooseTeam(horde, 50, 0));

        // Both: follow AllianceSharePct from the first bot on.
        foreach ((int share, int expected) in new[] { (50, 50), (75, 75), (25, 25), (0, 0), (100, 100), (60, 60) })
        {
            var both = new PlayerbotPopulationOptions { Factions = PopulationFactions.Both, AllianceSharePct = share };
            int a = 0, h = 0;
            for (int i = 0; i < 100; i++)
            {
                if (PopulationBandPlanner.ChooseTeam(both, a, h) == Team.Alliance) a++; else h++;
                // At every step the split is within one bot of the share.
                Assert.InRange(a - share / 100.0 * (i + 1), -1.0, 1.0);
            }

            Assert.Equal(expected, a);
        }

        // Existing bots count: with only Horde running the next ones are Alliance until the share is met.
        var half = new PlayerbotPopulationOptions { AllianceSharePct = 50 };
        Assert.Equal(Team.Alliance, PopulationBandPlanner.ChooseTeam(half, 0, 4));
        Assert.Equal(Team.Horde, PopulationBandPlanner.ChooseTeam(half, 4, 0));
        Assert.Equal(Team.Alliance, PopulationBandPlanner.ChooseTeam(half, 3, 3)); // a tie: Alliance
    }

    [Fact]
    public void WithoutBoost_OnlyBandsReachableAtLevelOneAreUsed()
    {
        var options = new PlayerbotPopulationOptions
        {
            Factions = PopulationFactions.Alliance, ZoneLevelSlack = 4,
            Bands = Bands((1, 10, 1), (11, 20, 1), (21, 30, 1)),
        };
        int[] desired = [2, 9, 9];
        PopulationRaceClassCache cache = Cache();
        PopulationZoneAtlas atlas = Atlas();
        var assigned = new Dictionary<uint, int>();
        var random = new Random(13);
        for (int i = 0; i < 100; i++)
        {
            PopulationPlan plan = PopulationBandPlanner.Plan(options, desired, [0, 0, 0], 0, 0, cache, atlas, assigned, boostAvailable: false, random)!;
            Assert.Equal(0, plan.Band); // the bigger deficits of the higher bands are not reachable without the boost
            Assert.Equal(1, plan.Level);
            Assert.Equal(Team.Alliance, plan.Team);
            Assert.Equal(12u, plan.Placement.Zone.ZoneId); // the only Alliance zone a level 1 bot fits (5 minus the slack of 4)
        }

        // Band 0 satisfied: nothing else can be given without the boost.
        Assert.Null(PopulationBandPlanner.Plan(options, desired, [2, 0, 0], 0, 0, cache, atlas, assigned, boostAvailable: false, random));
        // A band that does not contain level 1 is never used, even when it is the only one configured.
        var high = new PlayerbotPopulationOptions { Factions = PopulationFactions.Alliance, Bands = Bands((11, 20, 1)) };
        Assert.Null(PopulationBandPlanner.Plan(high, [5], [0], 0, 0, cache, atlas, assigned, boostAvailable: false, random));
    }

    [Fact]
    public void WithBoost_TheLargestDeficitBandWins_AndTheLevelFitsBandAndZone()
    {
        var options = new PlayerbotPopulationOptions
        {
            Factions = PopulationFactions.Alliance, ZoneLevelSlack = 4,
            Bands = Bands((1, 10, 1), (11, 20, 1), (31, 45, 1)),
        };
        PopulationRaceClassCache cache = Cache();
        PopulationZoneAtlas atlas = Atlas();
        var random = new Random(14);
        for (int i = 0; i < 200; i++)
        {
            PopulationPlan plan = PopulationBandPlanner.Plan(options, [2, 3, 9], [0, 0, 0], 0, 0, cache, atlas, new Dictionary<uint, int>(), true, random)!;
            Assert.Equal(2, plan.Band); // deficit 9
            Assert.InRange(plan.Level, 31, 45);
            Assert.InRange(plan.Level, plan.Placement.Zone.MinLevel - 4, plan.Placement.Zone.MaxLevel);
            Assert.Equal(33u, plan.Placement.Zone.ZoneId); // Stranglethorn (30-50) is the only zone for levels 31-45
        }

        // A band no zone can take is skipped for the next band with a deficit: no zone has levels 51-60.
        var withGap = new PlayerbotPopulationOptions { Factions = PopulationFactions.Alliance, Bands = Bands((1, 10, 1), (51, 60, 1)) };
        PopulationPlan fallback = PopulationBandPlanner.Plan(withGap, [1, 9], [0, 0], 0, 0, cache, atlas, new Dictionary<uint, int>(), true, random)!;
        Assert.Equal(0, fallback.Band);

        // Nothing planned when every band is at its share, or no zone takes the band at all.
        Assert.Null(PopulationBandPlanner.Plan(options, [1, 1, 1], [1, 1, 1], 0, 0, cache, atlas, new Dictionary<uint, int>(), true, random));
        Assert.Null(PopulationBandPlanner.Plan(options, [1, 1, 1], [0, 0, 0], 0, 0, cache, PopulationZoneAtlas.Empty, new Dictionary<uint, int>(), true, random));
        Assert.Null(PopulationBandPlanner.Plan(options, [1, 1, 1], [0, 0, 0], 0, 0, new PopulationRaceClassCache([]), atlas, new Dictionary<uint, int>(), true, random));
    }

    [Fact]
    public void BandOf_FindsTheBandThatHoldsTheLevel()
    {
        PopulationBand[] bands = Bands((1, 10, 1), (21, 30, 1));
        Assert.Equal(0, PopulationBandPlanner.BandOf(bands, 1));
        Assert.Equal(0, PopulationBandPlanner.BandOf(bands, 10));
        Assert.Equal(-1, PopulationBandPlanner.BandOf(bands, 11)); // between the bands
        Assert.Equal(1, PopulationBandPlanner.BandOf(bands, 21));
        Assert.Equal(1, PopulationBandPlanner.BandOf(bands, 30));
        Assert.Equal(-1, PopulationBandPlanner.BandOf(bands, 31));
        Assert.Equal(-1, PopulationBandPlanner.BandOf(bands, 0));
    }

    [Fact]
    public async Task TheRaceClassCache_AsksTheWorldDataStoreForEveryRaceAndConfiguredClass()
    {
        var store = new RecordingStore(Vanilla);
        PopulationRaceClassCache cache = await PopulationRaceClassCache.BuildAsync(store, [1u, 2u, 11u], CancellationToken.None);
        Assert.Equal(24, store.Asked.Count); // 8 races x 3 classes
        Assert.Equal(new byte[] { 1, 3 }, cache.RacesFor(Team.Alliance, 2)); // human and dwarf paladins
        Assert.Equal(new byte[] { 4 }, cache.RacesFor(Team.Alliance, 11));
        Assert.Equal(new byte[] { 6 }, cache.RacesFor(Team.Horde, 11));
        Assert.Empty(cache.RacesFor(Team.Horde, 2));
        Assert.Equal("1,2,11", cache.Key);
        Assert.Equal(12, cache.Count); // 8 warriors, 2 paladins, 2 druids
    }

    private sealed class RecordingStore(Dictionary<byte, byte[]> valid) : IWorldDataStore
    {
        public List<(byte, byte)> Asked { get; } = [];

        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default) => Task.FromResult<StartPosition?>(null);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default) => Task.FromResult<RaceInfo?>(null);

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => Task.FromResult<ClassInfo?>(null);

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        {
            Asked.Add((race, cls));
            return Task.FromResult(valid.TryGetValue(race, out byte[]? classes) && classes.Contains(cls));
        }
    }
}
