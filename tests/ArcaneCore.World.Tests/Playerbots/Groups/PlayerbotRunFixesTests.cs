using System.Numerics;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The run fixes after review: the stuck clock of a run's leader (a rest between two planning failures is not stuck time), the bosses of
/// the wing that are never trash and are credited when they die early, a boss that patrols away from its spawn, and
/// <c>.reload instance_encounter</c>.
/// </summary>
public sealed class PlayerbotRunFixesTests
{
    private const byte Human = 1, Mage = 8;
    private const uint Fireball = 133;

    // --- the stuck clock ---------------------------------------------------------------------------------------------

    [Fact]
    public void FailuresThatKeepComing_AreStuck_OnceThePlanHasFailedForLongerThanTheLimit()
    {
        long since = 0, last = 0;
        long start = 10_000;

        for (long now = start; now <= start + PlayerbotGroupAI.RunStuckMs; now += 1_000)
            Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, now));

        Assert.True(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, start + PlayerbotGroupAI.RunStuckMs + 1_000));
    }

    [Fact]
    public void ALongPauseBetweenTwoFailures_ARestAFightAWait_DoesNotCountAsStuckTime()
    {
        long since = 0, last = 0;
        Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, 10_000));
        Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, 11_000));

        // The leader rested for a minute (no walking attempt at all), then failed to plan once more: a new stretch, not 50 s of being stuck.
        long afterRest = 11_000 + 60_000;
        Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, afterRest));
        Assert.Equal(afterRest, since);

        // The new stretch is judged on its own.
        for (long now = afterRest + 1_000; now <= afterRest + PlayerbotGroupAI.RunStuckMs; now += 1_000)
            Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, now));
        Assert.True(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, afterRest + PlayerbotGroupAI.RunStuckMs + 1_000));
    }

    [Fact]
    public void APlannedWay_EndsTheStretch()
    {
        long since = 0, last = 0;
        Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, 10_000));
        Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, 30_000));
        Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: true, 31_000));
        Assert.Equal((0L, 0L), (since, last));

        // 41 s after the first failure, but the stretch was broken: not stuck.
        Assert.False(PlayerbotGroupAI.NoteStuck(ref since, ref last, planned: false, 51_000));
        Assert.Equal(51_000, since);
    }

    // --- the wing's bosses are never trash -----------------------------------------------------------------------------

    [Fact]
    public void ALaterBossOnTheRoute_IsNotPulledAsTrash_WhileTheTrashAroundItIs()
    {
        Vector3[] corridor = [new(0, 0, 0), new(50, 0, 0), new(100, 0, 0), new(150, 0, 0)];
        const uint first = 100, later = 200, trashEntry = 300;
        PlayerbotRunCreature laterBoss = new(1, later, new Vector3(50, 3, 0), true, true, false);
        PlayerbotRunCreature trash = new(2, trashEntry, new Vector3(100, 3, 0), true, true, false);

        // Only the boss the route leads to is excluded by entry: the later boss, nearer the route, is pulled first (the defect).
        Assert.Equal(1ul, PlayerbotRunRoute.NextPull(corridor, 0, [laterBoss, trash], first)!.Lead.Guid);

        // Every boss of the wing is excluded: the trash is the pull, and a route with only a boss on it has nothing to pull.
        Assert.Equal(2ul, PlayerbotRunRoute.NextPull(corridor, 0, [laterBoss, trash], first, [first, later])!.Lead.Guid);
        Assert.Null(PlayerbotRunRoute.NextPull(corridor, 0, [laterBoss], first, [first, later]));
    }

    [Fact]
    public void ThePlanKnowsItsBossEntries()
    {
        PlayerbotRunPlan plan = Plan((GroupTestWorld.OgreEntry, Vector3.Zero), (GroupTestWorld.WarlordEntry, Vector3.One));

        Assert.Equal([GroupTestWorld.OgreEntry, GroupTestWorld.WarlordEntry], plan.BossEntries);
        Assert.True(plan.IsBoss(GroupTestWorld.WarlordEntry));
        Assert.False(plan.IsBoss(GroupTestWorld.BruteEntry));
    }

    [Fact]
    public async Task ABossKilledEarly_IsCreditedToTheRun_WhateverBossTheRouteHasReached()
    {
        await using GroupTestWorld world = await StartAsync([]);
        (Guid id, _) = await world.AddBotAsync("Earlyleader", Human, Mage, 12, [Fireball]);

        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(id);
            PlayerbotRunPlan plan = Plan((OgreEntry, new Vector3(StartX, StartY, StartZ)), (WarlordEntry, new Vector3(StartX - 70f, StartY, StartZ)));
            var run = new PlayerbotRunMachine(plan, 3);
            run.Apply(PlayerbotRunSignal.Arrived);
            run.Apply(PlayerbotRunSignal.AllInside);
            Assert.Equal(PlayerbotRunStep.Clear, run.Step);
            Assert.Equal(OgreEntry, run.CurrentBoss!.Entry);

            PlayerbotGroupCoordinator.BotGroup group = GroupFor(world, leader, plan, run);
            CreatureMapSystem system = leader.Map!.FindUpdater<CreatureMapSystem>()!;
            Creature warlord = system.Creatures.Single(c => c.Entry == WarlordEntry);
            Creature brute = system.Creatures.Single(c => c.Entry == BruteEntry);

            // Trash is no objective; the later boss, met first, is.
            PlayerbotGroupAI.NoteBossFight(group, run, brute);
            Assert.True(group.Objective.IsEmpty);
            PlayerbotGroupAI.NoteBossFight(group, run, warlord);
            Assert.Equal(WarlordEntry, group.ObjectiveCreature);

            // The objective already noted is not replaced by another boss.
            PlayerbotGroupAI.NoteBossFight(group, run, system.Creatures.Single(c => c.Entry == OgreEntry));
            Assert.Equal(WarlordEntry, group.ObjectiveCreature);

            system.KillCreature(warlord);
            world.Coordinator.AdvanceRun(group, leader, run, 1_000);

            Assert.True(run.IsKilled(WarlordEntry));
            Assert.Equal(1, run.Killed);
            Assert.Equal(OgreEntry, run.CurrentBoss!.Entry); // the route still leads to the ogre
            Assert.Equal(PlayerbotRunStep.Clear, run.Step);
            Assert.True(group.Objective.IsEmpty);

            // A boss that is dead already is not an objective again.
            PlayerbotGroupAI.NoteBossFight(group, run, warlord);
            Assert.True(group.Objective.IsEmpty);
            return true;
        });
    }

    // --- a boss that patrols --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABossThatPatrolsAwayFromItsSpawn_IsNotCountedDone_ADeadOrAbsentOneIs()
    {
        await using GroupTestWorld world = await StartAsync([]);
        // 120 yards west of the start, 190 from the ogre (70 east of it): the ogre is alive, on a loaded grid and out of the leader's sight.
        Vector3 spot = new(StartX - 120f, StartY, StartZ);
        (Guid id, _) = await world.AddBotAsync("Patrolleader", Human, Mage, 12, [Fireball], spot);
        await world.RunAsync(1_500);

        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(id);
            Vector3 at = new(leader.X, leader.Y, leader.Z);
            PlayerbotRunPlan plan = Plan((OgreEntry, at)); // the boss's first spawn is where the leader stands; the ogre itself is elsewhere
            var run = new PlayerbotRunMachine(plan, 3);
            PlayerbotGroupCoordinator.BotGroup group = GroupFor(world, leader, plan, run);
            CreatureMapSystem system = leader.Map!.FindUpdater<CreatureMapSystem>()!;
            Creature ogre = system.Creatures.Single(c => c.Entry == OgreEntry);
            Assert.False(leader.VisibleObjects.Contains(ogre.Guid), "the ogre must be out of the leader's sight for this test");
            Assert.True(ogre.IsAlive);

            // Alive elsewhere: never missing, however long the leader waits at the spawn.
            PlayerbotGroupCoordinator coordinator = world.Coordinator;
            Assert.False(coordinator.ObjectiveMissing(group, leader, 1_000));
            Assert.False(coordinator.ObjectiveMissing(group, leader, 1_000 + PlayerbotGroupCoordinator.ObjectiveMissingMs + 1));
            Assert.False(coordinator.ObjectiveMissing(group, leader, 1_000 + 10 * PlayerbotGroupCoordinator.ObjectiveMissingMs));
            Assert.Same(ogre, PlayerbotGroupCoordinator.FindLivingOnMap(leader, OgreEntry));

            // Dead: missing once the wait is over.
            system.KillCreature(ogre);
            const uint t = 2_000_000;
            Assert.False(coordinator.ObjectiveMissing(group, leader, t));
            Assert.False(coordinator.ObjectiveMissing(group, leader, t + PlayerbotGroupCoordinator.ObjectiveMissingMs));
            Assert.True(coordinator.ObjectiveMissing(group, leader, t + PlayerbotGroupCoordinator.ObjectiveMissingMs + 1));
            Assert.Null(PlayerbotGroupCoordinator.FindLivingOnMap(leader, OgreEntry));

            // Never spawned (a boss of a saved instance has no creature): missing the same way.
            PlayerbotRunPlan absent = Plan((990_799u, at));
            PlayerbotGroupCoordinator.BotGroup absentGroup = GroupFor(world, leader, absent, new PlayerbotRunMachine(absent, 3));
            Assert.False(coordinator.ObjectiveMissing(absentGroup, leader, t));
            Assert.True(coordinator.ObjectiveMissing(absentGroup, leader, t + PlayerbotGroupCoordinator.ObjectiveMissingMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task AQuestGroupsObjective_IsStillJudgedBySight()
    {
        // The map lookup is for a run's boss only: a quest goal whose creature is alive elsewhere keeps its old "none in sight" rule.
        await using GroupTestWorld world = await StartAsync([]);
        Vector3 spot = new(StartX - 120f, StartY, StartZ);
        (Guid id, _) = await world.AddBotAsync("Questleader", Human, Mage, 12, [Fireball], spot);
        await world.RunAsync(1_500);

        await world.OnWorldAsync(() =>
        {
            Player leader = world.Player(id);
            var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Quest, 0, OgreEntry, 0, 0, Vector3.Zero, 0, new Vector3(leader.X, leader.Y, leader.Z), 3, 1, "quest");
            var group = new PlayerbotGroupCoordinator.BotGroup(1, goal, id, leader.Guid, 0);
            Assert.False(world.Coordinator.ObjectiveMissing(group, leader, 1_000));
            Assert.True(world.Coordinator.ObjectiveMissing(group, leader, 1_000 + PlayerbotGroupCoordinator.ObjectiveMissingMs + 1));
            return true;
        });
    }

    // --- .reload instance_encounter -------------------------------------------------------------------------------------

    [Fact]
    public async Task ReloadInstanceEncounter_ReplacesTheEncounters_AndTheCatalogueFollows()
    {
        var store = new MutableEncounters { Rows = [new InstanceEncounter(DungeonEntry.Deadmines, 0, 167, 0, BossEntry, true, "Deadmines Boss")] };
        await using GroupTestWorld world = await StartAsync([], dungeon: true, services: services => services.AddSingleton<IInstanceEncounterStore>(store));
        ReloadCoordinator reloads = world.World.Services.GetRequiredService<ReloadFeature>().Coordinator;
        Assert.Contains("instance_encounter", reloads.Names);

        Assert.Equal(1, await world.OnWorldAsync(() => world.Coordinator.Catalogue().Plans.Count));
        Assert.Single(world.Coordinator.Encounters);

        // The table is emptied: the reload applies, the encounters and the catalogue are empty.
        store.Rows = [];
        ReloadResult emptied = await reloads.ReloadAsync("instance_encounter");
        Assert.Equal(ReloadStatus.Applied, emptied.Status);
        Assert.Empty(world.Coordinator.Encounters);
        Assert.Equal(0, await world.OnWorldAsync(() => world.Coordinator.Catalogue().Plans.Count));

        // The table is filled again: the catalogue is rebuilt from it.
        store.Rows = [new InstanceEncounter(DungeonEntry.Deadmines, 0, 167, 0, BossEntry, true, "Deadmines Boss")];
        ReloadResult filled = await reloads.ReloadAsync("instance_encounter");
        Assert.Equal(ReloadStatus.Applied, filled.Status);
        Assert.Contains("1 instance encounters", filled.Message, StringComparison.Ordinal);
        Assert.Equal(1, await world.OnWorldAsync(() => world.Coordinator.Catalogue().Plans.Count));
    }

    // --- helpers --------------------------------------------------------------------------------------------------------

    private static Task<GroupTestWorld> StartAsync(uint[] quests, bool dungeon = false, Action<IServiceCollection>? services = null)
        => GroupTestWorld.StartAsync(quests, null, dungeon, services);

    private static PlayerbotRunPlan Plan(params (uint Entry, Vector3 Position)[] bosses)
        => new(0, "Test Keep", 0, 1, false, 5, 0, 0, Vector3.Zero, Vector3.Zero, 1,
            [.. bosses.Select((b, i) => new PlayerbotRunBoss(b.Entry, "Boss" + i, (uint)(100 + i), (uint)i, i == bosses.Length - 1, b.Position, 10, 12))], []);

    /// <summary>A run group whose leader is the bot, standing on the plan's map (the continent here), with the bot as its member.</summary>
    private static PlayerbotGroupCoordinator.BotGroup GroupFor(GroupTestWorld world, Player leader, PlayerbotRunPlan plan, PlayerbotRunMachine run)
    {
        PlayerbotRunBoss first = plan.Bosses[0];
        var goal = new PlayerbotGroupGoal(PlayerbotGroupGoalKind.Dungeon, 0, first.Entry, leader.MapId, leader.MapId, Vector3.Zero, 0, first.Position, plan.Size, 1, "run");
        var group = new PlayerbotGroupCoordinator.BotGroup(1, goal, Guid.NewGuid(), leader.Guid, 0) { Run = run };
        group.Members.Add(new PlayerbotGroupCoordinator.Member(Guid.NewGuid(), leader.Guid, leader.Name, PlayerbotGroupRole.Damage, PlayerbotRole.RangeDps, 0, real: false) { Joined = true });
        return group;
    }

    private sealed class MutableEncounters : IInstanceEncounterStore
    {
        public IReadOnlyList<InstanceEncounter> Rows { get; set; } = [];

        public Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Rows);
    }
}
