using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

public sealed class PlayerbotDeathSpotMemoryTests
{
    private const uint KillerEntry = 995101;

    [Fact]
    public async Task RecordedLoop_BlocksTargetAndRouteUntilConfiguredExpiry()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options =>
        {
            options.Risk.DangerMemorySeconds = 0;
            options.Risk.DeathLoopMemorySeconds = 30;
        });
        await DieThreeTimesAsync(world);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Map!.Combat.ResurrectPlayer(player, 1f, applySickness: false);
            world.Session.ManagedBudget = new ManagedActionBudget(8);
            world.Brain.Update(RiskTestWorld.ThinkMs);
            return true;
        });
        Creature target = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(995102), 45, 0));
        await world.SeeAsync(target);

        uint until = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotNavigation.Guard(player, world.Brain.Risk);
            PlayerbotHazard zone = Assert.Single(world.Brain.Risk.Hazards.Active(player.MapId, world.Host.World.NowMs));
            Assert.Equal("death-loop", zone.Kind);
            Assert.Equal(PlayerbotStallWatch.DeathLoopYards, zone.Radius);
            Assert.Contains(KillerEntry, zone.KillerEntries ?? []);
            Assert.Null(world.Brain.Risk.ChooseTarget(player, 0, _ => false, questObjective: false));
            Assert.False(PlayerbotNavigation.TryPlan(player, new Vector3(target.X, target.Y, target.Z), world.Options, out _));
            return zone.UntilMs;
        });
        uint now = await world.OnWorldAsync(() => world.Host.World.NowMs);
        await world.Host.World.AdvanceClockAsync(until - now - 1);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Assert.Null(world.Brain.Risk.ChooseTarget(player, 0, _ => false, questObjective: false));
            Assert.False(PlayerbotNavigation.TryPlan(player, new Vector3(target.X, target.Y, target.Z), world.Options, out _));
            return true;
        });
        await world.Host.World.AdvanceClockAsync(1);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Assert.Empty(world.Brain.Risk.Hazards.Active(player.MapId, world.Host.World.NowMs));
            Assert.Same(target, world.Brain.Risk.ChooseTarget(player, 0, _ => false, questObjective: false));
            Assert.True(PlayerbotNavigation.TryPlan(player, new Vector3(target.X, target.Y, target.Z), world.Options, out _));
            PlayerbotNavigation.Guard(player, null);
            return true;
        });
    }

    [Fact]
    public async Task SecondLoopInSameZone_DoublesLifetime_AndSuspendsCurrentQuest()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.DangerMemorySeconds = 0);
        await DieThreeTimesAsync(world);
        uint firstUntil = await world.OnWorldAsync(() =>
            Assert.Single(world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs)).UntilMs);
        await world.OnWorldAsync(() =>
        {
            world.Player.Map!.Combat.ResurrectPlayer(world.Player, 1f, applySickness: false);
            world.Session.ManagedBudget = new ManagedActionBudget(8);
            world.Brain.Update(RiskTestWorld.ThinkMs);
            return true;
        });
        await world.Host.World.AdvanceClockAsync(PlayerbotStallWatch.DeathLoopWindowMs + 1);
        await world.OnWorldAsync(() =>
        {
            world.Session.ManagedBudget = new ManagedActionBudget(8);
            world.Brain.Update(RiskTestWorld.ThinkMs); // an alive interval clears the previous loop report
            return true;
        });
        for (int i = 0; i < 3; i++)
        {
            if (i == 2)
                await world.OnWorldAsync(() =>
                {
                    world.Brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 0, 995103);
                    return true;
                });
            await DieOnceAsync(world, resurrect: i < 2);
        }
        await world.OnWorldAsync(() =>
        {
            uint now = world.Host.World.NowMs;
            PlayerbotHazard zone = Assert.Single(world.Brain.Risk.Hazards.Active(world.Player.MapId, now));
            Assert.True(zone.UntilMs > firstUntil);
            Assert.Equal(5_400_000u - RiskTestWorld.ThinkMs, zone.UntilMs - now); // helper advanced one think after recording
            Assert.True(world.Brain.Suspensions.IsQuestSuspended(995103, now));
            return true;
        });
    }

    private static async Task DieThreeTimesAsync(RiskTestWorld world)
    {
        for (int i = 0; i < 3; i++) await DieOnceAsync(world, resurrect: i < 2);
        await world.OnWorldAsync(() =>
        {
            Assert.StartsWith("death loop:", world.Brain.StallReport, StringComparison.Ordinal);
            return true;
        });
    }

    private static async Task DieOnceAsync(RiskTestWorld world, bool resurrect)
    {
        Creature killer = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(KillerEntry), 8, 0));
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            world.Brain.ObserveKillingBlows(player);
            player.Health = 1;
            player.Map!.Combat.DealDamage(killer, player, 2);
            Assert.False(player.IsAlive);
            world.Session.ManagedBudget = new ManagedActionBudget(8);
            world.Brain.Update(RiskTestWorld.ThinkMs);
            player.Map!.RemoveObject(killer);
            if (resurrect)
            {
                player.Map!.Combat.ResurrectPlayer(player, 1f, applySickness: false);
                world.Session.ManagedBudget = new ManagedActionBudget(8);
                world.Brain.Update(RiskTestWorld.ThinkMs);
            }
            return true;
        });
        await world.Host.World.AdvanceClockAsync(RiskTestWorld.ThinkMs);
    }
}
