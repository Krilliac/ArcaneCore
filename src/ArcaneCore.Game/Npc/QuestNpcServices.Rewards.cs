using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using ItemInventoryResult = ArcaneCore.Game.Items.InventoryResult;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Ordinary quest settlement: items, money, delivery-item removal, XP/level, repeatable history
/// and post-settlement reward effects. vmangos/core 4b3d241cffe245a1f68da11380bce96c23db48c0
/// QuestHandler.cpp and Player.cpp HandleQuestgiverCompleteQuestOpcode,
/// HandleQuestgiverRequestRewardOpcode, CanRewardQuest and RewardQuest. Only allowlisted quests
/// whose every requirement and reward has an adapter are offered; everything else fails closed.
/// </summary>
public sealed partial class QuestNpcServices
{
    /// <summary>
    /// CMSG_QUESTGIVER_COMPLETE_QUEST (vmangos HandleQuestgiverCompleteQuest, QuestHandler.cpp:383-397): the request-items
    /// window with the completable flag, redirected to the offer-reward window when the quest has no request text or no
    /// items and is complete (GossipDef.cpp:484-497). A repeatable quest not in the log is completable from the ender's
    /// list (CanCompleteRepeatableQuest). This request never credits an objective.
    /// </summary>
    public void CompleteQuest(Player player, ObjectGuid guid, uint questId)
    {
        if (!TryRewardQuest(player, guid, questId, out PlayerNpcState? state, out Quest? quest))
        {
            return;
        }

        bool complete = state.Quests.GetStatus(questId) != QuestStatus.Complete && quest.IsRepeatable
            ? CanCompleteRepeatableQuest(state, quest)
            : CanRewardQuest(state, quest, msg: false);
        SendRequestItems(player, guid, quest, complete, closeOnCancel: false);
    }

    /// <summary>
    /// CMSG_QUESTGIVER_REQUEST_REWARD (QuestHandler.cpp:283-312): an accepted quest must genuinely be complete before its
    /// reward selection is shown; an autocomplete quest only needs <c>CanTakeQuest</c> (CanCompleteQuest, Player.cpp:12612-12614).
    /// </summary>
    public void RequestReward(Player player, ObjectGuid guid, uint questId)
    {
        if (TryRewardQuest(player, guid, questId, out PlayerNpcState? state, out Quest? quest)
            && (quest.IsAutoComplete
                ? RewardBase(state, quest)
                : state.Quests.Get(questId) is { Status: QuestStatus.Complete } data && Pending(quest, data)
                    && ObjectivesComplete(player, quest, data)))
        {
            ResendOfferReward(player, guid, quest);
        }
    }

    /// <summary>Validate current progress, NPC interaction, choice, money and the complete inventory batch without live mutations.</summary>
    public bool TryPrepareReward(Player player, ObjectGuid guid, uint questId, uint choice,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out QuestRewardPlan? plan)
    {
        plan = null;
        if (!TryRewardQuest(player, guid, questId, out PlayerNpcState? state, out Quest? quest) || !RewardBase(state, quest))
        {
            return false;
        }

        // From here the quest is genuinely rewardable (vmangos CanRewardQuest base rules hold). A refusal for missing
        // items, money or bag space tells the client why and re-sends the offer window so the player can retry
        // (QuestHandler.cpp:262-271); the live state stays untouched.
        QuestStatusData? data = state.Quests.Get(questId);
        if (!RewardRequirements(player, quest, msg: true))
        {
            ResendOfferReward(player, guid, quest);
            return false;
        }

        if (!TryRewardGrants(quest, choice, out List<InventoryRewardGrant> grants, out uint chosenItem))
        {
            return false;
        }

        if (!TryRewardMoney(player, quest, out uint moneyAfter, out uint summaryMoney)
            || !TryRewardExperience(player, quest, out uint experience, out byte levelAfter)
            // Preflight refusals keep the reward available and mutate nothing: no hold, no journal change, and
            // the client simply keeps its offer window (no standard error packet exists for them).
            || !TryRewardSpell(player, guid, quest, out QuestRewardSpellGrant spellGrant)
            || !TryRewardReputation(player, quest, out QuestReputationStage reputation))
        {
            return false;
        }

        // vmangos RewardQuest stores the quest items first; items a reward spell creates follow them.
        grants.AddRange(spellGrant.CreatedItems.Select(item => new InventoryRewardGrant(item.Entry, item.Count, Created: true)));
        ItemInventoryResult result = player.Inventory.TryStageQuestRewards(grants, RequiredItemRemovals(quest),
            out InventoryRewardStage? stage, out uint failedEntry);
        if (result != ItemInventoryResult.Ok || stage is null)
        {
            SendRewardStageRefusal(player, questId, result, failedEntry);
            ResendOfferReward(player, guid, quest);
            return false;
        }

        // An autocomplete quest is claimed (or inserted) by the transaction whether or not it was ever in the log.
        bool autocomplete = quest.IsAutoComplete;
        if (autocomplete && data?.TimerEndUnix is not null and not 0)
        {
            return false;
        }

        CharacterQuestStatus expected = autocomplete ? VirtualRow(player, questId, data) : RewardRow(player, questId, data!);
        // vmangos RewardQuest: a repeatable returns to QUEST_STATUS_NONE (it may be taken again),
        // anything else stays COMPLETE; both remember that they were rewarded.
        plan = new QuestRewardPlan(this, player, guid, choice, moneyAfter, summaryMoney, experience, levelAfter, expected,
            expected with
            {
                Status = quest.IsRepeatable ? (byte)QuestStatus.None : expected.Status,
                Rewarded = true, Timer = 0, RewardChoice = chosenItem,
            }, stage, spellGrant, reputation, insertIfMissing: autocomplete);
        return true;
    }

