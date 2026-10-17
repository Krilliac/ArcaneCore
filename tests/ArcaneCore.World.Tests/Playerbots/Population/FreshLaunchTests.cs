using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Xunit;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>
/// <c>World:Playerbots:Population:FreshLaunch</c>: new bots stay level 1 at their race's start, the race and class mix stays balanced
/// across the factions, and nothing moves a bot out of its starting zone.
/// </summary>
public sealed class FreshLaunchTests
{
    /// <summary>Every race a warrior, each with its own start position on map 0 (so a bot that was teleported or boosted elsewhere shows).</summary>
    internal sealed class RaceStartDataStore : IWorldDataStore
    {
        public static StartPosition StartOf(byte race) => new(0, 12, -9000f + race * 100f, -100f * race, 80f, 0f);

        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<StartPosition?>(IsValid(race, cls) ? StartOf(race) : null);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
            => Task.FromResult<RaceInfo?>(new RaceInfo(49u + race, PopulationRaceClassCache.TeamOf(race) == Team.Alliance ? 1u : 2u));

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult<ClassInfo?>(new ClassInfo(60, 0, 1));

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => Task.FromResult(IsValid(race, cls));

        private static bool IsValid(byte race, byte cls) => race is >= 1 and <= 8 && cls == 1;
    }

    [Fact]
    public void TheOptionIsOffByDefault_AndTheRushBrakeHasItsDefaults()
    {
        var options = new PlayerbotPopulationOptions();
        Assert.False(options.FreshLaunch);
        Assert.Equal(0, options.RushBrakeP95Ms); // 0: 1.5 x TickP95BudgetMs
        Assert.Equal(90, options.RushCeilingMs());
        Assert.Equal(5, options.RushBrakeWindowSeconds);
        Assert.Equal(3, options.RushBrakeWindows);
        options.Validate();
        options.RushBrakeP95Ms = 49;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void PlanFresh_KeepsTheFactionsBalanced_AndReachesEveryRace()
    {
        var cache = new PopulationRaceClassCache(PopulationRaceClassCache.Races.Select(r => (r, (byte)1)));
        var options = new PlayerbotPopulationOptions { Factions = PopulationFactions.Both, AllianceSharePct = 50, Classes = [1] };
        var random = new Random(12345);
        int alliance = 0, horde = 0;
        HashSet<byte> races = [];
        for (int i = 0; i < 400; i++)
        {
            PopulationFreshPlan plan = Assert.IsType<PopulationFreshPlan>(PopulationBandPlanner.PlanFresh(options, alliance, horde, cache, random));
            Assert.Equal(PopulationRaceClassCache.TeamOf(plan.Race), plan.Team);
            races.Add(plan.Race);
            if (plan.Team == Team.Alliance) alliance++; else horde++;
            Assert.InRange(alliance - horde, -1, 1);
        }

        Assert.Equal(PopulationRaceClassCache.Races.Order(), races.Order());
    }

    [Fact]
    public async Task FreshBots_AreLevelOne_AtTheirRacesStart_BothFactionsBalanced_WithoutBoostOrTeleport()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.MaxBots = 8;
            o.Population.FreshLaunch = true;
            o.Population.TargetOnline = 8;
            o.Population.Factions = PopulationFactions.Both;
            o.Population.MaxStartsPerInterval = 8;
            o.Population.MaxCreatePerMinute = 60;
            // Bands that would put every bot at level 30+ and zones that would teleport it: fresh launch must ignore both.
            o.Population.Bands = [new PopulationBand { MinLevel = 31, MaxLevel = 40, Weight = 1 }];
        }, dataStore: () => new RaceStartDataStore());

        world.Ticks.Set(200, 10);
        PopulationStatus status = await world.PassAsync();

        Assert.Contains("created 8", status.Reason);
        Assert.True(status.FreshLaunch);
        await world.WaitForSettledAsync(8);
        var teams = new Dictionary<Team, int>();
        foreach (PlayerbotStatus bot in world.Bots.Snapshot())
        {
            CharacterRecord record = Assert.Single(await world.Host.Characters.GetByAccountAsync(
                (await world.Owners.LoadAllAsync()).Single(b => b.BotId == bot.BotId).AccountId));
            StartPosition start = RaceStartDataStore.StartOf(record.Race);
            (byte level, float x, float y, uint map, Team team) = await world.Host.World.InvokeAsync(() =>
            {
                Player player = world.Bots.FindSession(bot.BotId)!.Player!;
                return (player.Level, player.X, player.Y, player.MapId, player.Team);
            });
            Assert.Equal((byte)1, level);
            Assert.Equal(start.MapId, map);
            // The bot has had a moment to wander, but nothing has teleported it: it is within a short walk of its start.
            Assert.InRange(MathF.Sqrt(((x - start.X) * (x - start.X)) + ((y - start.Y) * (y - start.Y))), 0f, 80f);
            teams[team] = teams.GetValueOrDefault(team) + 1;
        }

        Assert.Equal(4, teams[Team.Alliance]);
        Assert.Equal(4, teams[Team.Horde]);
        Assert.Equal(8, world.Members.Snapshot.Count);
        Assert.All(world.Members.Snapshot, m => Assert.Equal(12u, m.ZoneId));
    }

    [Fact]
    public async Task ARunningFreshBot_IsNeverRelocatedOutOfItsStartingZone_ForItsLevelBand()
    {
        await using PopulationTestHost world = await PopulationTestHost.StartAsync(o =>
        {
            o.Population.TargetOnline = 1;
            o.Population.FreshLaunch = true;
            o.Population.Factions = PopulationFactions.Alliance;
        }, withRelocation: true, dataStore: () => new RaceStartDataStore());
        world.Population.ExcludedZones = [];
        // A pass outside fresh launch builds the zone atlas (fresh launch itself never does), so the relocation has every zone to choose from:
        // only the fresh-launch guard keeps the bot where it is.
        world.Population.FreshLaunch = false;
        world.Population.TargetOnline = 0;
        world.Ticks.Set(200, 10);
        await world.PassAsync();
        Assert.NotEmpty(world.Feature.Atlas.Zones);
        world.Population.FreshLaunch = true;
        world.Population.TargetOnline = 1;
        world.Ticks.Set(200, 10);
        Assert.Contains("created 1", (await world.PassAsync()).Reason);
        await world.WaitForSettledAsync(1);
        Guid bot = Assert.Single(world.Members.Snapshot).BotId;

        // Under-levelled for Westfall (lowest 8) and for the level rule: the relocation would move it to Elwynn, and a level 20 bot in
        // Elwynn is out-levelled and would be moved to Westfall. In fresh launch neither happens.
        foreach ((byte level, uint zone) in new[] { ((byte)5, PopulationTestHost.Westfall), ((byte)20, PopulationTestHost.Elwynn) })
        {
            await world.Host.World.InvokeAsync(() =>
            {
                Player player = world.Bots.FindSession(bot)!.Player!;
                player.Level = level;
                player.ZoneId = zone;
                return true;
            });
            world.Ticks.Set(200, 10);
            PopulationStatus status = await world.PassAsync();

            Assert.DoesNotContain("moved", status.Reason);
            Assert.Equal(12u, Assert.Single(world.Members.Snapshot).ZoneId);
            Assert.Equal(0u, await world.Host.World.InvokeAsync(() => world.Bots.FindSession(bot)!.Player!.MapId));
        }
    }
}
