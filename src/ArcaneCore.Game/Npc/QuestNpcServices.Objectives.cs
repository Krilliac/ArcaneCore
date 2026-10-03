using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Quest objective and timer rules, reimplemented from vmangos Objects/Player.cpp
/// KilledMonsterCredit, CastedCreatureOrGO, TalkedToCreature, ItemAddedQuestCheck,
/// ItemRemovedQuestCheck, AreaExploredOrEventHappens, MoneyChanged and FailQuest.
/// Packet bodies: vmangos Server/Packets/Quest.cpp (build 5875).
/// </summary>
public sealed partial class QuestNpcServices
{
    public void KilledMonsterCredit(Player player, uint entry, ObjectGuid guid)
        => CreditCreature(player, entry, guid, true, 0, false);

    public void CastedCreatureOrGo(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId)
        => CreditCreature(player, entry, guid, isCreature, spellId, false);

    public void TalkedToCreature(Player player, uint entry, ObjectGuid guid)
    {
        if (Ready(player) is { } state)
        {
            TalkedToCreature(state, entry, guid);
            Flush(state);
        }
    }

    private void TalkedToCreature(PlayerNpcState state, uint entry, ObjectGuid guid)
        => CreditCreature(state, entry, guid, true, 0, true);

    private void CreditCreature(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId, bool talking)
    {
        if (Ready(player) is { } state)
        {
            CreditCreature(state, entry, guid, isCreature, spellId, talking);
            Flush(state);
        }
    }

