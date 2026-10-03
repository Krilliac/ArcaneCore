using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Skills;

/// <summary>
/// The real answers for the equip, lock and quest gates, replacing <see cref="DefaultItemRequirements"/> once
/// the skills feature is installed (vmangos Player::GetSkillValue, HasSpell, CanDualWield). Honor and
/// reputation belong to other areas and keep answering through <paramref name="fallback"/> until their owners
/// replace them. A player without attached skills answers like a character that knows nothing: skill 0, no
/// dual wield, and no spell.
/// </summary>
public sealed class PlayerItemRequirements(IItemRequirements fallback) : IItemRequirements
{
    private readonly IItemRequirements _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));

    public bool CanDualWield(PlayerInventory inventory) => inventory.Player?.Skills?.CanDualWield ?? false;

    /// <summary>vmangos GetSkillValue: value plus permanent and temporary bonus; 0 for an unknown skill.</summary>
    public uint SkillValue(PlayerInventory inventory, uint skill) => inventory.Player?.Skills?.GetValue(skill) ?? 0;

    public bool HasSpell(PlayerInventory inventory, uint spellId) => inventory.Player?.Skills?.HasSpell(spellId) ?? false;

    public byte HonorRank(PlayerInventory inventory) => _fallback.HonorRank(inventory);

    public uint ReputationRank(PlayerInventory inventory, uint faction) => _fallback.ReputationRank(inventory, faction);
}
