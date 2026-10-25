using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The pin ledger every bot shares (<see cref="PlayerbotSharedPins"/>): a cell two different bots dead-end-pinned is a world pin for a while,
/// so a third bot escapes at its first stall and explore keeps out of the cell; and the indoor exemption of
/// <see cref="PlayerbotNavigation.IsUnreachableOnFoot"/>, which only holds while an all-terrain walk joins the bot to the indoor goal.
/// </summary>
public sealed class PlayerbotSharedPinsTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // --- the ledger ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void OneBotPinningTwice_IsNoWorldPin_TwoDifferentBotsAre()
    {
        var pins = new PlayerbotSharedPins();
        Assert.Null(pins.Report(0, 35, 5, bot: 1, nowMs: 1_000));
        Assert.Null(pins.Report(0, 36, 6, bot: 1, nowMs: 2_000)); // the same bot again: still one distinct bot
        Assert.False(pins.IsWorldPin(0, 35, 5, 2_000));
        string? created = pins.Report(0, 37, 7, bot: 2, nowMs: 3_000);
        Assert.NotNull(created);
        Assert.Contains("30 min", created, StringComparison.Ordinal);
        Assert.True(pins.IsWorldPin(0, 35, 5, 3_000));
        Assert.False(pins.IsWorldPin(0, 135, 5, 3_000), "another cell");
        Assert.False(pins.IsWorldPin(1, 35, 5, 3_000), "another map");
        Assert.Null(pins.Report(0, 35, 5, bot: 3, nowMs: 4_000)); // already a pin: not reported again
    }

    [Fact]
    public void ABotsPinOutsideTheWindow_DoesNotCountTowardsAWorldPin()
    {
        var pins = new PlayerbotSharedPins();
        Assert.Null(pins.Report(0, 35, 5, bot: 1, nowMs: 1_000));
        Assert.Null(pins.Report(0, 35, 5, bot: 2, nowMs: 1_000 + PlayerbotSharedPins.WindowMs + 1));
        Assert.False(pins.IsWorldPin(0, 35, 5, 1_000 + PlayerbotSharedPins.WindowMs + 1));
    }

    [Fact]
    public void AWorldPin_Expires_AndARepeatLastsTwiceAsLong()
    {
        var pins = new PlayerbotSharedPins();
        pins.Report(0, 35, 5, 1, 1_000);
        Assert.NotNull(pins.Report(0, 35, 5, 2, 1_000));
        Assert.True(pins.IsWorldPin(0, 35, 5, 1_000 + PlayerbotSharedPins.PinMs - 1));
        Assert.False(pins.IsWorldPin(0, 35, 5, 1_000 + PlayerbotSharedPins.PinMs), "the pin ended");

        uint later = 1_000 + PlayerbotSharedPins.PinMs + 10;
        pins.Report(0, 35, 5, 3, later);
        string? repeat = pins.Report(0, 35, 5, 4, later);
        Assert.NotNull(repeat);
        Assert.Contains("60 min", repeat, StringComparison.Ordinal);
        Assert.True(pins.IsWorldPin(0, 35, 5, later + (2 * PlayerbotSharedPins.PinMs) - 1));
        Assert.False(pins.IsWorldPin(0, 35, 5, later + (2 * PlayerbotSharedPins.PinMs)));
    }

    [Fact]
    public void TheLedger_IsBounded_AndSurvivesParallelReports()
    {
        var pins = new PlayerbotSharedPins();
        Parallel.For(0, 8, worker =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                float x = ((worker * 2_000) + i) % 600 * PlayerbotStallWatch.PinCellYards;
                pins.Report(0, x, 5, bot: (ulong)(worker + 1), nowMs: (uint)(1_000 + i));
                pins.IsWorldPin(0, x, 5, (uint)(1_000 + i));
            }
        });
        Assert.InRange(pins.Count, 1, PlayerbotSharedPins.MaxKeys);
    }

    [Fact]
    public void AWatchWithoutTheLedger_KnowsOnlyItsOwnPins()
    {
        var watch = new PlayerbotStallWatch();
        Assert.False(watch.IsWorldPin(0, 35, 5, 1_000));
        Assert.Null(watch.ReportDeadEndPin(1, 0, 35, 5, 1_000));
        Assert.False(watch.IsDeadEndPin(0, 35, 5, 1_000));
    }

    // --- the brains ---------------------------------------------------------------------------------------------------------

    private static (int X, int Y, CellTile Tile)[] Pocket() =>
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2], Walkable = (i, j) => i <= 1 && j <= 5 || i >= 4 }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6] }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] }),
        (11, 11, new CellTile(1, 1, 40, 40, 8, 8, Size: 5) { LinkedSides = [4, 6] }),
    ];

    private static async Task SettleMovementAsync(SyntheticNavWorld world)
    {
        for (int tick = 0; tick < 100; tick++)
        {
            await world.Host.OnWorldAsync(() => { PlayerbotMovementControl.Update(world.Session, world.Player); return true; });
            await world.Host.World.AdvanceClockAsync(50);
        }
    }

    [Fact]
    public async Task AfterBotAPinsACell_BotBsFirstStallThereEscapesToBind()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Pocket(), new FloorLineOfSight());
        var pinPos = new Vector3(30, 5, 0);
        var bindPos = new Vector3(5, 5, 0);
        await world.PlaceAsync(pinPos);
        var options = new PlayerbotOptions { Enabled = true };
        var pins = new PlayerbotSharedPins { World = world.Host.World };
        var botA = new PlayerbotBrain(world.Session, options) { SharedPins = pins };
        var botB = new PlayerbotBrain(world.Session, options) { SharedPins = pins };
        await world.Host.OnWorldAsync(() =>
        {
            world.Player.Home = new HomeBind(0, 12, bindPos.X, bindPos.Y, bindPos.Z);
            uint now = world.Host.World.NowMs;

            // Bot A stalls twice in the cell: its own dead-end pin, which the ledger holds as one bot's.
            PlayerbotNavigation.SetStallWatch(world.Player, botA.StallWatch);
            botA.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            botA.GiveUpStalledGoal(world.Player, shareable: false);
            botA.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            botA.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.True(botA.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, now));
            Assert.False(pins.IsWorldPin(0, pinPos.X, pinPos.Y, now), "one bot is no world pin");

            // A second, different bot (the ledger knows bots by id) pins the cell too: now it is the world's.
            Assert.NotNull(pins.Report(0, pinPos.X, pinPos.Y, world.Player.Guid.Value + 1, now));
            Assert.True(pins.IsWorldPin(0, pinPos.X, pinPos.Y, now));

            // Bot B has never stalled here: the cell is a dead-end pin for it before its first stall.
            PlayerbotNavigation.SetStallWatch(world.Player, botB.StallWatch);
            Assert.Equal(0, botB.StallWatch.PinStallCount(0, pinPos.X, pinPos.Y));
            Assert.True(botB.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, now));
            Assert.True(PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos));

            // Its first stall counts as a dead-end pin: the entry is set aside for longer than one ordinary stall.
            botB.HoldGoalForTests(PlayerbotGoalKind.Quest, 267, 66);
            botB.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.Equal(1, botB.StallWatch.PinStallCount(0, pinPos.X, pinPos.Y));
            Assert.True(botB.Suspensions.IsEntrySuspended(267, now + PlayerbotSuspensions.SuspendMs + 1_000), "escalated, not the ordinary ten minutes");
            return true;
        });

        await SettleMovementAsync(world);
        Assert.True(Vector2.Distance(new(world.Player.X, world.Player.Y), new(bindPos.X, bindPos.Y)) < 1f,
            $"expected the bot at its bind point {bindPos}, it is at ({world.Player.X},{world.Player.Y})");
    }

    [Fact]
    public async Task AFirstStallOutsideAnyPin_DoesNotEscalate()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Pocket(), new FloorLineOfSight());
        var pinPos = new Vector3(30, 5, 0);
        await world.PlaceAsync(pinPos);
        var pins = new PlayerbotSharedPins { World = world.Host.World };
        var bot = new PlayerbotBrain(world.Session, new PlayerbotOptions { Enabled = true }) { SharedPins = pins };
        await world.Host.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            bot.HoldGoalForTests(PlayerbotGoalKind.Quest, 268, 67);
            bot.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.False(bot.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, now));
            Assert.False(bot.Suspensions.IsEntrySuspended(268, now + PlayerbotSuspensions.SuspendMs + 1_000), "the ordinary ten minute set-aside");
            return true;
        });
    }

    [Fact]
    public async Task AWorldPin_EndsForTheBots_WhenItExpires()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Pocket(), new FloorLineOfSight());
        var pinPos = new Vector3(30, 5, 0);
        await world.PlaceAsync(pinPos);
        var pins = new PlayerbotSharedPins { World = world.Host.World };
        var bot = new PlayerbotBrain(world.Session, new PlayerbotOptions { Enabled = true }) { SharedPins = pins };
        await world.Host.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            pins.Report(0, pinPos.X, pinPos.Y, 100, now);
            pins.Report(0, pinPos.X, pinPos.Y, 101, now);
            PlayerbotNavigation.SetStallWatch(world.Player, bot.StallWatch);
            Assert.True(PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos));
            return true;
        });
        await world.Host.World.AdvanceClockAsync((int)PlayerbotSharedPins.PinMs - 1_000);
        Assert.True(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos)), "just before the end");
        await world.Host.World.AdvanceClockAsync(2_000);
        Assert.False(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos)), "past the end");
    }

    // --- explore ------------------------------------------------------------------------------------------------------------

    private static readonly Vector3 Start = new(5, 5, 0), FarSpawn = new(75, 5, 0), NearSpawn = new(20, 115, 0);

    // Tile A (the bot's) joins C, E, F to the north. Tile B (X 40-80) holds FarSpawn: its western 30 yards are a hole (joined to nothing by any
    // slope) or, with steepStairs, walkable only as steep slopes (joined to A when slopes count).
    private static (int X, int Y, CellTile Tile)[] Layout(bool steepStairs) =>
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5)
        {
            LinkedSides = [0, 6],
            Walkable = (i, _) => steepStairs || i >= 6,
            Flags = (i, _) => i < 6 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
        }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (12, 10, new CellTile(2, 0, 0, 80, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (13, 10, new CellTile(3, 0, 0, 120, 8, 8, Size: 5) { LinkedSides = [4] }),
    ];

    private async Task<SyntheticNavWorld> ExploreWorldAsync((int X, int Y, CellTile Tile)[] tiles, ILineOfSight lineOfSight, params Vector3[] spawns)
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99010, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [.. spawns.Select((at, index) => new CreatureSpawn { Guid = (uint)(1 + index), Entry = 99010, MapId = 0, X = at.X, Y = at.Y, Z = at.Z })], [], [], []);
        SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, tiles, lineOfSight, configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new Content(content));
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(14, 0, 0, 8, 0, 1)]));
        });
        await world.Nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.Empty)!;
        await world.PlaceAsync(Start);
        return world;
    }

    /// <summary>Thinks until the graphs and the tiles answer for <paramref name="goal"/> (both places on a region), so a negative answer is a verdict.</summary>
    private static async Task WaitForRegionsAsync(SyntheticNavWorld world, Vector3 goal)
    {
        for (int think = 0; think < 80; think++)
        {
            bool ready = await world.Host.OnWorldAsync(() =>
            {
                PlayerbotNavigation.IsUnreachableOnFoot(world.Player, goal, out int from, out int to);
                return from >= 0 && to >= 0;
            });
            if (ready) return;
            await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.Fail("the goal's region is never found");
    }

    private static Task<uint?> ExploreChoiceAsync(SyntheticNavWorld world, PlayerbotBrain brain)
        => world.Host.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 8;
            PlayerbotNavigation.SetStallWatch(player, brain.StallWatch);
            world.Session.ManagedBudget = new ManagedActionBudget(1);
            typeof(PlayerbotBrain).GetMethod("Explore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(brain, [player, 500u]);
            var candidate = (PlayerbotExploreAtlas.Candidate?)typeof(PlayerbotBrain)
                .GetField("_exploreCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(brain);
            return candidate?.Spawn.Guid;
        });

    private static PlayerbotOptions ExploreOptions => new() { Enabled = true, Risk = { Enabled = false } };

    [Fact]
    public async Task AnExploreCandidateInAWorldPin_IsIneligible_UntilThePinEnds()
    {
        await using SyntheticNavWorld world = await ExploreWorldAsync(Layout(steepStairs: false), new FloorLineOfSight(), NearSpawn);
        await WaitForRegionsAsync(world, NearSpawn);
        var pins = new PlayerbotSharedPins { World = world.Host.World };

        Assert.Equal(1u, await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions) { SharedPins = pins }));

        await world.Host.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            pins.Report(0, NearSpawn.X, NearSpawn.Y, 100, now);
            pins.Report(0, NearSpawn.X, NearSpawn.Y, 101, now);
            return true;
        });
        Assert.Null(await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions) { SharedPins = pins }));

        await world.Host.World.AdvanceClockAsync((int)PlayerbotSharedPins.PinMs + 1_000);
        Assert.Equal(1u, await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions) { SharedPins = pins }));
    }

    [Fact]
    public async Task AnIndoorSpawnInAnotherAllTerrainComponent_IsNotAnExploreCandidate()
    {
        await using SyntheticNavWorld world = await ExploreWorldAsync(Layout(steepStairs: false), new IndoorsWhere(at => at.X > 40), FarSpawn);
        await WaitForRegionsAsync(world, FarSpawn);
        Assert.True(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsUnreachableOnFoot(world.Player, FarSpawn, out _, out _)),
            "no walk of any slope joins the sealed room");
        Assert.Null(await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions)));
    }

    [Fact]
    public async Task AnIndoorSpawnOnlySteepStairsReach_IsStillAnExploreCandidate()
    {
        await using SyntheticNavWorld world = await ExploreWorldAsync(Layout(steepStairs: true), new IndoorsWhere(at => at.X > 40), FarSpawn);
        await WaitForRegionsAsync(world, FarSpawn);
        (bool unreachable, int from, int to) = await world.Host.OnWorldAsync(() =>
        {
            bool answer = PlayerbotNavigation.IsUnreachableOnFoot(world.Player, FarSpawn, out int a, out int b);
            return (answer, a, b);
        });
        Assert.NotEqual(from, to); // the slope-excluded graph splits the stairs (the control for the exemption)
        Assert.False(unreachable, "the all-terrain graph joins them");
        Assert.Equal(1u, await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions)));
    }

    [Fact]
    public async Task TheSameSpawnOutdoors_IsUnreachableWhenOnlySteepStairsReachIt()
    {
        await using SyntheticNavWorld world = await ExploreWorldAsync(Layout(steepStairs: true), new FloorLineOfSight(), FarSpawn);
        await WaitForRegionsAsync(world, FarSpawn);
        Assert.True(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsUnreachableOnFoot(world.Player, FarSpawn, out _, out _)),
            "an outdoor goal keeps the slope-excluded verdict");
    }

    private sealed class Content(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
