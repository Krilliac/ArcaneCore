using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// The population of a split cluster worker acts only on the maps the worker owns (<see cref="MapOwnership"/>, Cluster:Worker:OwnedMaps): it logs in
/// and creates nothing on another worker's maps, fills only its share of the target, and stays idle when it owns no allowed map. With no
/// restriction (combined mode) nothing changes. The survey is covered in <see cref="PopulationSurveyPathTests"/>.
/// </summary>
public sealed class PopulationOwnershipTests
{
    private static MapOwnership Owning(params uint[] maps) => new([.. maps.Select(m => new MapOwnership.Range(m, m))], allowInstanceMaps: false);

    /// <summary>The instances worker: a few dungeon maps and every instance copy, no continent.</summary>
    private static MapOwnership InstancesOnly() => new([new MapOwnership.Range(33, 34), new MapOwnership.Range(409, 409)], allowInstanceMaps: true);

    /// <summary>Every race a warrior; a race starts on its own continent (<see cref="PopulationStartAreas.ContinentOf"/>), all on one spot of that map.</summary>
    private sealed class ContinentStartDataStore : IWorldDataStore
    {
        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<StartPosition?>(IsValid(race, cls) ? new StartPosition(PopulationStartAreas.ContinentOf(race), 12, -8949.95f, -132.493f, 83.5312f, 0f) : null);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
            => Task.FromResult<RaceInfo?>(new RaceInfo(49u + race, PopulationRaceClassCache.TeamOf(race) == Team.Alliance ? 1u : 2u));

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<ClassInfo?>(new ClassInfo(60, 0, 1));

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult(IsValid(race, cls));

        private static bool IsValid(byte race, byte cls) => race is >= 1 and <= 8 && cls == 1;
    }

    [Fact]
    public void CombinedMode_OwnsEverything_AndTheTargetIsNotSplit()
    {
        var scope = new PopulationMapScope(MapOwnership.All, [0, 1]);
        Assert.True(scope.OwnsEverything);
        Assert.False(scope.IsEmpty);
        Assert.Equal(new uint[] { 0, 1 }, scope.Maps);
        Assert.All(PopulationRaceClassCache.Races, race => Assert.True(scope.OwnsRaceStart(race)));
        Assert.Equal(7, scope.ShareOf(7));
        Assert.Equal(0, scope.ShareOf(0));
    }

    [Fact]
    public void ASplitWorker_KeepsTheAllowedMapsItOwns_AndItsShareOfTheTarget()
    {
        var east = new PopulationMapScope(Owning(0), [0, 1]);
        var kalimdor = new PopulationMapScope(Owning(1), [0, 1]);
        Assert.Equal(new uint[] { 0 }, east.Maps);
        Assert.Equal(new uint[] { 1 }, kalimdor.Maps);
        Assert.True(east.OwnsRaceStart(1));   // human
        Assert.False(east.OwnsRaceStart(4));  // night elf starts on Kalimdor
        Assert.False(kalimdor.OwnsRaceStart(5)); // undead start in the Eastern Kingdoms
        Assert.True(kalimdor.OwnsRaceStart(2));
        Assert.Equal(50, east.ShareOf(100));
        Assert.Equal(50, kalimdor.ShareOf(100));
    }

    [Fact]
    public void AWorkerWithoutAContinent_IsEmpty_AndHasNoShare()
    {
        var instances = new PopulationMapScope(InstancesOnly(), [0, 1]);
        Assert.True(instances.IsEmpty);
        Assert.Empty(instances.Maps);
        Assert.Equal(0, instances.ShareOf(100));
        Assert.False(instances.OwnsRaceStart(1));
    }

    [Fact]
    public void ARestrictedRaceCache_OffersOnlyTheRacesOfOwnedStarts()
    {
        var cache = new PopulationRaceClassCache(PopulationRaceClassCache.Races.Select(r => (r, (byte)1)));
        var scope = new PopulationMapScope(Owning(0), [0, 1]);
        PopulationRaceClassCache restricted = cache.Restrict(scope.OwnsRaceStart);
        Assert.Equal(new byte[] { 1, 3, 7 }, restricted.RacesFor(Team.Alliance, 1));
        Assert.Equal(new byte[] { 5 }, restricted.RacesFor(Team.Horde, 1));
    }

