using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// A detached ordinary quest reward, prepared on the world thread. Persist all public after-values
/// atomically before calling <see cref="QuestNpcServices.ApplyReward"/>. Discarding a failed plan
/// leaves the live journal, money, inventory and client untouched.
/// </summary>
public sealed class QuestRewardPlan
{
    internal QuestRewardPlan(QuestNpcServices services, Player player, ObjectGuid questGiver, uint choice, uint moneyAfter,
        uint summaryMoney, uint experience, byte levelAfter, CharacterQuestStatus expectedQuest,
        CharacterQuestStatus rewardedQuest, InventoryRewardStage stage)
    {
        Services = services;
        Player = player;
        QuestGiver = questGiver;
        Choice = choice;
        Experience = experience;
        LevelBefore = player.Level;
        LevelAfter = levelAfter;
        MoneyBefore = player.Money;
        MoneyAfter = moneyAfter;
        SummaryMoney = summaryMoney;
        ExpectedQuest = expectedQuest;
        RewardedQuest = rewardedQuest;
        Stage = stage;
    }

    public Player Player { get; }
    public uint QuestId => ExpectedQuest.Quest;
    public uint Choice { get; }
    public InventorySnapshot BeforeInventory => Stage.Before;
    public InventorySnapshot InventoryAfter => Stage.After;
    public uint MoneyBefore { get; }
    public uint MoneyAfter { get; }

    /// <summary>Quest XP (Rate.XP.Quest applied; 0 at the maximum level, where RewMoneyMaxLevel pays instead).</summary>
    public uint Experience { get; }

    public byte LevelBefore { get; }

    /// <summary>The level the XP produces; persisted with the reward so a crash cannot lose a level-up.</summary>
    public byte LevelAfter { get; }

    /// <summary>The quest ender; reward spells use it as caster when they teach or target others.</summary>
    public ObjectGuid QuestGiver { get; }
    public CharacterQuestStatus ExpectedQuest { get; }
    public CharacterQuestStatus RewardedQuest { get; }
    internal QuestNpcServices Services { get; }
    internal InventoryRewardStage Stage { get; }
    internal uint SummaryMoney { get; }
    internal bool EffectsPublished { get; set; }
}
