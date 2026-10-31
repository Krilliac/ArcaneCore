using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>A five-Horde-bot RFC run on the manual world clock, from formation through trash and a retry.</summary>
public abstract class PlayerbotRunClearWorldTestBase
{
    private protected const uint KalimdorMap = 1;
    private protected const byte Orc = 2, Undead = 5;
    private protected const byte Warrior = 1, Priest = 5, Mage = 8;
    private protected const uint Taunt = GroupTestWorld.Taunt, Resurrection = GroupTestWorld.Resurrection;
    private protected const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    /// <summary>
    /// Puts <paramref name="id"/> on the RFC exit point with its trigger tracker already inside the volume (so standing still fires nothing),
    /// at full health, and has every live trash creature hit it once for <paramref name="damage"/> so it is engaged.
    /// </summary>
    private protected static Task StandEngagedOnTheStuckExit(GroupTestWorld world, Guid id, uint damage = 1)
        => world.OnWorldAsync(() =>
        {
            StandEngagedOnTheStuckExitNow(world, id, damage);
            return true;
        });

    /// <summary><see cref="StandEngagedOnTheStuckExit"/> on the world thread already.</summary>
    private protected static void StandEngagedOnTheStuckExitNow(GroupTestWorld world, Guid id, uint damage)
    {
        Player standing = world.Player(id);
        standing.Relocate(2.58019f, -0.013587f, -13.3668f, 0f, world.World.Host.World.NowMs);
        PlayerbotTriggerTracker tracker = PlayerbotAreaTriggers.TrackerOf(standing);
        var centre = new Vector3(standing.X, standing.Y, standing.Z);
        tracker.Observe(standing.Map!, standing.MapId, centre, tracker.Near(world.World.Host.World, standing.Map!, centre), fire: false);
        Assert.Contains(GroupRfcContent.Exit, tracker.Inside);
        standing.Health = standing.MaxHealth;
        foreach (Creature c in Pack(world, id).Where(c => c.IsAlive)) standing.Map!.Combat.DealDamage(c, standing, damage, direct: false);
    }

    /// <summary>
    /// The steps (changes only) of <paramref name="id"/> while it is alive, inside and in combat on the retry regroup, for <paramref name="ms"/>:
    /// from its first exit walk on; and whether it died meanwhile.
    /// </summary>
    private protected static async Task<(List<string> Steps, bool Died)> RegroupSteps(GroupTestWorld world, PlayerbotGroupCoordinator.BotGroup group,
        PlayerbotRunMachine run, Guid id, uint ms)
    {
        var steps = new List<string>();
        bool died = false;
        await world.RunUntilAsync(ms, () =>
        {
            died |= !world.Player(id).IsAlive;
            if (world.Player(id) is { IsAlive: true, MapId: GroupRfcContent.Map } p && p.Combat.IsInCombat
                && group.State == PlayerbotGroupState.Gathering && run.Step == PlayerbotRunStep.Retry)
            {
                string step = world.Coordinator.FindAI(id)!.Step;
                if ((steps.Count > 0 || step.StartsWith("retry:", StringComparison.Ordinal)) && (steps.Count == 0 || steps[^1] != step)) steps.Add(step);
            }

            return false;
        });
        return (steps, died);
    }

    /// <summary>Starts a no-resurrection RFC run, wipes three members on the first pack and waits for the retry regroup with the leader and
    /// the first member revived at the landing inside.</summary>
    private protected static async Task<((Guid Id, string Name)[] Bots, PlayerbotGroupCoordinator.BotGroup Group, PlayerbotRunMachine Run)> WipeIntoTheRetryRegroup(GroupTestWorld world)
    {
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: false);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            Creature attacker = Pack(world, bots[0].Id).First(c => c.IsAlive);
            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(attacker, victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);

        Assert.True(await world.RunUntilAsync(180_000, () => group.State == PlayerbotGroupState.Gathering && run.Step == PlayerbotRunStep.Retry
            && bots.Take(2).All(b => world.Player(b.Id) is { IsAlive: true, MapId: GroupRfcContent.Map })), await world.TraceAsync());
        return (bots, group, run);
    }

    private protected static async Task<GroupTestWorld> StartAsync()
        => await GroupTestWorld.StartAsync([], options =>
        {
            options.Groups.DungeonRuns.Enabled = true;
            options.Groups.DungeonRuns.StartChancePerMinute = 0;
        }, rfc: true);

    private protected static async Task<(Guid Id, string Name)[]> AddParty(GroupTestWorld world, bool resurrection)
    {
        var bots = new List<(Guid Id, string Name)>
        {
            await world.AddBotAsync("Rfctank", Orc, Warrior, 16, [Taunt], GroupTestWorld.RfcEntranceArea, mapId: 1),
            await world.AddBotAsync("Rfcheal", Undead, Priest, 16, resurrection ? [LesserHeal, Smite, Resurrection] : [LesserHeal, Smite], GroupTestWorld.RfcEntranceArea, mapId: 1),
        };
        foreach (char suffix in "abc") bots.Add(await world.AddBotAsync("Rfcmage" + suffix, Undead, Mage, 16, [Fireball], GroupTestWorld.RfcEntranceArea, mapId: 1));
        Assert.True(await world.RunUntilAsync(30_000, () => bots.All(b => world.Player(b.Id).MapId == 1)), await world.TraceAsync());
        await world.RunAsync(1_000); // coordinator receives the managed sessions before StartRun
        return [.. bots];
    }

    private protected static async Task<PlayerbotGroupCoordinator.BotGroup> StartRun(GroupTestWorld world, (Guid Id, string Name)[] bots)
    {
        PlayerbotGroupCoordinator.BotGroup? group = await world.OnWorldAsync(() =>
        {
            PlayerbotRunPlan plan = world.Coordinator.Catalogue().Plans.Single(p => p.MapId == GroupRfcContent.Map);
            return world.Coordinator.StartRun(plan, [.. bots.Select(b => b.Id)]);
        });
        Assert.True(group is not null, await world.TraceAsync());
        Assert.Equal(5, group!.Members.Count);
        Assert.Equal(GroupRfcContent.Entrance, group.Goal.EntranceTrigger);
        Assert.Equal(4, group.Run!.BossCount);
        return group;
    }

    private protected static Creature[] Pack(GroupTestWorld world, Guid member)
        => world.Player(member).MapId == GroupRfcContent.Map && world.Player(member).Map?.FindUpdater<CreatureMapSystem>() is { } creatures
            ? [.. creatures.Creatures.Where(c => c.Entry is GroupRfcContent.Trogg or GroupRfcContent.Shaman)] : [];

    private protected static Creature[] Bosses(GroupTestWorld world, Guid member)
        => world.Player(member).MapId == GroupRfcContent.Map && world.Player(member).Map?.FindUpdater<CreatureMapSystem>() is { } creatures
            ? [.. creatures.Creatures.Where(c => GroupRfcContent.BossEntries.Contains(c.Entry))] : [];
}