    [Fact]
    public async Task Restart_SkipsABotSavedOnAMapThisWorkerDoesNotOwn_WithoutALoginAttempt()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 1);
        world.Host.World.Ownership = Owning(0);
        PlayerbotOperationResult made = await world.Bots.CreateAsync("Parkedone", 1, 1);
        Guid parked = Assert.IsType<Guid>(made.BotId);
        ManagedPlayerbot record = Assert.IsType<ManagedPlayerbot>(world.Owners.Peek(parked));
        CharacterRecord character = Assert.IsType<CharacterRecord>(await world.Host.Characters.GetByIdAsync(record.CharacterId));
        character.MapId = 1; // saved on Kalimdor: another worker's bot
        world.Members.Seed(new PlayerbotPopulationMember(parked, record.CharacterId, PopulationTestHost.Elwynn, 0, 100, 5000));

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("started 0, created 1", status.Reason);
        Assert.Null(status.LastError);
        Assert.NotEqual(ManagedPlayerbotState.Running, world.Bots.Snapshot().Single(s => s.BotId == parked).State);
        await world.WaitForSettledAsync(1);
    }

    [Fact]
    public async Task FreshBots_AreCreatedOnlyAtRaceStartsOnOwnedMaps()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 8;
            o.Population.FreshLaunch = true;
            o.Population.TargetOnline = 16; // this worker owns half of the allowed maps: its share is 8
            o.Population.Factions = PopulationFactions.Both;
            o.Population.MaxStartsPerInterval = 8;
            o.Population.MaxCreatePerMinute = 60;
        }, dataStore: () => new ContinentStartDataStore());
        world.Host.World.Ownership = Owning(0);

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.True(status.Reason.Contains("created 8", StringComparison.Ordinal), status.Reason + " / " + status.LastError);
        Assert.Null(status.LastError);
        await world.WaitForSettledAsync(8);
        foreach (ManagedPlayerbot bot in await world.Owners.LoadAllAsync())
        {
            CharacterRecord record = Assert.Single(await world.Host.Characters.GetByAccountAsync(bot.AccountId));
            Assert.Equal(0u, record.MapId);
            Assert.True(PopulationStartAreas.ContinentOf(record.Race) == 0u, $"race {record.Race} starts on another worker's map");
        }
    }

    [Fact]
    public async Task ASplitWorker_FillsOnlyItsShareOfTheTarget_SoNoBandStaysShort()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 4);
        world.Host.World.Ownership = Owning(0); // allowed maps are 0 and 1: half of the target is this worker's

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();
        Assert.Contains("created 2", status.Reason);
        Assert.Equal(2, status.EffectiveTarget);
        await world.WaitForSettledAsync(2);

        world.Ticks.Set(200, 10);
        status = await world.PassAsync();
        Assert.Equal("at-target", status.State);
        PopulationBandStatus band = Assert.Single(status.Bands);
        Assert.Equal((2, 2), (band.Want, band.Have));
    }

    [Fact]
    public async Task AWorkerThatOwnsNoAllowedMap_RunsAnIdlePopulation()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 2);
        world.Host.World.Ownership = InstancesOnly();

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Equal("idle", status.State);
        Assert.Null(status.LastError);
        Assert.Empty(world.Members.Snapshot);
        Assert.Empty(await world.Owners.LoadAllAsync());
        Assert.Equal(0, world.RunningCount);
    }

    [Fact]
    public async Task CombinedMode_CreatesTheWholeTarget()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o => o.Population.TargetOnline = 4);

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("created 4", status.Reason);
        Assert.Equal(4, status.EffectiveTarget);
        await world.WaitForSettledAsync(4);
    }
}
