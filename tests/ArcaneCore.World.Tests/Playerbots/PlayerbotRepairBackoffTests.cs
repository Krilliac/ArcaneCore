using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A repair trip whose every plan is refused rests, and the rest doubles with each empty round in a row
/// (<see cref="PlayerbotTownGoals.RepairTripBackoffMs"/>, <see cref="PlayerbotTownGoals.RepairTripBackoffMaxMs"/>): a bot with no way to
/// any repair NPC no longer plans to every spawn of the map every 30 seconds. The rests start over when a plan succeeds, when the gear is
/// fine again (whatever the bot's money), and when the bot is on another map or <see cref="PlayerbotTownGoals.RepairBackoffResetYards"/>
/// from where the last round ended. Rests are measured in thinks of half a second on the manual world clock.
/// </summary>
public sealed class PlayerbotRepairBackoffTests
{
    private const uint Step = 500;
    private const uint FirstRest = PlayerbotTownGoals.RepairTripBackoffMs;

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly WorldTestHost _host;
        private readonly WorldSession _session;
        private readonly PlayerbotTownGoals _goals;
        public bool Reachable { get; set; }
        public ScriptedPathfinder Path { get; }

        private Scenario(WorldTestHost host, WorldSession session, ScriptedPathfinder path)
        {
            _host = host;
            _session = session;
            Path = path;
            _goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
        }

        public WorldTestHost Host => _host;

        /// <summary>Teleport the bot to the same spot on <paramref name="map"/> and acknowledge like a client until it stands there.</summary>
        public async Task TeleportToMap(uint map)
        {
            TeleportService teleports = _host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
            Assert.True(await OnWorld(p => teleports.TeleportTo(p, map, p.X, p.Y, p.Z, 0)), "teleport refused");
            for (int tick = 0; ; tick++)
            {
                Assert.True(tick < 200, "the bot did not arrive");
                bool there = await OnWorld(p =>
                {
                    PlayerbotMovementControl.Update(_session, p);
                    return p.IsInWorld && teleports.StageOf(p) is null && p.MapId == map;
                });
                if (there) return;
                await _host.World.AdvanceClockAsync(50);
            }
        }