    /// <summary>The reward spell (RewSpellCast, else RewSpell): grants resolved and preflight passed, or false.</summary>
    private bool TryRewardSpell(Player player, ObjectGuid questGiver, Quest quest, out QuestRewardSpellGrant grant)
    {
        grant = QuestRewardSpellGrant.None;
        uint spellId = RewardSpell(quest);
        return spellId == 0 || Deps.RewardEffects?.TryPrepareRewardSpell(player, questGiver, spellId, out grant) == true;
    }

    /// <summary>vmangos RewardReputation: only pairs with both a faction and a value count; they need the reputation owner.</summary>
    private static bool HasReputationReward(Quest quest)
    {
        for (int i = 0; i < quest.RewRepFaction.Count; i++)
        {
            if (quest.RewRepFaction[i] != 0 && quest.RewRepValue[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryRewardReputation(Player player, Quest quest, out QuestReputationStage stage)
    {
        stage = QuestReputationStage.Empty;
        if (!HasReputationReward(quest))
        {
            return true;
        }

        var rewards = new List<QuestReputationReward>(quest.RewRepFaction.Count);
        for (int i = 0; i < quest.RewRepFaction.Count; i++)
        {
            rewards.Add(new QuestReputationReward(quest.RewRepFaction[i], quest.RewRepValue[i]));
        }

        return Deps.ReputationRewards?.TryStage(player, quest.Template.QuestLevel, rewards, out stage) == true;
    }

    /// <summary>Publish a successfully committed plan. The caller must settle persistence while the world thread excludes intervening mutations.</summary>
    public void ApplyReward(QuestRewardPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Player player = plan.Player;
        if (!ReferenceEquals(plan.Services, this) || Ready(player) is not { } state
            || Quests.Get(plan.QuestId) is not { } quest
            || (plan.InsertIfMissing
                ? VirtualRow(player, plan.QuestId, state.Quests.Get(plan.QuestId))
                : state.Quests.Get(plan.QuestId) is { } live ? RewardRow(player, plan.QuestId, live) : null) != plan.ExpectedQuest
            || player.Money != plan.MoneyBefore || player.Level != plan.LevelBefore || !plan.Stage.MatchesBefore())
        {
            throw new InvalidOperationException("the quest reward plan no longer matches its live character");
        }

        // An autocomplete quest may never have been in the journal (no slot, perhaps no row yet).
        QuestStatusData data = state.Quests.GetOrAdd(plan.QuestId);
        int slot = state.Quests.FindSlot(plan.QuestId);
        if (slot >= QuestConstants.MaxQuestLogSize && !plan.InsertIfMissing)
        {
            throw new InvalidOperationException("the rewarded quest no longer occupies its journal slot");
        }

        player.Inventory.ApplyQuestRewardInventory(plan.Stage);
        player.Money = plan.MoneyAfter;
        data.Status = (QuestStatus)plan.RewardedQuest.Status;
        data.Rewarded = true;
        data.TimerEndUnix = 0;
        data.RewardChoice = plan.RewardedQuest.RewardChoice;
        state.Quests.RemoveTimed(quest.Id);
        if (slot < QuestConstants.MaxQuestLogSize)
        {
            state.Quests.SetSlot(slot, 0);
        }

        // The rewarded row was durably written by the transaction; it must never enter the
        // ordinary quest delta sink before that transaction. Other objectives may change below.
        player.Inventory.NotifyQuestRewardInventory(plan.Stage);
        MoneyChanged(state, player.Money);
        Flush(state);
        if (plan.Experience > 0 && Deps.Experience is { } experience)
        {
            // The committed level is authoritative; the live grant reproduces the preview.
            experience.GiveXp(player, plan.Experience);
            if (player.Level != plan.LevelAfter)
            {
                _logger.LogWarning("quest {Quest} XP produced level {Live}, settlement committed {Committed}",
                    quest.Id, player.Level, plan.LevelAfter);
            }
        }

        // Durable-state publications of the reward spell and the reputation reward: the rows were
        // committed with the journal, so these only update the live player and its client.
        if (plan.SpellGrant.LearnedSpells.Count > 0)
        {
            Deps.RewardEffects?.AnnounceLearnedSpells(player, plan.SpellGrant);
        }

        if (Deps.ReputationRewards is { } reputation && !reputation.Publish(player, plan.Reputation))
        {
            _logger.LogWarning("quest {Quest} reputation reward differed from its committed rows; the live rows were queued", quest.Id);
        }

        Send(player, WorldOpcode.SmsgQuestgiverQuestComplete,
            QuestPackets.Complete(quest, plan.Experience, plan.SummaryMoney));

        // vmangos Player::RewardQuest hands the quest to the player's battleground (Alterac Valley, Player.cpp:13093-13095) and the quest
        // giver's script (OnQuestRewarded); the owners subscribe here.
        QuestRewarded?.Invoke(player, plan.QuestGiver, quest);

        // vmangos HandleQuestgiverChooseRewardOpcode sends the next quest of the chain right after RewardQuest.
        OfferNextQuest(player, plan.QuestGiver, quest);
    }

    /// <summary>
    /// Effects that need a released character (spells skip settlement-held units): run after
    /// <see cref="Player.EndQuestSettlement"/>, once per published plan.
    /// </summary>
    public void PublishRewardEffects(QuestRewardPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (ReferenceEquals(plan.Services, this) && plan.Stage.Applied && !plan.EffectsPublished
            && Deps.RewardEffects is { } effects && Quests.Get(plan.QuestId) is { } quest
            && plan.Player.CanMutateQuestSettlementState)
        {
            plan.EffectsPublished = true;
            if (plan.SpellGrant.HasPostReleaseEffects)
            {
                effects.PublishRewardSpell(plan.Player, quest, plan.QuestGiver, plan.SpellGrant);
            }
        }
    }

    private bool TryRewardQuest(Player player, ObjectGuid guid, uint questId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PlayerNpcState? state,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Quest? quest)
    {
        state = Ready(player);
        quest = Quests.Get(questId);
        return state is not null && quest is not null && SupportedRewardQuest(quest)
            && player.Inventory.IsLoaded && player.Inventory.GuidAllocator is not null
            && (quest.IsAutoComplete || state.Quests.FindSlot(questId) < QuestConstants.MaxQuestLogSize)
            && InteractableNpc(player, guid, NpcFlags.QuestGiver) is { } npc
            && EndersOf(npc).Contains(questId);
    }

    /// <summary>vmangos RewardQuest: RewSpellCast wins over RewSpell.</summary>
    public static uint RewardSpell(Quest quest) => quest.Template.RewSpellCast != 0 ? quest.Template.RewSpellCast : quest.Template.RewSpell;

    /// <summary>Objective slots are either empty or complete; creature/GO counters fit the 6-bit slot field.</summary>
    private static bool CoherentObjectives(Quest quest)
    {
        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            if ((quest.ReqItemId[i] == 0) != (quest.ReqItemCount[i] == 0) || quest.ReqItemCount[i] > int.MaxValue)
            {
                return false;
            }

            int target = quest.ReqCreatureOrGOId[i];
            if (target == 0 ? quest.ReqCreatureOrGOCount[i] != 0 || quest.ReqSpell[i] != 0
                : quest.ReqCreatureOrGOCount[i] is 0 or > 63)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>vmangos RewardQuest destroys ReqItemId × ReqItemCount from the bags (not the bank) first.</summary>
    private static List<InventoryRewardGrant> RequiredItemRemovals(Quest quest)
    {
        var removals = new List<InventoryRewardGrant>();
        for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
        {
            if (quest.ReqItemId[i] != 0 && quest.ReqItemCount[i] != 0)
            {
                removals.Add(new InventoryRewardGrant(quest.ReqItemId[i], quest.ReqItemCount[i]));
            }
        }

        return removals;
    }

    /// <summary>
    /// vmangos RewardQuest XP: XPValue × Rate.XP.Quest below the maximum level (every completion,
    /// repeatables included), otherwise nothing (RewMoneyMaxLevel pays instead).
    /// </summary>
    private bool TryRewardExperience(Player player, Quest quest, out uint experience, out byte levelAfter)
    {
        experience = 0;
        levelAfter = player.Level;
        QuestXpSource xpSource = QuestExperienceRules.Resolve(Options.XpSource, Quests.HasRewXpColumn);
        if (QuestExperienceRules.FullXp(quest.Template, quest.QuestLevel, xpSource) <= 0)
        {
            return true;
        }

        if (Deps.Experience is not IQuestExperience progression)
        {
            return false;
        }

        if (player.Level >= progression.MaxPlayerLevel)
        {
            return true;
        }

        float scaled = QuestExperienceRules.Xp(quest.Template, quest.QuestLevel, player.Level, xpSource) * Options.RateXpQuest;
        if (!float.IsFinite(scaled) || scaled < 0 || scaled >= uint.MaxValue)
        {
            return false;
        }

        experience = (uint)scaled;
        levelAfter = progression.Preview(player.Level, progression.GetCurrentXp(player), experience).Level;
        return true;
    }

    private byte MaxQuestLevel => Deps.Experience is IQuestExperience progression
        ? progression.MaxPlayerLevel
        : (byte)Math.Min(Options.MaxPlayerLevel, byte.MaxValue);

    private static bool CoherentRewards(IReadOnlyList<uint> ids, IReadOnlyList<uint> counts, bool dense)
    {
        bool ended = false;
        for (int i = 0; i < ids.Count; i++)
        {
            if ((ids[i] == 0) != (counts[i] == 0) || counts[i] > int.MaxValue || (dense && ended && ids[i] != 0))
            {
                return false;
            }

            ended |= ids[i] == 0;
        }

        return true;
    }

    private static bool TryRewardGrants(Quest quest, uint choice, out List<InventoryRewardGrant> grants, out uint chosenItem)
    {
        grants = [];
        chosenItem = 0;
        for (int i = 0; i < QuestConstants.RewardsCount; i++)
        {
            if (quest.RewItemId[i] != 0)
            {
                grants.Add(new InventoryRewardGrant(quest.RewItemId[i], quest.RewItemCount[i]));
            }
        }

        if (quest.RewChoiceItemsCount == 0)
        {
            if (choice != 0)
            {
                return false;
            }
        }
        else
        {
            if (choice >= quest.RewChoiceItemsCount)
            {
                return false;
            }

            chosenItem = quest.RewChoiceItemId[(int)choice];
            grants.Add(new InventoryRewardGrant(chosenItem, quest.RewChoiceItemCount[(int)choice]));
        }

        return true;
    }

    private bool TryRewardMoney(Player player, Quest quest, out uint moneyAfter, out uint summaryMoney)
    {
        moneyAfter = player.Money;
        summaryMoney = 0;
        long delta = quest.Template.RewOrReqMoney;
        if (delta > 0)
        {
            float scaled = quest.Template.RewOrReqMoney * Options.RateDropMoney;
            if (!float.IsFinite(scaled) || scaled < 0 || scaled > (double)int.MaxValue)
            {
                return false;
            }

            delta = (long)scaled;
        }

        if (quest.Template.RewMoneyMaxLevel != 0 && player.Level >= MaxQuestLevel)
        {
            float scaled = quest.Template.RewMoneyMaxLevel * Options.RateDropMoney;
            if (!float.IsFinite(scaled) || scaled < 0 || scaled > (double)int.MaxValue)
            {
                return false;
            }

            delta += (long)scaled;
        }

        if (player.Money > MaxMoneyAmount || (delta < 0 && player.Money < -delta))
        {
            return false;
        }

        moneyAfter = (uint)Math.Clamp(player.Money + delta, 0, MaxMoneyAmount);
        summaryMoney = unchecked((uint)delta);
        return true;
    }

    private static Func<uint, uint> RewardDisplayOf(Player player) => id => player.Inventory.Templates.Find(id)?.DisplayId ?? 0;

    private static CharacterQuestStatus RewardRow(Player player, uint questId, QuestStatusData data) => new((int)player.Guid.Low,
        questId, (byte)data.Status, data.Rewarded, data.Explored, data.TimerEndUnix,
        data.CreatureOrGOCount[0], data.CreatureOrGOCount[1], data.CreatureOrGOCount[2], data.CreatureOrGOCount[3],
        data.ItemCount[0], data.ItemCount[1], data.ItemCount[2], data.ItemCount[3], data.RewardChoice);
}
