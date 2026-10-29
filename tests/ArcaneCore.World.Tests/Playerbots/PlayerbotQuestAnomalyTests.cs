using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.GameObjects;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// H1 hardening: <c>bot-quest/accept-refused</c>, <c>giver-not-found</c> and <c>objective-no-progress</c> from <see cref="PlayerbotQuestGoals"/>
/// (<c>turnin-refused</c> is covered in <see cref="PlayerbotQuestGoalsTests"/>). Real quest handlers and the synthetic questgiver of
/// <see cref="QuestInteractionFixture"/>; the server is made to refuse by its own rules (a source item that does not fit, a dead giver).
/// </summary>
public sealed class PlayerbotQuestAnomalyTests
{
    private static readonly string AcceptRefused = PlayerbotAnomalies.Key("quest", "accept-refused");
    private static readonly string GiverNotFound = PlayerbotAnomalies.Key("quest", "giver-not-found");
    private static readonly string NoProgress = PlayerbotAnomalies.Key("quest", "objective-no-progress");

    private static bool AnyQuestAnomaly(AnomalyCapture c) => c.Any(AcceptRefused) || c.Any(GiverNotFound) || c.Any(NoProgress);

    private static void Think(WorldSession session, PlayerbotQuestGoals goals)
    {
        session.ManagedBudget = new ManagedActionBudget(1);
        goals.Update(session.Player!, 500);
    }

