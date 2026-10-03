using ArcaneCore.Game.Fishing;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// The <c>SpecialLoot</c> configuration section: fishing, skinning and special loot sources. Every default is the retail / vmangos behaviour
/// (any deviation a server wants is a switch here that defaults to retail). The <c>Loot</c> section of <see cref="LootOptions"/> is untouched.
/// </summary>
public sealed class SpecialLootOptions
{
    public const string SectionName = "SpecialLoot";

    /// <summary><c>SpecialLoot:Fishing:*</c> (vmangos mangosd.conf SkillFail.Loot.Fishing, SkillFail.Gain.Fishing, SkillFail.Possible.FishingPool).</summary>
    public FishingOptions Fishing { get; set; } = new();

    /// <summary><c>SpecialLoot:Items:*</c>: container items (lockboxes, clams).</summary>
    public ItemLootOptions Items { get; set; } = new();
}

/// <summary>Container item loot switches.</summary>
public sealed class ItemLootOptions
{
    /// <summary>
    /// A container item whose loot was taken is destroyed completely (default true: vmangos <c>DestroyItem</c> destroys the whole stack, LootHandler.cpp:556-563).
    /// With false only one of a stack is consumed. Three classic-db lootable items stack (Bloated Mackerel 6644, Bloated Albacore 6646, Bundle of Reports
    /// 16783); which behaviour retail had is not verifiable from the references.
    /// </summary>
    public bool ConsumeWholeStack { get; set; } = true;
}