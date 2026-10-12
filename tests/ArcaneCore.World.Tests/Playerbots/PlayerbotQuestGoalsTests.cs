using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Game.Items;
using ArcaneCore.World.Features;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.GameObjects;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotQuestGoalsTests
{
    [Fact]
    public async Task QuestGiverWithinServerRadiusAdjustedReach_IsQueriedWithoutAnotherPath()
    {
        // vmangos Object::IsWithinDist and QuestNpcServices use 5 yards plus both bounding radii. At the
        // last mesh point beside Je'neu Sancrea (#218), the centres are 5.6 yards apart, within that range.
        await using QuestHost quest = await QuestHost.StartAsync();
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var giver = (ArcaneCore.Game.Creatures.Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            NpcInfo npc = quest.Feature.Services.Deps.Creatures!.Find(player, giver.Guid)!;
            float reach = QuestNpcServices.InteractionDistance + npc.BoundingRadius + player.BoundingRadius;
            Assert.True(reach > QuestNpcServices.InteractionDistance);
            float distance = (reach + QuestNpcServices.InteractionDistance) / 2f;
            giver.SetPosition(player.X + distance, player.Y, player.Z, 0);
            int queries = 0;
            quest.Session.ManagedDispatchObserver = opcode =>
            {
                if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++;
            };
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            goals.Update(player, 500);
            Assert.Equal(1, queries);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            goals.Update(player, 500);
            Assert.Equal(QuestStatus.Incomplete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            return true;
        });
    }

    [Fact]
    public async Task CrossContinentEnder_DoesNotBecomeAVisibleStarterGoal()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { EnderOnOtherMap = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            Assert.True(quest.Feature.Services.CanTakeQuest(player, QuestInteractionFixture.QuestId));
            Assert.False(new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true }).HasCandidate(player));
            return true;
        });
    }

    [Fact]
    public async Task UnhomedBot_RejectsVisibleStarterWithDistantSameMapEnder()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { DistantEnder = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            Assert.True(quest.Feature.Services.CanTakeQuest(player, QuestInteractionFixture.QuestId));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            for (int think = 0; think < 10; think++) Assert.False(goals.HasCandidate(player));
            Assert.Equal(1, goals.TripEvaluations);
            return true;
        });
    }

    [Fact]
    public async Task UnhomedBot_AbandonsCompletedDistantSameMapDeliveryAndFreesSlot()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { DistantEnder = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            var state = quest.Feature.Services.StateOf(player)!;
            int slot = state.Quests.FindSlot(QuestInteractionFixture.QuestId);
            Assert.InRange(slot, 0, QuestConstants.MaxQuestLogSize - 1);
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Assert.Equal(QuestStatus.Complete, state.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.None, state.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.Equal(QuestConstants.MaxQuestLogSize, state.Quests.FindSlot(QuestInteractionFixture.QuestId));
            Assert.False(goals.HasCandidate(player));
            Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
            Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));
            return true;
        });
    }

    [Fact]
    public async Task CompletedKillQuestFarFromItsLocalGiverAndEnder_IsKeptForReturnAndHandedIn()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            float giverX = player.X;
            player.SetPosition(giverX + 3_000f, player.Y, player.Z, 0);
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.Equal(QuestInteractionFixture.QuestId, goals.CompletedReturnQuest(player));
            WorldCollision.Of(quest.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            var brain = new PlayerbotBrain(quest.Session,
                new PlayerbotOptions { Enabled = true, MaxPathPoints = 4096, MaxRouteYards = 10_000 });
            try
            {
                float before = player.X;
                quest.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                PlayerbotMotion.ElapseForTests(player, 500);
                quest.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.True(player.X < before, "the completed quest must begin the return walk");
                Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.QuestId);
            }
            finally
            {
                brain.Stop();
                Assert.True(PlayerbotMovementControl.Stop(quest.Session, player));
            }
            // End the long synthetic trip at the giver/ender, then use the ordinary reward packets.
            player.SetPosition(giverX, player.Y, player.Z, 0);
            for (int i = 0; i < 8 && quest.Feature.PendingSettlementCount == 0
                && quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded == false; i++)
            {
                quest.Session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(player, 500);
            }
            Assert.True(quest.Feature.PendingSettlementCount > 0
                || quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
                "hand-in stage=" + goals.StageName);
            return true;
        });
        await quest.Feature.WaitForSettlementAsync((int)quest.Session.Player!.Guid.Low);
        Assert.True(await quest.Host.World.InvokeAsync(() =>
            quest.Feature.Services.StateOf(quest.Session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded));
    }

    [Fact]
    public async Task UnhomedBot_DoesNotRetryDistantAbandonWhileQuestIsSetAside()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { DistantEnder = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            suspensions.SuspendQuest(QuestInteractionFixture.QuestId, quest.Host.World.NowMs);
            goals.Suspensions = suspensions;
            Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.InRange(quest.Feature.Services.StateOf(player)!.Quests.FindSlot(QuestInteractionFixture.QuestId),
                0, QuestConstants.MaxQuestLogSize - 1);
            return true;
        });
    }

    [Fact]
    public async Task UnhomedBot_ServerRefusedAbandon_SendsOnePacket()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { DistantEnder = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            OpcodeTable opcodes = quest.Host.Opcodes;
            var refused = new OpcodeTable();
            refused.OnWorld(WorldOpcode.CmsgQuestlogRemoveQuest, (_, _, _) => { });
            OpcodeTable replacement = opcodes.TrySwap([WorldOpcode.CmsgQuestlogRemoveQuest], refused, out string? error)!;
            Assert.Null(error);
            opcodes.Replace(replacement);
            int attempts = 0;
            quest.Session.ManagedDispatchObserver = opcode =>
            {
                if (opcode == WorldOpcode.CmsgQuestlogRemoveQuest) attempts++;
            };
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            for (int i = 0; i < 3; i++)
            {
                quest.Session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.AbandonDistantCompletedQuest(player));
            }
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));
            Assert.Equal(1, attempts);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs),
                "a server-refused abandon must not set the durable refusal");
            return true;
        });
    }

    [Fact]
    public async Task HomedBot_PositionBasedAbandon_DoesNotSetTheDurableRefusal()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { DistantEnder = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            // A home far from the ender, and the bot beyond 600 yards of it: the abandon rests on the bot's position.
            goals.Home = new PlayerbotHomeArea(0, new System.Numerics.Vector3(-6948.95f + 8_000f, -132.49f, 83.53f), 100, null);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.None, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs),
                "a homed bot's position-based abandon must not be durable");
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task HomedBot_KeepsCompletedQuestWhoseEnderIsInsideHome()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { DistantEnder = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session,
                new PlayerbotOptions { Enabled = true, MaxDeliveryYards = 1 });
            goals.Home = new PlayerbotHomeArea(0, new System.Numerics.Vector3(-6948.95f, -132.49f, 83.53f), 100, null);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            return true;
        });
    }

    [Fact]
    public async Task PreviouslyCompletedCrossContinentDelivery_IsAbandonedByAnUnhomedBot()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { EnderOnOtherMap = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            Assert.Equal(QuestStatus.Incomplete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);

            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.NotEqual(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            return true;
        });
    }

    [Fact]
    public async Task UnhomedBot_OnAnotherContinentWithACompletedQuestWhoseEnderIsBesideItsGiver_KeepsItAndNeverRefusesIt()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            player.MapId = 1; // a GM summon or fleet move: the giver and ender stay together on map 0
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task UnhomedBot_OnTheEndersContinentNextToTheEnder_KeepsACrossContinentQuest()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { EnderOnOtherMap = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            player.MapId = 1; // the ender stands beside the bot on map 1
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task UnhomedBot_WithinTheLimitOfASameMapFarEnder_KeepsTheQuest()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { DistantEnder = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            player.SetPosition(-7500f, player.Y, player.Z, 0); // 551 yd from the ender, 1449 yd from the giver
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task HomedBot_OnTheEndersContinent_KeepsACrossContinentQuest()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { EnderOnOtherMap = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            goals.Home = new PlayerbotHomeArea(1, new System.Numerics.Vector3(-8948.95f, -132.49f, 83.53f), 100, null);
            player.MapId = 1;
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task HomedBot_OnTheGiversContinent_AbandonsACrossContinentQuestWithoutADurableRefusal()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { EnderOnOtherMap = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            goals.Home = new PlayerbotHomeArea(0, new System.Numerics.Vector3(-8948.95f, -132.49f, 83.53f), 100, null);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.NotEqual(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task UnhomedBot_CrossContinentAbandon_IsDurableEvenWhenTheDistanceLimitIsDisabled()
    {
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { EnderOnOtherMap = true });
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(QuestInteractionFixture.QuestId);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true, MaxDeliveryYards = 0 });
            var suspensions = new PlayerbotSuspensions();
            goals.Suspensions = suspensions;
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId,
                quest.Host.World.NowMs + PlayerbotSuspensions.SuspendMs + 1));
            return true;
        });
    }

    [Fact]
    public async Task VisibleRepeatableQuestWithQuestGatedDrops_RemainsAnAcceptableGoal()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 4886, Name = "Venom Sac", Class = (uint)ItemClass.Quest, Stackable = 20 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture
        {
            RepeatableItem = 4886, RepeatableItemCount = 4,
        });
        await quest.Host.OnWorldAsync(() =>
        {
            quest.Host.WorldServices.GetRequiredService<GameObjectLootFeature>().ReplaceLootContent(new LootContent(
                [(LootTableKind.Creature, new LootStoreRow(3127, 4886, -40f, 0, 1, 1))], []));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            Assert.True(goals.HasCandidate(quest.Session.Player!));
            return true;
        });
    }

    [Fact]
    public Task AutonomousQuestStages_RespectBudgetThenAcceptAndRewardOnlyCompletedProgress()
        => RunQuestStagesAsync(feature => feature.Options.OrdinaryRewardQuestIds = [QuestInteractionFixture.QuestId]);

    /// <summary>
    /// Quests:RewardMode is AllSupported by default (229faf27): every supported quest settles, with no allowlist. The bot must
    /// pursue exactly what the server settles, so under the default options it accepts and turns in the quest. Before the
    /// predicate it consulted the allowlist alone and never accepted anything on a default server.
    /// </summary>
    [Fact]
    public Task DefaultQuestOptions_BotAcceptsAndRewardsASupportedQuestWithoutAnAllowlist()
        => RunQuestStagesAsync(feature =>
        {
            Assert.Equal(QuestRewardMode.AllSupported, feature.Options.RewardMode);
            Assert.Empty(feature.Options.OrdinaryRewardQuestIds);
        });

    [Fact]
    public async Task AllowlistOnlyMode_WithoutTheQuestListed_NeverAccepts()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        await quest.Host.World.InvokeAsync(() =>
        {
            quest.Feature.Options.RewardMode = QuestRewardMode.AllowlistOnly;
            quest.Feature.Options.OrdinaryRewardQuestIds = [];
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            Player player = quest.Session.Player!;
            Assert.False(goals.HasCandidate(player));
            for (int i = 0; i < 3; i++)
            { quest.Session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Null(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            quest.Feature.Options.OrdinaryRewardQuestIds = [QuestInteractionFixture.QuestId];
            Assert.True(goals.HasCandidate(player));
            return true;
        });
    }

    /// <summary>
    /// A quest whose accept the server refuses (its source item does not fit in the full bags, vmangos
    /// CanGiveQuestSourceItemIfNeed) is asked for once; after the exchange's deadline the bot leaves it alone instead of asking
    /// again every few seconds. On the replayed live state Ironwander stood at Hands Springsprocket for good, accepting quest 2160
    /// (Supplies to Tannok, which hands out a crate) over and over.
    /// </summary>
    [Fact]
    public async Task AQuestTheServerRefusesToHandOut_IsNotAskedForAgainAfterTheDeadline()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9501, Name = "Quest crate", Class = (uint)ItemClass.Quest, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { SourceItem = 9501 }, manualClock: true);
        WorldSession session = quest.Session;
        int queries = 0;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++; };
            Assert.True(goals.HasCandidate(player));
            for (int think = 0; think < 4; think++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Equal(1, queries);
            Assert.Null(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)); // refused
            return true;
        });

        for (int second = 0; second < 30; second++)
        {
            await quest.Host.World.AdvanceClockAsync(1_000);
            await quest.Host.OnWorldAsync(() =>
            {
                for (int think = 0; think < 2; think++)
                { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(session.Player!, 500); }
                return true;
            });
        }

        await quest.Host.OnWorldAsync(() =>
        {
            Assert.Equal(1, queries);
            Assert.False(goals.HasCandidate(session.Player!));
            return true;
        });
    }

    private static async Task RunQuestStagesAsync(Action<QuestNpcFeature> configure)
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldTestHost host = quest.Host;
        WorldSession session = quest.Session;
        QuestNpcFeature feature = quest.Feature;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await host.World.InvokeAsync(() =>
        {
            configure(feature);
            var player = session.Player!;
            session.ManagedBudget = new ManagedActionBudget(0);
            goals.Update(player, 500);
            Assert.Null(feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            for (int i = 0; i < 2; i++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Equal(QuestStatus.Incomplete, feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500);
            Assert.Equal(90u, goals.PreferredCreatureEntry);
            Assert.False(feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
            // A producer event supplies completed progress; the bot never fabricates it.
            feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            for (int i = 0; i < 3; i++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.True(feature.PendingSettlementCount > 0 || feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
                "Reward stage=" + goals.StageName + "; packets=" + string.Join(',', session.DrainManagedPackets().Select(p => p.Opcode)));
            return true;
        });
        await feature.WaitForSettlementAsync((int)session.Player!.Guid.Low);
        await host.WaitForWorldAsync(() => feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
            "managed durable quest reward");
        await using var persisted = host.WorldServices.CreateAsyncScope();
        CharacterQuestData data = await persisted.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
            .LoadAsync((int)session.Player!.Guid.Low);
        Assert.True(data.Quests.Single(q => q.Quest == QuestInteractionFixture.QuestId).Rewarded);
    }

    /// <summary>A world with the synthetic quest giver beside a managed player and a SQLite character database.</summary>
    private sealed class QuestHost : IAsyncDisposable
    {
        private QuestHost(WorldTestHost host, WorldSession session)
        {
            Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }

        public WorldSession Session { get; }

        public QuestNpcFeature Feature => Host.WorldServices.GetRequiredService<QuestNpcFeature>();

        public static async Task<QuestHost> StartAsync(QuestInteractionFixture? questFixture = null, bool manualClock = false)
        {
            QuestInteractionFixture fixture = questFixture ?? new QuestInteractionFixture();
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-quest-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + databasePath,
            }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            QuestInteractionTestServices.Current.Value = fixture;
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services =>
                {
                    services.AddCharacterDatabase(configuration);
                    if (manualClock) services.AddSingleton<IWorldFeature, ManualClock>();
                });
            }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "managed questgiver visibility");
            return new QuestHost(host, session);
        }

        private sealed class ManualClock : IWorldFeature
        {
            public void Attach(WorldRuntime world) => world.UseManualClock();
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}

