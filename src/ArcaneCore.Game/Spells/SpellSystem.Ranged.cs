using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Ranged abilities in the cast pipeline (hunter lane). The pure rules live in
/// <see cref="AmmoRules"/> / <see cref="RangedSpellFacts"/> / <see cref="PlayerAmmo"/>; this
/// partial is the part that needs the spell system's private state. Call sites in
/// SpellSystem.cs are marked "ranged (hunter lane)".
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>Hunter / ranged settings (<c>Ranged:*</c>); every default is the retail behaviour.</summary>
    public RangedOptions RangedOptions { get; set; } = new();

    /// <summary>
    /// The unit's ranged attack speed modifier (vmangos Unit::m_modAttackSpeedPct[RANGED_ATTACK];
    /// 1.0 = none, 0.5 = twice as fast). It scales the cast time of ranged abilities such as
    /// Aimed Shot (SpellEntry::GetCastTime). The default reads the unit's real multiplier, which the attack speed auras
    /// (<see cref="AttackSpeedAuras"/>) move; a host may replace the reader.
    /// </summary>
    public Func<Unit, float> RangedAttackSpeedPct { get; set; } = static unit => unit.Combat.GetAttackSpeedPct(Combat.WeaponAttackType.RangedAttack);

    /// <summary>The weapon/ammunition checks of vmangos Spell::CheckItems (Spell.cpp:7390-7455); players only.</summary>
    private SpellCastResult CheckRangedItems(Unit caster, SpellInfo spell)
    {
        if (caster is not Player player || !RangedSpellFacts.UsesRangedWeapon(spell) || !RangedSpellFacts.HasWeaponDamageEffect(spell))
        {
            return SpellCastResult.CastOk;
        }

        Item? weapon = PlayerAmmo.RangedWeapon(player, nonBroken: true);
        if (weapon is null)
        {
            return SpellCastResult.EquippedItem;
        }

        RangedWeaponKind kind = AmmoRules.Classify(weapon.Template);
        if (RangedOptions.Ammo.Mode == AmmoMode.Infinite)
        {
            return SpellCastResult.CastOk;
        }

        PlayerInventory inventory = player.Inventory;
        if (kind == RangedWeaponKind.Thrown)
        {
            return inventory.GetItemCount(weapon.Entry) >= 1 ? SpellCastResult.CastOk : SpellCastResult.NoAmmo;
        }

        if (!AmmoRules.UsesAmmoSlot(kind))
        {
            return SpellCastResult.CastOk; // wands (and anything else) take no projectile
        }

        uint ammoId = PlayerAmmo.CurrentAmmoId(player);
        ItemTemplate? ammo = ammoId == 0 ? null : inventory.Templates.Find(ammoId);
        if (ammo is null)
        {
            return SpellCastResult.NoAmmo;
        }

        if (RangedSpellFacts.NeedsExoticAmmo(spell) && !AmmoRules.IsExotic(ammo))
        {
            return SpellCastResult.NeedExoticAmmo;
        }

        if (!AmmoRules.AmmoMatchesWeapon(kind, ammo))
        {
            return SpellCastResult.NoAmmo;
        }

        return inventory.GetItemCount(ammoId) >= 1 ? SpellCastResult.CastOk : SpellCastResult.NoAmmo;
    }

    /// <summary>
    /// vmangos Spell::TakeAmmo (Spell.cpp:5129-5171): one arrow or bullet, one thrown weapon, or durability for a
    /// non-stackable thrown weapon. The consumption itself is the item-mechanics lane's
    /// <see cref="PlayerInventory.ConsumeRangedAmmo"/> (the single implementation); this method keeps the cast-side guards
    /// and the <c>Ranged:Ammo:Mode</c> option.
    /// </summary>
    private void TakeAmmo(Unit caster, SpellInfo spell)
    {
        if (caster is not Player player || !RangedSpellFacts.UsesRangedWeapon(spell) || RangedOptions.Ammo.Mode == AmmoMode.Infinite)
        {
            return;
        }

        player.Inventory.ConsumeRangedAmmo(spell.Id);
    }

    /// <summary>
    /// The projectile shown for a ranged cast (vmangos Spell::WriteAmmoToPacket, player branch):
    /// a thrown weapon shows itself; a launcher shows the selected ammo; a weapon without ammo
    /// (a wand) reports its own inventory type with display 0. Non-player casters send zeros.
    /// </summary>
    internal static AmmoVisual GetAmmoVisual(Unit caster)
        => caster is Player player && player.Inventory.TryGetAmmoVisual(out uint displayId, out uint inventoryType)
            ? new AmmoVisual(displayId, inventoryType)
            : default;

    /// <summary>
    /// The ranged weapon speed added to the cooldown of a ranged-slot spell that restarts the swing
    /// timers (vmangos Player::AddCooldown, Player.cpp:22193-22197; players only).
    /// </summary>
    private static uint RangedRecoveryMs(Unit caster, SpellInfo spell)
        => caster is Player && RangedSpellFacts.IsRanged(spell) && !RangedSpellFacts.DoesNotResetCombatTimers(spell)
            ? caster.GetUInt32(UpdateFields.UnitFieldRangedattacktime)
            : 0u;

    private static SpellCastFlags WithAmmoFlag(SpellCastFlags flags, SpellInfo spell)
        => RangedSpellFacts.IsRanged(spell) ? flags | SpellCastFlags.Ammo : flags;
}
