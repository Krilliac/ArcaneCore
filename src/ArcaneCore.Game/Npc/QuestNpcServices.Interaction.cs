using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Creature quest status, details, acceptance and abandonment. Reimplemented from vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 Handlers/QuestHandler.cpp and Objects/Player.cpp
/// CanSeeStartQuest, AddQuest, RemoveQuestAtSlot. Item/source-spell/party/PvP side effects require
/// their real adapters, so this tranche accepts only ordinary quests without those dependencies.
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
            foreach (uint id in Quests.EndersOf(npc.Entry))
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

            foreach (uint id in Quests.StartersOf(npc.Entry))
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
            || !(Quests.StartersOf(npc.Entry).Contains(questId) || Quests.EndersOf(npc.Entry).Contains(questId)))
        {
            return;
        }

        Send(player, WorldOpcode.SmsgQuestgiverQuestDetails, QuestPackets.Details(guid, quest, Options.RateDropMoney,
            id => Deps.Items?.GetItem(id)?.DisplayId ?? player.Inventory.Templates.Find(id)?.DisplayId ?? 0));
    }

    /// <summary>Returns true only after creating a journal entry and handing its delta to persistence.</summary>
    public bool AcceptQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (Ready(player) is not { } state)
        {
            return false;
        }

        bool accepted = false;
        if (InteractableNpc(player, guid, NpcFlags.QuestGiver) is { } npc && Quests.Get(questId) is { } quest
            && Quests.StartersOf(npc.Entry).Contains(questId) && CanTakeQuest(state, quest, [])
            && state.Quests.Get(questId)?.Rewarded != true
            && JournalOnlyQuest(quest))
        {
            int slot = state.Quests.FindSlot(0);
            if (slot == QuestConstants.MaxQuestLogSize)
            {
                player.Session.Send(WorldOpcode.SmsgQuestlogFull, []);
            }
            else
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
                state.Quests.MarkChanged(questId);
                RefreshCompletion(state, quest, data, slot);
                Flush(state);
                accepted = true;
            }
        }

        CloseGossip(player);
        return accepted;
    }

    public bool AbandonQuest(Player player, byte slot)
    {
        if (Ready(player) is not { } state || slot >= QuestConstants.MaxQuestLogSize
            || Quests.Get(state.Quests.SlotQuestId(slot)) is not { } quest || !JournalOnlyQuest(quest)
            || state.Quests.Get(quest.Id) is not { } data)
        {
            return false;
        }

        data.Status = QuestStatus.None;
        data.TimerEndUnix = 0;
        state.Quests.RemoveTimed(quest.Id);
        state.Quests.SetSlot(slot, 0);
        state.Quests.MarkChanged(quest.Id);
        Flush(state);
        return true;
    }

    // Start-item grants/removal, deliver counters, reputation visibility, party confirmation,
    // PvP activation and auto rewards cannot be reproduced by a journal mutation alone.
    private static bool JournalOnlyQuest(Quest quest) => quest.Template.Type == 0 && !quest.IsRepeatable
        && quest.Template.SrcItemId == 0 && quest.Template.SrcSpell == 0 && quest.Template.RepObjectiveFaction == 0
        && quest.ReqItemId.All(id => id == 0) && quest.ReqSourceId.All(id => id == 0)
        && !quest.HasFlag(QuestFlags.PartyAccept | QuestFlags.AutoRewarded);
}
