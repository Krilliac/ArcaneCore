using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A repair trip whose every plan is refused rests, and the rest doubles with each empty round in a row
/// (<see cref="PlayerbotTownGoals.RepairTripBackoffMs"/>, <see cref="PlayerbotTownGoals.RepairTripBackoffMaxMs"/>): a bot with no way to
/// any repair NPC no longer plans to every spawn of the map every 30 seconds.
/// </summary>
public sealed class PlayerbotRepairBackoffTests
{
    [Fact]
    public async Task EmptyRounds_RestLongerEachTime_ThenAreAskedAgain()
    {
        var path = new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => false };
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 9212, Name = "boots", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
            InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = 20, ItemLevel = 10, Quality = 1, Stackable = 1,
            AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
        });
        using IDisposable itemScope = items.Use();
        ManyRepairServices.Current.Value = new ManyRepairFixture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: path);
                    PlayerbotEquipmentTests.EquipFromBags(player, 9212);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    player.Money = 100;
                    return true;
                });

                int Think() => host.OnWorldAsync(() =>
                {
                    int before = path.Queries;
                    goals.BeginThink();
                    session.ManagedBudget = new ManagedActionBudget(1);
                    goals.Update(session.Player!, 500);
                    return path.Queries - before;
                }).Result;

                async Task<int> RunFor(uint ms)
                {
                    int queries = 0;
                    for (uint t = 0; t < ms; t += 500)
                    {
                        await host.World.AdvanceClockAsync(500);
                        queries += Think();
                    }

                    return queries;
                }

                int first = await RunFor(10_000); // the first round: every spawn tried; the 30 s rest begins at about 2.5 s
                Assert.Equal(ScriptedPathfinder.SpawnCount, first);
                Assert.Equal(0, await RunFor(20_000)); // to 30 s: inside the first rest
                Assert.True(await RunFor(5_000) > 0); // to 35 s: the rest is over, a second round (its 60 s rest begins at about 35 s)
                await RunFor(10_000); // to 45 s
                Assert.Equal(0, await RunFor(40_000)); // to 85 s: a 30 s rest would have ended at about 65 s
                Assert.True(await RunFor(20_000) > 0); // to 105 s: the 60 s rest is over
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { ManyRepairServices.Current.Value = null; }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
