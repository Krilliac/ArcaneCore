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

        DialogStatus status = DialogStatus.None;
        if (!npc.IsHostile && (npc.NpcFlags & NpcFlags.QuestGiver) != 0)
        {
            foreach (uint id in EndersOf(npc))
            {
                if (Quests.Get(id) is not { IsActive: true } quest)
                {
                    continue;
                }

                DialogStatus candidate = state.Quests.GetStatus(id) switch
                {
                    QuestStatus.Complete when !state.Quests.RewardStatus(quest) => DialogStatus.Reward2,
                    QuestStatus.Incomplete => DialogStatus.Incomplete,
                    _ when quest.IsAutoComplete && CanTakeQuest(state, quest, []) => quest.IsRepeatable ? DialogStatus.RewardRep : DialogStatus.Reward2,
                    _ => DialogStatus.None,
                };
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
                    : Options.LowLevelHideDiff < 0 || player.Level <= (long)(quest.QuestLevel < 0 ? player.Level : quest.QuestLevel) + Options.LowLevelHideDiff
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
    /// SMSG_QUESTGIVER_QUEST_FAILED. The gossip window closes in every case. Returns true only after
    /// creating a journal entry and handing its delta to persistence.
    /// </summary>
    public bool AcceptQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (Ready(player) is not { } state)
        {
            return false;
        }

        bool accepted = false;
        if (InteractableNpc(player, guid, NpcFlags.QuestGiver) is { } npc && Quests.Get(questId) is { } quest
            && StartersOf(npc).Contains(questId))
        {
            if (RefuseTakeQuest(state, quest, []) is { } refusal)
            {
                if (refusal.Message is { } reason)
                {
                    Send(player, WorldOpcode.SmsgQuestgiverQuestInvalid, QuestPackets.QuestInvalid(reason));
                }
            }
            else if ((state.Quests.Get(questId)?.Rewarded != true || quest.IsRepeatable) && AcceptableQuest(quest))
            {
                int slot = state.Quests.FindSlot(0);
                if (slot == QuestConstants.MaxQuestLogSize)
                {
                    player.Session.Send(WorldOpcode.SmsgQuestlogFull, []);
                }
                else if (CanReceiveSourceItem(player, quest))
                {
                    QuestStatusData data = state.Quests.GetOrAdd(questId);
                    data.Status = QuestStatus.Incomplete;
                    data.Explored = false;
                    Array.Clear(data.CreatureOrGOCount);
                    Array.Clear(data.ItemCount);
                    data.TimerEndUnix = quest.HasSpecialFlag(QuestSpecialFlags.Timed) ? checked(UnixNow + quest.Template.LimitTime) : 0;
                    if (data.TimerEndUnix != 0)
                    {
                        state.Quests.AddTimed(questId);
                    }

                    state.Quests.SetSlot(slot, questId, (uint)Math.Clamp(data.TimerEndUnix, 0, uint.MaxValue));
                    if (quest.Template.Type == QuestNeeds.PvpType)
                    {
                        // vmangos Player::AddQuest (Player.cpp:12866-12867): a PvP quest flags the player.
                        MapCombat.UpdatePvp(player, true);
                    }

                    state.Quests.MarkChanged(questId);
                    GiveSourceItem(player, quest);
                    AdjustRequiredItemCounts(state, quest, data);
                    RefreshCompletion(state, quest, data, slot);
                    Flush(state);
                    accepted = true;
                }
            }
        }

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
