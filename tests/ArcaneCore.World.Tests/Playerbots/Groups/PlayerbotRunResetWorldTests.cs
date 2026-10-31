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
/// A completed run: the instance reset request and the fresh instance. Split from the former <c>PlayerbotRunWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunResetWorldTests : PlayerbotRunWorldTestBase
{
    // --- the dungeon -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CompletedRun_LeaderRequestsAndLogsTheReset_AndTheSameBotsEnterAFreshInstance()
    {
        await using GroupTestWorld world = await StartAsync(withLifecycleTrash: true);
        (Guid Id, string Name)[] bots = await AddPartyAsync(world, "Rerun");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup first = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;

        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), await world.TraceAsync());
        uint firstInstance = world.Player(bots[0].Id).Map!.InstanceId;
        Assert.NotEqual(0u, firstInstance);
        Creature firstTrash = await VisibleCreatureAsync(world, bots[0].Id, LifecycleTrash);
        Assert.True(firstTrash.IsAlive);
        Assert.True(await world.RunUntilAsync(360_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.True(first.Run!.Complete, await world.TraceAsync());
        Assert.False(firstTrash.IsAlive);
        Assert.Equal(1, world.Coordinator.RunTotals.Completed);
        int resetEvent = world.Coordinator.Events.FindIndex(e => e.EndsWith("reset instance 36", StringComparison.Ordinal));
        int disbandEvent = world.Coordinator.Events.FindIndex(e => e.Contains("disbanded (completed)", StringComparison.Ordinal));
        // This change: the leader made the request through the handler and noted the outcome before the disband.
        Assert.True(resetEvent >= 0 && disbandEvent > resetEvent, await world.TraceAsync());
        // The fresh instance below is not from that request: the disband itself resets the group's instances (InstanceManager.OnGroupDisbanding).
        Assert.All(bots, b => Assert.Null(world.Instances().GetPlayerBind(world.Player(b.Id).Guid, DungeonEntry.Deadmines)));

        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup second = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.Equal(bots.Select(b => b.Id).Order(), second.Members.Select(m => m.BotId).Order());
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), await world.TraceAsync());
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
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), await world.TraceAsync());

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
        Assert.True(await world.RunUntilAsync(2_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.EndsWith("reset request skipped: a member is not a managed bot", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("reset instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedRun_WithAnOfflineCharacterOnlyInTheServerGroup_LeaderDoesNotSendTheReset()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddPartyAsync(world, "Offline");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), await world.TraceAsync());

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
        Assert.True(await world.RunUntilAsync(2_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.EndsWith("reset request skipped: a member is not a managed bot", StringComparison.Ordinal));
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("reset instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResetRefusedWhileMemberInside_IsLogged_AndRunStillDisbands()
    {
        await using GroupTestWorld world = await StartAsync();
        (Guid Id, string Name)[] bots = await AddPartyAsync(world, "Refuse");
        world.Coordinator.RunRoll = () => 0d;
        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        world.Coordinator.RunRoll = () => 1d;
        Assert.True(await world.RunUntilAsync(180_000, () => bots.All(b => world.Player(b.Id).MapId == DungeonEntry.Deadmines)), await world.TraceAsync());

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
        Assert.True(await world.RunUntilAsync(2_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.Contains(world.Coordinator.Events, e => e.EndsWith("reset instance 36 failed: General", StringComparison.Ordinal));
        Assert.All(bots, b => Assert.Null(world.GroupManager.GetGroup(world.Player(b.Id).Guid)));
    }
}