        public static async Task<Scenario> StartAsync(Func<Scenario, ScriptedPathfinder>? pathfinder = null)
        {
            var items = new ItemTestContent();
            items.Templates.Templates.Add(new ItemTemplate
            {
                Entry = 9212, Name = "boots", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
                InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = 20, ItemLevel = 10, Quality = 1, Stackable = 1,
                AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
            });
            using IDisposable scope = items.Use();
            ManyRepairServices.Current.Value = new ManyRepairFixture();
            WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            Scenario? scenario = null;
            var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => scenario!.Reachable };
            scenario = new Scenario(host, session, path);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: path);
                PlayerbotEquipmentTests.EquipFromBags(player, 9212);
                player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                player.Money = 100;
                return true;
            });
            return scenario;
        }

        public Task<T> OnWorld<T>(Func<Player, T> action) => _host.OnWorldAsync(() => action(_session.Player!));

        public Task Break(bool broken) => OnWorld(player =>
        {
            Item boots = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!;
            boots.Durability = broken ? 0 : boots.MaxDurability;
            return true;
        });

        public Task Advance(uint ms) => _host.World.AdvanceClockAsync(ms);

        /// <summary>One think half a second on: the path queries it made.</summary>
        public async Task<int> Think()
        {
            await _host.World.AdvanceClockAsync(Step);
            return await _host.OnWorldAsync(() =>
            {
                int before = Path.Queries;
                _goals.BeginThink();
                _session.ManagedBudget = new ManagedActionBudget(1);
                _goals.Update(_session.Player!, Step);
                return Path.Queries - before;
            });
        }

        /// <summary>
        /// Thinks until the idle stretch after a round of planning is over and the next round begins; the stretch in ms. With
        /// <paramref name="inRound"/> a round is already under way (its queries have been seen).
        /// </summary>
        public async Task<uint> NextRest(bool inRound = false, uint minIdle = 0)
        {
            bool seen = inRound;
            uint idle = 0;
            for (int i = 0; i < 2_000; i++)
            {
                if (await Think() > 0)
                {
                    if (seen && idle > minIdle) return idle;
                    seen = true;
                    idle = 0;
                }
                else if (seen) idle += Step;
            }

            throw new TimeoutException("no rest followed by a round");
        }

        public async ValueTask DisposeAsync()
        {
            _session.Kick();
            await _session.ManagedClosed;
            await _host.DisposeAsync();
            ManyRepairServices.Current.Value = null;
        }
    }

    private static void AssertRest(uint expectedMs, uint measuredMs)
        => Assert.InRange(measuredMs, expectedMs - 1_000, expectedMs + 1_000);

    [Fact]
    public async Task EmptyRounds_RestLongerEachTime()
    {
        await using Scenario s = await Scenario.StartAsync();
        AssertRest(FirstRest, await s.NextRest());
        AssertRest(FirstRest * 2, await s.NextRest(inRound: true));
        AssertRest(FirstRest * 4, await s.NextRest(inRound: true));
    }

    /// <summary>The rests stop doubling at <see cref="PlayerbotTownGoals.RepairTripBackoffMaxMs"/>.</summary>
    [Fact]
    public async Task TheRest_StopsGrowingAtTheMaximum()
    {
        await using Scenario s = await Scenario.StartAsync();
        AssertRest(FirstRest, await s.NextRest());
        AssertRest(FirstRest * 2, await s.NextRest(inRound: true));
        AssertRest(FirstRest * 4, await s.NextRest(inRound: true));
        AssertRest(FirstRest * 8, await s.NextRest(inRound: true));
        AssertRest(PlayerbotTownGoals.RepairTripBackoffMaxMs, await s.NextRest(inRound: true)); // 480 s doubled: held to 300 s
        AssertRest(PlayerbotTownGoals.RepairTripBackoffMaxMs, await s.NextRest(inRound: true));
    }

    /// <summary>Far from where the last empty round ended, the bot plans again at once and its next rest is the short one.</summary>
    [Fact]
    public async Task BotMovedFar_DuringARest_PlansAtOnce_AndRestsShortAgain()
    {
        await using Scenario s = await Scenario.StartAsync();
        await s.NextRest();
        await s.NextRest(inRound: true);
        for (int i = 0; i < 6 && await s.Think() > 0; i++) { } // the third round ends: a 120 s rest begins
        Assert.Equal(0, await s.Think());
        await s.OnWorld(p => { p.Relocate(p.X - PlayerbotTownGoals.RepairBackoffResetYards - 50, p.Y, p.Z, p.Orientation, 0); return true; });
        Assert.True(await s.Think() > 0, "the bot, 350 yards from where the last round ended, kept resting");
        AssertRest(FirstRest, await s.NextRest(inRound: true));
    }

    /// <summary>A rest that has run out far from where it began does not count towards the next one either.</summary>
    [Fact]
    public async Task BotFarAway_WhenTheRestHasExpired_RestsShortAgain()
    {
        await using Scenario s = await Scenario.StartAsync();
        await s.NextRest();
        await s.NextRest(inRound: true);
        for (int i = 0; i < 6 && await s.Think() > 0; i++) { }
        await s.Advance(PlayerbotTownGoals.RepairTripBackoffMaxMs + 1_000); // the 120 s rest is over before the bot looks again
        await s.OnWorld(p => { p.Relocate(p.X - PlayerbotTownGoals.RepairBackoffResetYards - 50, p.Y, p.Z, p.Orientation, 0); return true; });
        AssertRest(FirstRest, await s.NextRest());
    }

    /// <summary>The gear repaired and worn out again: the first empty round after that rests the short time.</summary>
    [Theory]
    [InlineData(100u)]
    [InlineData(0u)]
    public async Task GearFineAgain_RestsStartOver_WhateverTheMoney(uint moneyWhileFine)
    {
        await using Scenario s = await Scenario.StartAsync();
        await s.NextRest();
        await s.NextRest(inRound: true);
        for (int i = 0; i < 6 && await s.Think() > 0; i++) { }
        await s.OnWorld(p => { p.Money = moneyWhileFine; return true; });
        await s.Break(false);
        await s.Think(); // looked at with the gear fine
        await s.Break(true);
        await s.OnWorld(p => { p.Money = 100; return true; });
        AssertRest(FirstRest, await s.NextRest());
    }

    /// <summary>One plan that succeeds (the walk to that NPC then comes to nothing): the next empty round rests the short time.</summary>
    [Fact]
    public async Task ASuccessfulPlan_StartsTheRestsOver()
    {
        await using Scenario s = await Scenario.StartAsync();
        await s.NextRest();
        await s.NextRest(inRound: true); // the third round has begun, three of the ten spawns refused
        s.Reachable = true;
        Assert.True(await s.Think() > 0); // the next spawn plans
        await s.OnWorld(p =>
        {
            PlayerbotMotion.Reset(p); // rooted: the walk makes no headway and is set aside after RepairTripStuckMs
            var movement = p.Movement;
            movement.Flags |= MovementFlags.Root;
            p.ApplyMovement(movement, 0);
            return true;
        });
        s.Reachable = false;
        uint idle = await s.NextRest(inRound: true, minIdle: 10_000); // the stuck walk re-plans each think: only a real rest is longer than that
        AssertRest(FirstRest, idle);
    }

    /// <summary>
    /// On another map the rests start over (a dungeon without repair NPCs must not run the rest up to 300 s, and leaving it must not leave
    /// the bot resting). Map 1 holds no repair NPC here: one think there, and back on map 0 at the same spot the trip plans at once.
    /// </summary>
    [Fact]
    public async Task OtherMap_StartsTheRestsOver()
    {
        await using Scenario s = await Scenario.StartAsync();
        await s.NextRest();
        await s.NextRest(inRound: true);
        for (int i = 0; i < 6 && await s.Think() > 0; i++) { }
        Assert.Equal(0, await s.Think()); // resting 120 s on map 0
        await s.TeleportToMap(1);
        await s.Think();
        await s.TeleportToMap(0);
        int queries = 0;
        for (int i = 0; i < 4; i++) queries += await s.Think();
        Assert.True(queries > 0, "back on map 0 the bot went on resting");
    }
}
