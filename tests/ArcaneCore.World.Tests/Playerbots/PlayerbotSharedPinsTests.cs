using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
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
/// so a third bot escapes at its first stall and explore keeps out of the cell. A world pin never refuses a plan (an NPC in the cell stays
/// reachable), is never made in a city or on an instanceable map, and an outdoor bot's explore skips indoor spawns.
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
        Assert.Null(pins.Report(0, 0, 35, 5, bot: 1, nowMs: 1_000));
        Assert.Null(pins.Report(0, 0, 36, 6, bot: 1, nowMs: 2_000)); // the same bot again: still one distinct bot
        Assert.False(pins.IsWorldPin(0, 0, 35, 5, 2_000));
        string? created = pins.Report(0, 0, 37, 7, bot: 2, nowMs: 3_000);
        Assert.NotNull(created);
        Assert.Contains("30 min", created, StringComparison.Ordinal);
        Assert.True(pins.IsWorldPin(0, 0, 35, 5, 3_000));
        Assert.False(pins.IsWorldPin(0, 0, 135, 5, 3_000), "another cell");
        Assert.False(pins.IsWorldPin(1, 0, 35, 5, 3_000), "another map");
        Assert.False(pins.IsWorldPin(0, 7, 35, 5, 3_000), "another instance of the map");
        Assert.Null(pins.Report(0, 0, 35, 5, bot: 3, nowMs: 4_000)); // already a pin: not reported again
    }

    [Fact]
    public void ABotsPinOutsideTheWindow_DoesNotCountTowardsAWorldPin()
    {
        var pins = new PlayerbotSharedPins();
        Assert.Null(pins.Report(0, 0, 35, 5, bot: 1, nowMs: 1_000));
        Assert.Null(pins.Report(0, 0, 35, 5, bot: 2, nowMs: 1_000 + PlayerbotSharedPins.WindowMs + 1));
        Assert.False(pins.IsWorldPin(0, 0, 35, 5, 1_000 + PlayerbotSharedPins.WindowMs + 1));
    }

    [Fact]
    public void AWorldPin_Expires_AndARepeatLastsTwiceAsLong()
    {
        var pins = new PlayerbotSharedPins();
        pins.Report(0, 0, 35, 5, 1, 1_000);
        Assert.NotNull(pins.Report(0, 0, 35, 5, 2, 1_000));
        Assert.True(pins.IsWorldPin(0, 0, 35, 5, 1_000 + PlayerbotSharedPins.PinMs - 1));
        Assert.False(pins.IsWorldPin(0, 0, 35, 5, 1_000 + PlayerbotSharedPins.PinMs), "the pin ended");

        uint later = 1_000 + PlayerbotSharedPins.PinMs + 10;
        pins.Report(0, 0, 35, 5, 3, later);
        string? repeat = pins.Report(0, 0, 35, 5, 4, later);
        Assert.NotNull(repeat);
        Assert.Contains("60 min", repeat, StringComparison.Ordinal);
        Assert.True(pins.IsWorldPin(0, 0, 35, 5, later + (2 * PlayerbotSharedPins.PinMs) - 1));
        Assert.False(pins.IsWorldPin(0, 0, 35, 5, later + (2 * PlayerbotSharedPins.PinMs)));
    }

    [Fact]
    public void AfterADayWithoutReports_ARepeatStartsAtTheFirstLengthAgain()
    {
        var pins = new PlayerbotSharedPins();
        pins.Report(0, 0, 35, 5, 1, 1_000);
        Assert.Contains("30 min", pins.Report(0, 0, 35, 5, 2, 1_000), StringComparison.Ordinal);

        // Inside the quiet period a repeat doubles (the control for the reset below).
        uint soon = 1_000 + PlayerbotSharedPins.PinMs + 10;
        pins.Report(0, 0, 35, 5, 3, soon);
        Assert.Contains("60 min", pins.Report(0, 0, 35, 5, 4, soon), StringComparison.Ordinal);

        uint quiet = soon + PlayerbotSharedPins.RepeatQuietMs + 1_000;
        pins.Report(0, 0, 35, 5, 5, quiet);
        string? again = pins.Report(0, 0, 35, 5, 6, quiet);
        Assert.NotNull(again);
        Assert.Contains("30 min", again, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenFull_TheLedgerDropsAnInactiveCellBeforeAnActiveWorldPin()
    {
        var pins = new PlayerbotSharedPins();
        // Cell 0 is the oldest record and an active world pin; cells 1..MaxKeys-1 hold one bot each and are newer.
        pins.Report(0, 0, 0, 0, 1, 1_000);
        Assert.NotNull(pins.Report(0, 0, 0, 0, 2, 1_000));
        for (int i = 1; i < PlayerbotSharedPins.MaxKeys; i++)
            pins.Report(0, 0, i * PlayerbotStallWatch.PinCellYards, 0, 10, (uint)(2_000 + i));
        Assert.Equal(PlayerbotSharedPins.MaxKeys, pins.Count);

        // One more cell: the oldest inactive record (cell 1) goes, not the older active pin (cell 0).
        pins.Report(0, 0, PlayerbotSharedPins.MaxKeys * PlayerbotStallWatch.PinCellYards, 0, 10, 100_000);
        Assert.Equal(PlayerbotSharedPins.MaxKeys, pins.Count);
        Assert.True(pins.IsWorldPin(0, 0, 0, 0, 100_000), "the active pin survived");
        // Cell 1's bot was forgotten: a second bot there makes no pin. Cell 2's bot is remembered: a second bot makes one.
        Assert.NotNull(pins.Report(0, 0, 2 * PlayerbotStallWatch.PinCellYards, 0, 11, 100_001));
        Assert.Null(pins.Report(0, 0, 1 * PlayerbotStallWatch.PinCellYards, 0, 11, 100_002));
    }

    [Fact]
    public void WhenFullOfActivePins_TheOldestReportedGoes()
    {
        var pins = new PlayerbotSharedPins();
        for (int i = 0; i < PlayerbotSharedPins.MaxKeys; i++)
        {
            float x = i * PlayerbotStallWatch.PinCellYards;
            pins.Report(0, 0, x, 0, 1, (uint)(1_000 + i));
            Assert.NotNull(pins.Report(0, 0, x, 0, 2, (uint)(1_000 + i)));
        }

        pins.Report(0, 0, PlayerbotSharedPins.MaxKeys * PlayerbotStallWatch.PinCellYards, 0, 1, 50_000);
        Assert.False(pins.IsWorldPin(0, 0, 0, 0, 50_000), "the oldest active pin made room");
        Assert.True(pins.IsWorldPin(0, 0, PlayerbotStallWatch.PinCellYards, 0, 50_000));
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
                pins.Report(0, 0, x, 5, bot: (ulong)(worker + 1), nowMs: (uint)(1_000 + i));
                pins.IsWorldPin(0, 0, x, 5, (uint)(1_000 + i));
            }
        });
        Assert.InRange(pins.Count, 1, PlayerbotSharedPins.MaxKeys);
    }

    [Fact]
    public void AWatchWithoutTheLedger_KnowsOnlyItsOwnPins()
    {
        var watch = new PlayerbotStallWatch();
        Assert.False(watch.IsWorldPin(0, 0, 35, 5, 1_000));
        Assert.Null(watch.ReportDeadEndPin(1, 0, 0, 35, 5, 1_000));
        Assert.False(watch.IsDeadEndPin(0, 35, 5, 1_000));
    }

    /// <summary>
    /// Timbermaw Hold (issues #1478 and #1479): stalls inside the tunnel, a region that is the only way between two large parts of the
    /// mesh, were shared as world pins, so every bot avoided the only route between Felwood and Winterspring. A region of a graph A - B - C
    /// that joins two large parts is a cut region and tells the world nothing; a region of a part does.
    /// </summary>
    [Fact]
    public void TwoBotsStallingInACutRegion_MakeNoWorldPin_ButTwoBotsStallingInAPartDo()
    {
        NavRegionGraph graph = NavRegionGraph.FromJoints(0, [PlayerbotNavigation.CutRegionPartPolygons + 100, 1, PlayerbotNavigation.CutRegionPartPolygons + 200],
            [(0, 1), (1, 2)]);
        Assert.True(PlayerbotNavigation.IsCutRegion(graph, 1));
        Assert.False(PlayerbotNavigation.IsCutRegion(graph, 0));
        Assert.False(PlayerbotNavigation.IsCutRegion(graph, 2));
        Assert.False(PlayerbotNavigation.IsCutRegion(graph, -1));

        var pins = new PlayerbotSharedPins();
        var botA = new PlayerbotStallWatch { SharedPins = pins };
        var botB = new PlayerbotStallWatch { SharedPins = pins };
        bool inB = PlayerbotNavigation.IsCutRegion(graph, 1);
        Assert.Null(botA.ReportDeadEndPin(1, 1, 0, 7020f, -2090f, 1_000, inB));
        Assert.Null(botB.ReportDeadEndPin(2, 1, 0, 7021f, -2091f, 2_000, inB));
        Assert.Equal(0, pins.Count);
        Assert.False(pins.IsWorldPin(1, 0, 7020f, -2090f, 2_000));
        Assert.False(botA.IsWorldPin(1, 0, 7020f, -2090f, 2_000));

        // The same two reports from a region of one of the parts: the second bot makes the cell a world pin.
        bool inA = PlayerbotNavigation.IsCutRegion(graph, 0);
        Assert.Null(botA.ReportDeadEndPin(1, 1, 0, 6627f, -2024f, 1_000, inA));
        Assert.NotNull(botB.ReportDeadEndPin(2, 1, 0, 6628f, -2025f, 2_000, inA));
        Assert.True(pins.IsWorldPin(1, 0, 6627f, -2024f, 2_000));
    }

    // --- the brains ---------------------------------------------------------------------------------------------------------

    private static (int X, int Y, CellTile Tile)[] Pocket() =>
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2], Walkable = (i, j) => i <= 1 && j <= 5 || i >= 4 }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6] }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] }),
        (11, 11, new CellTile(1, 1, 40, 40, 8, 8, Size: 5) { LinkedSides = [4, 6] }),
    ];

    private const uint OpenZone = 12, CapitalZone = 1519;

    /// <summary>Gives the world an area table: zone 12 is open country, zone 1519 a capital (what the share guard reads).</summary>
    private static Task InstallAreasAsync(SyntheticNavWorld world)
        => world.Host.OnWorldAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(world.Host.World);
            maps.Terrain.Areas = new AreaTable([
                new AreaTemplate(OpenZone, 0, 0, 12, 0, 1, "open", 0, 0),
                new AreaTemplate(CapitalZone, 0, 0, 1519, (uint)ArcaneCore.Game.WorldState.Zones.AreaFlags.Capital, 1, "capital", 0, 0)], maps.Registry);
            return true;
        });

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
        await InstallAreasAsync(world);
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
            world.Player.ZoneId = OpenZone;
            uint now = world.Host.World.NowMs;

            // Bot A stalls twice in the cell: its own dead-end pin, which the ledger holds as one bot's.
            PlayerbotNavigation.SetStallWatch(world.Player, botA.StallWatch);
            botA.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            botA.GiveUpStalledGoal(world.Player, shareable: false);
            botA.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            botA.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.True(botA.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, now));
            Assert.Equal(1, pins.Count);
            Assert.False(pins.IsWorldPin(0, 0, pinPos.X, pinPos.Y, now), "one bot is no world pin");

            // A second, different bot (the ledger knows bots by id) pins the cell too: now it is the world's.
            Assert.NotNull(pins.Report(0, 0, pinPos.X, pinPos.Y, world.Player.Guid.Value + 1, now));
            Assert.True(pins.IsWorldPin(0, 0, pinPos.X, pinPos.Y, now));

            // Bot B has never stalled here: the cell is a world pin for it before its first stall, but not a plan-refusing pin of its own.
            PlayerbotNavigation.SetStallWatch(world.Player, botB.StallWatch);
            Assert.Equal(0, botB.StallWatch.PinStallCount(0, pinPos.X, pinPos.Y));
            Assert.True(PlayerbotNavigation.IsWorldPin(world.Player, pinPos));
            Assert.False(botB.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, now));
            Assert.False(PlayerbotNavigation.IsDeadEndPin(world.Player, pinPos));

            // Its first stall counts as a dead-end pin: the entry is set aside for longer than one ordinary stall.
            botB.Suspensions.SuspendQuest(66, now);
            botB.Suspensions.SuspendQuest(66, now); // two set-asides already: an own pin would now mark the quest unreachable
            botB.HoldGoalForTests(PlayerbotGoalKind.Quest, 267, 66);
            botB.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.Equal(1, botB.StallWatch.PinStallCount(0, pinPos.X, pinPos.Y));
            Assert.True(botB.Suspensions.IsEntrySuspended(267, now + PlayerbotSuspensions.SuspendMs + 1_000), "escalated, not the ordinary ten minutes");
            Assert.False(botB.Suspensions.IsQuestUnreachable(66, now), "a world pin alone does not make the quest unreachable");

            // The control: its second stall in the cell is its own pin, which does.
            botB.HoldGoalForTests(PlayerbotGoalKind.Quest, 267, 66);
            botB.GiveUpStalledGoal(world.Player, shareable: false);
            Assert.True(botB.Suspensions.IsQuestUnreachable(66, now));
            return true;
        });

        await SettleMovementAsync(world);
        Assert.True(Vector2.Distance(new(world.Player.X, world.Player.Y), new(bindPos.X, bindPos.Y)) < 1f,
            $"expected the bot at its bind point {bindPos}, it is at ({world.Player.X},{world.Player.Y})");
    }

    [Fact]
    public async Task ABotsPinInACityOrAnUnknownZone_IsNeverSharedWithTheWorld()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Pocket(), new FloorLineOfSight());
        var pinPos = new Vector3(30, 5, 0);
        await world.PlaceAsync(pinPos);
        var pins = new PlayerbotSharedPins { World = world.Host.World };
        var options = new PlayerbotOptions { Enabled = true };

        async Task StallTwiceAsync(uint zone)
        {
            var bot = new PlayerbotBrain(world.Session, options) { SharedPins = pins };
            await world.Host.OnWorldAsync(() =>
            {
                world.Player.ZoneId = zone;
                for (int i = 0; i < 2; i++)
                {
                    bot.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
                    bot.GiveUpStalledGoal(world.Player, shareable: false);
                }

                Assert.True(bot.StallWatch.IsDeadEndPin(0, pinPos.X, pinPos.Y, world.Host.World.NowMs), "the bot's own pin is made all the same");
                return true;
            });
        }

        await InstallAreasAsync(world);
        await StallTwiceAsync(0); // a bot with no zone yet: the guard fails closed
        Assert.Equal(0, pins.Count);
        await StallTwiceAsync(999_999); // a zone the table does not hold
        Assert.Equal(0, pins.Count);
        await StallTwiceAsync(CapitalZone);
        Assert.Equal(0, pins.Count);
        await StallTwiceAsync(OpenZone);
        Assert.Equal(1, pins.Count); // the control: the same stalls in open country are shared
    }

    [Theory]
    [InlineData(MapType.Common, 0u, 0u, true)]
    [InlineData(MapType.Common, 0u, 87u, true)]   // an ordinary sub-area
    [InlineData(MapType.Instance, 0u, 0u, false)]
    [InlineData(MapType.Raid, 0u, 0u, false)]
    [InlineData(MapType.Battleground, 0u, 0u, false)]
    [InlineData(MapType.Common, 1519u, 0u, false)] // a capital zone
    [InlineData(MapType.Common, 0u, 88u, false)]   // a town inside an open zone (Goldshire, Sentinel Hill, Booty Bay)
    [InlineData(MapType.Common, 0u, 89u, false)]   // a slave-capital sub-area
    public void OnlyOpenWorldMapsOutsideTownsMayShareAPin(MapType type, uint zoneOverride, uint areaId, bool expected)
    {
        var template = new MapTemplate(0, 0, type, 0, 0, 0, -1, 0, 0, "map", "");
        var registry = new MapRegistry([template]);
        var areas = new AreaTable([
            new AreaTemplate(OpenZone, 0, 0, 12, 0, 1, "open", 0, 0),
            new AreaTemplate(CapitalZone, 0, 0, 1519, (uint)ArcaneCore.Game.WorldState.Zones.AreaFlags.Capital, 1, "capital", 0, 0),
            new AreaTemplate(87, 0, OpenZone, 87, 0, 1, "farm", 0, 0),
            new AreaTemplate(88, 0, OpenZone, 88, (uint)ArcaneCore.Game.WorldState.Zones.AreaFlags.City, 1, "town", 0, 0),
            new AreaTemplate(89, 0, OpenZone, 89, (uint)ArcaneCore.Game.WorldState.Zones.AreaFlags.SlaveCapital, 1, "slave capital", 0, 0)], registry);
        uint zone = zoneOverride == 0 ? OpenZone : zoneOverride;
        Assert.Equal(expected, PlayerbotBrain.CanShareDeadEndPin(template, areas, zone, areaId));
    }

    [Fact]
    public void AnUnknownZoneOrNoMap_CannotSharePins()
    {
        var template = new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "map", "");
        var areas = new AreaTable([new AreaTemplate(OpenZone, 0, 0, 12, 0, 1, "open", 0, 0)], new MapRegistry([template]));
        Assert.False(PlayerbotBrain.CanShareDeadEndPin(template, areas, 0, 0));
        Assert.False(PlayerbotBrain.CanShareDeadEndPin(template, areas, 999_999, 0));
        Assert.False(PlayerbotBrain.CanShareDeadEndPin(null, areas, OpenZone, 0));
    }

    [Fact]
    public void AnActiveWorldPin_KeepsNoBotsWaiting()
    {
        var pins = new PlayerbotSharedPins();
        pins.Report(0, 0, 35, 5, 1, 1_000);
        Assert.NotNull(pins.Report(0, 0, 35, 5, 2, 1_000));
        for (ulong bot = 3; bot < 50; bot++) pins.Report(0, 0, 35, 5, bot, 2_000);
        // After the pin ends, one bot alone does not make a new one: the reports made while it was active were not kept.
        uint after = 1_000 + PlayerbotSharedPins.PinMs + 1;
        Assert.Null(pins.Report(0, 0, 35, 5, 60, after));
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
            pins.Report(0, 0, pinPos.X, pinPos.Y, 100, now);
            pins.Report(0, 0, pinPos.X, pinPos.Y, 101, now);
            PlayerbotNavigation.SetStallWatch(world.Player, bot.StallWatch);
            Assert.True(PlayerbotNavigation.IsWorldPin(world.Player, pinPos));
            return true;
        });
        await world.Host.World.AdvanceClockAsync((int)PlayerbotSharedPins.PinMs - 1_000);
        Assert.True(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsWorldPin(world.Player, pinPos)), "just before the end");
        await world.Host.World.AdvanceClockAsync(2_000);
        Assert.False(await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsWorldPin(world.Player, pinPos)), "past the end");
    }

    /// <summary>
    /// Two bots stalling at an NPC's cell (a quest giver, a vendor, a flag) must not take the NPC away from a third bot: a world pin steers
    /// explore and a first stall only, and never refuses a plan. The control is a pin of the bot's own, which does refuse.
    /// </summary>
    [Fact]
    public async Task AWorldPinAtAnNpcsCell_StillLetsAThirdBotPlanToTheNpc()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Pocket(), new FloorLineOfSight());
        var npc = new Vector3(30, 5, 0);
        await world.PlaceAsync(new Vector3(5, 5, 0));
        var options = new PlayerbotOptions { Enabled = true };
        var pins = new PlayerbotSharedPins { World = world.Host.World };
        var third = new PlayerbotBrain(world.Session, options) { SharedPins = pins };
        var ownPin = new PlayerbotBrain(world.Session, options);

        PlanOutcome Plan(PlayerbotBrain brain)
        {
            TileWait wait = default;
            PlayerbotNavigation.SetStallWatch(world.Player, brain.StallWatch);
            return PlayerbotNavigation.PlanToward(world.Player, npc, options, ref wait, world.Host.World.NowMs, out _);
        }

        await world.Host.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            pins.Report(0, 0, npc.X, npc.Y, 100, now);
            Assert.NotNull(pins.Report(0, 0, npc.X, npc.Y, 101, now));
            Assert.True(pins.IsWorldPin(0, 0, npc.X, npc.Y, now));
            ownPin.StallWatch.RecordPinStall(0, npc.X, npc.Y, now);
            ownPin.StallWatch.RecordPinStall(0, npc.X, npc.Y, now);
            return true;
        });

        Assert.Equal(PlanOutcome.Refused, await world.Host.OnWorldAsync(() => Plan(ownPin)));

        PlanOutcome outcome = PlanOutcome.Pending;
        for (int think = 0; think < 40 && outcome == PlanOutcome.Pending; think++)
        {
            outcome = await world.Host.OnWorldAsync(() => Plan(third));
            if (outcome == PlanOutcome.Pending) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.Equal(PlanOutcome.Route, outcome);
    }

    // --- explore ------------------------------------------------------------------------------------------------------------

    private static readonly Vector3 Start = new(5, 5, 0), FarSpawn = new(75, 5, 0), NearSpawn = new(20, 115, 0);

    // Tile A (the bot's) joins C, E, F to the north. Tile B (X 40-80) holds FarSpawn: its western 30 yards are a hole.
    private static (int X, int Y, CellTile Tile)[] Layout() =>
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (12, 10, new CellTile(2, 0, 0, 80, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (13, 10, new CellTile(3, 0, 0, 120, 8, 8, Size: 5) { LinkedSides = [4] }),
    ];

    private async Task<SyntheticNavWorld> ExploreWorldAsync(ILineOfSight lineOfSight, params Vector3[] spawns)
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 99010, Name = "right", MinLevel = 8, MaxLevel = 8, Faction = 14 }],
            [.. spawns.Select((at, index) => new CreatureSpawn { Guid = (uint)(1 + index), Entry = 99010, MapId = 0, X = at.X, Y = at.Y, Z = at.Z })], [], [], []);
        SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout(), lineOfSight, configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new Content(content));
            services.AddSingleton(new FactionTemplateCatalog([
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(14, 0, 0, 8, 0, 1)]));
        });
        await world.PlaceAsync(Start);
        return world;
    }

    /// <summary>Thinks until the graph and the tiles answer for <paramref name="goal"/> (both places on a region), so a negative answer is a verdict.</summary>
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
        await using SyntheticNavWorld world = await ExploreWorldAsync(new FloorLineOfSight(), NearSpawn);
        await WaitForRegionsAsync(world, NearSpawn);
        var pins = new PlayerbotSharedPins { World = world.Host.World };

        Assert.Equal(1u, await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions) { SharedPins = pins }));

        await world.Host.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            pins.Report(0, 0, NearSpawn.X, NearSpawn.Y, 100, now);
            pins.Report(0, 0, NearSpawn.X, NearSpawn.Y, 101, now);
            return true;
        });
        Assert.Null(await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions) { SharedPins = pins }));

        await world.Host.World.AdvanceClockAsync((int)PlayerbotSharedPins.PinMs + 1_000);
        Assert.Equal(1u, await ExploreChoiceAsync(world, new PlayerbotBrain(world.Session, ExploreOptions) { SharedPins = pins }));
    }

    [Fact]
    public async Task AnOutdoorBot_SkipsIndoorExploreSpots_AnIndoorBotDoesNot()
    {
        await using SyntheticNavWorld world = await ExploreWorldAsync(new IndoorsWhere(at => at.X > 40), FarSpawn);
        await WaitForRegionsAsync(world, FarSpawn);
        (bool indoorSpot, bool outdoorSpot) = await world.Host.OnWorldAsync(() =>
        {
            bool skipIndoor = PlayerbotBrain.ExploreSkipsIndoor(world.Player);
            Assert.True(skipIndoor, "a bot standing outdoors leaves indoor spots out");
            return (PlayerbotBrain.ExploreSpotUsable(world.Player, FarSpawn, skipIndoor), PlayerbotBrain.ExploreSpotUsable(world.Player, NearSpawn, skipIndoor));
        });
        Assert.False(indoorSpot, "the indoor spot is left out for a bot standing outdoors");
        Assert.True(outdoorSpot, "an outdoor spot is not");

        await world.PlaceAsync(new Vector3(75, 25, 0));
        Assert.True(await world.Host.OnWorldAsync(() =>
        {
            bool skipIndoor = PlayerbotBrain.ExploreSkipsIndoor(world.Player);
            Assert.False(skipIndoor, "a bot already indoors judges nothing");
            return PlayerbotBrain.ExploreSpotUsable(world.Player, FarSpawn, skipIndoor);
        }), "a bot already indoors may explore indoors");
    }

    private sealed class Content(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
