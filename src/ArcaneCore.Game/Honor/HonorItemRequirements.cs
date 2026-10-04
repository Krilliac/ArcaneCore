using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// Equip gates read the HIGHEST honor rank reached: at 1.12 vmangos' <c>Player::GetHonorRank</c> answers
/// <c>GetHonorMgr().GetHighestRank().rank</c> (Player.cpp:10078-10081; only <c>AccurateEquipRequirements</c> before
/// patch 1.6 reverts to the current rank). Purchases from vendors use the current rank instead (see
/// <see cref="IPlayerHonor"/>). A player with no honor state answers through the wrapped provider. The decorator
/// composes with the skills and stats decorators in any order: it overrides only <see cref="HonorRank"/>.
/// </summary>
public sealed class HonorItemRequirements(IItemRequirements inner, IPlayerHonor honor) : IItemRequirements
{
    private readonly IPlayerHonor _honor = honor ?? throw new ArgumentNullException(nameof(honor));

    /// <summary>The wrapped provider.</summary>
    public IItemRequirements Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Wrap the inventory's requirements once (a second call does nothing).</summary>
    public static void Install(PlayerInventory inventory, IPlayerHonor honor)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Requirements is not HonorItemRequirements)
        {
            inventory.Requirements = new HonorItemRequirements(inventory.Requirements, honor);
        }
    }

    public bool CanDualWield(PlayerInventory inventory) => Inner.CanDualWield(inventory);

    public uint SkillValue(PlayerInventory inventory, uint skill) => Inner.SkillValue(inventory, skill);

    public bool HasSpell(PlayerInventory inventory, uint spellId) => Inner.HasSpell(inventory, spellId);

    public byte HonorRank(PlayerInventory inventory)
    {
        byte highest = inventory.Player is { } player ? _honor.HighestRank(player) : (byte)0;
        return highest != 0 ? highest : Inner.HonorRank(inventory);
    }

    public uint ReputationRank(PlayerInventory inventory, uint faction) => Inner.ReputationRank(inventory, faction);
}
