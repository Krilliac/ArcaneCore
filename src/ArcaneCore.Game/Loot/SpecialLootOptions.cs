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
}