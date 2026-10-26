using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
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
            uint markedAt = quest.Host.World.NowMs;
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.None, state.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.Equal(QuestConstants.MaxQuestLogSize, state.Quests.FindSlot(QuestInteractionFixture.QuestId));
            Assert.False(goals.HasCandidate(player));
            Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, markedAt));
            // The unreachable-ender mark lasts a minute first (PlayerbotDeferLedger), then ends: a repeat is what makes it longer.
            Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, markedAt + 59_000));
            Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, markedAt + 61_000)); // past its end the mark is over
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
    public async Task UnhomedBot_CrossContinentAbandon_IsMarkedUnreachableEvenWhenTheDistanceLimitIsDisabled()
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
            uint markedAt = quest.Host.World.NowMs;
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            // The unreachable-ender mark lasts a minute first (PlayerbotDeferLedger), then ends: a repeat is what makes it longer.
            Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, markedAt + 59_000));
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, markedAt + 61_000));
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
            session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++; };
            // The bags fill once the exchange has begun: a giver of a source item the full bags cannot take is not asked at all (c2f3484f).
            Assert.True(goals.HasCandidate(player));
            session.ManagedBudget = new ManagedActionBudget(1);
            goals.Update(player, 500);
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            for (int think = 0; think < 3; think++)
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

    [Fact]
    public async Task CompletedQuestWhoseTurnInIsRefusedForFullBags_RecordsDiagnosticAndSetsTheQuestAsideImmediately()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 25, Name = "Worn Shortsword", Class = (uint)ItemClass.Weapon, Quality = 1, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 2092, Name = "Worn Dagger", Class = (uint)ItemClass.Weapon, Quality = 1, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        var suspensions = new PlayerbotSuspensions();
        goals.Suspensions = suspensions;
        await quest.Host.OnWorldAsync(() =>
        {
            quest.Feature.Services.ReplaceQuests(new QuestStore(new QuestContent(
                [new QuestTemplate
                {
                    Entry = QuestInteractionFixture.QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, LimitTime = 30,
                    Title = "Synthetic quest", Details = "Accept a bounded journal task.", RequestItemsText = "Finish the synthetic task.",
                    ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1u, RewItemId1 = 25, RewItemCount1 = 1u,
                }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId }])));

            Player player = session.Player!;
            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);

            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));

            int chooseRewardSent = 0;
            session.ManagedDispatchObserver = opcode =>
            {
                if (opcode == WorldOpcode.CmsgQuestgiverChooseReward) chooseRewardSent++;
            };

            Assert.Equal(0u, goals.CompletedReturnQuest(player));
            Assert.True(goals.NeedsBagSpaceForTurnIn);

            for (int think = 0; think < 5; think++)
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.Update(player, 500));
            }

            Assert.Equal(0, chooseRewardSent);
            Assert.False(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
            Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));
            Assert.True(goals.NeedsBagSpaceForTurnIn);
            Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
            Assert.Equal(0u, goals.CompletedReturnQuest(player));

            Assert.Equal(1u, player.Inventory.DestroyItemCount(9502, 1));
            Assert.Equal(QuestInteractionFixture.QuestId, goals.CompletedReturnQuest(player));
            Assert.False(goals.NeedsBagSpaceForTurnIn);
            Assert.False(goals.IsRefused(QuestInteractionFixture.QuestId));
            return true;
        });
    }

    [Fact]
    public async Task CompletedQuestWithVisibleEnderAndBlockedCloseApproach_SetsQuestAsideAfterThreeFailedApproaches()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));

            var giver = (ArcaneCore.Game.Creatures.Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 40f, player.Y, player.Z, 0); // visible ender within 100 yd (#388 at 7 yd, #342 at 64 yd, #548 at 68 yd)
            Assert.Contains(QuestInteractionFixture.Guid, player.VisibleObjects);
            WorldCollision.Of(quest.Host.World).Install(lineOfSight: new BlockedFloor(player.Z), pathfinder: new BlockedPathfinder());

            var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
            try
            {
                for (int think = 0; think < 3; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                }

                Assert.True(brain.QuestGoals.IsRefused(QuestInteractionFixture.QuestId));
                Assert.True(brain.Suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
                Assert.True(brain.Suspensions.IsEntrySuspended(QuestInteractionFixture.Entry, quest.Host.World.NowMs));
                Assert.NotNull(brain.LastTurnInRefusal);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.LastTurnInRefusal!.Value.QuestId);
                Assert.NotEqual(PlayerbotGoalKind.Quest, brain.Goal);
            }
            finally { brain.Stop(); }
            return true;
        });
    }

    [Fact]
    public async Task VisibleEnder_DetourThatLeavesTheStraightLine_IsWalkedNotRefusedAsNoRoute()
    {
        // A visible ender whose exact mesh query fails (inside a house, across a ridge) still has a
        // PlanToward leg that walks away from the straight line first: a region portal, a road around
        // the building. That leg is not a dead-end partial (PartialToGoal). Treating it as "no exact
        // route" set quest 428 aside at Rane Yorick (NoRouteToVisibleEnder, ~88 yd, guid 0) and left
        // the brain on goal Quest with no quest (stall near the Barrens road).
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));

            var giver = (ArcaneCore.Game.Creatures.Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 40f, player.Y, player.Z, 0);
            Assert.Contains(QuestInteractionFixture.Guid, player.VisibleObjects);
            var paths = new DetourAwayPathfinder();
            WorldCollision.Of(quest.Host.World).Install(lineOfSight: new BlockedFloor(player.Z), pathfinder: paths);

            var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
            try
            {
                for (int think = 0; think < 3; think++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                }

                Assert.True(paths.PartialQueries > 0, "the exact plan failed and a toward-leg was asked");
                Assert.False(brain.QuestGoals.IsRefused(QuestInteractionFixture.QuestId));
                Assert.Null(brain.LastTurnInRefusal);
                Assert.False(brain.Suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
                Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.QuestId);
            }
            finally { brain.Stop(); }
            return true;
        });
    }

    /// <summary>
    /// Exact plans (partial disallowed) have no corridor. Toward-plans answer a whole leg that steps
    /// away from the ender, the way a region portal around a building does.
    /// </summary>
    private sealed class DetourAwayPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public int PartialQueries { get; private set; }

        public PathResult FindPath(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, PathOptions? options = null)
        {
            if (options?.AllowPartial != true)
                return new PathResult(PathType.NoPath, []);
            PartialQueries++;
            float away = start.X <= end.X ? -25f : 25f;
            return new PathResult(PathType.Normal, [start, new System.Numerics.Vector3(start.X + away, start.Y, start.Z)]);
        }
    }

    [Fact]
    public async Task CompletedQuestTurnIn_DrainsQueuedServerResponseBeforeCheckingStageTimeout_AfterLongThinkGap()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));

            // First think sends CmsgQuestgiverCompleteQuest and transitions to CompleteResponse,
            // while the server immediately queues SmsgQuestgiverRequestItems into the session's managed inbox:
            session.ManagedBudget = new ManagedActionBudget(1);
            goals.Update(player, 500);
            Assert.Equal("CompleteResponse", goals.StageName);
            return true;
        });

        // Simulate a 16-second think gap on a loaded server (912 bots):
        await quest.Host.World.AdvanceClockAsync(16_000);

        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            // Next thinks must drain the already-queued response packet and finish the turn-in without a false timeout:
            for (int i = 0; i < 3; i++)
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(player, 500);
            }

            Assert.Null(goals.LastTurnInRefusal);
            Assert.False(goals.IsRefused(QuestInteractionFixture.QuestId));
            return true;
        });
        await quest.Feature.WaitForSettlementAsync((int)session.Player!.Guid.Low);
        await quest.Host.WaitForWorldAsync(
            () => quest.Feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
            "turn-in completed after long think gap");
    }

    private sealed class BlockedFloor(float height) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, bool ignoreM2 = true) => false;
        public bool TryGetObjectHit(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, float modifyDistance, out System.Numerics.Vector3 hit)
        { hit = from; return true; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => height;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class BlockedPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, PathOptions? options = null)
            => new(PathType.Incomplete, [start, start]);
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

    [Theory]
    [InlineData("InventoryFull", "inventory full", false)]
    [InlineData("RewardRefused", "reward refused by the server", true)]
    public async Task TurnInRefusal_IsSilentAndDeferredForFullBags_AndWarningOtherwise(string code, string reason, bool expectsAnomaly)
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        var suspensions = new PlayerbotSuspensions();
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true }) { Suspensions = suspensions };
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            goals.RecordTurnInRefusal(player, QuestInteractionFixture.QuestId, QuestInteractionFixture.Entry, reason, code, quest.Host.World.NowMs);

            if (!expectsAnomaly)
            {
                Assert.False(capture.Any(PlayerbotAnomalies.Key("quest", "turnin-refused")));
                Assert.True(goals.NeedsBagSpaceForTurnIn);
                Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));
                Assert.False(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
            }
            else
            {
                var notice = Assert.Single(capture.Notices, n => n.Key == PlayerbotAnomalies.Key("quest", "turnin-refused"));
                Assert.Equal(ArcaneCore.Kernel.Diagnostics.AnomalySeverity.Warning, notice.Severity);
                Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
            }

            return true;
        });
    }

    [Fact]
    public async Task StarterQuestWithVisibleGiverAndBlockedCloseApproach_SuspendsQuestAndEntryAfterThreeFailedApproaches_AndClearsQuestId()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var giver = (ArcaneCore.Game.Creatures.Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 40f, player.Y, player.Z, 0);
            Assert.Contains(QuestInteractionFixture.Guid, player.VisibleObjects);
            WorldCollision.Of(quest.Host.World).Install(lineOfSight: new BlockedFloor(player.Z), pathfinder: new BlockedPathfinder());

            var suspensions = new PlayerbotSuspensions();
            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true }) { Suspensions = suspensions };
            for (int think = 0; think < 3; think++)
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.Update(player, 500));
                Assert.Equal(0u, goals.QuestId);
                Assert.Equal(0u, goals.TargetEntry);
            }

            Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));
            Assert.True(suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, quest.Host.World.NowMs));
            Assert.True(suspensions.IsEntrySuspended(QuestInteractionFixture.Entry, quest.Host.World.NowMs));
            return true;
        });
    }

    [Fact]
    public async Task InstantCompleteQuestAbandonedBeforeNextThink_ClearsStage_DoesNotFireAcceptRefusedTimeout_AndRefusesReaccept()
    {
        await using QuestHost quest = await QuestHost.StartAsync(
            new QuestInteractionFixture { NoObjectives = true, DistantEnder = true }, manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true, MaxDeliveryYards = 0 });
        using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var c = PlayerbotAnomalies.CaptureFor(player);
            for (int think = 0; think < 3 && quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId) is null; think++)
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(player, 500);
            }

            Assert.Equal(QuestStatus.Complete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            Assert.False(goals.Busy);

            goals.Home = new PlayerbotHomeArea(0, new System.Numerics.Vector3(-8948.95f, -132.49f, 83.53f), 100, null);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.False(goals.Busy);
            Assert.True(goals.IsRefused(QuestInteractionFixture.QuestId));

            quest.Feature.Services.ReplaceQuests(new QuestStore(new QuestContent(
                [new QuestTemplate { Entry = 900099, Method = 2, MinLevel = 1, QuestLevel = 1, PrevQuestId = 900098, Title = "Chained" }],
                [], [])));
            Assert.Equal("PrevQuest", goals.DiagnoseAcceptRefusal(
                player, quest.Feature.Services, quest.Feature.Services.StateOf(player)!, QuestInteractionFixture.Guid, 900099, "StageTimeout", "timeout").Code);
            return c;
        });

        await quest.Host.World.AdvanceClockAsync(20_000);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.Update(player, 500));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("quest", "accept-refused")), capture.Describe());
            return true;
        });
    }


    [Fact]
    public async Task ATurnedInQuest_StartsCleanWhateverItsEarlierMarksWere()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        const uint Q = QuestInteractionFixture.QuestId;
        var suspensions = new PlayerbotSuspensions();
        PlayerbotQuestGoals? goals = null;
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            var body = new PacketWriter(12);
            body.WriteUInt64(QuestInteractionFixture.Guid.Value);
            body.WriteUInt32(Q);
            quest.Session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            // An earlier abandon and a refusal left strikes behind (both marks ended already).
            uint now = quest.Host.World.NowMs;
            suspensions.Ledger.Defer(new DeferKey(DeferKind.UnreachableQuest, Q), now, untilMs: now);
            suspensions.Ledger.Defer(new DeferKey(DeferKind.UnreachableQuest, Q), now, untilMs: now);
            Assert.Equal(2, suspensions.Ledger.Strikes(new DeferKey(DeferKind.UnreachableQuest, Q)));
            goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true }) { Suspensions = suspensions };
            for (int i = 0; i < 8 && quest.Feature.PendingSettlementCount == 0
                && !quest.Feature.Services.StateOf(player)!.Quests.Get(Q)!.Rewarded; i++)
            {
                quest.Session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(player, 500);
            }

            return true;
        });
        await quest.Feature.WaitForSettlementAsync((int)quest.Session.Player!.Guid.Low);
        await quest.Host.World.InvokeAsync(() =>
        {
            Player player = quest.Session.Player!;
            for (int i = 0; i < 6 && suspensions.Ledger.Strikes(new DeferKey(DeferKind.UnreachableQuest, Q)) != 0; i++)
            {
                quest.Session.ManagedBudget = new ManagedActionBudget(1);
                goals!.Update(player, 500);
            }

            Assert.True(quest.Feature.Services.StateOf(player)!.Quests.Get(Q)!.Rewarded);
            Assert.Equal(0, suspensions.Ledger.Strikes(new DeferKey(DeferKind.UnreachableQuest, Q)));
            Assert.Equal(0, suspensions.Ledger.Strikes(new DeferKey(DeferKind.Quest, Q)));
            return true;
        });
    }

    [Fact]
    public async Task ANeverSatisfiableTurnIn_IsNotWalkedBackToWithinItsGrowingBound()
    {
        // A quest with no objectives and a turn-in the ender keeps refusing ("requirements not met"): the refusal marks it
        // unreachable (PlayerbotQuestGoals.RefuseTurnIn). Each repeat sets it aside for longer, 60 s then 10 min, 1 h, ... so the
        // bot is not back at the ender every ten minutes for ever.
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { NoObjectives = true }, manualClock: true);
        var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
        var suspensions = new PlayerbotSuspensions();
        goals.Suspensions = suspensions;
        const uint Q = QuestInteractionFixture.QuestId;
        async Task<uint> RefuseAsync()
        {
            uint at = 0;
            await quest.Host.World.InvokeAsync(() =>
            {
                Player player = quest.Session.Player!;
                at = quest.Host.World.NowMs;
                var body = new PacketWriter(12);
                body.WriteUInt64(QuestInteractionFixture.Guid.Value);
                body.WriteUInt32(Q);
                if (quest.Feature.Services.StateOf(player)!.Quests.Get(Q) is null)
                {
                    quest.Session.ManagedBudget = new ManagedActionBudget(1);
                    quest.Session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, body.ToArray());
                }

                goals.RecordTurnInRefusal(player, Q, QuestInteractionFixture.Entry, "requirements not met", "DontHaveReq", at);
                return true;
            });
            return at;
        }

        uint[] steps = [60_000, 600_000, 3_600_000];
        for (int refusal = 0; refusal < steps.Length; refusal++)
        {
            uint at = await RefuseAsync();
            uint bound = steps[refusal];
            await quest.Host.World.InvokeAsync(() =>
            {
                var key = new DeferKey(DeferKind.UnreachableQuest, Q);
                Assert.Equal(refusal + 1, suspensions.Ledger.Strikes(key));
                Assert.True(suspensions.IsQuestUnreachable(Q, at + bound - 1)); // set aside for exactly the step ...
                Assert.False(suspensions.IsQuestUnreachable(Q, at + bound)); // ... and no longer
                Assert.True(goals.IsRefused(Q));
                Assert.Equal(0u, goals.CompletedReturnQuest(quest.Session.Player!)); // not walked back to
                return true;
            });
            // The ordinary ten minute set-aside ends first; the next refusal is the bot walking back after it.
            await quest.Host.World.AdvanceClockAsync(PlayerbotSuspensions.SuspendMs + 1_000);
        }
    }

    /// <summary>
    /// The quest giver's fixture with a full journal's worth of quests (<see cref="Filler"/>, kill quests of creature 90 no giver offers)
    /// and, at the fixture's giver instead of its own quest, <see cref="Offered"/>: a level 10 quest, worth taking for a level 10 bot.
    /// The first <c>grey</c> fillers are grey to a level 10 bot (grey level 4): levels 3, 2, 2, ...
    /// </summary>
    private sealed class FullLogQuests(QuestInteractionFixture original, int grey) : IQuestContentStore
    {
        public const uint Filler = 910_000;
        public const uint Offered = 910_100;

        public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestContent content = await ((IQuestContentStore)original).LoadAsync(cancellationToken);
            static QuestTemplate Kill(uint entry, int level) => new()
            {
                Entry = entry, Method = 2, MinLevel = 1, QuestLevel = level, Title = "Quest " + entry, Details = "Kill one.",
                RequestItemsText = "Kill one.", ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1,
            };
            var templates = new List<QuestTemplate>(content.Templates);
            for (int i = 0; i < QuestConstants.MaxQuestLogSize; i++)
                templates.Add(Kill(Filler + (uint)i, i < grey ? (i == 0 ? 3 : 2) : 10));
            templates.Add(Kill(Offered, 10));
            var offered = new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = Offered };
            return new QuestContent(templates, [.. content.Starters.Where(r => r.Quest != QuestInteractionFixture.QuestId), offered],
                [.. content.Enders, offered]);
        }
    }

    private static PlayerNpcState FillTheLog(QuestHost quest, Player player)
    {
        player.Level = 10;
        PlayerNpcState state = quest.Feature.Services.StateOf(player)!;
        for (uint i = 0; i < QuestConstants.MaxQuestLogSize; i++)
            Assert.Equal(GmQuestAddResult.Added, quest.Feature.Services.GmAddQuest(player, quest.Feature.Services.Quests.Get(FullLogQuests.Filler + i)!));
        Assert.False(PlayerbotQuestGoals.HasFreeLogSlot(state));
        return state;
    }

    /// <summary>A level 10 bot with the first <paramref name="count"/> <see cref="FullLogQuests.Filler"/> quests in its log (the log keeps free slots).</summary>
    private static PlayerNpcState AddFillers(QuestHost quest, Player player, uint count)
    {
        player.Level = 10;
        PlayerNpcState state = quest.Feature.Services.StateOf(player)!;
        for (uint i = 0; i < count; i++)
            Assert.Equal(GmQuestAddResult.Added, quest.Feature.Services.GmAddQuest(player, quest.Feature.Services.Quests.Get(FullLogQuests.Filler + i)!));
        Assert.True(PlayerbotQuestGoals.HasFreeLogSlot(state));
        return state;
    }

    /// <summary>The filler quests still in the log (the bot may take the fixture's offered quest into a freed slot).</summary>
    private static int Fillers(PlayerNpcState state)
        => state.Quests.Statuses.Count(row => row.Key is >= FullLogQuests.Filler and < FullLogQuests.Filler + 3 && row.Value.Status == QuestStatus.Incomplete);

    private static int Think(QuestHost quest, PlayerbotQuestGoals goals, Player player)
    {
        int abandons = 0;
        quest.Session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestlogRemoveQuest) abandons++; };
        quest.Session.ManagedBudget = new ManagedActionBudget(1);
        goals.Update(player, 500);
        return abandons;
    }

    /// <summary>"Skip and drop if quests become greyed": with a free slot an incomplete grey quest is abandoned and set aside; a non-grey one stays.</summary>
    [Fact]
    public async Task AnIncompleteGreyQuest_WithAFreeLogSlot_IsAbandonedAndRefused_AndANonGreyOneIsKept()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 1));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = AddFillers(quest, player, 2); // Filler+0 is grey (level 3), Filler+1 is level 10
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            Assert.Equal(1, Think(quest, goals, player));
            Assert.Equal(QuestStatus.None, state.Quests.Get(FullLogQuests.Filler)?.Status);
            Assert.True(goals.IsRefused(FullLogQuests.Filler));
            Assert.Equal(QuestStatus.Incomplete, state.Quests.Get(FullLogQuests.Filler + 1)?.Status);
            return true;
        });
    }

    /// <summary>A grey quest whose objectives are done is still turned in: the drop rule leaves it.</summary>
    [Fact]
    public async Task ACompleteGreyQuest_IsNotDropped()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 1));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = AddFillers(quest, player, 1);
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Assert.Equal(QuestStatus.Complete, state.Quests.Get(FullLogQuests.Filler)?.Status);
            Assert.Equal(0u, PlayerbotQuestGoals.GreyQuestToDrop(player, quest.Feature.Services, state));
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            Assert.Equal(0, Think(quest, goals, player));
            Assert.Equal(QuestStatus.Complete, state.Quests.Get(FullLogQuests.Filler)?.Status);
            return true;
        });
    }

    [Fact]
    public async Task WithDropGreyQuestsOff_AGreyQuestIsKeptAndStillHunted()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 1));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = AddFillers(quest, player, 1);
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true, DropGreyQuests = false });
            Assert.Equal(0, Think(quest, goals, player));
            Assert.Equal(QuestStatus.Incomplete, state.Quests.Get(FullLogQuests.Filler)?.Status);
            goals.RefreshObjectiveEntry(player, quest.Feature.Services, state);
            Assert.Equal(90u, goals.PreferredCreatureEntry);
            return true;
        });
    }

    [Fact]
    public async Task AGreyQuestIsNotDroppedInCombat()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 1));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = AddFillers(quest, player, 1);
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            player.Map!.Combat.SetInCombatState(player, 60_000);
            Assert.True(player.Combat.IsInCombat);
            Assert.Equal(0, Think(quest, goals, player));
            Assert.Equal(QuestStatus.Incomplete, state.Quests.Get(FullLogQuests.Filler)?.Status);
            return true;
        });
    }

    /// <summary>One abandon per think, and the next one waits <see cref="PlayerbotQuestGoals.GreyShedIntervalMs"/>.</summary>
    [Fact]
    public async Task GreyQuests_AreDroppedOnePerThink_AndRateLimited()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 3), manualClock: true);
        PlayerbotQuestGoals? goals = null;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = AddFillers(quest, player, 3);
            goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            Assert.Equal(1, Think(quest, goals, player));
            Assert.Equal(0, Think(quest, goals, player)); // the same instant: rate limited
            Assert.Equal(2, Fillers(state));
            return true;
        });
        await quest.Host.World.AdvanceClockAsync(PlayerbotQuestGoals.GreyShedIntervalMs / 2); // still inside the interval
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            Assert.Equal(0, Think(quest, goals!, player));
            return true;
        });
        await quest.Host.World.AdvanceClockAsync(PlayerbotQuestGoals.GreyShedIntervalMs);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = quest.Feature.Services.StateOf(player)!;
            Assert.Equal(1, Think(quest, goals!, player));
            Assert.Equal(1, Fillers(state));
            return true;
        });
    }

    /// <summary>While the drop is rate-limited a grey quest still in the log gives the bot no kill objective to hunt.</summary>
    [Fact]
    public async Task AGreyQuestStillInTheLog_GivesNoHuntObjective_ButANonGreyOneDoes()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 1));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = AddFillers(quest, player, 1);
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            goals.RefreshObjectiveEntry(player, quest.Feature.Services, state);
            Assert.Equal(0u, goals.PreferredCreatureEntry);
            Assert.Equal(QuestStatus.Incomplete, state.Quests.Get(FullLogQuests.Filler)?.Status); // not dropped yet: only skipped
            Assert.Equal(GmQuestAddResult.Added, quest.Feature.Services.GmAddQuest(player, quest.Feature.Services.Quests.Get(FullLogQuests.Filler + 1)!));
            goals.RefreshObjectiveEntry(player, quest.Feature.Services, state);
            Assert.Equal(90u, goals.PreferredCreatureEntry);
            return true;
        });
    }

    /// <summary>
    /// GitHub #786: a bot with a full journal walked to quest givers and asked for quests the server refuses with SMSG_QUESTLOG_FULL,
    /// thousands of times ("code=QuestLogFull"). With nothing it may give up (no grey or failed quest) it leaves the giver alone.
    /// </summary>
    [Fact]
    public async Task AFullLogWithNothingToShed_DoesNotAskTheGiver()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 0));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = FillTheLog(quest, player);
            using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            int asks = 0, abandons = 0;
            quest.Session.ManagedDispatchObserver = opcode =>
            {
                if (opcode is WorldOpcode.CmsgQuestgiverQueryQuest or WorldOpcode.CmsgQuestgiverAcceptQuest) asks++;
                if (opcode == WorldOpcode.CmsgQuestlogRemoveQuest) abandons++;
            };
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            Assert.False(goals.HasCandidate(player));
            for (int think = 0; think < 4; think++)
            { quest.Session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Equal(0, asks);
            Assert.Equal(0, abandons);
            Assert.Null(state.Quests.Get(FullLogQuests.Offered));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("quest", "accept-refused")), capture.Describe());
            return true;
        });
    }

    /// <summary>
    /// GitHub #786: at a giver offering a quest worth taking, a full journal sheds its lowest grey quest through the ordinary abandon
    /// packet (the lowest level first, then the lowest id), and the bot takes the offered quest. The shed quest is set aside, not
    /// taken straight back.
    /// </summary>
    [Fact]
    public async Task AFullLogAtAGiverOfAWorthwhileQuest_ShedsItsLowestGreyQuestAndTakesTheOffer()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 3));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            PlayerNpcState state = FillTheLog(quest, player);
            Assert.Equal(FullLogQuests.Filler + 1, PlayerbotQuestGoals.LowValueQuestToShed(player, quest.Feature.Services, state));
            using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            for (int think = 0; think < 4 && state.Quests.Get(FullLogQuests.Offered) is null; think++)
            { quest.Session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }

            Assert.Equal(QuestStatus.Incomplete, state.Quests.Get(FullLogQuests.Offered)?.Status);
            Assert.Equal(QuestStatus.None, state.Quests.Get(FullLogQuests.Filler + 1)?.Status); // the one shed
            Assert.Equal(QuestConstants.MaxQuestLogSize, state.Quests.FindSlot(FullLogQuests.Filler + 1));
            for (uint i = 0; i < QuestConstants.MaxQuestLogSize; i++)
                if (i != 1) Assert.Equal(QuestStatus.Incomplete, state.Quests.Get(FullLogQuests.Filler + i)?.Status);
            Assert.True(goals.IsRefused(FullLogQuests.Filler + 1));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("quest", "accept-refused")), capture.Describe());
            return true;
        });
    }

    /// <summary>A grey quest at the giver is no reason to give up another one: a full log skips it.</summary>
    [Fact]
    public async Task AFullLogAtAGiverOfAGreyQuest_ShedsNothing()
    {
        await using QuestHost quest = await QuestHost.StartAsync(quests: original => new FullLogQuests(original, grey: 3));
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            FillTheLog(quest, player);
            player.Level = 20; // the offered level 10 quest is grey now (grey level 13)
            int abandons = 0, asks = 0;
            quest.Session.ManagedDispatchObserver = opcode =>
            {
                if (opcode == WorldOpcode.CmsgQuestlogRemoveQuest) abandons++;
                if (opcode is WorldOpcode.CmsgQuestgiverQueryQuest or WorldOpcode.CmsgQuestgiverAcceptQuest) asks++;
            };
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true, DropGreyQuests = false });
            for (int think = 0; think < 4; think++)
            { quest.Session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Equal(0, abandons);
            Assert.Equal(0, asks);
            return true;
        });
    }

    /// <summary>
    /// GitHub #786 ("code=OutOfRange reason=out of interaction range (44.4 yd)"): a bot pulled 44 yards from the giver mid-exchange (a
    /// fight) timed the stage out at that distance, reported the giver as refusing it and set the quest aside for ten minutes. The
    /// server refused nothing: the bot left the conversation, and it starts it again when it is back in reach.
    /// </summary>
    [Fact]
    public async Task AnExchangeTheBotWasPulledAwayFrom_IsNotARefusal_AndIsTakenUpAgainInReach()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        float homeX = 0, homeY = 0, homeZ = 0;
        int queries = 0;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            (homeX, homeY, homeZ) = (player.X, player.Y, player.Z);
            session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++; };
            session.ManagedBudget = new ManagedActionBudget(1);
            goals.Update(player, 500);
            Assert.Equal(1, queries);
            Assert.Equal("AcceptDetails", goals.StageName);
            // Pulled away before the response was read: 44 yards off, as in the live report.
            _ = session.DrainManagedPackets();
            player.Relocate(player.X + 44f, player.Y, player.Z, player.Orientation, quest.Host.World.NowMs);
            return true;
        });

        await quest.Host.World.AdvanceClockAsync(16_000);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            session.ManagedBudget = new ManagedActionBudget(0);
            goals.Update(player, 500);
            Assert.False(capture.Any(PlayerbotAnomalies.Key("quest", "accept-refused")), capture.Describe());
            Assert.False(goals.IsRefused(QuestInteractionFixture.QuestId));
            Assert.False(goals.Busy);

            // Back at the giver it asks again and takes the quest.
            player.Relocate(homeX, homeY, homeZ, player.Orientation, quest.Host.World.NowMs);
            for (int think = 0; think < 4 && quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId) is null; think++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Equal(2, queries);
            Assert.Equal(QuestStatus.Incomplete, quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            return true;
        });
    }

    /// <summary>
    /// A quest-giving game object is in reach as the server measures it (vmangos GameObject::IsAtInteractDistance): its per-type distance
    /// to its centre, without bounding radii. With the creature rule a large object's radius had the bot ask from where the server
    /// answers "out of interaction range".
    /// </summary>
    [Fact]
    public async Task AQuestGivingGameObject_IsInReachByTheServersObjectRule()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            NpcInfo At(float distance, bool gameObject) => new(ObjectGuid.WithEntry(HighGuid.GameObject, 1, 1), 1, 1, NpcFlags.QuestGiver,
                player.MapId, player.X + distance, player.Y, player.Z, 6f, true, false, false, false, 0,
                IsGameObject: gameObject, GameObjectInteractionDistance: gameObject ? 5.55556f : 0f);
            Assert.True(PlayerbotQuestGoals.WithinReach(player, At(5.5f, gameObject: true)));
            Assert.False(PlayerbotQuestGoals.WithinReach(player, At(8f, gameObject: true))); // inside 5 + radii, outside the object's distance
            Assert.True(PlayerbotQuestGoals.WithinReach(player, At(8f, gameObject: false))); // a creature keeps 5 + both radii
            return true;
        });
    }

    private sealed class StarterPlusRemoteEnderQuests(QuestInteractionFixture original) : IQuestContentStore
    {
        public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestContent content = await ((IQuestContentStore)original).LoadAsync(cancellationToken);
            var unrelated = new QuestTemplate
            {
                Entry = QuestInteractionFixture.UnrelatedId,
                Method = 2,
                MinLevel = 1,
                QuestLevel = 1,
                Title = "Starter quest",
                Details = "Take starter quest.",
                RequestItemsText = "Finish starter.",
                ReqCreatureOrGOId1 = 90,
                ReqCreatureOrGOCount1 = 1,
            };
            var templates = content.Templates.Where(t => t.Entry != QuestInteractionFixture.UnrelatedId).Append(unrelated).ToArray();
            var starters = new[]
            {
                new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId },
                new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.UnrelatedId },
            };
            var enders = new[]
            {
                new CreatureQuestRelation { Id = QuestInteractionFixture.RemoteEnder, Quest = QuestInteractionFixture.QuestId },
                new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.UnrelatedId },
            };
            return new QuestContent(templates, starters, enders);
        }
    }

    [Fact]
    public async Task VisibleStarterBeforeEnder_DoesNotPreemptCompletedQuestTurnIn_AndStageCompletionContinues()
    {
        var enderSpawn = new ArcaneCore.Kernel.WorldData.Creatures.CreatureSpawn
        {
            Guid = 900021,
            Entry = QuestInteractionFixture.RemoteEnder,
            MapId = 0,
            X = -8948.95f + 2f,
            Y = -132.49f,
            Z = 83.53f,
        };
        await using QuestHost quest = await QuestHost.StartAsync(
            new QuestInteractionFixture { EnderSpawns = [enderSpawn] },
            quests: original => new StarterPlusRemoteEnderQuests(original));
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var enderGuid = ObjectGuid.WithEntry(HighGuid.Unit, QuestInteractionFixture.RemoteEnder, 900021);
            player.VisibleObjects.Add(enderGuid);

            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));

            var opcodes = new List<WorldOpcode>();
            session.ManagedDispatchObserver = op => opcodes.Add(op);
            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true })
            {
                ReturnQuest = QuestInteractionFixture.QuestId,
            };

            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.Update(player, 500));
            Assert.Contains(WorldOpcode.CmsgQuestgiverCompleteQuest, opcodes);
            Assert.DoesNotContain(WorldOpcode.CmsgQuestgiverQueryQuest, opcodes);
            Assert.Equal(QuestInteractionFixture.QuestId, goals.QuestId);
            Assert.Equal(QuestInteractionFixture.RemoteEnder, goals.TargetEntry);
            return true;
        });
    }

    [Fact]
    public async Task CloseApproachFailure_ReportsNonZeroEnderGuid()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));

            var giver = (ArcaneCore.Game.Creatures.Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 40f, player.Y, player.Z, 0);
            WorldCollision.Of(quest.Host.World).Install(lineOfSight: new BlockedFloor(player.Z), pathfinder: new BlockedPathfinder());

            using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true })
            {
                ReturnQuest = QuestInteractionFixture.QuestId,
            };
            for (int think = 0; think < 3; think++)
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(player, 500);
            }

            Assert.True(capture.Any(PlayerbotAnomalies.Key("quest", "turnin-refused")), capture.Describe());
            Assert.Contains($"guid=0x{QuestInteractionFixture.Guid.Value:X}", capture.Describe());
            Assert.DoesNotContain("guid=0x0 ", capture.Describe());
            return true;
        });
    }

    [Fact]
    public async Task CompletedAutoCompleteQuest_WhosePrevQuestRequirementWasInvalidated_IsSkippedAndAbandonedWithoutDontHaveReqRefusal()
    {
        const uint ExclusiveFollowUpQuestId = 1462;
        const uint InvalidatedBreadcrumbQuestId = 1463;
        var extraTemplates = new QuestTemplate[]
        {
            new()
            {
                Entry = ExclusiveFollowUpQuestId,
                Title = "Earth Sapta",
                Method = 2,
                ZoneOrSort = 14,
                MinLevel = 1,
                QuestLevel = 1,
            },
            new()
            {
                Entry = InvalidatedBreadcrumbQuestId,
                Title = "Earth Sapta Breadcrumb",
                Method = 0, // IsAutoComplete
                ZoneOrSort = 14,
                MinLevel = 1,
                QuestLevel = 1,
                PrevQuestId = -(int)ExclusiveFollowUpQuestId, // only valid while 1462 is active and unrewarded
            },
        };

        await using QuestHost quest = await QuestHost.StartAsync(quests: fixture =>
            new ExtraQuestStore(fixture, extraTemplates, starterEntries: [], enderEntries: [(QuestInteractionFixture.Entry, InvalidatedBreadcrumbQuestId)]));
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            PlayerNpcState state = quest.Feature.Services.StateOf(player)!;
            state.Quests.Load(
            [
                new CharacterQuestStatus((int)player.Guid.Low, QuestInteractionFixture.QuestId, (byte)QuestStatus.Complete, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                new CharacterQuestStatus((int)player.Guid.Low, ExclusiveFollowUpQuestId, (byte)QuestStatus.Complete, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                new CharacterQuestStatus((int)player.Guid.Low, InvalidatedBreadcrumbQuestId, (byte)QuestStatus.Complete, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            ], quest.Feature.Services.Quests);

            Quest breadcrumb = quest.Feature.Services.Quests.Get(InvalidatedBreadcrumbQuestId)!;
            Assert.False(quest.Feature.Services.MeetsTurnInRequirements(player, breadcrumb));

            using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });

            Assert.Equal(0u, goals.CompletedReturnQuest(player));
            session.ManagedBudget = new ManagedActionBudget(4);
            Assert.False(goals.Update(player, 500));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("quest", "turnin-refused")), capture.Describe());

            session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.NotEqual(QuestStatus.Complete, state.Quests.Get(InvalidatedBreadcrumbQuestId)?.Status);
            return true;
        });
    }

    private sealed class BlockedCenterOpenRingPathfinder(System.Numerics.Vector3 npcCenter) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, PathOptions? options = null)
        {
            float distToCenter = System.Numerics.Vector2.Distance(new(end.X, end.Y), new(npcCenter.X, npcCenter.Y));
            if (distToCenter < 2.0f)
                return new PathResult(PathType.NoPath, []);
            return PathResult.StraightLine(start, end, PathType.Normal);
        }
    }

    [Fact]
    public async Task VisibleEnder_WithBlockedCenterButReachableInteractionRing_WalksToRingAndTurnsInWithoutNoRouteRefusal()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));

            var giver = (ArcaneCore.Game.Creatures.Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            var npcPos = new System.Numerics.Vector3(player.X + 12f, player.Y, player.Z);
            giver.SetPosition(npcPos.X, npcPos.Y, npcPos.Z, 0);
            WorldCollision.Of(quest.Host.World).Install(pathfinder: new BlockedCenterOpenRingPathfinder(npcPos));

            using ArcaneCore.Kernel.Diagnostics.AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true })
            {
                ReturnQuest = QuestInteractionFixture.QuestId,
            };

            Assert.True(goals.Update(player, 500));
            Assert.False(capture.Any(PlayerbotAnomalies.Key("quest", "turnin-refused")), capture.Describe());
            return true;
        });
    }

    private sealed class ProbeBudgetInspectingPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public long ObservedProbeNodeBudget { get; private set; }

        public PathResult FindPath(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, PathOptions? options = null)
        {
            if (PlayerbotThinkProbe.Active is { } active)
                ObservedProbeNodeBudget = active.NodeBudget;
            return new PathResult(PathType.NoPath, []);
        }
    }

    [Fact]
    public async Task QuestPhase_CapsFallbackNavigationNodeBudgetTo8192()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var accept = new PacketWriter(12);
            accept.WriteUInt64(QuestInteractionFixture.Guid.Value);
            accept.WriteUInt32(QuestInteractionFixture.QuestId);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, accept.ToArray()));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));

            var giver = (ArcaneCore.Game.Creatures.Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 40f, player.Y, player.Z, 0);
            var pathfinder = new ProbeBudgetInspectingPathfinder();
            WorldCollision.Of(quest.Host.World).Install(lineOfSight: new BlockedFloor(player.Z), pathfinder: pathfinder);

            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true })
            {
                ReturnQuest = QuestInteractionFixture.QuestId,
            };
            var probe = new PlayerbotThinkProbe { NodeBudget = 32_768 };
            probe.Begin();
            try
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(player, 500);
            }
            finally
            {
                probe.End();
            }

            Assert.Equal(32_768L, probe.NodeBudget);
            Assert.Equal(8_192L, pathfinder.ObservedProbeNodeBudget);
            return true;
        });
    }

    private sealed class ExtraQuestStore(
        QuestInteractionFixture inner,
        IReadOnlyList<QuestTemplate> extraTemplates,
        IReadOnlyList<(uint Entry, uint QuestId)> starterEntries,
        IReadOnlyList<(uint Entry, uint QuestId)> enderEntries) : IQuestContentStore
    {
        public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestContent content = await ((IQuestContentStore)inner).LoadAsync(cancellationToken);
            var templates = content.Templates.Concat(extraTemplates).ToArray();
            var starters = content.Starters.Concat(starterEntries.Select(x => new CreatureQuestRelation { Id = x.Entry, Quest = x.QuestId })).ToArray();
            var enders = content.Enders.Concat(enderEntries.Select(x => new CreatureQuestRelation { Id = x.Entry, Quest = x.QuestId })).ToArray();
            return new QuestContent(templates, starters, enders);
        }
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

        public static async Task<QuestHost> StartAsync(QuestInteractionFixture? questFixture = null, bool manualClock = false,
            Func<QuestInteractionFixture, IQuestContentStore>? quests = null)
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
                    if (quests is not null) services.AddSingleton(quests(fixture));
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
