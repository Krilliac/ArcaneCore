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
/// Zone capacity and the relocation veto. The fixture has the live Deadwind Pass shape: a small level 50-60 zone (41) whose surface holds
/// few level 58 creatures (its crypt holds more) and a large one (42), both open to both teams.
/// </summary>
public sealed class PopulationZoneCapacityTests
{
    private const uint Small = 41, Big = 42;

    private static AreaTemplate Area(uint entry, uint zone, int level) => new(entry, 0, zone, entry, 0, level, "Area" + entry, AreaTeams.None, 0);

    private static List<AreaTemplate> Areas() =>
    [
        Area(Small, 0, 0), Area(410, Small, 50), Area(411, Small, 60),
        Area(Big, 0, 0), Area(420, Big, 50), Area(421, Big, 60),
    ];

    private static GraveyardCatalog Graveyards()
    {
        WorldSafeLoc[] locs = [new(36, 0, 100f, 100f, 50f, 0f, "GY36"), new(37, 0, 5000f, 100f, 50f, 0f, "GY37")];
        GraveyardLink[] links = [new(36, Small, 0), new(37, Big, 0)];
        return GraveyardCatalog.Build(new GraveyardContent(locs, links), null, out _);
    }

    private static FactionTemplateCatalog Factions() => new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
        new FactionTemplateRecord(2, 2, 0, OwnMask: 5, FriendlyMask: 4, HostileMask: 10),
        new FactionTemplateRecord(14, 7, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
    ]);

    private static readonly Dictionary<uint, CreatureTemplate> Templates = new()
    {
        [1] = new() { Entry = 1, Name = "Ghoul", MinLevel = 58, MaxLevel = 59, Faction = 14 },
        [2] = new() { Entry = 2, Name = "Rat", MinLevel = 20, MaxLevel = 20, Faction = 14 },
    };

    // Zone 41 is x below 1000, zone 42 above. A negative Z is underground (the fake surface test).
    private static CreatureSpawn Spawn(uint guid, uint entry, float x, float z = 0f) => new() { Guid = guid, Entry = entry, MapId = 0, X = x, Y = 0, Z = z };

    private static PopulationSpawnCapacity Survey(IEnumerable<CreatureSpawn> spawns, List<int>? batchSizes = null)
        => PopulationCreatureSurvey.CountSurface(spawns, s => s.X < 1000 ? Small : Big,
            batch =>
            {
                batchSizes?.Add(batch.Count);
                return [.. batch.Select(s => s.Z >= 0)];
            },
            e => Templates.GetValueOrDefault(e), Factions(), new HashSet<uint>(), CancellationToken.None);

    private static List<CreatureSpawn> World()
    {
        var spawns = new List<CreatureSpawn>();
        uint guid = 1;
        for (int i = 0; i < 9; i++) spawns.Add(Spawn(guid++, 1, 10 + i)); // Small: 9 on the surface
        for (int i = 0; i < 6; i++) spawns.Add(Spawn(guid++, 1, 30 + i, z: -40f)); // Small: 6 in the crypt
        for (int i = 0; i < 5; i++) spawns.Add(Spawn(guid++, 2, 50 + i)); // Small: 5 level 20 rats on the surface (they do not fit a level 60 bot)
        for (int i = 0; i < 60; i++) spawns.Add(Spawn(guid++, 1, 2000 + i)); // Big: 60 on the surface
        return spawns;
    }

    private static PopulationZoneAtlas Atlas(int spawnsPerBot, PopulationSpawnCapacity? capacity)
        => PopulationZoneAtlas.Build(Areas(), Graveyards(), [0], new HashSet<uint>(), null, null, spawnsPerBot, capacity);

    private static readonly PopulationLevelRule Rule = PopulationLevelRule.Flat(4);

    [Fact]
    public void IndoorAndUndergroundSpawns_AndSpawnsOfOtherLevels_DoNotCount()
    {
        PopulationSpawnCapacity capacity = Survey(World());
        Assert.Equal(9, capacity.Fitting(Small, Team.Alliance, 60, 60)); // 9 surface ghouls; the crypt has 6 more and the level 20 rats do not fit
        Assert.Equal(9, capacity.Fitting(Small, Team.Horde, 60, 60));
        Assert.Equal(60, capacity.Fitting(Big, Team.Alliance, 60, 60));
        Assert.Equal(5, capacity.Fitting(Small, Team.Alliance, 20, 20)); // the rats fit a level 20 bot, the ghouls (58) do not
        Assert.Equal(0, capacity.Fitting(99, Team.Alliance, 1, 60)); // a zone without spawns
        // The window runs from 5 levels below the lowest bot level to 3 above the highest: a level 58 creature fits bots of 55..63.
        Assert.Equal(9, capacity.Fitting(Small, Team.Alliance, 63, 63));
        Assert.Equal(0, capacity.Fitting(Small, Team.Alliance, 64, 64));
        Assert.Equal(0, capacity.Fitting(Small, Team.Alliance, 50, 50)); // a level 58 creature is 8 above a level 50 bot
    }

    [Fact]
    public void TheSurface_IsAskedInBatchesNoLargerThanTheBatchLimit()
    {
        var sizes = new List<int>();
        var spawns = new List<CreatureSpawn>();
        for (uint i = 0; i < 300; i++) spawns.Add(Spawn(i + 1, 1, 10 + i % 100));
        Survey(spawns, sizes);
        Assert.Equal([PopulationCreatureSurvey.SurfaceBatch, PopulationCreatureSurvey.SurfaceBatch, 300 - 2 * PopulationCreatureSurvey.SurfaceBatch], sizes);
    }

    [Fact]
    public void AZoneWithFewFittingSpawns_StopsTakingBotsAtItsCap()
    {
        PopulationSpawnCapacity capacity = Survey(World()); // Small: 9 / 3 = 3 bots; Big: 60 / 3 = 20
        PopulationZoneAtlas atlas = Atlas(3, capacity);
        var assigned = new Dictionary<uint, int> { [Small] = 2, [Big] = 19 };
        var random = new Random(7);

        // Under the cap both zones are offered, the least assigned first (Small).
        Assert.Equal(Small, atlas.PickSafe(60, 60, Team.Alliance, Rule, assigned, random, (_, _) => true)!.Placement.Zone.ZoneId);
        Assert.NotNull(atlas.Pick(60, 60, Team.Alliance, Rule, assigned, random));

        // At its cap Small takes nobody, even when it is the least assigned: every pick goes to Big.
        assigned[Small] = 3;
        assigned[Big] = 10;
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(Big, atlas.PickSafe(60, 60, Team.Horde, Rule, assigned, random, (_, _) => true)!.Placement.Zone.ZoneId);
            Assert.Equal(Big, atlas.Pick(60, 60, Team.Horde, Rule, assigned, random)!.Zone.ZoneId);
        }

        // Both full: no zone, rather than overfilling.
        assigned[Big] = 20;
        Assert.Null(atlas.PickSafe(60, 60, Team.Alliance, Rule, assigned, random, (_, _) => true));
        Assert.Null(atlas.Pick(60, 60, Team.Alliance, Rule, assigned, random));
    }

    [Fact]
    public void TheCap_IsOffWithoutSpawnsPerBot_OrBeforeTheSurveyIsIn_AndAppliesWhenItArrives()
    {
        var full = new Dictionary<uint, int> { [Small] = 500, [Big] = 500 };
        var random = new Random(3);
        PopulationSpawnCapacity capacity = Survey(World());
        Assert.NotNull(Atlas(0, capacity).PickSafe(60, 60, Team.Alliance, Rule, full, random, (_, _) => true)); // SpawnsPerBot 0: no cap
        PopulationZoneAtlas pending = Atlas(3, null);
        Assert.NotNull(pending.PickSafe(60, 60, Team.Alliance, Rule, full, random, (_, _) => true)); // survey not in: no cap
        pending.Capacity = capacity; // the survey arrives
        Assert.Null(pending.PickSafe(60, 60, Team.Alliance, Rule, full, random, (_, _) => true));
    }

    [Fact]
    public void AZoneWithTooFewSpawnsForOneBot_TakesNone()
    {
        var spawns = new List<CreatureSpawn> { Spawn(1, 1, 10), Spawn(2, 1, 11) }; // Small: 2 / 3 = 0 bots
        spawns.AddRange(World().Where(s => s.X >= 2000));
        PopulationZoneAtlas atlas = Atlas(3, Survey(spawns));
        var random = new Random(5);
        for (int i = 0; i < 50; i++)
            Assert.Equal(Big, atlas.PickSafe(60, 60, Team.Alliance, Rule, new Dictionary<uint, int>(), random, (_, _) => true)!.Placement.Zone.ZoneId);
    }

    [Fact]
    public void TheBandCapacity_IsTheSumOfTheCapsOfTheZonesThatFitIt_SoAShortfallShowsUp()
    {
        PopulationZoneAtlas atlas = Atlas(3, Survey(World())); // Small 3 + Big 20
        var band = new PopulationBand { MinLevel = 60, MaxLevel = 60, Weight = 1 };
        Assert.Equal(23, atlas.BandCapacity(band, [Team.Alliance, Team.Horde], Rule));
        Assert.Equal(3, atlas.CapOf(atlas.Find(Small)!, Team.Alliance, 60, 60));
        Assert.Null(Atlas(3, null).BandCapacity(band, [Team.Alliance], Rule)); // survey not in: no cap to report
        Assert.Contains(atlas.CapacityLines(new PlayerbotPopulationOptions { Bands = [band], Factions = PopulationFactions.Both }), l => l.Contains("hold 23 bots"));
    }

    [Fact]
    public void TheVetoLedger_ForgetsStrikesADayAfterTheVetoEnded()
    {
        var vetoes = new PopulationZoneVetoes();
        Guid bot = Guid.NewGuid();
        vetoes.RecordEscape(bot, Small, 0);
        vetoes.RecordEscape(bot, Small, 100); // second strike: 2 h from 100
        long later = 100 + 7200 + PopulationZoneVetoes.ForgetAfterSeconds + 1;
        vetoes.RecordEscape(bot, Big, later); // sweeps the stale entry
        Assert.Equal(1, vetoes.Count);
        vetoes.RecordEscape(bot, Small, later);
        Assert.True(vetoes.IsVetoed(bot, Small, later + 3599)); // back to a first strike: one hour
        Assert.False(vetoes.IsVetoed(bot, Small, later + 3600));
    }

    [Fact]
    public void TheVeto_LastsAnHourAndDoublesOnRepeatUpToEightHours()
    {
        var vetoes = new PopulationZoneVetoes();
        Guid bot = Guid.NewGuid();
        const long t0 = 1_000_000;
        Assert.False(vetoes.IsVetoed(bot, Small, t0));
        vetoes.RecordEscape(bot, Small, t0);
        Assert.True(vetoes.IsVetoed(bot, Small, t0 + 3599));
        Assert.False(vetoes.IsVetoed(bot, Small, t0 + 3600));
        Assert.False(vetoes.IsVetoed(bot, Big, t0)); // another zone
        Assert.False(vetoes.IsVetoed(Guid.NewGuid(), Small, t0)); // another bot

        vetoes.RecordEscape(bot, Small, t0 + 3600); // a repeat: two hours
        Assert.True(vetoes.IsVetoed(bot, Small, t0 + 3600 + 7199));
        Assert.False(vetoes.IsVetoed(bot, Small, t0 + 3600 + 7200));

        Assert.Equal([3600L, 7200L, 14400L, 28800L, 28800L, 28800L], Enumerable.Range(1, 6).Select(PopulationZoneVetoes.TtlOf));
    }

    [Fact]
    public void ABotWithARecordedPinEscape_IsNotRelocatedBackUntilTheTtlExpires()
    {
        PopulationZoneAtlas atlas = Atlas(0, null); // Small is the zone it escaped; Big is the other
        var vetoes = new PopulationZoneVetoes();
        Guid bot = Guid.NewGuid(), other = Guid.NewGuid();
        const long now = 5_000_000;
        vetoes.RecordEscape(bot, Small, now);
        var assigned = new Dictionary<uint, int> { [Small] = 0, [Big] = 400 }; // Small is by far the least assigned
        var random = new Random(11);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(Big, atlas.PickSafe(60, 60, Team.Alliance, Rule, assigned, random, (_, _) => true, z => vetoes.IsVetoed(bot, z, now + 1800))!.Placement.Zone.ZoneId);
            // Another bot is not held back by it.
            Assert.Equal(Small, atlas.PickSafe(60, 60, Team.Alliance, Rule, assigned, random, (_, _) => true, z => vetoes.IsVetoed(other, z, now + 1800))!.Placement.Zone.ZoneId);
        }

        // The TTL has expired: Small is offered again.
        Assert.Equal(Small, atlas.PickSafe(60, 60, Team.Alliance, Rule, assigned, random, (_, _) => true, z => vetoes.IsVetoed(bot, z, now + 3600))!.Placement.Zone.ZoneId);

        // A veto on every fitting zone leaves the bot where it is (no selection).
        vetoes.RecordEscape(bot, Big, now);
        Assert.Null(atlas.PickSafe(60, 60, Team.Alliance, Rule, assigned, random, (_, _) => true, z => vetoes.IsVetoed(bot, z, now + 60)));
    }
}
