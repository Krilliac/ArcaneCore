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
        foreach (uint id in EndersOf(npc))
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

        foreach (uint id in StartersOf(npc))
        {
            if (Quests.Get(id) is { } quest && CanTakeQuest(state, quest, []))
            {
                state.Menu.AddQuestItem(id, quest.IsAutoComplete ? DialogStatus.RewardRep : DialogStatus.Available);
            }
        }
    }

    private bool CanTakeQuest(PlayerNpcState state, Quest quest, HashSet<uint> visited, bool visibilityOnly = false)
        => RefuseTakeQuest(state, quest, visited, visibilityOnly) is null;

    /// <summary>A refused CanTakeQuest: the message vmangos sends (SendCanTakeQuestResponse), or none.</summary>
    private readonly record struct TakeRefusal(QuestInvalidReason? Message);

    private static readonly TakeRefusal Silent = new(null);

    private static TakeRefusal Refused(QuestInvalidReason reason) => new(reason);

    /// <summary>
    /// vmangos Player::CanTakeQuest (Player.cpp:12565-12577): null when the quest can be taken, otherwise
    /// the first failing check in vmangos's order, with the message its SatisfyQuest* sends. MaxLevel and
    /// IsActive refuse silently. <paramref name="visibilityOnly"/> skips the level and timed checks (the
    /// quest-giver status icon applies its own level handling). <paramref name="skipStatusCheck"/> is vmangos's
    /// skipStatusCheck: the autocomplete turn-in ignores the quest's current status (Player.cpp:12684-12698).
    /// </summary>
    private TakeRefusal? RefuseTakeQuest(PlayerNpcState state, Quest quest, HashSet<uint> visited, bool visibilityOnly = false,
        bool skipStatusCheck = false)
    {
        Player player = state.Quests.Player;
        QuestTemplate t = quest.Template;
        if (!visited.Add(quest.Id))
        {
            return Silent;
        }

        if (!visibilityOnly && t.MaxLevel != 0 && t.MaxLevel < player.Level)
        {
            return Silent;
        }

        // SatisfyQuestStatus (13532-13545)
        if (!skipStatusCheck && state.Quests.GetStatus(quest.Id) != QuestStatus.None)
        {
            return Refused(QuestInvalidReason.AlreadyOn);
        }

        // SatisfyQuestExclusiveGroup (13560-13592)
        if (t.ExclusiveGroup > 0 && Quests.ExclusiveGroup(t.ExclusiveGroup).Any(id => id != quest.Id
            && state.Quests.GetStatus(id) is QuestStatus.Incomplete or QuestStatus.Complete))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        // SatisfyQuestClass (13473-13489), SatisfyQuestRace (13491-13507)
        if (t.RequiredClasses != 0 && (t.RequiredClasses & Mask((byte)player.Class)) == 0)
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        if (t.RequiredRaces != 0 && (t.RequiredRaces & Mask((byte)player.Race)) == 0)
        {
            return Refused(QuestInvalidReason.WrongRace);
        }

        // SatisfyQuestLevel (13319-13330): the message is DONT_HAVE_REQ, not LOW_LEVEL.
        if (!visibilityOnly && player.Level < t.MinLevel)
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        // SatisfyQuestSkill (13285-13303), SatisfyQuestCondition (13305-13317)
        if (t.RequiredSkill != 0 && (Deps.Spells is not { } skills || skills.GetSkillValue(player, t.RequiredSkill) < t.RequiredSkillValue))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        if (t.RequiredCondition != 0 && !(Deps.Conditions?.IsSatisfied(t.RequiredCondition, player, null) ?? false))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        // SatisfyQuestReputation (13509-13530)
        if ((t.RequiredMinRepFaction != 0 && (Deps.Reputation is not { } minRep
                || minRep.GetReputation(player, t.RequiredMinRepFaction) < t.RequiredMinRepValue))
            || (t.RequiredMaxRepFaction != 0 && (Deps.Reputation is not { } maxRep
                || maxRep.GetReputation(player, t.RequiredMaxRepFaction) >= t.RequiredMaxRepValue)))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        // SatisfyQuestPreviousQuest (13345-13435)
        if (quest.PrevQuests.Count > 0 && !quest.PrevQuests.Any(signedId => PreviousQuestSatisfied(state, signedId)))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        // SatisfyQuestTimed (13547-13558)
        if (!visibilityOnly && quest.HasSpecialFlag(QuestSpecialFlags.Timed) && state.Quests.TimedQuests.Count > 0)
        {
            return Refused(QuestInvalidReason.OnlyOneTimed);
        }

        // SatisfyQuestNextChain (13594-13614), SatisfyQuestPrevChain (13616-13644)
        if (quest.NextQuestInChain != 0 && state.Quests.GetStatus(quest.NextQuestInChain) is QuestStatus.Incomplete or QuestStatus.Complete)
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        if (quest.PrevChainQuests.Any(state.Quests.IsCurrent))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        // SatisfyQuestBreadcrumbQuest (13437-13453), SatisfyQuestDependentBreadcrumbQuests (13455-13471)
        if (quest.BreadcrumbForQuestId != 0 && !(Quests.Get(quest.BreadcrumbForQuestId) is { } target
            && CanTakeQuest(state, target, visited)))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        if (quest.DependentBreadcrumbQuests.Any(id => state.Quests.Get(id) is { Rewarded: false,
            Status: QuestStatus.Incomplete or QuestStatus.Complete or QuestStatus.Failed }))
        {
            return Refused(QuestInvalidReason.DontHaveReq);
        }

        return quest.IsActive ? null : Silent;
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
        Player player = state.Quests.Player;
        uint Display(uint id) => Deps.Items?.GetItem(id)?.DisplayId
            ?? player.Inventory.Templates.Find(id)?.DisplayId ?? 0;
        bool complete = CanDisplayReward(state, quest);
        if (item.Icon is DialogStatus.RewardRep or DialogStatus.Incomplete || (quest.IsRepeatable && complete))
        {
            if (quest.RequestItemsText.Length == 0 || (quest.ReqItemsCount == 0 && complete))
            {
                Send(state.Quests.Player, WorldOpcode.SmsgQuestgiverOfferReward,
                    QuestPackets.OfferReward(npc.Guid, quest, Options.RateDropMoney, Display));
            }
            else
            {
                Send(state.Quests.Player, WorldOpcode.SmsgQuestgiverRequestItems,
                    QuestPackets.RequestItems(npc.Guid, quest, complete, Display));
            }
        }
        else
        {
            Send(state.Quests.Player, WorldOpcode.SmsgQuestgiverQuestDetails,
                QuestPackets.Details(npc.Guid, quest, Options.RateDropMoney, Display));
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
                && InventoryCount(state.Quests.Player, quest.ReqItemId[i]) < quest.ReqItemCount[i])
            {
                return false;
            }
        }

        return true;
    }
}
