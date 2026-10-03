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
    /// vmangos Player::DurabilityPointLossForEquipSlot(EQUIPMENT_SLOT_RANGED): called once per
    /// throw of a non-stackable thrown weapon (Spell::TakeAmmo). Durability loss belongs to the
    /// item mechanics; until it installs a handler here such weapons do not wear.
    /// </summary>
    public Action<Player, byte>? EquipSlotDurabilityLoss { get; set; }

    /// <summary>
    /// The unit's ranged attack speed modifier (vmangos Unit::m_modAttackSpeedPct[RANGED_ATTACK];
    /// 1.0 = none, 0.5 = twice as fast). It scales the cast time of ranged abilities such as
    /// Aimed Shot (SpellEntry::GetCastTime). The attack-speed auras own the real value and install
    /// the reader here; the default is neutral.
    /// </summary>
    public Func<Unit, float> RangedAttackSpeedPct { get; set; } = static _ => 1.0f;

    /// <summary>The cast time of <paramref name="spell"/> for <paramref name="caster"/> (vmangos SpellEntry::GetCastTime with the Spell's auto-repeat flag).</summary>
    private int CastTimeFor(Unit caster, SpellInfo spell)
        => spell.GetCastTime(caster.Level, CastSpeed(caster), RangedSpellFacts.IsAutoRepeatRanged(spell), RangedAttackSpeedPct(caster));

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

    /// <summary>vmangos Spell::TakeAmmo (Spell.cpp:5129-5171): one arrow or bullet, one thrown weapon, or durability for a non-stackable thrown weapon.</summary>
    private void TakeAmmo(Unit caster, SpellInfo spell)
    {
        if (caster is not Player player || AmmoRules.NoAmmoSpellIds.Contains(spell.Id) || !RangedSpellFacts.UsesRangedWeapon(spell)
            || RangedOptions.Ammo.Mode == AmmoMode.Infinite)
        {
            return;
        }

        Item? weapon = PlayerAmmo.RangedWeapon(player, nonBroken: true);
        if (weapon is null || AmmoRules.Classify(weapon.Template) == RangedWeaponKind.Wand)
        {
            return;
        }

        if (weapon.Template.GetInventoryType() == InventoryType.Thrown)
        {
            if (weapon.Template.Stackable == 1)
            {
                EquipSlotDurabilityLoss?.Invoke(player, InventorySlots.Ranged);
            }
            else
            {
                player.Inventory.DestroyItemCount(weapon, 1);
            }
        }
        else
        {
            uint ammoId = PlayerAmmo.CurrentAmmoId(player);
            if (ammoId != 0)
            {
                player.Inventory.DestroyItemCount(ammoId, 1);
            }
        }
    }

    /// <summary>
    /// The projectile shown for a ranged cast (vmangos Spell::WriteAmmoToPacket, player branch):
    /// a thrown weapon shows itself; a launcher shows the selected ammo; a weapon without ammo
    /// (a wand) reports its own inventory type with display 0. Non-player casters send zeros.
    /// </summary>
    internal static AmmoVisual GetAmmoVisual(Unit caster)
    {
        if (caster is not Player player || PlayerAmmo.RangedWeapon(player, nonBroken: false) is not { } weapon)
        {
            return default;
        }

        if (weapon.Template.GetInventoryType() == InventoryType.Thrown)
        {
            return new AmmoVisual(weapon.Template.DisplayId, weapon.Template.InventoryType);
        }

        uint ammoId = PlayerAmmo.CurrentAmmoId(player);
        if (ammoId != 0 && player.Inventory.Templates.Find(ammoId) is { } ammo)
        {
            return new AmmoVisual(ammo.DisplayId, ammo.InventoryType);
        }

        return new AmmoVisual(0, weapon.Template.InventoryType);
    }

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
