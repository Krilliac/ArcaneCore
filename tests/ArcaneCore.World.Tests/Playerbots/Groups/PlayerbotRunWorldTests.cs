using System.Diagnostics.Metrics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Dungeon and raid runs on the real world (<see cref="PlayerbotGroupCoordinator"/> with the scenario test world, its manual clock and
/// managed bots): which free bots are matched for which wing (level band, roles, raid quotas, the entrance's requirements and the
/// permanent raid binds), what <see cref="PlayerbotGroupCoordinator.Waives"/> lets through, and what a stopped bot does to a run.
/// <para>
/// The world data is the Deadmines of the group tests (one boss, <see cref="BossEntry"/>, the encounter listed here) plus three raids
/// beside it whose entrances copy the requirement kinds of real classic-db <c>areatrigger_teleport</c> rows: Onyxia's Lair 2848 (level 50,
/// key item 16309), Molten Core window 3528 (level 50, quest 7848) and Naxxramas 4156 (level 51; classic-db's quest 9378 is replaced by
/// a conditions-table reference here, 9001, which no evaluator in the test world satisfies, so the kind is the condition's). The bosses,
/// their levels and encounters are the <see cref="RunFixtures"/> rows. The raid maps admit 10 players here (classic-db: 40) so ten bots
/// fill one.
/// </para>
/// </summary>
public sealed class PlayerbotRunWorldTests
{
    private const byte Human = 1;
    private const byte Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    private const uint OnyxiaTrigger = 9201, MoltenCoreTrigger = 9202, NaxxramasTrigger = 9203;
    private const uint DrakefireAmulet = 16309;
    private const uint NaxxramasCondition = 9001;
    private const uint LifecycleTrash = 990741;

    private static readonly AreaTriggerTeleport DeadminesTeleport = new(DungeonEntry.EntranceTrigger, "Deadmines - Entering", "You must be at least level 10 to enter.", 10,
        DungeonEntry.Deadmines, -16.4f, -383.07f, 61.78f, 1.9f);

    private static readonly AreaTriggerTeleport OnyxiaTeleport = new(OnyxiaTrigger, "Onyxia's Lair - Entering", "", 50, OnyxiasLair, 30.8916f, -54.079f, -5.02784f, 0f,
        RequiredItem: DrakefireAmulet);

    private static readonly AreaTriggerTeleport MoltenCoreTeleport = new(MoltenCoreTrigger, "The Molten Core Window Entrance", "", 50, MoltenCore, 1091.89f, -466.985f, -105.084f, 0f,
        RequiredQuestDone: 7848);

    private static readonly AreaTriggerTeleport NaxxramasTeleport = new(NaxxramasTrigger, "Naxxramas (Entrance)", "", 51, Naxxramas, 3498.28f, -5349.9f, 144.968f, 0f,
        RequiredCondition: NaxxramasCondition);

    // --- the dungeon -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CompletedRun_LeaderRequestsAndLogsTheReset_AndTheSameBotsEnterAFreshInstance()
    {
        await using GroupTestWorld world = await StartAsync(withLifecycleTrash: true);
        (Guid Id, string Name)[] bots = await AddPartyAsync(world, "Rerun");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup first = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;

        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), world.Trace());
        uint firstInstance = world.Player(bots[0].Id).Map!.InstanceId;
        Assert.NotEqual(0u, firstInstance);
        Creature firstTrash = await VisibleCreatureAsync(world, bots[0].Id, LifecycleTrash);
        Assert.True(firstTrash.IsAlive);
        Assert.True(await world.RunUntilAsync(360_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.True(first.Run!.Complete, world.Trace());
        Assert.False(firstTrash.IsAlive);
        Assert.Equal(1, world.Coordinator.RunTotals.Completed);
        int resetEvent = world.Coordinator.Events.FindIndex(e => e.EndsWith("reset instance 36", StringComparison.Ordinal));
        int disbandEvent = world.Coordinator.Events.FindIndex(e => e.Contains("disbanded (completed)", StringComparison.Ordinal));
        // This change: the leader made the request through the handler and noted the outcome before the disband.
        Assert.True(resetEvent >= 0 && disbandEvent > resetEvent, world.Trace());
        // The fresh instance below is not from that request: the disband itself resets the group's instances (InstanceManager.OnGroupDisbanding).
        Assert.All(bots, b => Assert.Null(world.Instances().GetPlayerBind(world.Player(b.Id).Guid, DungeonEntry.Deadmines)));

        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup second = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.Equal(bots.Select(b => b.Id).Order(), second.Members.Select(m => m.BotId).Order());
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), world.Trace());
        uint secondInstance = world.Player(bots[0].Id).Map!.InstanceId;
        Assert.NotEqual(firstInstance, secondInstance);
        Assert.Equal(BossEntry, second.Run!.CurrentBoss!.Entry);
        Assert.True((await VisibleCreatureAsync(world, bots[0].Id, BossEntry)).IsAlive);
        Assert.True((await VisibleCreatureAsync(world, bots[0].Id, LifecycleTrash)).IsAlive);
    }

    [Fact]
    public async Task CompletedRun_WithARealPlayerInTheGroup_LeaderDoesNotSendTheReset()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddPartyAsync(world, "Human");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), world.Trace());

        // ResetSave unbinds every player on the save, so a bot must never reset while a human is in the group.
        await world.OnWorldAsync(() =>
        {
            group.Members.Add(new PlayerbotGroupCoordinator.Member(Guid.NewGuid(), default, "Realone", PlayerbotGroupRole.Damage, PlayerbotRole.MeleeDps, 0, real: true));
            group.Run!.Apply(PlayerbotRunSignal.Arrived);
            group.Run.Apply(PlayerbotRunSignal.AllInside);
            group.Run.Apply(PlayerbotRunSignal.BossDead(BossEntry));
            group.Run.Apply(PlayerbotRunSignal.Left);
            Assert.True(group.Run.Complete);
            group.Succeeded = true;
            group.State = PlayerbotGroupState.Disbanding;
            return true;
        });
        Assert.True(await world.RunUntilAsync(2_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Contains(world.Coordinator.Events, e => e.EndsWith("reset request skipped: a member is not a managed bot", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("reset instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedRun_WithAnOfflineCharacterOnlyInTheServerGroup_LeaderDoesNotSendTheReset()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddPartyAsync(world, "Offline");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), world.Trace());

        // A character the coordinator never tracked (offline, so possibly human) sits in the server group; no coordinator member is Real.
        await world.OnWorldAsync(() =>
        {
            var stranger = new ObjectGuid(0x7FFFFF01UL);
            Assert.True(world.GroupManager.GetGroup(group.LeaderGuid)!.AddMemberSlot(stranger, "Stranger"));
            Assert.DoesNotContain(group.Members, m => m.Real || m.Guid == stranger);
            group.Run!.Apply(PlayerbotRunSignal.Arrived);
            group.Run.Apply(PlayerbotRunSignal.AllInside);
            group.Run.Apply(PlayerbotRunSignal.BossDead(BossEntry));
            group.Run.Apply(PlayerbotRunSignal.Left);
            Assert.True(group.Run.Complete);
            group.Succeeded = true;
            group.State = PlayerbotGroupState.Disbanding;
            return true;
        });
        Assert.True(await world.RunUntilAsync(2_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Contains(world.Coordinator.Events, e => e.EndsWith("reset request skipped: a member is not a managed bot", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("reset instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetRefusedWhileMemberInside_IsLogged_AndRunStillDisbands()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddPartyAsync(world, "Refuse");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), world.Trace());

        // Force the precise refusal interleaving: the run has completed, but a member is still inside when the leader resets.
        await world.OnWorldAsync(() =>
        {
            group.Run!.Apply(PlayerbotRunSignal.Arrived);
            group.Run.Apply(PlayerbotRunSignal.AllInside);
            group.Run.Apply(PlayerbotRunSignal.BossDead(BossEntry));
            group.Run.Apply(PlayerbotRunSignal.Left);
            Assert.True(group.Run.Complete);
            group.Succeeded = true;
            group.State = PlayerbotGroupState.Disbanding;
            return true;
        });
        Assert.True(await world.RunUntilAsync(2_000, () => world.Coordinator.Groups.Count == 0), world.Trace());
        Assert.Contains(world.Coordinator.Events, e => e.EndsWith("reset instance 36 failed: General", StringComparison.Ordinal));
        Assert.All(bots, b => Assert.Null(world.GroupManager.GetGroup(world.Player(b.Id).Guid)));
    }

    [Fact]
    public async Task FiveBotsInTheBand_WithATankAndAHealer_FormADungeonRun_TheOneOutOfTheBandIsLeftOut()
    {
        await using GroupTestWorld world = await StartAsync();
        var tank = await world.AddBotAsync("Dgtank", Human, Warrior, 12, [Taunt], EntranceArea);
        var healer = await world.AddBotAsync("Dgheal", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        var mages = new List<(Guid Id, string Name)>();
        foreach (string name in new[] { "Dgmagea", "Dgmageb", "Dgmagec" }) mages.Add(await world.AddBotAsync(name, Human, Mage, 12, [Fireball], EntranceArea));
        var strong = await world.AddBotAsync("Dgstrong", Human, Mage, 40, [Fireball], EntranceArea); // the wing's band is 10..15

        // The start roll decides: with a roll above the chance no run starts, however well the bots fit.
        world.Coordinator.RunRoll = () => 1d;
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);

        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(PlayerbotGroupGoalKind.Dungeon, group.Goal.Kind);
        Assert.Equal(DungeonEntry.Deadmines, group.Goal.MapId);
        Assert.Equal(DungeonEntry.EntranceTrigger, group.Goal.EntranceTrigger);
        Assert.Equal(5, group.Goal.Size);
        Assert.Equal(PlayerbotRunStep.Travel, group.Run!.Step);
        Assert.Equal(BossEntry, group.Run.CurrentBoss!.Entry);
        Assert.Equal(5, group.Members.Count);
        Assert.Equal(1, group.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(1, group.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        Assert.Equal(3, group.Members.Count(m => m.Role == PlayerbotGroupRole.Damage));
        Assert.Equal(tank.Id, group.LeaderBotId);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Find(healer.Id)!.Role);
        Assert.All(mages, m => Assert.NotNull(group.Find(m.Id)));
        Assert.Null(group.Find(strong.Id));
        Assert.False(world.Coordinator.Drives(strong.Id));
        Assert.Equal((0, 0), world.Coordinator.RunTotals);
        Assert.Equal(1, world.Coordinator.Totals.Formed);
        Assert.Contains(world.Coordinator.Report(), line => line.Contains("run=The Deadmines(36):0 boss=1/1(Deadmines Boss)", StringComparison.Ordinal));
        Assert.Contains(world.Coordinator.Report(), line => line.StartsWith("runs enabled: dungeon 1/2 raid 0/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAHealer_NoRunStarts_UntilOneIsThere()
    {
        await using GroupTestWorld world = await StartAsync();
        await world.AddBotAsync("Nhtank", Human, Warrior, 12, [Taunt], EntranceArea);
        foreach (string name in new[] { "Nhmagea", "Nhmageb", "Nhmagec", "Nhmaged" }) await world.AddBotAsync(name, Human, Mage, 12, [Fireball], EntranceArea);
        world.Coordinator.RunRoll = () => 0d;

        await world.RunAsync(30_000);
        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);

        await world.AddBotAsync("Nhheal", Human, Priest, 12, [LesserHeal, Smite], EntranceArea);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        Assert.Equal(PlayerbotGroupRole.Healer, world.Coordinator.Groups.Single().Members.Single(m => m.Name == "Nhheal").Role);
    }

    [Fact]
    public async Task TheTravelPolicy_IsFightThrough_UnlessTheRunIsToldToTeleport_AndNullOutsideARun()
    {
        await using GroupTestWorld world = await StartAsync();
        var members = await AddPartyAsync(world, "Tp");
        var outsider = await world.AddBotAsync("Tpout", Human, Mage, 40, [Fireball], EntranceArea);
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());

        Assert.All(members, m => Assert.Equal(PlayerbotGroupTravelPolicy.FightThrough, world.Coordinator.TravelPolicyFor(m.Id)));
        Assert.Null(world.Coordinator.TravelPolicyFor(outsider.Id));
        Assert.Null(world.Coordinator.TravelPolicyFor(Guid.NewGuid()));

        world.Options.Groups.DungeonRuns.TeleportToEntrance = true;
        Assert.All(members, m => Assert.Equal(PlayerbotGroupTravelPolicy.Teleport, world.Coordinator.TravelPolicyFor(m.Id)));
        Assert.Null(world.Coordinator.TravelPolicyFor(outsider.Id));
    }

    [Fact]
    public async Task AStoppedBot_LeavesItsRun_AStoppedLeaderGivesTheRunUp_AndTheOthersSetTheWingAside()
    {
        await using GroupTestWorld world = await StartAsync();
        var members = await AddPartyAsync(world, "Fg");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        PlayerbotRunMachine run = group.Run!;
        (Guid Id, string Name) follower = members.First(m => m.Id != group.LeaderBotId);
        (Guid Id, string Name) leftBehind = members.First(m => m.Id != group.LeaderBotId && m.Id != follower.Id);

        await world.OnWorldAsync(() =>
        {
            // A follower that stops (ManagedPlayerbotFeature.StopAsync calls Forget) is out of the group and the group goes on.
            world.Coordinator.Forget(follower.Id);
            Assert.Equal(4, group.Members.Count);
            Assert.Null(group.Find(follower.Id));
            Assert.False(world.Coordinator.Drives(follower.Id));
            Assert.Single(world.Coordinator.Groups);
            Assert.False(run.Finished);
            Assert.Equal((0, 0), world.Coordinator.RunTotals);

            // The leader stops: the run is given up, the group ends, the members still in it are released and set the wing aside.
            world.Coordinator.Forget(group.LeaderBotId);
            Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
            Assert.Equal("leader-stopped", run.Reason);
            Assert.Empty(world.Coordinator.Groups);
            Assert.Equal((0, 1), world.Coordinator.RunTotals);
            Assert.Equal(1, world.Coordinator.Totals.Failed);
            Assert.All(members, m => Assert.False(world.Coordinator.Drives(m.Id)));
            Assert.True(world.Coordinator.IsRunSetAside(leftBehind.Id, DungeonEntry.Deadmines, 0));
            Assert.False(world.Coordinator.IsRunSetAside(follower.Id, DungeonEntry.Deadmines, 0)); // it left before the run ended
            return true;
        });

        // Nobody is matched again: three bots are set aside, two are free and a party takes five.
        await world.RunAsync(30_000);
        Assert.Empty(world.Coordinator.Groups);
    }

    [Fact]
    public async Task ADungeonRunsBots_AreNeverWaivedAnything_WhateverSkipPrerequisitesSays()
    {
        await using GroupTestWorld world = await StartAsync(options => options.Groups.DungeonRuns.Raids.SkipPrerequisites = true);
        var members = await AddPartyAsync(world, "Dw");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());

        await world.OnWorldAsync(() =>
        {
            Player bot = world.Player(members[0].Id);
            Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(bot, DeadminesTeleport));
            Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(bot, OnyxiaTeleport)); // not a raid run
            return true;
        });
    }

    // --- the raids ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARaid_NeedsEveryBotToMeetTheEntrancesRequirements_PerKind_AndFormsOnceTheyDo()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Pr", warriors: 1, priests: 3, mages: 6);
        world.Coordinator.RunRoll = () => 0d;

        // No bot has the amulet, the attunement quest or the condition: each raid is left out for the first requirement that refuses.
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);
        AssertNoted(world, "run Onyxia's Lair: 10 bots in the level band lack the item requirement");
        AssertNoted(world, "run Molten Core: 10 bots in the level band lack the quest requirement");
        AssertNoted(world, "run Naxxramas: 10 bots in the level band lack the condition requirement");

        // Nine of ten with the amulet is not enough: the whole raid must meet the requirement.
        await GiveAmuletAsync(world, raid.Take(9));
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);
        AssertNoted(world, "run Onyxia's Lair: 1 bots in the level band lack the item requirement");

        await GiveAmuletAsync(world, raid.Skip(9));
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(group.Run!.Plan.Raid);
        Assert.Equal(OnyxiasLair, group.Run.Plan.MapId);
        Assert.Equal(PlayerbotGroupGoalKind.Raid, group.Goal.Kind);
        Assert.Equal(10, group.Goal.Size);
        Assert.Equal(10, group.Members.Count);
        Assert.Equal(raid.Select(r => r.Id).Order(), group.Members.Select(m => m.BotId).Order());
    }

    [Fact]
    public async Task ARaid_TakesItsRoleQuotas_OneTankThreeHealers_FromElevenBots()
    {
        await using GroupTestWorld world = await StartAsync(options => options.MaxBots = 12);
        var raid = await AddRaidAsync(world, "Rq", warriors: 1, priests: 3, mages: 7);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;

        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(10, group.Members.Count);
        Assert.Equal(1, group.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(3, group.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        Assert.Equal(6, group.Members.Count(m => m.Role == PlayerbotGroupRole.Damage));
        Assert.Equal(2, group.Members.Select(m => m.SubGroup).Distinct().Count());
        Assert.Equal(raid[0].Id, group.LeaderBotId); // the warrior tanks and leads
        Assert.Equal(PlayerbotGroupRole.Tank, group.Find(raid[0].Id)!.Role);
        Assert.Single(raid, r => group.Find(r.Id) is null); // the seventh mage stays free
    }

    [Fact]
    public async Task ARaid_WithoutItsHealers_IsNotFormed_EvenWhenEveryBotMeetsTheRequirements()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Rh", warriors: 1, priests: 2, mages: 7);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;

        await world.RunAsync(30_000);

        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
    }

    [Fact]
    public async Task ABotWithAPermanentBindToTheRaid_IsLeftOut_ATemporaryBindIsNot()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Pb", warriors: 1, priests: 3, mages: 6);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;
        (Guid Id, string Name) saved = raid[^1];

        await Bind(world, saved.Id, instance: 5000, permanent: true);
        await world.RunAsync(20_000);
        Assert.Empty(world.Coordinator.Groups);
        AssertNoted(world, "run Onyxia's Lair: 1 bots are locked to the raid");

        // Unbound and bound temporarily (a raid never saves a temporary bind that way; it is the case of a group bind): not locked.
        Assert.True(await world.OnWorldAsync(() => world.Instances().UnbindPlayer(world.Player(saved.Id).Guid, OnyxiasLair)));
        await Bind(world, saved.Id, instance: 5001, permanent: false);
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        Assert.NotNull(world.Coordinator.Groups.Single().Find(saved.Id));
    }

    [Fact]
    public async Task WithSkipPrerequisites_TheRaidIsMatchedWithoutThem_AndWaivesOnlyForItsOwnBotsAtItsOwnEntrance()
    {
        await using GroupTestWorld world = await StartAsync(options =>
        {
            options.MaxBots = 12;
            options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
        });
        var raid = await AddRaidAsync(world, "Sk", warriors: 1, priests: 3, mages: 7);
        world.Coordinator.RunRoll = () => 0d;

        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(OnyxiasLair, group.Run!.Plan.MapId); // the lowest map of the three raids all ten bots fit
        Assert.Equal(10, group.Members.Count);
        (Guid Id, string Name) member = raid.First(r => group.Find(r.Id) is not null);
        (Guid Id, string Name) free = raid.First(r => group.Find(r.Id) is null);
        await using WorldTestClient real = await world.World.EnterWorldAsync("RUNREAL", "Runreal", AccountSecurity.Player);

        const AreaTriggerWaivers All = AreaTriggerWaivers.Item | AreaTriggerWaivers.Quest | AreaTriggerWaivers.Condition;
        await world.OnWorldAsync(() =>
        {
            Player bot = world.Player(member.Id);
            PlayerbotGroupCoordinator coordinator = world.Coordinator;

            // A member of the raid at the raid's entrance: the key item, the quest and the condition, never the level.
            Assert.Equal(All, coordinator.Waives(bot, OnyxiaTeleport));
            Assert.True(AreaTriggerRequirements.Check(bot, OnyxiaTeleport with { RequiredLevel = 61 }, null, [], coordinator.Waives(bot, OnyxiaTeleport)).Failed == AreaTriggerRequirement.Level);

            // Another teleport (another raid, a dungeon): checked as always.
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, MoltenCoreTeleport));
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, NaxxramasTeleport));
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, DeadminesTeleport));

            // A bot that is not in the run, and a real player (a session that is not a managed bot's).
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(world.Player(free.Id), OnyxiaTeleport));
            Player human = world.World.Host.World.FindOnlinePlayer("Runreal")!;
            Assert.False(human.Session is WorldSession { IsManaged: true });
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(human, OnyxiaTeleport));

            // The options are live: each switch that turns the feature off ends the waiver at once.
            world.Options.Groups.DungeonRuns.Raids.SkipPrerequisites = false;
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
            Assert.Equal(All, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Raids.Enabled = false;
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Raids.Enabled = true;
            world.Options.Groups.DungeonRuns.Enabled = false;
            Assert.Equal(AreaTriggerWaivers.None, coordinator.Waives(bot, OnyxiaTeleport));
            world.Options.Groups.DungeonRuns.Enabled = true;
            Assert.Equal(All, coordinator.Waives(bot, OnyxiaTeleport));
            return true;
        });
    }

    [Fact]
    public async Task WithoutSkipPrerequisites_ABotOfARaidRunIsWaivedNothing()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Ns", warriors: 1, priests: 3, mages: 6);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());

        await world.OnWorldAsync(() =>
        {
            Assert.All(raid, r => Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(world.Player(r.Id), OnyxiaTeleport)));
            return true;
        });
    }

    [Fact]
    public async Task TheWaiver_RefusesNullArguments()
    {
        await using GroupTestWorld world = await StartAsync();
        var bot = await world.AddBotAsync("Nullbot", Human, Mage, 12, [Fireball], EntranceArea);

        await world.OnWorldAsync(() =>
        {
            Assert.Throws<ArgumentNullException>(() => world.Coordinator.Waives(null!, OnyxiaTeleport));
            Assert.Throws<ArgumentNullException>(() => world.Coordinator.Waives(world.Player(bot.Id), null!));
            return true;
        });
    }

    [Fact]
    public async Task TheEntranceLevel_IsNeverSkipped_ABotBelowItInABandOverrideStaysUnmatched()
    {
        // The band is widened under the entrance's level 50 on purpose: only the entrance check can refuse the level-45 bots now.
        await using GroupTestWorld world = await StartAsync(options =>
        {
            options.MaxBots = 12;
            options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
            options.Groups.DungeonRuns.LevelBandOverrides[OnyxiasLair.ToString()] = "40-63";
        });
        var bots = new List<(Guid Id, string Name)>
        {
            await world.AddBotAsync("Lvwara", Human, Warrior, 45, [Taunt], EntranceArea),
        };
        foreach (char letter in "abc") bots.Add(await world.AddBotAsync("Lvpri" + letter, Human, Priest, 45, [LesserHeal, Smite], EntranceArea));
        foreach (char letter in "abcdef") bots.Add(await world.AddBotAsync("Lvmag" + letter, Human, Mage, 45, [Fireball], EntranceArea));
        world.Coordinator.RunRoll = () => 0d;

        await world.RunAsync(30_000);

        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        AssertNoted(world, "run Onyxia's Lair: 10 bots in the level band lack the level requirement");
    }

    [Fact]
    public async Task WhenTheRunsLeaderStops_EveryMemberLosesTheRaidWaiver_AndTheRunIsCountedOnce()
    {
        await using GroupTestWorld world = await StartAsync(options =>
        {
            options.MaxBots = 12;
            options.Groups.DungeonRuns.Raids.SkipPrerequisites = true;
        });
        var raid = await AddRaidAsync(world, "Sd", warriors: 1, priests: 3, mages: 7);
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.True(await world.RunUntilAsync(30_000, () => group.Members.All(m => m.Joined)), world.Trace()); // the waiver is for joined members
        (Guid Id, string Name)[] members = [.. raid.Where(r => group.Find(r.Id) is not null)];
        Assert.Equal(10, members.Length);
        PlayerbotRunMachine run = group.Run!;

        await world.OnWorldAsync(() =>
        {
            Assert.All(members, m => Assert.NotEqual(AreaTriggerWaivers.None, world.Coordinator.Waives(world.Player(m.Id), OnyxiaTeleport)));

            // The feature shuts down: every bot is forgotten, the leader first (ManagedPlayerbotFeature.StopAsync calls Forget per bot).
            world.Coordinator.Forget(group.LeaderBotId);
            foreach ((Guid id, string _) in members) world.Coordinator.Forget(id);
            world.Coordinator.Forget(Guid.NewGuid()); // a bot the coordinator never knew: nothing happens

            Assert.Equal(PlayerbotRunStep.GiveUp, run.Step);
            Assert.Equal("leader-stopped", run.Reason);
            Assert.Empty(world.Coordinator.Groups);
            Assert.All(members, m =>
            {
                Assert.False(world.Coordinator.Drives(m.Id));
                Assert.Null(world.Coordinator.GroupOf(m.Id));
                Assert.Equal(AreaTriggerWaivers.None, world.Coordinator.Waives(world.Player(m.Id), OnyxiaTeleport));
            });
            Assert.Equal((0, 1), world.Coordinator.RunTotals);
            Assert.Equal(1, world.Coordinator.Totals.Failed);
            return true;
        });
    }

    // --- bot QA and metrics ------------------------------------------------------------------------------------------

    private static (List<string> Outcomes, MeterListener Listener) ListenToRuns()
    {
        var outcomes = new List<string>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "ArcaneCore.Playerbots.Runs" && instrument.Name == "arcanecore.playerbot.runs") l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Key == "outcome") lock (outcomes) outcomes.Add((string)tag.Value!);
        });
        listener.Start();
        return (outcomes, listener);
    }

    private static Task<GroupTestWorld> StartWithQaAsync(PlayerbotQaSignalBus bus, Action<PlayerbotOptions>? configure = null)
        => GroupTestWorld.StartAsync([], configure, dungeon: true, services =>
        {
            Register(services);
            services.AddSingleton<IPlayerbotQaSink>(new PlayerbotQaSink(bus));
        }, Bosses());

    [Fact]
    public async Task AGivenUpRun_ReportsOneRunFailedSignalToBotQa_ByContentIds_WithNoBotOrGroupIdentity_AndCountsItAsGivenUp()
    {
        var bus = new PlayerbotQaSignalBus(64);
        (List<string> outcomes, MeterListener listener) = ListenToRuns();
        using MeterListener _ = listener;
        await using GroupTestWorld world = await StartWithQaAsync(bus);
        (Guid Id, string Name)[] members = await AddPartyAsync(world, "Qaz");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;

        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Forget(group.LeaderBotId);
            return true;
        });

        var signals = new List<QaSignal>();
        bus.Drain(signals, 16);
        QaSignal signal = Assert.Single(signals);
        Assert.Equal(QaSignalKind.RunFailed, signal.Kind);
        Assert.Equal("leader-stopped", signal.Reason);
        Assert.Equal(DungeonEntry.Deadmines, signal.Map);
        Assert.False(string.IsNullOrEmpty(signal.Stage));
        Assert.Null(signal.BotKey);
        Assert.Equal(0, signal.BotClass);
        string everything = signal.Message + "|" + signal.Detail + "|" + signal.Stage + "|" + signal.Reason + "|" + PlayerbotQaFingerprint.Key(signal);
        Assert.All(members, m => Assert.DoesNotContain(m.Name, everything, StringComparison.Ordinal));
        Assert.Equal($"leader-stopped in {signal.Detail} at {signal.Stage}", PlayerbotQaAggregator.Subject(signal, null));
        Assert.Equal(1, world.Coordinator.RunTotals.GivenUp);
        lock (outcomes) Assert.Contains("given_up", outcomes);
    }

    [Fact]
    public async Task ARunThatTimesOut_ReportsARunFailedSignalWithTheTimeoutReason_AndCountsItAsATimeout()
    {
        var bus = new PlayerbotQaSignalBus(64);
        (List<string> outcomes, MeterListener listener) = ListenToRuns();
        using MeterListener _ = listener;
        await using GroupTestWorld world = await StartWithQaAsync(bus);
        await AddPartyAsync(world, "Qat");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        // A run lasts under a minute here and MaxRunMinutes cannot go below 10 (validated), so the step timeout the group AI raises is injected the way its own
        // faults are (RunFault, applied by Advance): the same Finish, disband and report path as a real gather or travel timeout.
        await world.OnWorldAsync(() =>
        {
            group.RunFault = "travel-timeout";
            return true;
        });

        Assert.True(await world.RunUntilAsync(240_000, () => world.Coordinator.Groups.Count == 0), world.Trace());

        var signals = new List<QaSignal>();
        bus.Drain(signals, 16);
        Assert.True(signals.Count == 1, world.Trace());
        QaSignal signal = Assert.Single(signals);
        Assert.Equal(QaSignalKind.RunFailed, signal.Kind);
        Assert.Equal("travel-timeout", signal.Reason);
        Assert.Equal(DungeonEntry.Deadmines, signal.Map);
        Assert.Equal(1, world.Coordinator.RunTotals.GivenUp);
        lock (outcomes) Assert.Contains("timeout", outcomes);
    }

    [Fact]
    public async Task AGivenUpRun_WithBotQaOff_ChangesNothing()
    {
        await using GroupTestWorld world = await StartAsync();
        await AddPartyAsync(world, "Qao");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), world.Trace());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;

        await world.OnWorldAsync(() =>
        {
            world.Coordinator.Forget(group.LeaderBotId);
            return true;
        });

        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal((0, 1), world.Coordinator.RunTotals);
    }

    // --- the world ---------------------------------------------------------------------------------------------------

    private static void AssertNoted(GroupTestWorld world, string text)
        => Assert.True(world.Coordinator.Events.Any(e => e.EndsWith(text, StringComparison.Ordinal)), $"not noted: {text}{Environment.NewLine}{world.Trace()}");

    /// <summary>
    /// Quest groups never take the last group slot: with MaxGroups 2 and four bots waiting on an elite quest for two (two pairs, one goal),
    /// the second pair waits while a due dungeon run takes the slot the first pair left. Match() runs before StartRuns() in every scan, so
    /// before the reserve a quest group refilled every slot the moment one freed and no run ever started (w29: four quest groups held the
    /// slots 97.6% of the time, zero runs formed).
    /// </summary>
    [Fact]
    public async Task WithTheSlotsFullOfQuestGroups_ADueDungeonRun_StillStarts()
    {
        await using GroupTestWorld world = await StartAsync(o => { o.MaxBots = 12; o.Groups.MaxGroups = 2; }, quests: [EntranceEliteQuest]);
        world.Coordinator.RunRoll = () => 1d; // no run until the quest bots hold the slots
        var pairs = new List<(Guid Id, string Name)>();
        foreach (string name in new[] { "Slota", "Slotb", "Slotc", "Slotd" }) pairs.Add(await world.AddBotAsync(name, Human, Mage, 13, [Fireball], EntranceArea));
        Assert.True(await world.RunUntilAsync(60_000, () => pairs.All(b => world.HasQuest(b.Id, EntranceEliteQuest))), world.Trace());
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Count >= 1), world.Trace());
        await world.RunAsync(10_000); // the second pair would have formed by now without the reserve

        var party = await AddPartyAsync(world, "Slotrun");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)),
            "no dungeon run started while quest groups held the slots" + Environment.NewLine + world.Trace());
        // No quest group ended to free the slot: the run took the one that was kept for it.
        Assert.Equal((0, 0), (world.Coordinator.Totals.Completed, world.Coordinator.Totals.Failed));
        Assert.True(world.Coordinator.Groups.Count(g => g.Run is null) <= 1, world.Trace());
        Assert.True(world.Coordinator.Groups.Count <= 2, world.Trace());
        Assert.Equal(1, world.Coordinator.QuestGroupLimit);
        Assert.All(party, m => Assert.True(world.Coordinator.GroupOf(m.Id)?.Run is not null, world.Trace()));
    }

    private static Task<GroupTestWorld> StartAsync(Action<PlayerbotOptions>? configure = null, bool withLifecycleTrash = false, uint[]? quests = null)
        => GroupTestWorld.StartAsync(quests ?? [], configure, dungeon: true, Register,
            withLifecycleTrash ? [.. Bosses(), LifecycleTrashSpawn()] : Bosses());

    /// <summary>On the world thread: the visible set and the map are mutated by the tick, so enumerating them from the test thread races it.</summary>
    private static Task<Creature> VisibleCreatureAsync(GroupTestWorld world, Guid botId, uint entry) => world.OnWorldAsync(() =>
    {
        Player player = world.Player(botId);
        return player.VisibleObjects.Select(g => player.Map!.FindObject(g)).OfType<Creature>().Single(c => c.Entry == entry);
    });

    // Scenario-only pack member on the straight entrance-to-boss route; 600 s respawn exceeds both runs.
    private static (CreatureTemplate Template, CreatureSpawn Spawn) LifecycleTrashSpawn() =>
    (
        new CreatureTemplate
        {
            Entry = LifecycleTrash, Name = "Lifecycle Trash", Faction = 14, CreatureType = 7, MinLevel = 12, MaxLevel = 12,
            DisplayIds = [903], MinLevelHealth = 80, MaxLevelHealth = 80, MinMeleeDamage = 2, MaxMeleeDamage = 3,
            MeleeBaseAttackTime = 2000,
        },
        new CreatureSpawn
        {
            Guid = LifecycleTrash, Entry = LifecycleTrash, MapId = DungeonEntry.Deadmines,
            X = -16.4f, Y = -365f, Z = global::ArcaneCore.World.Tests.Playerbots.Scenarios.DeadminesTestContent.InsideFloor,
            SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600,
        }
    );

    private static (CreatureTemplate Template, CreatureSpawn Spawn)[] Bosses() =>
    [
        Creature(OnyxiasLair, 10184, "Onyxia", 63, -4.8689f, -217.171f, -86.7104f, 2490001),
        Creature(MoltenCore, 11982, "Magmadar", 63, 1144.63f, -1020.48f, -185.663f, 4090221),
        Creature(Naxxramas, 15956, "Anub'Rekhan", 63, 3316.47f, -3476.23f, 287.26f, 5330151),
    ];

    private static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new RaidMaps());
        services.AddSingleton<IInstanceEncounterStore>(new Encounters());
        if (services.LastOrDefault(d => d.ServiceType == typeof(IItemTemplateSource))?.ImplementationInstance is ArcaneCore.World.Tests.Items.InMemoryItemTemplateSource items)
            items.Templates.Add(new ItemTemplate { Entry = DrakefireAmulet, Name = "Drakefire Amulet", Class = 12, Quality = 1, Stackable = 1, MaxCount = 1 });
    }

    private static async Task<(Guid Id, string Name)[]> AddPartyAsync(GroupTestWorld world, string prefix)
    {
        var members = new List<(Guid Id, string Name)>
        {
            await world.AddBotAsync(prefix + "tank", Human, Warrior, 12, [Taunt], EntranceArea),
            await world.AddBotAsync(prefix + "heal", Human, Priest, 12, [LesserHeal, Smite], EntranceArea),
        };
        foreach (char letter in "abc") members.Add(await world.AddBotAsync(prefix + "mage" + letter, Human, Mage, 12, [Fireball], EntranceArea));
        return [.. members];
    }

    /// <summary>Level 60 bots at the entrance area: the warriors first, then the priests, then the mages (names carry a letter, never a digit).</summary>
    private static async Task<(Guid Id, string Name)[]> AddRaidAsync(GroupTestWorld world, string prefix, int warriors, int priests, int mages)
    {
        var bots = new List<(Guid Id, string Name)>();
        for (int i = 0; i < warriors; i++) bots.Add(await world.AddBotAsync(prefix + "war" + (char)('a' + i), Human, Warrior, 60, [Taunt], EntranceArea));
        for (int i = 0; i < priests; i++) bots.Add(await world.AddBotAsync(prefix + "pri" + (char)('a' + i), Human, Priest, 60, [LesserHeal, Smite], EntranceArea));
        for (int i = 0; i < mages; i++) bots.Add(await world.AddBotAsync(prefix + "mag" + (char)('a' + i), Human, Mage, 60, [Fireball], EntranceArea));
        return [.. bots];
    }

    private static Task GiveAmuletAsync(GroupTestWorld world, IEnumerable<(Guid Id, string Name)> bots)
    {
        (Guid Id, string Name)[] list = [.. bots];
        return world.OnWorldAsync(() =>
        {
            foreach ((Guid id, string _) in list)
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, world.Player(id).Inventory.AddItem(DrakefireAmulet, 1, out _));
            return true;
        });
    }

    /// <summary>Load a saved instance of Onyxia's Lair and the bot's bind to it (the way the instance manager reads its stored binds at startup).</summary>
    private static Task Bind(GroupTestWorld world, Guid botId, uint instance, bool permanent)
        => world.OnWorldAsync(() =>
        {
            int character = (int)world.Player(botId).Guid.Low;
            world.Instances().Load(new InstanceStoreSnapshot([new InstanceRecord(instance, OnyxiasLair, 0)],
                [new CharacterInstanceBindRecord(character, instance, permanent)], [], []));
            Assert.Equal(permanent, world.Instances().GetPlayerBind(world.Player(botId).Guid, OnyxiasLair)?.Permanent);
            return true;
        });

    /// <summary>The Deadmines' encounter and the raids' (the classic-db rows of <see cref="RunFixtures.Encounters"/>).</summary>
    private sealed class Encounters : IInstanceEncounterStore
    {
        public Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstanceEncounter>>(
            [
                new InstanceEncounter(DungeonEntry.Deadmines, 0, 167, 0, BossEntry, true, "Deadmines Boss"),
                .. RunFixtures.Encounters().Where(e => e.MapId is OnyxiasLair or MoltenCore or Naxxramas),
            ]);
    }

    private sealed class RaidMaps : IMapDataStore
    {
        private static readonly MapTemplate[] Continents =
        [
            new(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ];

        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                .. Continents,
                new MapTemplate(DungeonEntry.Deadmines, 0, MapType.Instance, 0, 10, 0, 0, -11208.4f, 1672.3f, "The Deadmines", ""),
                new MapTemplate(OnyxiasLair, 0, MapType.Raid, 0, 10, 5, 1, -4753.31f, -3752.42f, "Onyxia's Lair", ""),
                new MapTemplate(MoltenCore, 0, MapType.Raid, 0, 10, 7, 0, -7510.56f, -1036.7f, "Molten Core", ""),
                new MapTemplate(Naxxramas, 0, MapType.Raid, 0, 10, 7, 0, 0, 0, "Naxxramas", ""),
            ],
            [],
            [
                new AreaTriggerTemplate(DungeonEntry.EntranceTrigger, 0, -11208.6f, 1679.6f, 24.6f, 0f, 5f, 10f, 8f, 1.5f, "Deadmines Entrance"),
                new AreaTriggerTemplate(DungeonEntry.ExitTrigger, DungeonEntry.Deadmines, -14.6f, -390.5f, 62.4f, 5f, 0, 0, 0, 0, "Deadmines Exit"),
                new AreaTriggerTemplate(OnyxiaTrigger, 0, -11078f, 1679.6f, 24.6f, 5f, 0, 0, 0, 0, "Onyxia's Lair entrance"),
                new AreaTriggerTemplate(MoltenCoreTrigger, 0, -11078f, 1779.6f, 24.6f, 5f, 0, 0, 0, 0, "Molten Core window"),
                new AreaTriggerTemplate(NaxxramasTrigger, 0, -11078f, 1879.6f, 24.6f, 5f, 0, 0, 0, 0, "Naxxramas entrance"),
            ],
            [
                DeadminesTeleport,
                new AreaTriggerTeleport(DungeonEntry.ExitTrigger, "Deadmines - Exiting", "", 0, 0, -11208.7f, 1675.9f, 24.5733f, 4.71239f),
                OnyxiaTeleport,
                MoltenCoreTeleport,
                NaxxramasTeleport,
            ],
            []));
    }
}

/// <summary>The scenario world's Deadmines ids, and the instance manager of the test world.</summary>
internal static class DungeonEntry
{
    public const uint Deadmines = ArcaneCore.World.Playerbots.Scenarios.DungeonEntryScenario.Deadmines;
    public const uint EntranceTrigger = ArcaneCore.World.Playerbots.Scenarios.DungeonEntryScenario.EntranceTrigger;
    public const uint ExitTrigger = ArcaneCore.World.Playerbots.Scenarios.DungeonEntryScenario.ExitTrigger;

    public static InstanceManager Instances(this GroupTestWorld world) => world.World.Services.GetRequiredService<InstanceFeature>().Instances;
}
