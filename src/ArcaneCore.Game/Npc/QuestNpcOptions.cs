using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Quest and NPC service settings (configuration section "Quests"). Defaults are the vmangos
/// mangosd.conf.dist values: Quests.LowLevelHideDiff 4, Quests.HighLevelHideDiff 7,
/// MaxPlayerLevel 60, Rate.XP.Quest 1, Rate.Drop.Money 1.
/// </summary>
public sealed class QuestNpcOptions
{
    public const string SectionName = "Quests";

    /// <summary>Optional developer-supplied build-5875 FactionTemplate.dbc; absent means unknown NPC factions.</summary>
    public string? FactionTemplateDbcPath { get; set; }

    /// <summary>
    /// Developer-validated ordinary item/money quests. The imported template omits reputation,
    /// mail and script rewards, so a template alone cannot prove that rewarding it is supported.
    /// </summary>
    public uint[] OrdinaryRewardQuestIds { get; set; } = [];

    /// <summary>
    /// Quests:RewardMode. <see cref="QuestRewardMode.AllSupported"/> (default, retail) rewards every quest whose needs have adapters;
    /// <see cref="QuestRewardMode.AllowlistOnly"/> restores the earlier opt-in through <see cref="OrdinaryRewardQuestIds"/>.
    /// </summary>
    public QuestRewardMode RewardMode { get; set; } = QuestRewardMode.AllSupported;

    /// <summary>
    /// Quests:SharePushRequiresQuest (default false, retail): vmangos HandlePushQuestToParty (QuestHandler.cpp:403-459) offers any
    /// quest id to the party without checking that the pusher holds it; the receiver's accept does check (Player::CanShareQuest).
    /// Switch on to refuse such pushes up front.
    /// </summary>
    public bool SharePushRequiresQuest { get; set; }

    /// <summary>Quests:LogWithheld (default true): log at startup how many quests are withheld and why.</summary>
    public bool LogWithheld { get; set; } = true;

    /// <summary>
    /// areatrigger_involvedrelation rows (trigger id → exploration quest). Exploration/event
    /// quests without a relation stay unavailable: nothing else could complete them.
    /// </summary>
    public QuestAreaTrigger[] AreaTriggerQuests { get; set; } = [];

    /// <summary>Quests.LowLevelHideDiff (negative = never grey out).</summary>
    public int LowLevelHideDiff { get; set; } = 4;

    /// <summary>Quests.HighLevelHideDiff (negative = never hide).</summary>
    public int HighLevelHideDiff { get; set; } = 7;

    /// <summary>MaxPlayerLevel (quest XP turns into money at this level).</summary>
    public uint MaxPlayerLevel { get; set; } = 60;

    /// <summary>
    /// Quests:XpSource. <see cref="QuestXpSource.Auto"/> (default) uses the RewXP column when the loaded quests have one
    /// (vmangos data) and derives the experience from RewMoneyMaxLevel otherwise (classic-db data).
    /// </summary>
    public QuestXpSource XpSource { get; set; } = QuestXpSource.Auto;

    /// <summary>
    /// Quests.IgnoreRaid (vmangos CONFIG_BOOL_QUEST_IGNORE_RAID, default off): every quest counts as allowed in raid groups
    /// (<c>Quest::IsAllowedInRaid</c>); otherwise raid group members get no kill credit and no quest drops for ordinary quests.
    /// </summary>
    public bool IgnoreRaid { get; set; }

    /// <summary>Rate.XP.Quest.</summary>
    public float RateXpQuest { get; set; } = 1.0f;

    /// <summary>Rate.Drop.Money (quest money rewards).</summary>
    public float RateDropMoney { get; set; } = 1.0f;

    /// <summary>
    /// Seconds one reward settlement may take end to end (save, drains, the reward transaction)
    /// before it is abandoned and reconciled from the stored rows. Operational, not a gameplay
    /// rule: 5 is the shipped value. A test that deliberately holds a settlement open raises it so
    /// its observation window is not a race against this deadline.
    /// </summary>
    public int SettlementBudgetSeconds { get; set; } = 5;

    /// <summary><see cref="SettlementBudgetSeconds"/> as a duration, never below one second.</summary>
    public TimeSpan SettlementBudget => TimeSpan.FromSeconds(Math.Max(1, SettlementBudgetSeconds));
}
