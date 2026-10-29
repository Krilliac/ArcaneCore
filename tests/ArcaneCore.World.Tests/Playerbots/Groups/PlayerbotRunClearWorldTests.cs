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
        ((Guid Id, string Name)[] bots, PlayerbotGroupCoordinator.BotGroup group, PlayerbotRunMachine run) = await WipeIntoTheRetryRegroup(world);

        // The group regroups on the Retry step with the leader revived back inside: the moment the run's own state says so, the surviving trash
        // hits one of the revived members, as it does when they come back within its reach. Each creature then attacks that member (vmangos
        // Creature::OnEnterCombat -> AttackedBy) and keeps at it, as dungeon trash does not leash: the members in combat walk out through the
        // exit with the leader instead of fighting it there.
        await world.OnWorldAsync(() =>
        {
            Player member = world.Player(bots[1].Id);
            foreach (Creature c in Pack(world, bots[0].Id).Where(c => c.IsAlive)) member.Map!.Combat.DealDamage(c, member, 1, direct: false);
            return true;
        });

        // Well inside GatherCatchUpMs (60 s): everyone is outside the instance, alive and out of combat, and the run has not re-entered yet.
        // While the leader is still inside the group is not gathered (even once the members alive have come to it), so it never travels or re-enters from there.
        bool leftEarly = false, stepOk = true;
        var steps = new HashSet<string>();
        Assert.True(await world.RunUntilAsync(20_000, () =>
        {
            foreach ((Guid id, string _) in bots) steps.Add(world.Coordinator.FindAI(id)!.Step);
            if (world.Player(bots[0].Id).MapId == GroupRfcContent.Map) leftEarly |= group.State != PlayerbotGroupState.Gathering;
            // The last member out completes the gathering, so the run may already have stepped on to Enter in that same tick; either way it has not cleared.
            stepOk &= run.Step is PlayerbotRunStep.Retry or PlayerbotRunStep.Enter;
            return bots.All(b => world.Player(b.Id) is { IsAlive: true, MapId: KalimdorMap } p && !p.Combat.IsInCombat);
        }), await world.TraceAsync());
        Assert.False(leftEarly, "the group left Gathering while its leader was still inside the instance");
        Assert.True(stepOk, "the run moved past Retry/Enter before the group was outside");
        Assert.Contains("retry:run-exit", steps); // the engaged member walked out through the retry regroup's exit walk, not by winning the fight
        Assert.True(await world.RunUntilAsync(180_000, () => run.Step == PlayerbotRunStep.Clear
            && bots.All(b => world.Player(b.Id).MapId == GroupRfcContent.Map && world.Player(b.Id).IsAlive)), await world.TraceAsync());
        Assert.Equal(1, run.Wipes);
    }

    /// <summary>
    /// A member in the retry regroup that the trash engages while it stands on an exit whose trigger does not fire fails its exit walk on every
    /// think (its step stays retry:run-exit, as the walk plans while it stands on the point; the stand counts as the failure): past WipeExitGiveUp
    /// it goes on with the regroup's own think (it fights back, or with no target follows its leader) for the rest of the regroup, instead of
    /// turning back to the exit walk every other think (the give-up count is not reset while the regroup lasts; a death would reset it, so the
    /// member starts at full health and must not die here).
    /// </summary>
    [Fact]
    public async Task ARetryRegroupMemberWhoseExitNeverFires_GivesUpOnTheExitWalk_WithoutTurningBackToIt()
    {
        await using GroupTestWorld world = await StartAsync();
        ((Guid Id, string Name)[] bots, PlayerbotGroupCoordinator.BotGroup group, PlayerbotRunMachine run) = await WipeIntoTheRetryRegroup(world);
        (Guid Id, string Name) member = bots[1];
        await StandEngagedOnTheStuckExit(world, member.Id);
        (List<string> steps, bool died) = await RegroupSteps(world, group, run, member.Id, 10_000);
        string seen = string.Join(" > ", steps);
        Assert.False(died, "the member died in the window: " + seen);
        int firstOther = steps.FindIndex(s => !s.StartsWith("retry:", StringComparison.Ordinal));
        Assert.True(steps.Count > 0 && steps[0].StartsWith("retry:", StringComparison.Ordinal), seen + await world.TraceAsync());
        Assert.True(firstOther > 0, "the member never gave up on the exit: " + seen + await world.TraceAsync());
        Assert.DoesNotContain(steps.Skip(firstOther), s => s.StartsWith("retry:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A death restarts the exit give-up: a wipe survivor that gave up on the exit (it stood on an exit that did not fire), dies and is revived
    /// while the group is still wiped (another survivor keeps the wipe going) stands somewhere new, so when the trash engages it again it walks to
    /// the exit again (wipe:run-exit) instead of keeping the give-up from before its death.
    /// </summary>
    [Fact]
    public async Task AWipeSurvivorThatGaveUpOnTheExit_DiesAndIsRevivedStillWiped_TriesTheExitAgain()
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
        (Guid Id, string Name) member = bots[4], holder = bots[3];
        PlayerbotGroupAI ai = world.Coordinator.FindAI(member.Id)!;
        // Both survivors stand engaged on the stuck exit: the holder gives up too and stays in combat, so the wipe does not settle meanwhile.
        // The member takes the harder hits, so the trash goes for it (it must stay engaged until it gives up).
        await StandEngagedOnTheStuckExit(world, holder.Id);
        await StandEngagedOnTheStuckExit(world, member.Id, damage: 5);
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
        Assert.True(await world.RunUntilAsync(10_000, () => group.State == PlayerbotGroupState.Wiped && ai.WipeExitFails >= PlayerbotGroupAI.WipeExitGiveUp),
            ai.Step + await world.TraceAsync());
        // Its next think is past the exit walk, so a wipe:run-exit step from here on is a fresh one.
        Assert.True(await world.RunUntilAsync(5_000, () => !ai.Step.StartsWith("wipe:", StringComparison.Ordinal)), ai.Step + await world.TraceAsync());

        await world.OnWorldAsync(() =>
        {
            Player dying = world.Player(member.Id);
            dying.Map!.Combat.DealDamage(Pack(world, member.Id).First(c => c.IsAlive), dying, dying.Health, direct: false);
            Assert.False(dying.IsAlive);
            return true;
        });
        // One tick for the bot to see its death (the group AI's update runs every tick), too short for the think that releases its spirit.
        await world.RunAsync(1);
        string notWiped = await world.OnWorldAsync(() =>
        {
            if (group.State != PlayerbotGroupState.Wiped) return $"the wipe settled while the member was dead: {group.State}";
            Player revived = world.Player(member.Id);
            if (revived.IsAlive || revived.MapId != GroupRfcContent.Map) return $"the member is not dead inside: alive={revived.IsAlive}, map {revived.MapId}, step {ai.Step}";
            revived.Map!.Combat.ResurrectPlayer(revived, 1f, applySickness: false);
            Assert.True(revived.IsAlive);
            // Revived on the exit point whose trigger still does not fire, and engaged again (in the same tick, before the bot moves).
            StandEngagedOnTheStuckExitNow(world, member.Id, damage: 5);
            return "";
        });
        Assert.True(notWiped.Length == 0, notWiped + await world.TraceAsync());

        var steps = new List<string>();
        await world.RunUntilAsync(5_000, () =>
        {
            Player p = world.Player(member.Id);
            string step = $"{ai.Step} ({group.State}, map {p.MapId}, alive={p.IsAlive}, combat={p.Combat.IsInCombat}, fails={ai.WipeExitFails})";
            if (steps.Count == 0 || steps[^1] != step) steps.Add(step);
            return false;
        });
        Assert.True(steps.Any(s => s.StartsWith("wipe:run-exit ", StringComparison.Ordinal)), string.Join(" > ", steps) + await world.TraceAsync());
    }

    /// <summary>
    /// A give-up holds for the phase it was reached in: a survivor that gave up on the exit while the group was wiped (it stood on an exit that
    /// did not fire) and lived through the wipe walks to the exit again (retry:run-exit) when the trash engages it once the retry regroup is on,
    /// instead of keeping the wipe's give-up.
    /// </summary>
    [Fact]
    public async Task AWipeSurvivorThatGaveUpOnTheExit_TriesItAgainInTheRetryRegroup()
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
        (Guid Id, string Name) survivor = bots[4];
        PlayerbotGroupAI ai = world.Coordinator.FindAI(survivor.Id)!;
        await StandEngagedOnTheStuckExit(world, survivor.Id);
        await world.OnWorldAsync(() =>
        {
            Creature attacker = Pack(world, bots[0].Id).First(c => c.IsAlive);
            foreach ((Guid id, string _) in bots.Take(4))
            {
                Player victim = world.Player(id);
                victim.Map!.Combat.DealDamage(attacker, victim, victim.Health, direct: false);
                Assert.False(victim.IsAlive);
            }

            return true;
        });
        Assert.True(await world.RunUntilAsync(5_000, () => run.Step == PlayerbotRunStep.Wiped), await world.TraceAsync());
        Assert.True(await world.RunUntilAsync(10_000, () => group.State == PlayerbotGroupState.Wiped && ai.WipeExitFails >= PlayerbotGroupAI.WipeExitGiveUp),
            ai.Step + await world.TraceAsync());

        // The wipe ends: the survivor finishes the pack (nothing is left to hold it in combat), and it waits at the landing for the others.
        await world.OnWorldAsync(() =>
        {
            Player alive = world.Player(survivor.Id);
            MapCombat combat = alive.Map!.Combat;
            foreach (Creature c in Pack(world, survivor.Id).Where(c => c.IsAlive)) combat.DealDamage(alive, c, c.Health, direct: false);
            Assert.All(Pack(world, survivor.Id), c => Assert.False(c.IsAlive));
            combat.CombatStop(alive);
            alive.Relocate(0.797643f, -8.23429f, -15.5288f, 0f, world.World.Host.World.NowMs);
            alive.Health = alive.MaxHealth;
            return true;
        });
        var path = new List<string>();
        bool died = false;
        Assert.True(await world.RunUntilAsync(180_000, () =>
        {
            died |= !world.Player(survivor.Id).IsAlive;
            string at = $"{group.State}/{ai.Step}/map{world.Player(survivor.Id).MapId}";
            if (path.Count == 0 || path[^1] != at) path.Add(at);
            return group.State == PlayerbotGroupState.Gathering && run.Step == PlayerbotRunStep.Retry;
        }), string.Join(" > ", path) + await world.TraceAsync());
        Assert.False(died, "the survivor died before the retry regroup (a death resets the give-up too): " + string.Join(" > ", path));
        // A creature of the instance that is still up engages it inside (the pack is dead, so the nearest one left, a boss in this test
        // content, hits it from wherever it is).
        string left = await world.OnWorldAsync(() =>
        {
            Player alive = world.Player(survivor.Id);
            if (!alive.IsAlive || alive.MapId != GroupRfcContent.Map) return "the survivor is not alive inside: " + string.Join(" > ", path);
            Creature? other = alive.Map!.FindUpdater<CreatureMapSystem>()?.Creatures.Where(c => c.IsAlive)
                .OrderBy(c => Vector3.Distance(new Vector3(c.X, c.Y, c.Z), new Vector3(alive.X, alive.Y, alive.Z))).FirstOrDefault();
            if (other is null) return "no live creature left in the instance";
            alive.Map!.Combat.DealDamage(other, alive, 1, direct: false);
            return alive.Combat.IsInCombat ? "" : $"the hit from {other.Entry} did not put the survivor in combat";
        });
        Assert.True(left.Length == 0, left);

        var steps = new List<string>();
        await world.RunUntilAsync(5_000, () =>
        {
            Player p = world.Player(survivor.Id);
            string step = $"{ai.Step} ({group.State}/{run.Step}, map {p.MapId}, alive={p.IsAlive}, combat={p.Combat.IsInCombat}, fails={ai.WipeExitFails})";
            if (steps.Count == 0 || steps[^1] != step) steps.Add(step);
            return false;
        });
        Assert.True(steps.Any(s => s.StartsWith("retry:run-exit ", StringComparison.Ordinal)), string.Join(" > ", steps) + await world.TraceAsync());
    }

    /// <summary>
    /// Puts <paramref name="id"/> on the RFC exit point with its trigger tracker already inside the volume (so standing still fires nothing),
    /// at full health, and has every live trash creature hit it once for <paramref name="damage"/> so it is engaged.
    /// </summary>
    private static Task StandEngagedOnTheStuckExit(GroupTestWorld world, Guid id, uint damage = 1)
        => world.OnWorldAsync(() =>
        {
            StandEngagedOnTheStuckExitNow(world, id, damage);
            return true;
        });

    /// <summary><see cref="StandEngagedOnTheStuckExit"/> on the world thread already.</summary>
    private static void StandEngagedOnTheStuckExitNow(GroupTestWorld world, Guid id, uint damage)
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
    private static async Task<(List<string> Steps, bool Died)> RegroupSteps(GroupTestWorld world, PlayerbotGroupCoordinator.BotGroup group,
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
    private static async Task<((Guid Id, string Name)[] Bots, PlayerbotGroupCoordinator.BotGroup Group, PlayerbotRunMachine Run)> WipeIntoTheRetryRegroup(GroupTestWorld world)
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
        // Retry is a transient step (it lasts through the regroup's gathering, about 1.3 s of game time here, then Enter and Clear): when the survivors take a few seconds to get out, the run has
        // already passed through it by the time this wait starts, so wait for the run to leave Wiped (Retry is the only way out of it) and require it
        // to have done so on the single wipe rather than by giving up.
        Assert.True(await world.RunUntilPacedAsync(() => run.Step != PlayerbotRunStep.Wiped, 180_000, TimeSpan.FromSeconds(240)), await world.TraceAsync());
        Assert.True(await world.OnWorldAsync(() => run is { Wipes: 1, Step: not PlayerbotRunStep.GiveUp }), await world.TraceAsync());
    }

    /// <summary>
    /// A survivor that stands on the exit point while its trigger does not fire (the bot never reports a move onto it) is within WalkTo's arrival
    /// distance, which used to reset the give-up count on every think and hold it there for good: it must give up on the exit and retreat.
    /// </summary>
    [Trait("Category", "Timing")]
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
