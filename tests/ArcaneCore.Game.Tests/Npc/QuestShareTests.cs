using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// Quest sharing and party accept (vmangos QuestHandler.cpp:107-196, 332-381, 403-474; Player.cpp:12855-12860, 13795-13802,
/// 14407-14441). Message numbers are the 1.12 QuestPartyMessage of gtker/wow_messages (versions "1 2").
/// </summary>
public sealed class QuestShareTests
{
    private const uint Shared = 960001;
    private const uint Party = 960002;
    private const uint Timed = 960003;
    private const uint Plain = 960004;

    private static QuestTemplate Sharable(uint id, uint extraFlags = 0, uint limit = 0, byte minLevel = 1) => new()
    {
        Entry = id, Method = 2, QuestLevel = 1, MinLevel = minLevel, Title = $"Shared {id}", Details = "Together.", Objectives = "Do it.",
        QuestFlags = (uint)QuestFlags.Sharable | extraFlags, LimitTime = limit,
        ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2,
    };

    private static List<QuestTemplate> Templates(int extra = 0)
    {
        List<QuestTemplate> list =
        [
            Sharable(Shared), Sharable(Party, (uint)QuestFlags.PartyAccept), Sharable(Timed, limit: 600),
            new QuestTemplate { Entry = Plain, Method = 2, QuestLevel = 1, Title = "Not sharable", ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2 },
        ];
        for (int i = 0; i < extra; i++)
        {
            list.Add(Sharable(970000 + (uint)i));
        }

        return list;
    }

