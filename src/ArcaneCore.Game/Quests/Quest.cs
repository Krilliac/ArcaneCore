using ArcaneCore.Kernel.Quests;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// A loaded quest: the quest_template row as arrays plus the data vmangos derives at load
/// (Quest ctor and ObjectMgr::LoadQuests): objective/reward counts, computed special flags,
/// the previous-quest lists and dependent breadcrumbs. Immutable after <see cref="QuestStore"/>
/// built it.
/// </summary>
public sealed class Quest
{
    private readonly List<int> _prevQuests = [];
    private readonly List<uint> _prevChainQuests = [];
    private readonly List<uint> _dependentBreadcrumbs = [];

    internal Quest(QuestTemplate t)
    {
        Template = t;
        Id = t.Entry;
        ReqItemId = [t.ReqItemId1, t.ReqItemId2, t.ReqItemId3, t.ReqItemId4];
        ReqItemCount = [t.ReqItemCount1, t.ReqItemCount2, t.ReqItemCount3, t.ReqItemCount4];
        ReqSourceId = [t.ReqSourceId1, t.ReqSourceId2, t.ReqSourceId3, t.ReqSourceId4];
        ReqSourceCount = [t.ReqSourceCount1, t.ReqSourceCount2, t.ReqSourceCount3, t.ReqSourceCount4];
        ReqCreatureOrGOId = [t.ReqCreatureOrGOId1, t.ReqCreatureOrGOId2, t.ReqCreatureOrGOId3, t.ReqCreatureOrGOId4];
        ReqCreatureOrGOCount = [t.ReqCreatureOrGOCount1, t.ReqCreatureOrGOCount2, t.ReqCreatureOrGOCount3, t.ReqCreatureOrGOCount4];
        ReqSpell = [t.ReqSpellCast1, t.ReqSpellCast2, t.ReqSpellCast3, t.ReqSpellCast4];
        ObjectiveText = [t.ObjectiveText1, t.ObjectiveText2, t.ObjectiveText3, t.ObjectiveText4];
        RewChoiceItemId = [t.RewChoiceItemId1, t.RewChoiceItemId2, t.RewChoiceItemId3, t.RewChoiceItemId4, t.RewChoiceItemId5, t.RewChoiceItemId6];
        RewChoiceItemCount = [t.RewChoiceItemCount1, t.RewChoiceItemCount2, t.RewChoiceItemCount3, t.RewChoiceItemCount4, t.RewChoiceItemCount5, t.RewChoiceItemCount6];
        RewItemId = [t.RewItemId1, t.RewItemId2, t.RewItemId3, t.RewItemId4];
        RewItemCount = [t.RewItemCount1, t.RewItemCount2, t.RewItemCount3, t.RewItemCount4];
        RewRepFaction = [t.RewRepFaction1, t.RewRepFaction2, t.RewRepFaction3, t.RewRepFaction4, t.RewRepFaction5];
        RewRepValue = [t.RewRepValue1, t.RewRepValue2, t.RewRepValue3, t.RewRepValue4, t.RewRepValue5];
        DetailsEmote = [t.DetailsEmote1, t.DetailsEmote2, t.DetailsEmote3, t.DetailsEmote4];
        DetailsEmoteDelay = [t.DetailsEmoteDelay1, t.DetailsEmoteDelay2, t.DetailsEmoteDelay3, t.DetailsEmoteDelay4];
        OfferRewardEmote = [t.OfferRewardEmote1, t.OfferRewardEmote2, t.OfferRewardEmote3, t.OfferRewardEmote4];
        OfferRewardEmoteDelay = [t.OfferRewardEmoteDelay1, t.OfferRewardEmoteDelay2, t.OfferRewardEmoteDelay3, t.OfferRewardEmoteDelay4];

        // vmangos Quest ctor: Method & QUEST_METHOD_DISABLED → inactive; the objective/reward counts.
        IsActive = (t.Method & QuestConstants.MethodDisabled) == 0;
        ReqItemsCount = ReqItemId.Count(i => i != 0);
        ReqCreatureOrGOCountTotal = ReqCreatureOrGOId.Count(i => i != 0);
        RewItemsCount = RewItemId.Count(i => i != 0);
        RewChoiceItemsCount = RewChoiceItemId.Count(i => i != 0);

        // vmangos ObjectMgr::LoadQuests: only the DB-allowed bits are taken from the table, then
        // DELIVER (any ReqItemId), KILL_OR_CAST|SPEAKTO (any ReqCreatureOrGOId), TIMED (LimitTime).
        QuestSpecialFlags flags = (QuestSpecialFlags)t.SpecialFlags & QuestSpecialFlags.DbAllowed;
        if (ReqItemsCount > 0)
        {
            flags |= QuestSpecialFlags.Deliver;
        }

        if (ReqCreatureOrGOCountTotal > 0)
        {
            flags |= QuestSpecialFlags.KillOrCast | QuestSpecialFlags.SpeakTo;
        }

        if (t.LimitTime != 0)
        {
            flags |= QuestSpecialFlags.Timed;
        }

        SpecialFlags = flags;
    }

    public QuestTemplate Template { get; }

    public uint Id { get; }

    public string Title => Template.Title;

    public string Details => Template.Details;

    public string Objectives => Template.Objectives;

    public string OfferRewardText => Template.OfferRewardText;

    public string RequestItemsText => Template.RequestItemsText;

    public string EndText => Template.EndText;

