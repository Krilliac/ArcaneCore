using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// The combat answers that depend on a player's equipment and learned abilities (vmangos
/// Unit::HaveOffhandWeapon, GetUnitParryChance / GetUnitBlockChance gates and
/// Player::GetShieldBlockValue). The player stat system implements it and the daemon installs it on every
/// map's <see cref="MapCombat.Stats"/>; <see cref="CombatHooks"/> keeps the defaults for everything the
/// source does not answer. Every method returns null for a unit it does not know, which falls back to
/// <see cref="CombatHooks"/>. World thread only.
/// </summary>
public interface ICombatStatSource
{
    /// <summary>Whether the unit can swing an off-hand weapon (usable, unbroken weapon in the off-hand slot).</summary>
    bool? HasOffhandWeapon(Unit unit);

    /// <summary>Player::CanParry() and a weapon to parry with.</summary>
    bool? PlayerCanParry(Player player);

    /// <summary>Player::CanBlock(), off-hand usable and an unbroken item with a block value in the off-hand slot.</summary>
    bool? PlayerCanBlock(Player player);

    /// <summary>The value a successful block removes from the damage.</summary>
    uint? ShieldBlockValue(Unit unit);
}