/// <summary>
/// A quest's creature objective that is friendly to the bot is credited another way (an item or a spell used on it: quest 5441
/// "Lazy Peons", the Foreman's Blackjack on a sleeping peon of the bot's own faction). The bot does not hunt it: in the live stress
/// test (2026-10-08) bots in the Valley of Trials walked from peon to peon they could not attack (goal Grind target 10556) and on out
/// of the valley to their deaths.
/// </summary>
public sealed class PlayerbotKillObjectiveTests
{
    private static readonly FactionTemplateCatalog Factions = new(
    [
        new FactionTemplateRecord(1, 0, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12), // the player
        new FactionTemplateRecord(12, 0, 0, OwnMask: 2, FriendlyMask: 3, HostileMask: 0), // a peon of its own side
        new FactionTemplateRecord(14, 0, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1), // a monster
        new FactionTemplateRecord(7, 0, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 0), // a neutral beast
    ]);

    [Theory]
    [InlineData(12u, false)]
    [InlineData(14u, true)]
    [InlineData(7u, true)]
    [InlineData(999u, true)] // unknown template: unresolved, still an objective
    public void OnlyACreatureNotFriendlyToTheBot_IsAKillObjective(uint npcTemplate, bool killable)
        => Assert.Equal(killable, PlayerbotQuestGoals.IsKillable(CreatePlayer(), npcTemplate, Factions, reactions: null));

