using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>A pending shared-quest offer (vmangos Player::m_questShareInfo): who offered which quest.</summary>
public sealed record QuestShareInfo(ObjectGuid Sharer, uint QuestId);

/// <summary>
/// Quest sharing and the party-accept confirmation box, reimplemented from vmangos Handlers/QuestHandler.cpp
/// (HandleQuestgiverAcceptQuestOpcode :107-196, HandleQuestConfirmAccept :332-381, HandlePushQuestToParty :403-459,
/// HandleQuestPushResult :461-474) and Objects/Player.cpp (CanShareQuest :13795-13802, AddQuest :12855-12860,
/// SendQuestConfirmAccept :14407-14430, SendPushToPartyResponse :14432-14441). Packet layouts: gtker/wow_messages
/// quest/cmsg_pushquesttoparty.wowm, msg_quest_push_result.wowm, smsg_quest_confirm_accept.wowm, cmsg_quest_confirm_accept.wowm.
/// <para>
/// Deviations: players in different map instances (another map, or another copy of the same map) are never within
/// sharing distance (vmangos compares coordinates only); a
/// pending offer lives in memory with the offered player, never in the database. By default the pusher is not required
/// to hold the quest (vmangos does not check it; the receiver's later accept does, <see cref="CanShareQuest"/>);
/// <c>Quests:SharePushRequiresQuest</c> adds that check up front.
/// </para>
/// </summary>
public sealed partial class QuestNpcServices
{
    /// <summary>QUEST_SHARE_DISTANCE (vmangos Object.h:72).</summary>
    public const float QuestShareDistance = 14.0f;

    private readonly ConditionalWeakTable<Player, QuestShareInfo> _shares = new();

    /// <summary>The offer waiting for the player's answer, if any.</summary>
    public QuestShareInfo? ShareInfoOf(Player player) => _shares.TryGetValue(player, out QuestShareInfo? info) ? info : null;

    private void SetShareInfo(Player player, ObjectGuid sharer, uint questId) => _shares.AddOrUpdate(player, new QuestShareInfo(sharer, questId));

    private void ClearShareInfo(Player player) => _shares.Remove(player);

    /// <summary>vmangos Player::CanShareQuest: a sharable quest the player is currently on.</summary>
    public bool CanShareQuest(Player sharer, uint questId)
        => Quests.Get(questId) is { } quest && quest.HasFlag(QuestFlags.Sharable)
            && Ready(sharer) is { } state && state.Quests.IsCurrent(questId);

