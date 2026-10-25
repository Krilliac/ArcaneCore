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
public sealed class PlayerbotRunClearWorldTests
{
    private const uint KalimdorMap = 1;
    private const byte Orc = 2, Undead = 5;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint Taunt = GroupTestWorld.Taunt, Resurrection = GroupTestWorld.Resurrection;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    [Fact]
    public async Task FiveHordeBots_EnterOneBoundInstance_ClearTheFirstPackBeforeAnyBoss()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: true);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;

        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Creature[] initialPack = Pack(world, bots[0].Id);
        Assert.Equal(3, initialPack.Length);
        Assert.Equal(2, initialPack.Count(c => c.Entry == GroupRfcContent.Trogg));
        Assert.Single(initialPack, c => c.Entry == GroupRfcContent.Shaman);
        Assert.All(initialPack, c =>
        {
            Assert.InRange(c.Level, (byte)13, (byte)15);
            Assert.InRange(Vector3.Distance(new Vector3(c.X, c.Y, c.Z), new Vector3(0.797643f, -8.23429f, -15.5288f)), 15f, 25f);
        });
        Assert.All(initialPack, a => Assert.All(initialPack, b => Assert.True(Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z)) <= 10f)));
        await world.OnWorldAsync(() =>
        {
            uint instance = world.Player(bots[0].Id).Map!.InstanceId;
            Assert.NotEqual(0u, instance);
            Assert.All(bots, b => Assert.Equal(instance, world.Player(b.Id).Map!.InstanceId));
            var server = world.GroupManager.GetGroup(world.Player(bots[0].Id).Guid)!;
            Assert.Equal(instance, world.World.Services.GetRequiredService<InstanceFeature>().Instances.GetGroupBind(server, GroupRfcContent.Map)?.Save.InstanceId);
            Assert.Equal(5, server.MemberCount);
            return true;
        });

        bool sawPack = false, bossEarly = false;
        Assert.True(await world.RunUntilAsync(180_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            sawPack |= run.PullsLeft > 0;
            bossEarly |= trash.Any(c => c.IsAlive) && Bosses(world, bots[0].Id).Any(c => c.Combat.IsInCombat);
            return sawPack && trash.Length == 3 && trash.All(c => !c.IsAlive) && run.PullsLeft == 0;
        }), await world.TraceAsync());
        Assert.False(bossEarly, await world.TraceAsync());
        Assert.Equal(0, run.Killed);
        Assert.Equal(11517u, run.CurrentBoss!.Entry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MidPullWipe_RecoversAndReenters_WithoutRespawningKilledTrash(bool resurrection)
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());

        // Wait for one real trash kill with the rest of its pack still alive, then force a majority wipe.
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        ulong[] killed = await world.OnWorldAsync(() =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            ulong[] dead = [.. trash.Where(c => !c.IsAlive).Select(c => c.Guid.Value)];
            Creature attacker = trash.First(c => c.IsAlive);
            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(attacker, victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }
            return dead;
        });
        Assert.NotEmpty(killed);
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);
        bool sawGhostEntrance = false, sawGhostOutside = false, sawRevivedInside = false;
        Assert.True(await world.RunUntilAsync(180_000, () =>
        {
            sawGhostEntrance |= bots.Take(3).Any(b => world.Coordinator.FindAI(b.Id)?.Recovery.LastStep == PlayerbotRecoveryStep.WalkToEntrance);
            sawGhostOutside |= bots.Take(3).Any(b => world.Player(b.Id).MapId == 1 && (world.Player(b.Id).Flags & PlayerFlags.Ghost) != 0);
            sawRevivedInside |= bots.Take(3).Any(b => world.Player(b.Id).MapId == GroupRfcContent.Map && world.Player(b.Id).IsAlive
                && MathF.Abs(world.Player(b.Id).X - 0.797643f) < 8f && MathF.Abs(world.Player(b.Id).Y + 8.23429f) < 8f);
            return run.Step == PlayerbotRunStep.Retry;
        }), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(180_000, () => run.Step == PlayerbotRunStep.Enter), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(180_000, () => run.Step == PlayerbotRunStep.Clear
            && bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map && world.Player(b.Id).IsAlive)), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);
        Assert.Equal(11517u, run.CurrentBoss!.Entry);
        Assert.All(killed, guid => Assert.Contains(Pack(world, bots[0].Id), c => c.Guid.Value == guid && !c.IsAlive));
        if (!resurrection)
        {
            Assert.True(sawGhostEntrance, await world.TraceAsync());
            Assert.True(sawGhostOutside, await world.TraceAsync());
            Assert.True(sawRevivedInside, await world.TraceAsync());
            Assert.All(bots, b => Assert.Null(world.Coordinator.FindAI(b.Id)!.LastResurrection));
        }
    }

    /// <summary>
    /// After a wipe the released bots are revived at the instance landing at about half health while the surviving trash is still a few yards
    /// away, and the group then regroups for up to GatherCatchUpMs. Holding inside for that time left the revived bots to be pulled and fought
    /// at half health with no wipe check running; on the Retry step the leader must walk back out through the exit instead (the members follow),
    /// so the group re-enters at full strength with the single wipe it already had.
    /// </summary>
    [Fact]
    public async Task AWipedRunWhoseLeaderRevivesInside_RegroupsOutsideTheExit_BeforeTheRetry()
    {
        await using GroupTestWorld world = await StartAsync();
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

        // The group regroups on the Retry step with the leader revived back inside: the moment the run's own state says so, the surviving trash
        // hits one of the revived members, as it does when they come back within its reach. Each creature then attacks that member (vmangos
        // Creature::OnEnterCombat -> AttackedBy) and keeps at it, as dungeon trash does not leash: the members in combat walk out through the
        // exit with the leader instead of fighting it there.
        Assert.True(await world.RunUntilAsync(180_000, () => group.State == PlayerbotGroupState.Gathering && run.Step == PlayerbotRunStep.Retry
            && bots.Take(2).All(b => world.Player(b.Id) is { IsAlive: true, MapId: GroupRfcContent.Map })), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            Player member = world.Player(bots[1].Id);
            foreach (Creature c in Pack(world, bots[0].Id).Where(c => c.IsAlive)) member.Map!.Combat.DealDamage(c, member, 1, direct: false);
            return true;
        });

        // Well inside GatherCatchUpMs (60 s): everyone is outside the instance, alive and out of combat, and the run has not re-entered yet.
        // While the leader is still inside the group is not gathered (even once the members alive have come to it), so it never travels or re-enters from there.
        bool leftEarly = false, stepOk = true;
        Assert.True(await world.RunUntilAsync(20_000, () =>
        {
            if (world.Player(bots[0].Id).MapId == GroupRfcContent.Map) leftEarly |= group.State != PlayerbotGroupState.Gathering;
            // The last member out completes the gathering, so the run may already have stepped on to Enter in that same tick; either way it has not cleared.
            stepOk &= run.Step is PlayerbotRunStep.Retry or PlayerbotRunStep.Enter;
            return bots.All(b => world.Player(b.Id) is { IsAlive: true, MapId: KalimdorMap } p && !p.Combat.IsInCombat);
        }), await world.TraceAsync());
        Assert.False(leftEarly, "the group left Gathering while its leader was still inside the instance");
        Assert.True(stepOk, "the run moved past Retry/Enter before the group was outside");
        Assert.True(await world.RunUntilAsync(180_000, () => run.Step == PlayerbotRunStep.Clear
            && bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map && world.Player(b.Id).IsAlive)), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);
    }

    /// <summary>
    /// Dungeon trash never leashes (CreatureMapSystem.IsOutOfThreatArea is false in an instanceable map), so a wiped group's leash-based retreat
    /// can run in a line that misses the exit trigger and be chased for good. The survivors here start south of the exit box, where the way
    /// straight away from the pack does not cross it: they must walk out through the exit instead.
    /// </summary>
    [Fact]
    public async Task AWipeInsideTheInstance_SurvivorsLeaveThroughTheExit_EvenWhenTheWayAwayMissesIt()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: true);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        (Guid Id, string Name)[] survivors = [.. bots.Skip(3)];
        await world.OnWorldAsync(() =>
        {
            Creature[] live = [.. Pack(world, bots[0].Id).Where(c => c.IsAlive)];
            foreach ((Guid id, string _) in survivors)
            {
                Player survivor = world.Player(id);
                survivor.Relocate(-2f, -16f, -15.5288f, 0f, world.World.Host.World.NowMs);
                foreach (Creature c in live) survivor.Map!.Combat.DealDamage(c, survivor, 1, direct: false);
                Assert.True(survivor.IsAlive);
            }

            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(live[0], victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());

        // The exit is a few yards away and the trash hits for 2-3 every 2 s: both survivors are out, alive and out of combat, long before they
        // could die (a survivor chased east by the leash retreat is still inside, in combat, at this point).
        Assert.True(await world.RunUntilPacedAsync(() => survivors.All(b => world.Player(b.Id) is { IsAlive: true, MapId: 1 } p && !p.Combat.IsInCombat), 30_000), await world.TraceAsync());
        Assert.True(await world.RunUntilPacedAsync(() => run.Step == PlayerbotRunStep.Retry, 180_000, TimeSpan.FromSeconds(240)), await world.TraceAsync());
    }

    /// <summary>
    /// A survivor that stands on the exit point while its trigger does not fire (the bot never reports a move onto it) is within WalkTo's arrival
    /// distance, which used to reset the give-up count on every think and hold it there for good: it must give up on the exit and retreat.
    /// </summary>
    [Fact]
    public async Task AWipeSurvivorStandingOnAnExitThatNeverFires_GivesUpOnTheExitAndRetreats()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddParty(world, resurrection: true);
        PlayerbotGroupCoordinator.BotGroup group = await StartRun(world, bots);
        PlayerbotRunMachine run = group.Run!;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map)), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            Creature[] trash = Pack(world, bots[0].Id);
            return trash.Length == 3 && trash.Count(c => !c.IsAlive) >= 1 && trash.Any(c => c.IsAlive);
        }), await world.TraceAsync());
        (Guid Id, string Name) survivor = bots[4];
        await world.OnWorldAsync(() =>
        {
            Creature[] live = [.. Pack(world, bots[0].Id).Where(c => c.IsAlive)];
            Player standing = world.Player(survivor.Id);
            standing.Relocate(2.58019f, -0.013587f, -13.3668f, 0f, world.World.Host.World.NowMs);
            // The bot's tracker has already seen it enter the exit volume (without sending the trigger), so standing still fires nothing.
            PlayerbotTriggerTracker tracker = PlayerbotAreaTriggers.TrackerOf(standing);
            var centre = new Vector3(standing.X, standing.Y, standing.Z);
            tracker.Observe(standing.Map!, standing.MapId, centre, tracker.Near(world.World.Host.World, standing.Map!, centre), fire: false);
            Assert.Contains(GroupRfcContent.Exit, tracker.Inside);
            foreach (Creature c in live) standing.Map!.Combat.DealDamage(c, standing, 1, direct: false);
            Assert.True(standing.IsAlive);
            foreach ((Guid id, string _) in bots.Take(3))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(live[0], victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());

        var steps = new List<string>();
        Assert.True(await world.RunUntilAsync(30_000, () =>
        {
            string step = world.Coordinator.FindAI(survivor.Id)!.Step;
            if (steps.Count == 0 || steps[^1] != step) steps.Add(step);
            return step == "retreat";
        }), string.Join(" > ", steps) + await world.TraceAsync());
    }

    private static async Task<GroupTestWorld> StartAsync()
        => await GroupTestWorld.StartAsync([], options =>
        {
            options.Groups.DungeonRuns.Enabled = true;
            options.Groups.DungeonRuns.StartChancePerMinute = 0;
        }, rfc: true);

    private static async Task<(Guid Id, string Name)[]> AddParty(GroupTestWorld world, bool resurrection)
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

    private static async Task<PlayerbotGroupCoordinator.BotGroup> StartRun(GroupTestWorld world, (Guid Id, string Name)[] bots)
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

    private static Creature[] Pack(GroupTestWorld world, Guid member)
        => world.Player(member).MapId == GroupRfcContent.Map && world.Player(member).Map?.FindUpdater<CreatureMapSystem>() is { } creatures
            ? [.. creatures.Creatures.Where(c => c.Entry is GroupRfcContent.Trogg or GroupRfcContent.Shaman)] : [];

    private static Creature[] Bosses(GroupTestWorld world, Guid member)
        => world.Player(member).MapId == GroupRfcContent.Map && world.Player(member).Map?.FindUpdater<CreatureMapSystem>() is { } creatures
            ? [.. creatures.Creatures.Where(c => GroupRfcContent.BossEntries.Contains(c.Entry))] : [];
}