    [Fact]
    public void PushToParty_OffersTheDetailsFromTheSharer_AndRemembersTheOffer()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Shared);
        kit.Push(Shared);

        Assert.Equal([(kit.Member.Guid, QuestShareMessage.SharingQuest)], kit.PushResults(kit.Sharer));
        byte[] details = Assert.Single(kit.Sent(kit.Member, WorldOpcode.SmsgQuestgiverQuestDetails));
        Assert.Equal(kit.Sharer.Guid.Value, BitConverter.ToUInt64(details, 0));
        Assert.Equal(new QuestShareInfo(kit.Sharer.Guid, Shared), kit.Services.ShareInfoOf(kit.Member));
    }

    [Fact]
    public void PushToParty_AnswersEachRefusalInVmangosOrder()
    {
        // Too far.
        using (var far = new ShareKit(Templates()))
        {
            far.Take(far.Sharer, Shared);
            far.Member.X = far.Sharer.X + 20;
            far.Push(Shared);
            Assert.Equal([QuestShareMessage.SharingQuest, QuestShareMessage.TooFar], far.PushResults(far.Sharer).Select(r => r.Message));
            Assert.Empty(far.Sent(far.Member, WorldOpcode.SmsgQuestgiverQuestDetails));
            Assert.Null(far.Services.ShareInfoOf(far.Member));
        }

        // Already finished, already on it, cannot take it, log full, busy: each its own message.
        void Case(Action<ShareKit> prepare, QuestShareMessage expected, int extra = 0)
        {
            using var kit = new ShareKit(Templates(extra));
            kit.Take(kit.Sharer, Shared);
            prepare(kit);
            kit.Push(Shared);
            Assert.Equal([QuestShareMessage.SharingQuest, expected], kit.PushResults(kit.Sharer).Select(r => r.Message));
            Assert.Empty(kit.Sent(kit.Member, WorldOpcode.SmsgQuestgiverQuestDetails));
        }

        Case(k => k.Take(k.Member, Shared, complete: true), QuestShareMessage.FinishQuest);
        Case(k => k.Take(k.Member, Shared), QuestShareMessage.HaveQuest);
        Case(k => { for (int i = 0; i < 20; i++) { k.Take(k.Member, 970000 + (uint)i); } }, QuestShareMessage.LogFull, extra: 20);
        Case(k => { k.Push(Shared); k.Clear(); }, QuestShareMessage.Busy); // a first push left an offer pending
    }

    [Fact]
    public void CantTakeQuest_UsesTheMinimumLevelOfTheQuest()
    {
        using var kit = new ShareKit([Sharable(Shared, minLevel: 30), .. Templates().Skip(1)]);
        kit.Take(kit.Sharer, Shared);
        kit.Push(Shared);
        Assert.Equal([QuestShareMessage.SharingQuest, QuestShareMessage.CantTakeQuest], kit.PushResults(kit.Sharer).Select(r => r.Message));
    }

    [Fact]
    public void AcceptingTheSharedQuest_AddsItAndTellsTheSharer_AndConsumesTheOffer()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Shared);
        kit.Push(Shared);
        kit.Clear();

        Assert.True(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Shared));
        Assert.Equal(QuestStatus.Incomplete, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Shared));
        Assert.Equal([(kit.Member.Guid, QuestShareMessage.AcceptQuest)], kit.PushResults(kit.Sharer));
        Assert.Null(kit.Services.ShareInfoOf(kit.Member));
    }

    [Fact]
    public void AcceptingFromAPlayer_RequiresASharableQuestTheSharerIsOn()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Plain);                 // on it, but not sharable
        Assert.False(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Plain));
        Assert.False(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Shared)); // sharable, but the sharer is not on it
        Assert.Equal(QuestStatus.None, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Plain));
        Assert.Equal(QuestStatus.None, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Shared));
    }

    [Fact]
    public void AcceptingFromADeadPlayer_Fails()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Shared);
        kit.Sharer.Health = 0;
        Assert.False(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Shared));
    }

    [Fact]
    public void AcceptingWhenTheSharerMovedAway_TellsTheSharerTooFar_AndAddsNothing()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Shared);
        kit.Push(Shared);
        kit.Member.X = kit.Sharer.X + 30;
        kit.Clear();

        Assert.False(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Shared));
        Assert.Equal([(kit.Member.Guid, QuestShareMessage.TooFar)], kit.PushResults(kit.Sharer));
        Assert.Equal(QuestStatus.None, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Shared));
        Assert.Null(kit.Services.ShareInfoOf(kit.Member));
    }

    [Fact]
    public void ThePushResultOfTheReceiver_IsForwardedWithTheReceiversGuid_AndDropsTheOffer()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Shared);
        kit.Push(Shared);
        kit.Clear();

        kit.Services.QuestPushResult(kit.Member, (byte)QuestShareMessage.DeclineQuest);
        Assert.Equal([(kit.Member.Guid, QuestShareMessage.DeclineQuest)], kit.PushResults(kit.Sharer));
        Assert.Null(kit.Services.ShareInfoOf(kit.Member));
        // Without an offer the packet does nothing.
        kit.Clear();
        kit.Services.QuestPushResult(kit.Member, 2);
        Assert.Empty(kit.PushResults(kit.Sharer));
    }

    [Fact]
    public void ASharedTimedQuest_TakesTheSharersRemainingTime()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Timed, timerEnd: 160); // the services clock reads 100: sixty seconds left
        kit.Push(Timed);
        Assert.True(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Timed));
        Assert.Equal(160, kit.Services.StateOf(kit.Member)!.Quests.Get(Timed)!.TimerEndUnix);
    }

    [Fact]
    public void APartyAcceptQuest_ConfirmsToEligibleMembersOnTheSameMap_AndTheyCanAcceptIt()
    {
        using var kit = new ShareKit(Templates(), withThird: true);
        kit.Take(kit.Sharer, Party);
        kit.Push(Party);
        kit.Third.X = 0;
        Assert.True(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Party));

        // The third member (not offered by the push because it was out of range at the time) is offered the confirmation box.
        byte[] box = Assert.Single(kit.Sent(kit.Third, WorldOpcode.SmsgQuestConfirmAccept));
        Assert.Equal(QuestSharePackets.ConfirmAccept(Party, "Shared " + Party, kit.Member.Guid).AsSpan().ToArray(), box);
        Assert.Equal(new QuestShareInfo(kit.Member.Guid, Party), kit.Services.ShareInfoOf(kit.Third));

        kit.Services.ConfirmAcceptQuest(kit.Third, Party);
        Assert.Equal(QuestStatus.Incomplete, kit.Services.StateOf(kit.Third)!.Quests.GetStatus(Party));
        Assert.Null(kit.Services.ShareInfoOf(kit.Third));
    }

    [Fact]
    public void AConfirmedPartyAcceptQuest_IsNotReOfferedToMembersWhoHaveNotAnsweredYet()
    {
        // vmangos fans a PARTY_ACCEPT quest out only from the accept handler (QuestHandler.cpp:166-191); HandleQuestConfirmAccept
        // (332-381) just adds the quest. Fourth has not answered when Third confirms: no second box, offer still names the accepter.
        using var kit = new ShareKit(Templates(), withFourth: true);
        kit.Take(kit.Sharer, Party);
        Assert.True(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Party));
        Assert.Single(kit.Sent(kit.Third, WorldOpcode.SmsgQuestConfirmAccept));
        Assert.Single(kit.Sent(kit.Fourth, WorldOpcode.SmsgQuestConfirmAccept));

        kit.Services.ConfirmAcceptQuest(kit.Third, Party);
        Assert.Equal(QuestStatus.Incomplete, kit.Services.StateOf(kit.Third)!.Quests.GetStatus(Party));
        Assert.Single(kit.Sent(kit.Fourth, WorldOpcode.SmsgQuestConfirmAccept));
        Assert.Equal(new QuestShareInfo(kit.Member.Guid, Party), kit.Services.ShareInfoOf(kit.Fourth));
    }

    [Fact]
    public void APartyAcceptFanOut_TellsAMemberWhoCannotTakeTheQuestWhy()
    {
        // QuestHandler.cpp:179 calls CanTakeQuest(qInfo, true): the refusal message goes to the member and no box is sent.
        using var kit = new ShareKit(Templates(), withFourth: true);
        kit.Third.Level = 0;
        kit.Take(kit.Sharer, Party);
        Assert.True(kit.Services.AcceptQuest(kit.Member, kit.Sharer.Guid, Party));
        Assert.Empty(kit.Sent(kit.Third, WorldOpcode.SmsgQuestConfirmAccept));
        Assert.Null(kit.Services.ShareInfoOf(kit.Third));
        Assert.Equal([QuestPackets.QuestInvalid(QuestInvalidReason.DontHaveReq).AsSpan().ToArray()],
            kit.Sent(kit.Third, WorldOpcode.SmsgQuestgiverQuestInvalid));
    }

    [Fact]
    public void ConfirmAccept_NeedsAMatchingOffer_AndAPartyAcceptQuest()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Party);
        kit.Take(kit.Sharer, Shared);
        kit.Services.ConfirmAcceptQuest(kit.Member, Party);                       // no offer
        Assert.Equal(QuestStatus.None, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Party));
        kit.Push(Shared);                                                          // an offer for an ordinary sharable quest
        kit.Services.ConfirmAcceptQuest(kit.Member, Shared);                       // not a party-accept quest
        kit.Services.ConfirmAcceptQuest(kit.Member, Party);                        // the offer names another quest
        Assert.Equal(QuestStatus.None, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Shared));
        Assert.Equal(QuestStatus.None, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Party));
    }

    [Fact]
    public void ConfirmAccept_RefusesAPlayerOutsideTheGroup()
    {
        using var kit = new ShareKit(Templates());
        kit.Take(kit.Sharer, Party);
        kit.Push(Party);
        kit.Party.SameGroup = false;
        kit.Services.ConfirmAcceptQuest(kit.Member, Party);
        Assert.Equal(QuestStatus.None, kit.Services.StateOf(kit.Member)!.Quests.GetStatus(Party));
        Assert.NotNull(kit.Services.ShareInfoOf(kit.Member)); // the offer stays pending, as in vmangos
    }

    [Fact]
    public void PartyAcceptQuests_AreWithheldWithoutAGroupLookup()
    {
        using var with = new ShareKit(Templates());
        Assert.Equal(QuestAdapter.None, with.Services.MissingAdapters(with.Services.Quests.Get(Party)!));
        using var without = new QuestFlowKit(Templates(), starters: [Party]);
        Assert.Equal(QuestAdapter.PartyAccept, without.Services.MissingAdapters(without.Services.Quests.Get(Party)!));
    }

    [Fact]
    public void SharePushRequiresQuest_RefusesAPushOfAQuestTheSharerIsNotOn_OnlyWhenSwitchedOn()
    {
        using var retail = new ShareKit(Templates());
        retail.Push(Shared); // retail: the push goes out although the pusher has nothing
        Assert.Single(retail.Sent(retail.Member, WorldOpcode.SmsgQuestgiverQuestDetails));

        using var strict = new ShareKit(Templates(), o => o.SharePushRequiresQuest = true);
        strict.Push(Shared);
        Assert.Empty(strict.Sent(strict.Member, WorldOpcode.SmsgQuestgiverQuestDetails));
        Assert.Empty(strict.PushResults(strict.Sharer));
    }

    [Fact]
    public void SharePackets_HaveTheWowMessagesLayout()
    {
        byte[] push = QuestSharePackets.PushResult(new ObjectGuid(0x0102030405060708), QuestShareMessage.Busy).AsSpan().ToArray();
        Assert.Equal([8, 7, 6, 5, 4, 3, 2, 1, 5], push);
        byte[] confirm = QuestSharePackets.ConfirmAccept(0x11223344, "Hi", new ObjectGuid(1)).AsSpan().ToArray();
        Assert.Equal([0x44, 0x33, 0x22, 0x11, (byte)'H', (byte)'i', 0, 1, 0, 0, 0, 0, 0, 0, 0], confirm);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], Enum.GetValues<QuestShareMessage>().Select(m => (int)m));
    }

    /// <summary>Two (or three) players of one group standing together, each with a loaded journal, over one quest service.</summary>
    private sealed class ShareKit : IDisposable
    {
        private readonly Dictionary<Player, FakeSession> _sessions = [];

        public ShareKit(IReadOnlyList<QuestTemplate> templates, Action<QuestNpcOptions>? configure = null, bool withThird = false, bool withFourth = false)
        {
            Sharer = Add(1, 0);
            Member = Add(2, 3);
            Third = withThird || withFourth ? Add(3, 60) : Member;
            Fourth = withFourth ? Add(4, 61) : Member;
            Party = new FakeParty([Sharer, Member, .. withThird || withFourth ? [Third] : Array.Empty<Player>(), .. withFourth ? [Fourth] : Array.Empty<Player>()]);
            var options = new QuestNpcOptions();
            configure?.Invoke(options);
            Services = new QuestNpcServices(new QuestStore(new QuestContent(templates, [], [])), NpcStore.Empty,
                new QuestNpcDependencies(Party: Party), options, new QuestFlowKit.RecordingSink(), () => 100,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            foreach (Player player in _sessions.Keys)
            {
                PlayerNpcState state = Services.Track(player);
                Services.CompleteLoad(state, new CharacterQuestData([], []));
            }

            Clear();
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();

        public Player Sharer { get; }

        public Player Member { get; }

        public Player Third { get; }

        public Player Fourth { get; }

        public FakeParty Party { get; }

        public QuestNpcServices Services { get; }

        private Player Add(uint guid, float x)
        {
            var session = new FakeSession((int)guid);
            Player player = TestWorld.CreatePlayer(guid, x, 0, session);
            player.Level = 10;
            ItemTestData.Wire(player.Inventory);
            player.Inventory.Load([]);
            World.AddPlayer(player);
            _sessions[player] = session;
            return player;
        }

        /// <summary>Put the quest in the player's journal as if it had been accepted (or completed).</summary>
        public void Take(Player player, uint questId, bool complete = false, long timerEnd = 0)
        {
            PlayerNpcState state = Services.StateOf(player)!;
            int slot = state.Quests.FindSlot(0);
            QuestStatusData data = state.Quests.GetOrAdd(questId);
            data.Status = complete ? QuestStatus.Complete : QuestStatus.Incomplete;
            data.TimerEndUnix = timerEnd;
            state.Quests.SetSlot(slot, questId, (uint)timerEnd);
            if (timerEnd != 0)
            {
                state.Quests.AddTimed(questId);
            }

        }

        public void Push(uint questId) => Services.PushQuestToParty(Sharer, questId);

        public IReadOnlyList<byte[]> Sent(Player player, WorldOpcode opcode)
            => [.. _sessions[player].Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

        public List<(ObjectGuid Subject, QuestShareMessage Message)> PushResults(Player player)
            => [.. _sessions[player].Sent.Where(p => p.Opcode == WorldOpcode.MsgQuestPushResult)
                .Select(p => (new ObjectGuid(BitConverter.ToUInt64(p.Payload, 0)), (QuestShareMessage)p.Payload[8]))];

        public void Clear()
        {
            foreach (FakeSession session in _sessions.Values)
            {
                session.Clear();
            }
        }

        public void Dispose() => World.Dispose();
    }

    private sealed class FakeParty(IReadOnlyList<Player> members) : IQuestParty
    {
        public bool SameGroup { get; set; } = true;

        public IReadOnlyList<Player> MembersOf(Player player) => members.Contains(player) ? members : [];

        public bool IsInSameGroup(Player first, Player second) => SameGroup;

        public bool IsInSameRaid(Player first, Player second) => SameGroup;

        public Player? FindPlayer(ObjectGuid guid) => members.FirstOrDefault(m => m.Guid == guid);
    }
}