    [Fact]
    public async Task AcceptTheServerRefuses_Fires_OnceAtTheStageDeadline_WithTheDiagnosedCode()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9501, Name = "Quest crate", Class = (uint)ItemClass.Quest, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { SourceItem = 9501 }, manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        using AnomalyCapture capture = await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            AnomalyCapture c = PlayerbotAnomalies.CaptureFor(player);
            Assert.True(goals.HasCandidate(player));
            Think(session, goals); // the exchange starts with room in the bags; they fill before the accept
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            for (int think = 0; think < 3; think++) Think(session, goals);
            return c;
        });

        for (int second = 0; second < 30; second++)
        {
            await quest.Host.World.AdvanceClockAsync(1_000);
            await quest.Host.OnWorldAsync(() => { Think(session, goals); return true; });
        }

        Assert.Equal(1, capture.Count(AcceptRefused));
        AnomalyNotice notice = capture.Notices.Single(n => n.Key == AcceptRefused);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains($"quest={QuestInteractionFixture.QuestId}", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("code=InventoryFull", notice.Detail, StringComparison.Ordinal);
        Assert.False(capture.Any(GiverNotFound) || capture.Any(NoProgress), capture.Describe());
    }


    private static Creature Giver(Player player) => (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;

    private static void Kill(Player player) => Giver(player).Health = 0;

    private static void Revive(Player player)
    {
        Creature giver = Giver(player);
        giver.Health = giver.MaxHealth;
        Assert.True(giver.IsAlive);
    }

    [Fact]
    public async Task AGiverThatDiesAtAccept_ChangesGoalWithinOneThink_WithoutWaitingOutTheDeadline_AndIsReportedAtInfo()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var sent = new List<WorldOpcode>();
            session.ManagedDispatchObserver = opcode => sent.Add(opcode);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            Assert.True(goals.HasCandidate(player));
            Think(session, goals); // the query goes out
            Assert.True(goals.Busy);
            _ = session.DrainManagedPackets(); // its answer is lost
            Kill(player);

            Think(session, goals); // the very next think, no clock has moved: the 15 s deadline is not reached

            Assert.False(goals.Busy);
            Assert.True(goals.IsGiverWait(QuestInteractionFixture.QuestId));
            Assert.False(goals.IsRefused(QuestInteractionFixture.QuestId)); // not refused, not suspended: only waiting
            Assert.False(capture.Any(AcceptRefused), capture.Describe());
            Assert.DoesNotContain(WorldOpcode.CmsgQuestlogRemoveQuest, sent);
            Assert.Equal(1, capture.Count(GiverNotFound));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == GiverNotFound);
            Assert.Equal(AnomalySeverity.Info, notice.Severity);
            Assert.Contains($"quest={QuestInteractionFixture.QuestId}", notice.Detail, StringComparison.Ordinal);
            Assert.Null(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            return true;
        });
    }

    [Fact]
    public async Task AEnderThatDiesAtTurnIn_ChangesGoalAtOnce_AndTheCompletedQuestStaysInTheLogUnabandoned()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var sent = new List<WorldOpcode>();
            session.ManagedDispatchObserver = opcode => sent.Add(opcode);
            for (int think = 0; think < 6; think++) Think(session, goals);
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            for (int think = 0; think < 3 && !goals.Busy; think++) Think(session, goals); // the turn-in exchange starts
            Assert.True(goals.Busy);
            Assert.True(goals.StageName is "CompleteResponse" or "RewardRequest" or "RewardOffer" or "RewardChoice", goals.StageName);
            _ = session.DrainManagedPackets(); // no answer comes
            Kill(player);

            Think(session, goals);

            Assert.False(goals.Busy);
            QuestStatusData status = quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!;
            Assert.Equal(QuestStatus.Complete, status.Status);
            Assert.False(status.Rewarded);
            Assert.DoesNotContain(WorldOpcode.CmsgQuestlogRemoveQuest, sent);
            Assert.False(goals.IsRefused(QuestInteractionFixture.QuestId));
            Assert.True(goals.IsGiverWait(QuestInteractionFixture.QuestId));
            Assert.Equal(0u, goals.CompletedReturnQuest(player)); // the return trip waits too
            Assert.Equal(1, capture.Count(GiverNotFound));
            return true;
        });
    }

    [Fact]
    public async Task AfterTheWait_AndOnceTheGiverRespawned_TheBotComesBackAndAccepts_NotBefore_AndOnlyThatQuestWaits()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        int queries = 0;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++; };
            Think(session, goals);
            Assert.Equal(1, queries);
            _ = session.DrainManagedPackets();
            Kill(player);
            Think(session, goals);
            Assert.True(goals.IsGiverWait(QuestInteractionFixture.QuestId));
            Assert.False(goals.IsGiverWait(QuestInteractionFixture.QuestId + 1)); // another quest's step is not skipped
            Revive(player); // the giver is back at once, but the bot has not looked again yet
            return true;
        });

        // Before the retry time (60 s for a giver whose respawn is unknown) nothing is asked and the controller leaves the bot to other goals.
        await quest.Host.World.AdvanceClockAsync(30_000);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Assert.False(goals.HasCandidate(player));
            for (int think = 0; think < 3; think++)
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(goals.Update(player, 500));
            }

            Assert.Equal(1, queries);
            return true;
        });

        await quest.Host.World.AdvanceClockAsync(40_000);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Assert.False(goals.IsGiverWait(QuestInteractionFixture.QuestId));
            Assert.True(goals.HasCandidate(player));
            for (int think = 0; think < 6; think++) Think(session, goals);
            Assert.True(queries >= 2, "the bot went back to the giver");
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)); // and accepted
            return true;
        });
    }

    [Fact]
    public async Task ADeadGiverIsReportedOncePerEpisode_AndANewDeathAfterTheGiverIsBackIsANewEpisode()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        using AnomalyCapture capture = await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            AnomalyCapture c = PlayerbotAnomalies.CaptureFor(player);
            Think(session, goals);
            _ = session.DrainManagedPackets();
            Kill(player);
            for (int think = 0; think < 5; think++) Think(session, goals); // dead and waiting: more thinks add nothing
            Assert.Equal(1, c.Count(GiverNotFound));
            Revive(player);
            return c;
        });

        await quest.Host.World.AdvanceClockAsync(70_000);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Assert.True(goals.HasCandidate(player)); // the giver is seen alive after the wait: the episode is over
            Think(session, goals); // the query goes out again
            Assert.True(goals.Busy);
            _ = session.DrainManagedPackets();
            Kill(player);
            Think(session, goals);
            Assert.Equal(2, capture.Count(GiverNotFound));
            Assert.All(capture.Notices.Where(n => n.Key == GiverNotFound), n => Assert.Equal(AnomalySeverity.Info, n.Severity));
            return true;
        });
    }

    [Fact]
    public async Task ALivingGiver_IsAsked_AcceptedAndNeverWaited()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            for (int think = 0; think < 6; think++)
            {
                Think(session, goals);
                Assert.False(goals.IsGiverWait(QuestInteractionFixture.QuestId));
            }

            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            Assert.False(AnyQuestAnomaly(capture), capture.Describe());
            return true;
        });
    }

    [Fact]
    public async Task AQuestTakenAndHandedInNormally_ReportsNothing()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            for (int think = 0; think < 6; think++) Think(session, goals);
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            for (int think = 0; think < 6; think++) Think(session, goals);

            Assert.False(AnyQuestAnomaly(capture), capture.Describe());
            return true;
        });
    }

    [Fact]
    public async Task AnObjectiveThatGainsNoCreditForThreeMinutesOfContestedHunting_FiresNoProgress_Once()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        var contention = new PlayerbotContention(new PlayerbotOptions { Enabled = true });
        goals.Contention = contention;
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            // Silent while the contested time is under the limit (8 lost mobs = 160 s of 180 s).
            for (int i = 0; i < 8; i++) contention.NoteLostMob(90);
            Think(session, goals);
            Assert.False(capture.Any(NoProgress), capture.Describe());

            // Over it: the entry is set aside and the stall reported, once.
            contention.NoteLostMob(90);
            contention.NoteLostMob(90);
            Think(session, goals);
            Think(session, goals);
            Assert.Equal(1, capture.Count(NoProgress));
            Assert.Contains($"quest={QuestInteractionFixture.QuestId} entry=90 progress=0 reason=no-credit-timeout", capture.Notices.Single(n => n.Key == NoProgress).Detail, StringComparison.Ordinal);
            return true;
        });
    }

    [Fact]
    public async Task AnObjectiveWithPartialProgress_SuspendsOnContentionWithoutReportingNoProgressAnomaly()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        var contention = new PlayerbotContention(new PlayerbotOptions { Enabled = true });
        goals.Contention = contention;
        await quest.Host.OnWorldAsync(() =>
        {
            quest.Feature.Services.ReplaceQuests(new QuestStore(new QuestContent(
                [new QuestTemplate
                {
                    Entry = QuestInteractionFixture.QuestId, Method = 2, MinLevel = 1, QuestLevel = 1,
                    Title = "Two kills", Details = "Kill two.", RequestItemsText = "Kill two.",
                    ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2u,
                }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId }])));

            Player player = session.Player!;
            for (int think = 0; think < 4; think++) Think(session, goals);
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Think(session, goals);

            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            for (int i = 0; i < 10; i++) contention.NoteLostMob(90);
            Think(session, goals);
            Think(session, goals);

            Assert.True(contention.IsEntrySuspended(90, quest.Host.World.NowMs));
            Assert.False(capture.Any(NoProgress), capture.Describe());
            return true;
        });
    }

    // GitHub #1541: every spawn of the objective visited is a camp waiting for its respawn, not a stall. The first sight of it only opens
    // an episode; the stall is reported when the camp is still all visited a full visit window later (here no spawn of entry 90 is known,
    // so the longest one, PlayerbotWorldDestinations.MaxVisitMs) and the objective has gained nothing since: once, at Warning for none.
    [Fact]
    public async Task AnObjectiveWhoseSpawnsAreAllVisited_FiresNoProgress_OnceAfterAFullVisitWindowWithoutCredit_AndAnUnvisitedOneIsSilent()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            // Not visited: the bot still has somewhere to look, nothing to report.
            goals.AllSpawnsVisited = (_, _, _) => false;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.False(capture.Any(NoProgress), capture.Describe());

            // Every spawn just walked: the camp waits for its respawn, which is no stall yet.
            goals.AllSpawnsVisited = (_, _, _) => true;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.Equal(0u, goals.PreferredCreatureEntry);
            Assert.False(capture.Any(NoProgress), capture.Describe());
            return true;
        });

        await quest.Host.World.AdvanceClockAsync(PlayerbotWorldDestinations.MaxVisitMs + 1_000);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.Equal(1, capture.Count(NoProgress));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == NoProgress);
            Assert.Contains($"quest={QuestInteractionFixture.QuestId} entry=90 progress=0 reason=all-spawns-visited", notice.Detail, StringComparison.Ordinal);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            return true;
        });
    }

    // GitHub #1541: a camp that is all visited again a full window later is no stall when the objective gained credit in between.
    [Fact]
    public async Task AnAllVisitedObjectiveThatGainsCreditWithinTheVisitWindow_FiresNothing()
    {
        await using QuestHost quest = await QuestHost.StartAsync(manualClock: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            quest.Feature.Services.ReplaceQuests(new QuestStore(new QuestContent(
                [new QuestTemplate
                {
                    Entry = QuestInteractionFixture.QuestId, Method = 2, MinLevel = 1, QuestLevel = 1,
                    Title = "Three kills", Details = "Kill three.", RequestItemsText = "Kill three.",
                    ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 3u,
                }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId }])));
            Player player = session.Player!;
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.NotNull(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            goals.AllSpawnsVisited = (_, _, _) => true;
            Think(session, goals);
            Assert.False(capture.Any(NoProgress), capture.Describe());
            return true;
        });

        await quest.Host.World.AdvanceClockAsync(PlayerbotWorldDestinations.MaxVisitMs / 2);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            quest.Feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            return true;
        });

        await quest.Host.World.AdvanceClockAsync(PlayerbotWorldDestinations.MaxVisitMs / 2 + 1_000);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            for (int think = 0; think < 4; think++) Think(session, goals);
            Assert.False(capture.Any(NoProgress), capture.Describe());
            return true;
        });
    }

    private const uint ObjectQuestId = 900003;
    private const uint ObjectSpawnGuid = 900030;

    /// <summary>
    /// The synthetic quest world plus a quest-giver game object that shares the creature giver's entry (the game object and creature
    /// tables are separate, so the same number can be both). The object starts <see cref="ObjectQuestId"/> when asked to, else nothing.
    /// </summary>
    private static async Task<QuestHost> StartWithObjectGiverAsync(bool objectStartsQuest)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        var template = new GameObjectTemplate { Entry = QuestInteractionFixture.Entry, Type = (uint)GameObjectType.QuestGiver, DisplayId = 5, Name = "Synthetic board", Data = data };
        // Human start is (-8949.95, -132.49, 83.53): the object is 2 yd away, as the creature giver is.
        var spawn = new GameObjectSpawn { Guid = ObjectSpawnGuid, Entry = QuestInteractionFixture.Entry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        (uint, uint)[] starters = objectStartsQuest ? [(QuestInteractionFixture.Entry, ObjectQuestId)] : [];
        GameObjectTestStore.Current.Value = new GameObjectTestContext(new GameObjectContent([template], [spawn], [], starters, []), LootContent.Empty);
        QuestHost quest;
        try
        {
            quest = await QuestHost.StartAsync(manualClock: true);
        }
        finally { GameObjectTestStore.Current.Value = null; }

        ObjectGuid objectGuid = ObjectGuid.WithEntry(HighGuid.GameObject, QuestInteractionFixture.Entry, ObjectSpawnGuid);
        await quest.Host.WaitForWorldAsync(() => quest.Session.Player!.VisibleObjects.Contains(objectGuid), "quest giver object visibility");
        if (objectStartsQuest)
        {
            await quest.Host.OnWorldAsync(() =>
            {
                QuestTemplate Kill(uint entry) => new()
                {
                    Entry = entry, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Synthetic quest " + entry,
                    Details = "Accept a bounded journal task.", RequestItemsText = "Finish the synthetic task.",
                    ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1u,
                };
                var relation = new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = QuestInteractionFixture.QuestId };
                quest.Feature.Services.ReplaceQuests(new QuestStore(new QuestContent(
                    [Kill(QuestInteractionFixture.QuestId), Kill(ObjectQuestId)], [relation], [relation])
                {
                    GameObjectStarters = [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = ObjectQuestId }],
                }));
                return true;
            });
        }

        return quest;
    }

    // Live bot-quest/accept-refused code=StageTimeout in AcceptDetails: a quest-giver game object whose entry equals a creature starter's
    // was read through the creature relation tables, so the bot asked the object for a quest it does not start and the server dropped the query.
    [Fact]
    public async Task AGameObjectGiver_IsNotAskedForTheQuestOfACreatureWithTheSameEntry()
    {
        await using QuestHost quest = await StartWithObjectGiverAsync(objectStartsQuest: false);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        using AnomalyCapture capture = await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Kill(player); // the creature giver is gone: the object is the only living quest giver in sight
            return PlayerbotAnomalies.CaptureFor(player);
        });

        for (int second = 0; second < 30; second++)
        {
            await quest.Host.World.AdvanceClockAsync(1_000);
            await quest.Host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.False(goals.HasCandidate(player));
                Think(session, goals);
                return true;
            });
        }

        await quest.Host.OnWorldAsync(() =>
        {
            Assert.Null(quest.Feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId));
            return true;
        });
        Assert.False(AnyQuestAnomaly(capture), capture.Describe());
    }

    [Fact]
    public async Task AGameObjectGiver_IsAskedForTheQuestItStarts_AndTheBotAcceptsIt()
    {
        await using QuestHost quest = await StartWithObjectGiverAsync(objectStartsQuest: true);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        using AnomalyCapture capture = await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Kill(player);
            return PlayerbotAnomalies.CaptureFor(player);
        });

        for (int second = 0; second < 30; second++)
        {
            await quest.Host.World.AdvanceClockAsync(1_000);
            await quest.Host.OnWorldAsync(() => { Think(session, goals); return true; });
        }

        await quest.Host.OnWorldAsync(() =>
        {
            QuestStatusData? accepted = quest.Feature.Services.StateOf(session.Player!)!.Quests.Get(ObjectQuestId);
            Assert.NotNull(accepted);
            Assert.Equal(QuestStatus.Incomplete, accepted.Status);
            Assert.Null(quest.Feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)); // the creature's quest was not taken from the object
            return true;
        });
        Assert.False(AnyQuestAnomaly(capture), capture.Describe());
    }

    // The diagnosis names a giver that does not offer the quest when no server failure code was drained (a drained code wins).
    [Fact]
    public async Task AnAcceptTimedOutOnAGiverThatDoesNotOfferTheQuest_IsDiagnosedAsGiverDoesNotOffer()
    {
        await using QuestHost quest = await StartWithObjectGiverAsync(objectStartsQuest: false);
        WorldSession session = quest.Session;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            QuestNpcServices services = quest.Feature.Services;
            ObjectGuid objectGuid = ObjectGuid.WithEntry(HighGuid.GameObject, QuestInteractionFixture.Entry, ObjectSpawnGuid);

            (string code, string reason) = goals.DiagnoseAcceptRefusal(player, services, services.StateOf(player)!, objectGuid,
                QuestInteractionFixture.QuestId, "StageTimeout", "stage timed out");

            Assert.Equal("GiverDoesNotOffer", code);
            Assert.Contains(QuestInteractionFixture.QuestId.ToString(), reason, StringComparison.Ordinal);

            // The creature that does start the quest keeps the caller's fallback.
            (string creatureCode, _) = goals.DiagnoseAcceptRefusal(player, services, services.StateOf(player)!, QuestInteractionFixture.Guid,
                QuestInteractionFixture.QuestId, "StageTimeout", "stage timed out");
            Assert.Equal("StageTimeout", creatureCode);
            return true;
        });
    }

    // GitHub #786: with the bags already full the giver of a source-item quest is not asked at all (no query, no 15 s stage, no report).
    [Fact]
    public async Task ASourceItemQuest_IsNotAskedFor_WhenTheBagsAreAlreadyFull()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9501, Name = "Quest crate", Class = (uint)ItemClass.Quest, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { SourceItem = 9501 }, manualClock: true);
        WorldSession session = quest.Session;
        int queries = 0;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        using AnomalyCapture capture = await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++; };
            AnomalyCapture c = PlayerbotAnomalies.CaptureFor(player);
            Assert.False(goals.HasCandidate(player));
            Think(session, goals, 4);
            return c;
        });
        for (int second = 0; second < 20; second++)
        {
            await quest.Host.World.AdvanceClockAsync(1_000);
            await quest.Host.OnWorldAsync(() => { Think(session, goals, 2); return true; });
        }

        Assert.Equal(0, queries);
        Assert.False(capture.Any(AcceptRefused), capture.Describe());
    }

    // A quest whose source item the full bags cannot take is refused by the server (vmangos CanGiveQuestSourceItemIfNeed, Player.cpp:13643-13676).
    // On the live server 256 bots asked for such a quest again every ten minutes (10k accept-refused reports): the refusal now holds until a
    // bag slot is free, not for a fixed ten minutes.
    [Fact]
    public async Task AnAcceptRefusedForFullBags_IsNotAskedAgainAfterTheTenMinuteTimer_UntilASlotIsFree()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9501, Name = "Quest crate", Class = (uint)ItemClass.Quest, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { SourceItem = 9501 }, manualClock: true);
        WorldSession session = quest.Session;
        int queries = 0;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });

        using AnomalyCapture capture = await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++; };
            AnomalyCapture c = PlayerbotAnomalies.CaptureFor(player);
            Think(session, goals, 1); // the exchange starts with room in the bags; they fill before the accept
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            Think(session, goals, 3);
            return c;
        });

        // Past the exchange's deadline: refused once, reported once.
        for (int second = 0; second < 20; second++)
        {
            await quest.Host.World.AdvanceClockAsync(1_000);
            await quest.Host.OnWorldAsync(() => { Think(session, goals, 2); return true; });
        }
        Assert.Equal(1, queries);
        Assert.Equal(1, capture.Count(AcceptRefused));

        // Well past the ten minute timer with the bags still full: no walk, no second query, no second report.
        for (int minute = 0; minute < 30; minute++)
        {
            await quest.Host.World.AdvanceClockAsync(60_000);
            await quest.Host.OnWorldAsync(() => { Think(session, goals, 2); return true; });
        }
        await quest.Host.OnWorldAsync(() =>
        {
            Assert.Equal(1, queries);
            Assert.Equal(1, capture.Count(AcceptRefused));
            Assert.False(goals.HasCandidate(session.Player!));
            return true;
        });

        // A slot freed: the quest is asked for again.
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Assert.Equal(1u, player.Inventory.DestroyItemCount(9502, 1));
            Think(session, goals, 1);
            Assert.True(goals.HasCandidate(player));
            return true;
        });
    }

    [Fact]
    public async Task SourceItemFits_IsFalseOnlyWhenTheItemHasNoRoomAndIsNotOwned()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9501, Name = "Quest crate", Class = (uint)ItemClass.Quest, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { SourceItem = 9501 }, manualClock: true);
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = quest.Session.Player!;
            ArcaneCore.Game.Quests.Quest template = quest.Feature.Services.Quests.Get(QuestInteractionFixture.QuestId)!;
            Assert.True(PlayerbotQuestGoals.SourceItemFits(player, template));
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            Assert.False(PlayerbotQuestGoals.SourceItemFits(player, template));
            Assert.Equal(1u, player.Inventory.DestroyItemCount(9502, 1));
            Assert.True(PlayerbotQuestGoals.SourceItemFits(player, template));
            return true;
        });
    }

    private static void Think(WorldSession session, PlayerbotQuestGoals goals, int times)
    {
        for (int think = 0; think < times; think++)
            Think(session, goals);
    }

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