    private void CreditCreature(PlayerNpcState state, uint entry, ObjectGuid guid, bool isCreature, uint spellId, bool talking,
        bool inRaidGroup = false, bool originalCaster = true)
    {
        if (entry == 0)
        {
            return;
        }

        bool kill = isCreature && spellId == 0 && !talking;
        foreach ((Quest quest, QuestStatusData data, int slot) in LoggedQuests(state))
        {
            if (data.Status != QuestStatus.Incomplete || !quest.HasSpecialFlag(QuestSpecialFlags.KillOrCast)
                || (talking && !quest.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent))
                || (kill && inRaidGroup && !IsAllowedInRaid(quest))
                || (!originalCaster && !quest.HasFlag(QuestFlags.Sharable)))
            {
                continue;
            }

            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                int target = quest.ReqCreatureOrGOId[i];
                if (quest.ReqSpell[i] != spellId || (isCreature ? target <= 0 : target >= 0)
                    || (uint)Math.Abs((long)target) != entry || data.CreatureOrGOCount[i] >= quest.ReqCreatureOrGOCount[i])
                {
                    continue;
                }

                uint count = ++data.CreatureOrGOCount[i];
                state.Quests.SetSlotCounter(slot, i, count);
                state.Quests.MarkChanged(quest.Id);
                var body = new PacketWriter(24);
                body.WriteUInt32(quest.Id);
                body.WriteUInt32(isCreature ? entry : entry | 0x80000000u);
                body.WriteUInt32(count);
                body.WriteUInt32(quest.ReqCreatureOrGOCount[i]);
                body.WriteUInt64(guid.Value);
                Send(state.Quests.Player, WorldOpcode.SmsgQuestupdateAddKill, body);
                RefreshCompletion(state, quest, data, slot);
                if (spellId != 0 || !isCreature)
                {
                    break;
                }
            }
        }
    }

    public void ItemAdded(Player player, uint entry, uint count)
    {
        if (Ready(player) is not { } state || entry == 0 || count == 0)
        {
            return;
        }

        foreach ((Quest quest, QuestStatusData data, int slot) in LoggedQuests(state))
        {
            if (data.Status != QuestStatus.Incomplete || !quest.HasSpecialFlag(QuestSpecialFlags.Deliver))
            {
                continue;
            }

            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (quest.ReqItemId[i] != entry)
                {
                    continue;
                }

                uint previous = data.ItemCount[i];
                data.ItemCount[i] = (uint)Math.Min((ulong)previous + count, quest.ReqItemCount[i]);
                if (data.ItemCount[i] != previous)
                {
                    state.Quests.MarkChanged(quest.Id);
                    if (entry != quest.Template.SrcItemId)
                    {
                        var body = new PacketWriter(8);
                        body.WriteUInt32(entry);
                        body.WriteUInt32(data.ItemCount[i] - previous);
                        Send(player, WorldOpcode.SmsgQuestupdateAddItem, body);
                    }
                }

                RefreshCompletion(state, quest, data, slot);
                break;
            }
        }

        Flush(state);
    }

    public void ItemRemoved(Player player, uint entry, uint count)
    {
        if (Ready(player) is not { } state || entry == 0 || count == 0)
        {
            return;
        }

        foreach ((Quest quest, QuestStatusData data, int slot) in LoggedQuests(state))
        {
            if (!Pending(quest, data))
            {
                continue;
            }

            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                if (quest.ReqItemId[i] != entry)
                {
                    continue;
                }

                // Raised after removal, so the inventory count is already the remaining amount.
                uint remaining = Deps.Items is not null || player.Inventory.IsLoaded
                    ? InventoryCount(player, entry)
                    : (data.ItemCount[i] > count ? data.ItemCount[i] - count : 0);
                uint value = Math.Min(remaining, quest.ReqItemCount[i]);
                if (value != data.ItemCount[i])
                {
                    data.ItemCount[i] = value;
                    state.Quests.MarkChanged(quest.Id);
                    RefreshCompletion(state, quest, data, slot);
                }

                break;
            }
        }

        Flush(state);
    }

    public void AreaExploredOrEventHappens(Player player, uint questId)
    {
        if (Ready(player) is not { } state || Quests.Get(questId) is not { } quest
            || state.Quests.Get(questId) is not { Status: QuestStatus.Incomplete } data)
        {
            return;
        }

        int slot = state.Quests.FindSlot(questId);
        if (slot >= QuestConstants.MaxQuestLogSize)
        {
            return;
        }

        if (!data.Explored)
        {
            data.Explored = true;
            state.Quests.SetSlotState(slot, QuestConstants.SlotStateComplete);
            state.Quests.MarkChanged(questId);
            Send(player, WorldOpcode.SmsgQuestupdateComplete, QuestIdBody(questId));
        }

        RefreshCompletion(state, quest, data, slot);
        Flush(state);
    }

    public void MoneyChanged(Player player)
    {
        if (Ready(player) is { } state)
        {
            MoneyChanged(state, player.Money);
            Flush(state);
        }
    }

    private void MoneyChanged(PlayerNpcState state, uint money)
    {
        foreach ((Quest quest, QuestStatusData data, int slot) in LoggedQuests(state))
        {
            if (quest.Template.RewOrReqMoney < 0)
            {
                RefreshCompletion(state, quest, data, slot);
            }
        }
    }

    /// <summary>vmangos Player::Update timed-quest expiry; also checked immediately after loading.</summary>
    public void CheckTimers(Player player)
    {
        if (Ready(player) is not { } state)
        {
            return;
        }

        foreach (uint questId in state.Quests.TimedQuests.ToArray())
        {
            if (state.Quests.Get(questId) is { } data && data.TimerEndUnix <= UnixNow)
            {
                FailQuest(state, questId);
            }
        }

        Flush(state);
    }

    public void FailQuest(Player player, uint questId)
    {
        if (Ready(player) is { } state)
        {
            FailQuest(state, questId);
            Flush(state);
        }
    }

    private void FailQuest(PlayerNpcState state, uint questId)
    {
        if (Quests.Get(questId) is not { } quest || state.Quests.Get(questId) is not { } data
            || !Pending(quest, data))
        {
            return;
        }

        bool timed = quest.HasSpecialFlag(QuestSpecialFlags.Timed);
        int slot = state.Quests.FindSlot(questId);
        data.Status = quest.IsRepeatable && !timed ? QuestStatus.None : QuestStatus.Failed;
        data.TimerEndUnix = 0;
        state.Quests.RemoveTimed(questId);
        state.Quests.MarkChanged(questId);
        if (slot < QuestConstants.MaxQuestLogSize)
        {
            if (data.Status == QuestStatus.None)
            {
                state.Quests.SetSlot(slot, 0);
            }
            else
            {
                state.Quests.SetSlotTimer(slot, 1);
                state.Quests.SetSlotState(slot, QuestConstants.SlotStateFail);
            }
        }

        Send(state.Quests.Player, timed ? WorldOpcode.SmsgQuestupdateFailedtimer : WorldOpcode.SmsgQuestupdateFailed, QuestIdBody(questId));
    }

    private IEnumerable<(Quest Quest, QuestStatusData Data, int Slot)> LoggedQuests(PlayerNpcState state)
    {
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint questId = state.Quests.SlotQuestId(slot);
            if (Quests.Get(questId) is { } quest && state.Quests.Get(questId) is { } data)
            {
                yield return (quest, data, slot);
            }
        }
    }

    private void RefreshCompletion(PlayerNpcState state, Quest quest, QuestStatusData data, int slot)
    {
        if (!Pending(quest, data))
        {
            return;
        }

        bool complete = ObjectivesComplete(state.Quests.Player, quest, data);
        QuestStatus status = complete ? QuestStatus.Complete : QuestStatus.Incomplete;
        if (data.Status != status)
        {
            data.Status = status;
            state.Quests.MarkChanged(quest.Id);
            if (complete)
            {
                state.Quests.SetSlotState(slot, QuestConstants.SlotStateComplete);
            }
            else
            {
                state.Quests.RemoveSlotState(slot, QuestConstants.SlotStateComplete);
            }
        }
    }

    /// <summary>vmangos CanCompleteQuest objective checks for an already accepted quest.</summary>
    private bool ObjectivesComplete(Player player, Quest quest, QuestStatusData data)
    {
        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            if (data.ItemCount[i] < quest.ReqItemCount[i]
                || (quest.ReqCreatureOrGOId[i] != 0 && data.CreatureOrGOCount[i] < quest.ReqCreatureOrGOCount[i]))
            {
                return false;
            }
        }

        return (!quest.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent) || data.Explored)
            && (!quest.HasSpecialFlag(QuestSpecialFlags.Timed) || data.TimerEndUnix > UnixNow)
            && (quest.Template.RewOrReqMoney >= 0 || player.Money >= -(long)quest.Template.RewOrReqMoney)
            && (quest.Template.RepObjectiveFaction == 0 || (Deps.Reputation is { } rep
                && rep.GetReputation(player, quest.Template.RepObjectiveFaction) >= quest.Template.RepObjectiveValue));
    }

    private static PacketWriter QuestIdBody(uint questId)
    {
        var body = new PacketWriter(4);
        body.WriteUInt32(questId);
        return body;
    }
}