    public byte MinLevel => Template.MinLevel;

    public byte MaxLevel => Template.MaxLevel;

    public int QuestLevel => Template.QuestLevel;

    public QuestFlags Flags => (QuestFlags)Template.QuestFlags;

    public QuestSpecialFlags SpecialFlags { get; }

    public bool IsActive { get; }

    /// <summary>vmangos Quest::IsAutoComplete: Method 0.</summary>
    public bool IsAutoComplete => Template.Method == 0;

    /// <summary>vmangos Quest::IsRepeatable: SpecialFlags & QUEST_SPECIAL_FLAG_REPEATABLE.</summary>
    public bool IsRepeatable => HasSpecialFlag(QuestSpecialFlags.Repeatable);

    public IReadOnlyList<uint> ReqItemId { get; }

    public IReadOnlyList<uint> ReqItemCount { get; }

    public IReadOnlyList<uint> ReqSourceId { get; }

    public IReadOnlyList<uint> ReqSourceCount { get; }

    public IReadOnlyList<int> ReqCreatureOrGOId { get; }

    public IReadOnlyList<uint> ReqCreatureOrGOCount { get; }

    public IReadOnlyList<uint> ReqSpell { get; }

    public IReadOnlyList<string> ObjectiveText { get; }

    public IReadOnlyList<uint> RewChoiceItemId { get; }

    public IReadOnlyList<uint> RewChoiceItemCount { get; }

    public IReadOnlyList<uint> RewItemId { get; }

    public IReadOnlyList<uint> RewItemCount { get; }

    /// <summary>quest_template.RewRepFaction1..5 (vmangos QUEST_REPUTATIONS_COUNT).</summary>
    public IReadOnlyList<uint> RewRepFaction { get; }

    /// <summary>quest_template.RewRepValue1..5, parallel to <see cref="RewRepFaction"/>.</summary>
    public IReadOnlyList<int> RewRepValue { get; }

    public IReadOnlyList<uint> DetailsEmote { get; }

    public IReadOnlyList<uint> DetailsEmoteDelay { get; }

    public IReadOnlyList<uint> OfferRewardEmote { get; }

    public IReadOnlyList<uint> OfferRewardEmoteDelay { get; }

    /// <summary>vmangos m_reqitemscount.</summary>
    public int ReqItemsCount { get; }

    /// <summary>vmangos m_reqCreatureOrGOcount.</summary>
    public int ReqCreatureOrGOCountTotal { get; }

    /// <summary>vmangos m_rewitemscount.</summary>
    public int RewItemsCount { get; }

    /// <summary>vmangos m_rewchoiceitemscount.</summary>
    public int RewChoiceItemsCount { get; }

    /// <summary>vmangos Quest::prevQuests (signed: negative = must be active, not rewarded).</summary>
    public IReadOnlyList<int> PrevQuests => _prevQuests;

    /// <summary>vmangos Quest::prevChainQuests (quests whose NextQuestInChain is this one).</summary>
    public IReadOnlyList<uint> PrevChainQuests => _prevChainQuests;

    /// <summary>vmangos Quest::DependentBreadcrumbQuests.</summary>
    public IReadOnlyList<uint> DependentBreadcrumbQuests => _dependentBreadcrumbs;

    /// <summary>BreadcrumbForQuestId after the load-time validation.</summary>
    public uint BreadcrumbForQuestId { get; internal set; }

    /// <summary>NextQuestInChain after the load-time validation (0 when the target is missing).</summary>
    public uint NextQuestInChain { get; internal set; }

    public bool HasFlag(QuestFlags flag) => (Flags & flag) != 0;

    public bool HasSpecialFlag(QuestSpecialFlags flag) => (SpecialFlags & flag) != 0;

    /// <summary>
    /// vmangos Quest::GetRewOrReqMoney: a positive reward is scaled by Rate.Drop.Money; a
    /// requirement (negative) is not.
    /// </summary>
    public int GetRewOrReqMoney(float moneyRate)
        => Template.RewOrReqMoney <= 0 ? Template.RewOrReqMoney : (int)(Template.RewOrReqMoney * moneyRate);

    /// <summary>vmangos Quest::GetRewMoneyMaxLevelAtComplete (patch ≥ 1.10: RewMoneyMaxLevel × money rate).</summary>
    public int GetRewMoneyMaxLevelAtComplete(float moneyRate) => (int)(Template.RewMoneyMaxLevel * moneyRate);

    /// <summary>vmangos Quest::XPValue: RewXP reduced in steps above quest level + 5 (ceil).</summary>
    public uint XpValue(uint playerLevel)
    {
        if (Template.RewXP == 0)
        {
            return 0;
        }

        uint qLevel = (uint)Math.Max(0, QuestLevel);
        float full = Template.RewXP;
        float factor = playerLevel <= qLevel + 5 ? 1.0f
            : playerLevel == qLevel + 6 ? 0.8f
            : playerLevel == qLevel + 7 ? 0.6f
            : playerLevel == qLevel + 8 ? 0.4f
            : playerLevel == qLevel + 9 ? 0.2f
            : 0.1f;
        return (uint)MathF.Ceiling(full * factor);
    }

    internal void AddPrevQuest(int signedQuestId) => _prevQuests.Add(signedQuestId);

    internal void AddPrevChainQuest(uint questId) => _prevChainQuests.Add(questId);

    internal void AddDependentBreadcrumb(uint questId) => _dependentBreadcrumbs.Add(questId);
}
