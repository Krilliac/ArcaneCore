using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Creature quest status, details, acceptance and abandonment. Reimplemented from vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 Handlers/QuestHandler.cpp and Objects/Player.cpp
/// CanSeeStartQuest, CanAddQuest, AddQuest, RemoveQuestAtSlot and TakeQuestSourceItem. Source items,
/// delivery counters, exploration triggers, repeatable and timed quests have adapters; source spells,
/// party confirmation, PvP activation and auto rewards still fail closed.
/// </summary>
public sealed partial class QuestNpcServices
{
    public void QuestgiverStatusQuery(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } state || FindNpc(player, guid) is not { } npc)
        {
            return;
        }

        // vmangos HandleQuestgiverStatusQueryOpcode (QuestHandler.cpp:36-77) only refuses hostile creatures; it never tests the
        // quest-giver npc flag (four relation NPCs of classic-db lack it and still show their marks).
        DialogStatus status = DialogStatus.None;
        if (!npc.IsHostile)
        {
            foreach (uint id in EndersOf(npc))
            {
                if (Quests.Get(id) is not { IsActive: true } quest)
                {
                    continue;
                }

                // GetDialogStatus (QuestHandler.cpp:517-526): a finished-and-unrewarded quest or a takeable autocomplete quest
                // is Reward2, or RewardRep when it is an autocomplete repeatable one; otherwise an incomplete quest.
                DialogStatus candidate = (state.Quests.GetStatus(id) == QuestStatus.Complete && !state.Quests.RewardStatus(quest))
                    || (quest.IsAutoComplete && CanTakeQuest(state, quest, []))
                    ? quest.IsAutoComplete && quest.IsRepeatable ? DialogStatus.RewardRep : DialogStatus.Reward2
                    : state.Quests.GetStatus(id) == QuestStatus.Incomplete ? DialogStatus.Incomplete : DialogStatus.None;
                if ((byte)candidate > (byte)status)
                {
                    status = candidate;
                }
            }

            foreach (uint id in StartersOf(npc))
            {
                if (Quests.Get(id) is not { } quest || !CanTakeQuest(state, quest, [], visibilityOnly: true)
                    || (Options.HighLevelHideDiff >= 0 && (long)player.Level + Options.HighLevelHideDiff < quest.MinLevel))
                {
                    continue;
                }

                DialogStatus candidate = player.Level < quest.MinLevel ? DialogStatus.Unavailable
                    : quest.IsAutoComplete || (quest.IsRepeatable && !quest.HasFlag(QuestFlags.Unk2) && state.Quests.Get(id)?.Rewarded == true)
                        ? DialogStatus.RewardRep
                    : Options.LowLevelHideDiff < 0 || player.Level <= (long)QuestLevelForPlayer(player, quest) + Options.LowLevelHideDiff
                        ? DialogStatus.Available : DialogStatus.Chat;
                if ((byte)candidate > (byte)status)
                {
                    status = candidate;
                }
            }
        }

        // Quest.cpp and gtker quest/smsg_questgiver_status.wowm: GUID followed by u32 status.
        var body = new PacketWriter(12);
        body.WriteUInt64(guid.Value);
        body.WriteUInt32((uint)status);
        Send(player, WorldOpcode.SmsgQuestgiverStatus, body);
    }

    /// <summary>vmangos Player::GetQuestLevelForPlayer (Player.h:1114): the quest's level when positive, else the player's own (14 classic-db quests have level 0).</summary>
    private static int QuestLevelForPlayer(Player player, Quest quest) => quest.QuestLevel > 0 ? quest.QuestLevel : player.Level;

    public void QuestgiverQueryQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (Ready(player) is null || InteractableNpc(player, guid, NpcFlags.QuestGiver) is not { } npc
            || Quests.Get(questId) is not { IsActive: true } quest
            || !(StartersOf(npc).Contains(questId) || EndersOf(npc).Contains(questId)))
        {
            return;
        }

        Send(player, WorldOpcode.SmsgQuestgiverQuestDetails, QuestPackets.Details(guid, quest, Options.RateDropMoney,
            id => Deps.Items?.GetItem(id)?.DisplayId ?? player.Inventory.Templates.Find(id)?.DisplayId ?? 0));
    }

    /// <summary>
    /// CMSG_QUESTGIVER_ACCEPT_QUEST (vmangos HandleQuestgiverAcceptQuestOpcode, QuestHandler.cpp:108-196). A
    /// refusal from CanTakeQuest sends SMSG_QUESTGIVER_QUEST_INVALID with the reason of the first failing
    /// check; a full log sends SMSG_QUESTLOG_FULL; a source item that cannot be given sends
    /// SMSG_QUESTGIVER_QUEST_FAILED. The gossip window closes in every case and the player's pending share offer is
    /// consumed (QuestHandler.cpp:131-189). The giver is a quest NPC or game object, or the player who shares the
    /// quest (<see cref="AcceptFromPlayer"/>). Returns true only after creating a journal entry and handing its
    /// delta to persistence.
    /// </summary>
    public bool AcceptQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (Ready(player) is not { } state)
        {
            return false;
        }

        bool accepted = false;
        if (guid.IsPlayer)
        {
            accepted = AcceptFromPlayer(player, state, guid, questId);
        }
        else if (InteractableNpc(player, guid, NpcFlags.QuestGiver) is { } npc && Quests.Get(questId) is { } quest
            && StartersOf(npc).Contains(questId))
        {
            accepted = AddQuestFrom(player, state, quest, sharedTimerEnd: null);
        }

        if (accepted && Quests.Get(questId) is { } taken && taken.HasFlag(QuestFlags.PartyAccept))
        {
            // The fan-out belongs to the accept handler alone (QuestHandler.cpp:166-191); AddQuest and the confirm handler (332-381) have none.
            OfferPartyAccept(player, taken);
        }

        ClearShareInfo(player);
        CloseGossip(player);
        if (accepted && Quests.Get(questId) is { Template.SrcSpell: not 0 } sourceSpell && Deps.SpellCaster is { } spellCaster)
        {
            // vmangos HandleQuestgiverAcceptQuestOpcode (QuestHandler.cpp:202-203): the source spell is cast on the player
            // after the quest is in the log and the gossip window is closed.
            spellCaster.CastOnSelf(player, sourceSpell.Template.SrcSpell);
        }

        return accepted;
    }

    /// <summary>
    /// The common part of every accept (vmangos CanTakeQuest(msg), CanAddQuest(msg), AddQuest, then
    /// the completion check, QuestHandler.cpp:131-196 and Player.cpp:12820-12934; the party fan-out is the caller's, see <see cref="AcceptQuest"/>). A refusal sends its message and returns
    /// false. <paramref name="sharedTimerEnd"/> is the sharer's timer end for a shared timed quest (Player.cpp:12855-12860).
    /// </summary>
    private bool AddQuestFrom(Player player, PlayerNpcState state, Quest quest, long? sharedTimerEnd)
    {
        if (RefuseTakeQuest(state, quest, []) is { } refusal)
        {
            if (refusal.Message is { } reason)
            {
                Send(player, WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(reason));
            }

            return false;
        }

        if (!((state.Quests.Get(quest.Id)?.Rewarded != true || quest.IsRepeatable) && AcceptableQuest(quest)))
        {
            return false;
        }

        int slot = state.Quests.FindSlot(0);
        if (slot == QuestConstants.MaxQuestLogSize)
        {
            player.Session.Send(WorldOpcode.SmsgQuestlogFull, []);
            return false;
        }

        if (!CanReceiveSourceItem(player, quest))
        {
            return false;
        }

        QuestStatusData data = state.Quests.GetOrAdd(quest.Id);
        data.Status = QuestStatus.Incomplete;
        data.Explored = false;
        Array.Clear(data.CreatureOrGOCount);
        Array.Clear(data.ItemCount);
        data.TimerEndUnix = !quest.HasSpecialFlag(QuestSpecialFlags.Timed) ? 0
            : sharedTimerEnd is { } shared ? checked(UnixNow + Math.Max(0, shared - UnixNow))
            : checked(UnixNow + quest.Template.LimitTime);
        if (data.TimerEndUnix != 0)
        {
            state.Quests.AddTimed(quest.Id);
        }

        state.Quests.SetSlot(slot, quest.Id, (uint)Math.Clamp(data.TimerEndUnix, 0, uint.MaxValue));
        if (quest.Template.Type == QuestNeeds.PvpType)
        {
            // vmangos Player::AddQuest (Player.cpp:12866-12867): a PvP quest flags the player.
            MapCombat.UpdatePvp(player, true);
        }

        state.Quests.MarkChanged(quest.Id);
        GiveSourceItem(player, quest);
        AdjustRequiredItemCounts(state, quest, data);
        RefreshCompletion(state, quest, data, slot);
        Flush(state);
        return true;
    }

    /// <summary>
    /// CMSG_QUESTLOG_REMOVE_QUEST (vmangos RemoveQuestAtSlot, Player.cpp:13033-13066): the source item is taken
    /// back first (and the abandon refused if it cannot come off), quest-bound required items are destroyed,
    /// then the quest leaves the log.
    /// </summary>
    public bool AbandonQuest(Player player, byte slot)
    {
        if (Ready(player) is not { } state || slot >= QuestConstants.MaxQuestLogSize
            || Quests.Get(state.Quests.SlotQuestId(slot)) is not { } quest || !AcceptableQuest(quest)
            || state.Quests.Get(quest.Id) is not { } data)
        {
            return false;
        }

        if (!TakeOrReplaceQuestStartItems(player, quest))
        {
            return false;
        }

        DestroyQuestBoundRequiredItems(player, quest);
        data.Status = QuestStatus.None;
        data.TimerEndUnix = 0;
        state.Quests.RemoveTimed(quest.Id);
        state.Quests.SetSlot(slot, 0);
        state.Quests.MarkChanged(quest.Id);
        Flush(state);
        return true;
    }

    /// <summary>CMSG_QUESTGIVER_CANCEL (vmangos HandleQuestgiverCancel, QuestHandler.cpp:314-317): CloseGossip.</summary>
    public void QuestgiverCancel(Player player)
    {
        if (Ready(player) is not null)
        {
            CloseGossip(player);
        }
    }

    /// <summary>
    /// CMSG_QUESTLOG_SWAP_QUEST (vmangos HandleQuestLogSwapQuest, QuestHandler.cpp:319-325): equal or
    /// out-of-range slots are ignored; otherwise the three slot fields trade places.
    /// </summary>
    public void SwapQuestSlots(Player player, byte slot1, byte slot2)
    {
        if (Ready(player) is not { } state || slot1 == slot2 || slot1 >= QuestConstants.MaxQuestLogSize || slot2 >= QuestConstants.MaxQuestLogSize)
        {
            return;
        }

        state.Quests.SwapSlots(slot1, slot2);
    }

}