    [Fact]
    public void WithReputation_AFriendlyReaction_IsNotAKillObjective()
    {
        Player player = CreatePlayer();
        Assert.False(PlayerbotQuestGoals.IsKillable(player, 14, Factions, new Reaction(ArcaneCore.Game.Reputation.ReputationRank.Friendly)));
        Assert.True(PlayerbotQuestGoals.IsKillable(player, 12, Factions, new Reaction(ArcaneCore.Game.Reputation.ReputationRank.Neutral)));
    }

    private sealed class Reaction(ArcaneCore.Game.Reputation.ReputationRank rank) : ArcaneCore.Game.Reputation.INpcReactionSource
    {
        public bool TryGetNpcReaction(Player player, FactionTemplateRecord npc, FactionTemplateRecord playerTemplate,
            out ArcaneCore.Game.Reputation.ReputationRank reaction)
        {
            reaction = rank;
            return true;
        }
    }

    private static Player CreatePlayer()
    {
        var character = new ArcaneCore.Kernel.Characters.CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Peon", Race = 2, Class = 1, Gender = 0, Level = 3,
            MapId = 1, ZoneId = 14, X = 0, Y = 0, Z = 0,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 51, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new KillObjectiveSession());
    }

    private sealed class KillObjectiveSession : IPlayerSession
    {
        public int AccountId => 1;

        public ArcaneCore.Kernel.Accounts.AccountSecurity Security => ArcaneCore.Kernel.Accounts.AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }
}
