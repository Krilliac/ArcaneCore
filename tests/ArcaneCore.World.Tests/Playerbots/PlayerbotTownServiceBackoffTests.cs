using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// What the town controller does at a service NPC it is in reach of: the idle NPC left out of the search
/// (<see cref="PlayerbotTownGoals.NothingToDoMs"/>, <see cref="PlayerbotTownGoals.RequestBackoffMs"/>), the repair NPC of a trip that is never
/// left out, and the in-reach bot that steps to a spot of its own when another bot stands on its place.
/// </summary>
public sealed class PlayerbotTownServiceBackoffTests
{
    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    /// <summary>A food vendor the bot stands in reach of, with no food in its bags and 100 copper.</summary>
    private static async Task RunAsync(bool manualClock, int extraBots, Func<WorldTestHost, WorldSession[], PlayerbotTownGoals, Task> body,
        Action<IServiceCollection>? configure = null, IEnumerable<ItemTemplate>? extraItems = null)
    {
        var npc = new TownVendorFixture();
        ItemTestContent items = PlayerbotTownGoalsTests.FoodAndGrayContent();
        foreach (ItemTemplate template in extraItems ?? []) items.Templates.Templates.Add(template);
        using IDisposable itemScope = items.Use();
        TownVendorServices.Current.Value = npc;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                if (manualClock) services.AddSingleton<IWorldFeature, ManualClock>();
                configure?.Invoke(services);
            });
            var sessions = new List<WorldSession> { await PlayerbotMovementControlTests.EnterAsync(host) };
            try
            {
                for (int i = 0; i < extraBots; i++)
                    sessions.Add(await Combat.CombatTestSessions.EnterAsync(host, "CROWD" + (char)('A' + i), "Crowdbot" + (char)('a' + i)));
                foreach (WorldSession session in sessions)
                {
                    for (int i = 0; i < 100 && !await host.OnWorldAsync(() => session.Player!.VisibleObjects.Contains(TownVendorFixture.Guid)); i++)
                    {
                        if (manualClock) await host.World.AdvanceClockAsync(100);
                        else await Task.Delay(50);
                    }
                }

                await host.OnWorldAsync(() =>
                {
                    PlayerbotTownGoalsTests.RegisterFoodSpell(host);
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                    sessions[0].Player!.Money = 100;
                    return true;
                });
                var goals = new PlayerbotTownGoals(sessions[0], new PlayerbotOptions { Enabled = true });
                await body(host, [.. sessions], goals);
            }
            finally
            {
                foreach (WorldSession session in sessions) { session.Kick(); await session.ManagedClosed; }
            }
        }
        finally { TownVendorServices.Current.Value = null; }
    }

    /// <summary>A vendor that is useful (the bot has no food) yet where nothing is asked for drops out of the search for a minute, then returns.</summary>
    [Fact]
    public async Task AUsefulVendorWhereNothingIsAsked_IsLeftOutForTheNothingToDoMemo_ThenReturns()
    {
        await RunAsync(manualClock: true, extraBots: 0, async (host, sessions, goals) =>
        {
            Player player = sessions[0].Player!;
            goals.ServicesForTests = _ => false;
            await host.OnWorldAsync(() =>
            {
                Assert.True(goals.HasCandidate(player));
                sessions[0].ManagedBudget = new ManagedActionBudget(4);
                Assert.False(goals.Update(player, 1500));
                Assert.False(goals.HasCandidate(player));
                return true;
            });
            await host.World.AdvanceClockAsync(PlayerbotTownGoals.NothingToDoMs - 1_000);
            Assert.False(await host.OnWorldAsync(() => goals.HasCandidate(player)));
            await host.World.AdvanceClockAsync(2_000);
            Assert.True(await host.OnWorldAsync(() => goals.HasCandidate(player)));
        });
    }

    /// <summary>A request that could not be sent (the action budget is spent) backs the NPC off for seconds, not for the minute of a verdict.</summary>
    [Fact]
    public async Task ASpentBudget_BacksTheVendorOffForSeconds_NotForAMinute()
    {
        await RunAsync(manualClock: true, extraBots: 0, async (host, sessions, goals) =>
        {
            Player player = sessions[0].Player!;
            await host.OnWorldAsync(() =>
            {
                Assert.True(goals.HasCandidate(player));
                sessions[0].ManagedBudget = new ManagedActionBudget(0);
                Assert.False(goals.Update(player, 1500));
                Assert.False(goals.HasCandidate(player));
                Assert.Equal(100u, player.Money);
                return true;
            });
            Assert.True(PlayerbotTownGoals.RequestBackoffMs < PlayerbotTownGoals.NothingToDoMs);
            await host.World.AdvanceClockAsync(PlayerbotTownGoals.RequestBackoffMs + 500);
            Assert.True(await host.OnWorldAsync(() => goals.HasCandidate(player)), "the vendor was still left out after the short backoff");
        });
    }

    /// <summary>The repair NPC of a repair trip is never left out for doing nothing: that would end the visit and send the bot to a farther NPC.</summary>
    [Fact]
    public async Task TheRepairNpcOfARepairTrip_IsNotLeftOut_WhenNothingIsAsked()
    {
        await RunAsync(manualClock: true, extraBots: 0, async (host, sessions, goals) =>
        {
            Player player = sessions[0].Player!;
            int asked = 0;
            goals.ServicesForTests = _ => { asked++; return false; };
            await host.OnWorldAsync(() =>
            {
                PlayerbotEquipmentTests.EquipFromBags(player, 9211);
                player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                ((Creature)player.Map!.FindObject(TownVendorFixture.Guid)!).NpcFlags = (uint)(NpcFlags.Vendor | NpcFlags.Repair);
                Assert.True(PlayerbotTownGoals.NeedsRepairTrip(player));
                Assert.True(goals.HasCandidate(player));
                sessions[0].ManagedBudget = new ManagedActionBudget(4);
                goals.Update(player, 1500);
                sessions[0].ManagedBudget = new ManagedActionBudget(4);
                goals.Update(player, 1500);
                Assert.Equal(2, asked); // the second think visited it again: it was not left out
                Assert.True(goals.HasCandidate(player), "the repair NPC was left out");
                return true;
            });
        }, services => services.AddSingleton(PlayerbotTownGoalsTests.RepairPrices()), [PlayerbotTownGoalsTests.Boots(9211, durability: 20)]);
    }

    /// <summary>
    /// A bot in reach whose walk to its own spot does not go on (here: the map's pathfinder was replaced, which makes the route stale)
    /// trades at once from where it stands, instead of returning false and leaving the bot to the lower rungs.
    /// </summary>
    [Fact]
    public async Task AnInReachBotWhoseSpotWalkWillNotAdvance_StillTradesThatThink()
    {
        await RunAsync(manualClock: false, extraBots: 1, async (host, sessions, goals) =>
        {
            Player player = sessions[0].Player!;
            await host.WaitForWorldAsync(() => player.VisibleObjects.Contains(sessions[1].Player!.Guid), "the second bot in sight");
            await host.OnWorldAsync(() =>
            {
                sessions[1].Player!.Relocate(player.X, player.Y, player.Z, player.Orientation, host.World.NowMs);
                sessions[0].ManagedBudget = new ManagedActionBudget(4);
                goals.BeginThink();
                Assert.True(goals.Update(player, 500)); // plans the walk to its own spot and sets out
                Assert.Equal(0u, player.Inventory.GetItemCount(117));
                // A new pathfinder makes the route stale: it cannot be advanced.
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                sessions[0].ManagedBudget = new ManagedActionBudget(4);
                goals.BeginThink();
                Assert.True(goals.Update(player, 500), "the think returned false with the bot in reach");
                Assert.Equal(1u, player.Inventory.GetItemCount(117));
                Assert.Equal(90u, player.Money);
                return true;
            });
        });
    }

    /// <summary>The in-reach bot that never makes progress toward its spot is not set aside: after the stuck time it trades from where it stands.</summary>
    [Fact]
    public async Task AnInReachBotThatMakesNoProgressTowardItsSpot_TradesFromWhereItStands()
    {
        await RunAsync(manualClock: false, extraBots: 1, async (host, sessions, goals) =>
        {
            Player player = sessions[0].Player!;
            await host.WaitForWorldAsync(() => player.VisibleObjects.Contains(sessions[1].Player!.Guid), "the second bot in sight");
            await host.OnWorldAsync(() =>
            {
                sessions[1].Player!.Relocate(player.X, player.Y, player.Z, player.Orientation, host.World.NowMs);
                float x = player.X, y = player.Y;
                bool bought = false;
                // The motion is never elapsed, so the bot never leaves where it stands.
                for (int step = 0; step < 60 && !bought; step++)
                {
                    sessions[0].ManagedBudget = new ManagedActionBudget(4);
                    goals.BeginThink();
                    goals.Update(player, 500);
                    bought = player.Inventory.GetItemCount(117) >= 1;
                }

                Assert.True(bought, "the bot never traded");
                Assert.True(Math.Abs(player.X - x) < 1f && Math.Abs(player.Y - y) < 1f, "the bot moved: the test does not model one with no progress");
                Assert.False(goals.Suspensions?.IsEntrySuspended(TownVendorFixture.Entry, host.World.NowMs) ?? false);
                return true;
            });
        });
    }

    /// <summary>A bot that comes into reach on top of another bot steps to its own spot (2 to 4 yards from the NPC) and trades there.</summary>
    [Fact]
    public async Task AnInReachBotStandingOnAnotherBot_StepsToItsOwnSpot_ThenTrades()
    {
        await RunAsync(manualClock: false, extraBots: 1, async (host, sessions, goals) =>
        {
            Player player = sessions[0].Player!;
            await host.WaitForWorldAsync(() => player.VisibleObjects.Contains(sessions[1].Player!.Guid), "the second bot in sight");
            await host.OnWorldAsync(() =>
            {
                sessions[1].Player!.Relocate(player.X, player.Y, player.Z, player.Orientation, host.World.NowMs);
                var npcPosition = new Vector3(-8948.95f, -132.49f, 83.53f);
                Vector3 spot = PlayerbotStandSpots.ServiceSpot(player.Guid.Value, TownVendorFixture.Guid.Value, npcPosition, 0);
                Assert.True(Vector2.Distance(new(player.X, player.Y), new(spot.X, spot.Y)) > PlayerbotTownGoals.VisitSpotArrivedYards + 0.25f,
                    "the test bot starts on its own spot");
                for (int step = 0; step < 60 && player.Inventory.GetItemCount(117) < 1; step++)
                {
                    PlayerbotMotion.ElapseForTests(player, 500);
                    sessions[0].ManagedBudget = new ManagedActionBudget(4);
                    goals.BeginThink();
                    goals.Update(player, 500);
                }

                Assert.Equal(1u, player.Inventory.GetItemCount(117));
                Assert.True(Vector2.Distance(new(player.X, player.Y), new(spot.X, spot.Y)) <= PlayerbotTownGoals.VisitSpotArrivedYards + 0.25f,
                    $"the bot ended {Vector2.Distance(new(player.X, player.Y), new(spot.X, spot.Y)):F2} yd from its own spot");
                return true;
            });
        });
    }
}