    /// <summary>vmangos WorldObject::IsWithinDist(obj, QUEST_SHARE_DISTANCE, is3D, SizeFactor::None): strictly inside, in 3D, in one map instance.</summary>
    private static bool WithinShareDistance(Player first, Player second)
    {
        // Same Map object, not just the same map id: two copies of an instance share local coordinates.
        if (first.Map is not { } map || !ReferenceEquals(map, second.Map))
        {
            return false;
        }

        float dx = first.X - second.X;
        float dy = first.Y - second.Y;
        float dz = first.Z - second.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) < QuestShareDistance * QuestShareDistance;
    }

    /// <summary>vmangos Player::SendPushToPartyResponse: MSG_QUEST_PUSH_RESULT to <paramref name="to"/> about <paramref name="subject"/>.</summary>
    private static void SendPushResult(Player to, Player subject, QuestShareMessage message)
        => Send(to, WorldOpcode.MsgQuestPushResult, QuestSharePackets.PushResult(subject.Guid, message));

    /// <summary>
    /// CMSG_PUSHQUESTTOPARTY (HandlePushQuestToParty, QuestHandler.cpp:403-459). Each other group member is told the quest is
    /// being shared, then in vmangos order: too far, already finished, already on it, cannot take it, log full, busy
    /// with another offer; a member who passes gets the quest details from the pusher and a pending offer.
    /// </summary>
    public void PushQuestToParty(Player pusher, uint questId)
    {
        if (Quests.Get(questId) is not { } quest || Deps.Party is not { } party || Ready(pusher) is null
            || (Options.SharePushRequiresQuest && !CanShareQuest(pusher, questId)))
        {
            return;
        }

        foreach (Player member in party.MembersOf(pusher))
        {
            if (ReferenceEquals(member, pusher) || Ready(member) is not { } state)
            {
                continue;
            }

            SendPushResult(pusher, member, QuestShareMessage.SharingQuest);
            QuestShareMessage? refusal = null;
            if (!WithinShareDistance(pusher, member))
            {
                refusal = QuestShareMessage.TooFar;
            }
            else if (state.Quests.GetStatus(questId) == QuestStatus.Complete)
            {
                refusal = QuestShareMessage.FinishQuest;
            }
            else if (state.Quests.GetStatus(questId) != QuestStatus.None)
            {
                refusal = QuestShareMessage.HaveQuest;
            }
            else if (!CanTakeQuest(state, quest, []))
            {
                refusal = QuestShareMessage.CantTakeQuest;
            }
            else if (state.Quests.FindSlot(0) == QuestConstants.MaxQuestLogSize)
            {
                refusal = QuestShareMessage.LogFull;
            }
            else if (ShareInfoOf(member) is not null)
            {
                refusal = QuestShareMessage.Busy;
            }

            if (refusal is { } message)
            {
                SendPushResult(pusher, member, message);
                continue;
            }

            Send(member, WorldOpcode.SmsgQuestgiverQuestDetails, QuestPackets.Details(pusher.Guid, quest, Options.RateDropMoney,
                id => Deps.Items?.GetItem(id)?.DisplayId ?? member.Inventory.Templates.Find(id)?.DisplayId ?? 0));
            SetShareInfo(member, pusher.Guid, questId);
        }
    }

    /// <summary>
    /// MSG_QUEST_PUSH_RESULT from the receiver (HandleQuestPushResult, QuestHandler.cpp:461-474): the answer is forwarded to
    /// the player who offered the quest with the receiver's own guid, and the pending offer is dropped.
    /// </summary>
    public void QuestPushResult(Player receiver, byte message)
    {
        if (ShareInfoOf(receiver) is not { } info)
        {
            return;
        }

        if (Deps.Party?.FindPlayer(info.Sharer) is { } sharer)
        {
            Send(sharer, WorldOpcode.MsgQuestPushResult, QuestSharePackets.PushResult(receiver.Guid, (QuestShareMessage)message));
        }

        ClearShareInfo(receiver);
    }

    /// <summary>
    /// The shared-quest branch of CMSG_QUESTGIVER_ACCEPT_QUEST (QuestHandler.cpp:107-196 with a player as giver). The giver must
    /// be sharing a sharable quest it is on and both players alive (CanInteractWithQuestGiver, Player.cpp:2437); an offer
    /// for this quest must still be pending with the sharer on the map within <see cref="QuestShareDistance"/>, else the
    /// sharer is told TOO_FAR; success tells the sharer ACCEPT_QUEST. A timed quest takes the sharer's remaining time.
    /// </summary>
    private bool AcceptFromPlayer(Player player, PlayerNpcState state, ObjectGuid guid, uint questId)
    {
        if (Deps.Party?.FindPlayer(guid) is not { } giver || !CanShareQuest(giver, questId)
            || !player.IsAlive || !giver.IsAlive || Quests.Get(questId) is not { } quest)
        {
            return false;
        }

        // CanTakeQuest(msg) runs before the offer is consulted ("prevent cheating", QuestHandler.cpp:134-139).
        if (RefuseTakeQuest(state, quest, []) is { } refusal)
        {
            if (refusal.Message is { } reason)
            {
                Send(player, WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(reason));
            }

            return false;
        }

        if (ShareInfoOf(player) is { } info && info.QuestId == questId)
        {
            if (player.Map?.FindObject(info.Sharer) is not Player sharer)
            {
                return false;
            }

            if (!WithinShareDistance(player, sharer))
            {
                SendPushResult(sharer, player, QuestShareMessage.TooFar);
                return false;
            }

            SendPushResult(sharer, player, QuestShareMessage.AcceptQuest);
        }

        long? sharedEnd = Ready(giver)?.Quests.Get(questId) is { TimerEndUnix: not 0 } timed ? timed.TimerEndUnix : null;
        return AddQuestFrom(player, state, quest, sharedEnd);
    }

    /// <summary>
    /// After a PARTY_ACCEPT quest was accepted through <see cref="AcceptQuest"/> (QuestHandler.cpp:166-191): every other group member in the accepter's map instance (vmangos IsInMap) who
    /// could take it (one who cannot is sent the refusal) gets a pending offer, any open gossip window closed and an SMSG_QUEST_CONFIRM_ACCEPT naming the quest.
    /// </summary>
    private void OfferPartyAccept(Player accepter, Quest quest)
    {
        if (Deps.Party is not { } party)
        {
            return;
        }

        foreach (Player member in party.MembersOf(accepter))
        {
            if (ReferenceEquals(member, accepter) || accepter.Map is not { } map || !ReferenceEquals(member.Map, map) || Ready(member) is not { } state)
            {
                continue;
            }

            if (RefuseTakeQuest(state, quest, []) is { } refusal)
            {
                // CanTakeQuest(qInfo, true) (QuestHandler.cpp:179) tells the member why it was not offered the quest.
                if (refusal.Message is { } reason)
                {
                    Send(member, WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(reason));
                }

                continue;
            }

            SetShareInfo(member, accepter.Guid, quest.Id);
            CloseGossip(member);
            Send(member, WorldOpcode.SmsgQuestConfirmAccept, QuestSharePackets.ConfirmAccept(quest.Id, quest.Template.Title, accepter.Guid));
        }
    }

    /// <summary>
    /// CMSG_QUEST_CONFIRM_ACCEPT (HandleQuestConfirmAccept, QuestHandler.cpp:332-381): the member confirms a party-accept quest
    /// someone else accepted. The pending offer must name this quest and a player who is still online and in the same group
    /// (same raid for a quest allowed in raids), still on it when sharable; the quest must still be takeable, and then it is
    /// added without a giver. A refusal before the add leaves the offer pending, as in vmangos.
    /// </summary>
    public void ConfirmAcceptQuest(Player player, uint questId)
    {
        if (Ready(player) is not { } state || Quests.Get(questId) is not { } quest || !quest.HasFlag(QuestFlags.PartyAccept)
            || ShareInfoOf(player) is not { } info || info.QuestId != questId || Deps.Party is not { } party)
        {
            return;
        }

        if (party.FindPlayer(info.Sharer) is not { } original)
        {
            ClearShareInfo(player);
            return;
        }

        if (IsAllowedInRaid(quest) ? !party.IsInSameRaid(player, original) : !party.IsInSameGroup(player, original))
        {
            return;
        }

        if (quest.HasFlag(QuestFlags.Sharable) && !(Ready(original)?.Quests.IsCurrent(questId) ?? false))
        {
            return;
        }

        if (RefuseTakeQuest(state, quest, []) is { } refusal)
        {
            if (refusal.Message is { } reason)
            {
                Send(player, WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(reason));
            }

            return;
        }

        AddQuestFrom(player, state, quest, sharedTimerEnd: null);
        ClearShareInfo(player);
    }
}
