using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Npc;

/// <summary>Quest menus: vmangos Objects/Player.cpp PrepareQuestMenu/SendPreparedQuest and SatisfyQuest*.</summary>
public sealed partial class QuestNpcServices
{
    private void PrepareQuestMenu(PlayerNpcState state, NpcInfo npc)
    {
        state.Menu.ClearQuestMenu();
        foreach (uint id in Quests.EndersOf(npc.Entry))
        {
            if (Quests.Get(id) is not { IsActive: true } quest)
            {
                continue;
            }

            DialogStatus icon = state.Quests.GetStatus(id) switch
            {
                QuestStatus.Complete when !state.Quests.RewardStatus(quest) => DialogStatus.RewardRep,
                QuestStatus.Incomplete => DialogStatus.Incomplete,
                QuestStatus.Available => DialogStatus.Chat,
                _ => DialogStatus.None,
            };
            if (icon != DialogStatus.None)
            {
                state.Menu.AddQuestItem(id, icon);
            }
        }

        foreach (uint id in Quests.StartersOf(npc.Entry))
        {
            if (Quests.Get(id) is { } quest && CanTakeQuest(state, quest, []))
            {
                state.Menu.AddQuestItem(id, quest.IsAutoComplete ? DialogStatus.RewardRep : DialogStatus.Available);
            }
        }
    }

    private bool CanTakeQuest(PlayerNpcState state, Quest quest, HashSet<uint> visited)
    {
        Player player = state.Quests.Player;
        QuestTemplate t = quest.Template;
        if (!visited.Add(quest.Id) || !quest.IsActive || state.Quests.GetStatus(quest.Id) != QuestStatus.None
            || player.Level < t.MinLevel || (t.MaxLevel != 0 && player.Level > t.MaxLevel)
            || (t.RequiredClasses != 0 && (t.RequiredClasses & Mask((byte)player.Class)) == 0)
            || (t.RequiredRaces != 0 && (t.RequiredRaces & Mask((byte)player.Race)) == 0)
            || (t.RequiredSkill != 0 && (Deps.Spells?.GetSkillValue(player, t.RequiredSkill) ?? 0) < t.RequiredSkillValue)
            || (t.RequiredCondition != 0 && !(Deps.Conditions?.IsSatisfied(t.RequiredCondition, player, null) ?? false))
            || (quest.HasSpecialFlag(QuestSpecialFlags.Timed) && state.Quests.TimedQuests.Count > 0)
            || (t.RequiredMinRepFaction != 0 && (Deps.Reputation is not { } minRep
                || minRep.GetReputation(player, t.RequiredMinRepFaction) < t.RequiredMinRepValue))
            || (t.RequiredMaxRepFaction != 0 && (Deps.Reputation is not { } maxRep
                || maxRep.GetReputation(player, t.RequiredMaxRepFaction) >= t.RequiredMaxRepValue)))
        {
            return false;
        }

        if (t.ExclusiveGroup > 0 && Quests.ExclusiveGroup(t.ExclusiveGroup).Any(id => id != quest.Id
            && state.Quests.GetStatus(id) is QuestStatus.Incomplete or QuestStatus.Complete))
        {
            return false;
        }

        if (quest.PrevChainQuests.Any(state.Quests.IsCurrent)
            || (quest.NextQuestInChain != 0 && state.Quests.GetStatus(quest.NextQuestInChain) is QuestStatus.Incomplete or QuestStatus.Complete)
            || quest.DependentBreadcrumbQuests.Any(id => state.Quests.Get(id) is { Rewarded: false,
                Status: QuestStatus.Incomplete or QuestStatus.Complete or QuestStatus.Failed }))
        {
            return false;
        }

        if (quest.PrevQuests.Count > 0 && !quest.PrevQuests.Any(signedId => PreviousQuestSatisfied(state, signedId)))
        {
            return false;
        }

        return quest.BreadcrumbForQuestId == 0 || (Quests.Get(quest.BreadcrumbForQuestId) is { } target
            && CanTakeQuest(state, target, visited));
    }

    private bool PreviousQuestSatisfied(PlayerNpcState state, int signedId)
    {
        uint id = (uint)Math.Abs((long)signedId);
        if (Quests.Get(id) is not { } previous)
        {
            return false;
        }

        bool Satisfied(uint questId) => signedId > 0 ? state.Quests.Get(questId)?.Rewarded == true : state.Quests.IsCurrent(questId);
        return Satisfied(id) && (previous.Template.ExclusiveGroup >= 0
            || Quests.ExclusiveGroup(previous.Template.ExclusiveGroup).All(Satisfied));
    }

    private void SendPreparedQuest(PlayerNpcState state, NpcInfo npc)
    {
        if (state.Menu.QuestItems.Count == 0)
        {
            return;
        }

        if (state.Menu.QuestItems.Count > 1)
        {
            Send(state.Quests.Player, WorldOpcode.SmsgQuestgiverQuestList,
                QuestPackets.List(npc.Guid, state.Menu.QuestItems.Select(i => (Quests.Get(i.QuestId)!, i.Icon)).ToArray()));
            return;
        }

        QuestMenuItem item = state.Menu.QuestItems[0];
        Quest quest = Quests.Get(item.QuestId)!;
        bool complete = CanDisplayReward(state, quest);
        if (item.Icon is DialogStatus.RewardRep or DialogStatus.Incomplete || (quest.IsRepeatable && complete))
        {
            if (quest.RequestItemsText.Length == 0 || (quest.ReqItemsCount == 0 && complete))
            {
                Send(state.Quests.Player, WorldOpcode.SmsgQuestgiverOfferReward,
                    QuestPackets.OfferReward(npc.Guid, quest, Options.RateDropMoney, DisplayOf));
            }
            else
            {
                Send(state.Quests.Player, WorldOpcode.SmsgQuestgiverRequestItems,
                    QuestPackets.RequestItems(npc.Guid, quest, complete, DisplayOf));
            }
        }
        else
        {
            Send(state.Quests.Player, WorldOpcode.SmsgQuestgiverQuestDetails,
                QuestPackets.Details(npc.Guid, quest, Options.RateDropMoney, DisplayOf));
        }
    }

    private bool CanDisplayReward(PlayerNpcState state, Quest quest)
    {
        if (state.Quests.RewardStatus(quest)
            || !(state.Quests.GetStatus(quest.Id) == QuestStatus.Complete
                || ((quest.IsAutoComplete || quest.IsRepeatable) && CanTakeQuest(state, quest, [])))
            || (quest.Template.RewOrReqMoney < 0 && state.Quests.Player.Money < -(long)quest.Template.RewOrReqMoney))
        {
            return false;
        }

        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            if (quest.ReqItemId[i] != 0
                && (Deps.Items?.GetItemCount(state.Quests.Player, quest.ReqItemId[i], false) ?? 0) < quest.ReqItemCount[i])
            {
                return false;
            }
        }

        return true;
    }
}
