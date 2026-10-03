namespace ArcaneCore.Game.Npc;

/// <summary>
/// Quest and NPC service settings (configuration section "Quests"). Defaults are the vmangos
/// mangosd.conf.dist values: Quests.LowLevelHideDiff 4, Quests.HighLevelHideDiff 7,
/// MaxPlayerLevel 60, Rate.XP.Quest 1, Rate.Drop.Money 1.
/// </summary>
public sealed class QuestNpcOptions
{
    public const string SectionName = "Quests";

    /// <summary>Quests.LowLevelHideDiff (negative = never grey out).</summary>
    public int LowLevelHideDiff { get; set; } = 4;

    /// <summary>Quests.HighLevelHideDiff (negative = never hide).</summary>
    public int HighLevelHideDiff { get; set; } = 7;

    /// <summary>MaxPlayerLevel (quest XP turns into money at this level).</summary>
    public uint MaxPlayerLevel { get; set; } = 60;

    /// <summary>Rate.XP.Quest.</summary>
    public float RateXpQuest { get; set; } = 1.0f;

    /// <summary>Rate.Drop.Money (quest money rewards).</summary>
    public float RateDropMoney { get; set; } = 1.0f;
}
