using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>The percent scale of <c>Items:DurabilityLossChanceDamage</c> (vmangos roll_chance_f: <c>chance > frand(0, 100)</c>).</summary>
    private const float DurabilityChancePercentScale = 100f;

    /// <summary>
    /// Armor wear on a hit taken (mangosserver Unit::DealDamage, "random durability for items (HIT TAKEN)", Unit.cpp:1093-1098):
    /// when a player victim survives a damage event, <c>Items:DurabilityLossChanceDamage</c> percent of the time one worn piece
    /// of armor with durability is picked uniformly and loses one point. The reference rolls <c>urand(0, EQUIPMENT_SLOT_END - 1)</c>
    /// over all 19 slots with equal weight (Unit.cpp:1096) and lets an empty slot (PlayerDurability.cpp:253-259) or an item without
    /// durability (PlayerDurability.cpp:213-228) absorb the roll; no reference core weights the slots. Here the pool is the worn
    /// armor only (<see cref="PlayerInventory.CollectWornArmorWithDurability"/>: no empty slot, no neck, ring, trinket, shirt or
    /// tabard, no weapon, since the weapon wears on the hit done), still uniform within the pool, so every successful percent roll
    /// wears a piece of armor (docs/areas/items.md, deliberate difference). Nothing is drawn when no armor is worn. Called from
    /// <see cref="DealDamage"/> on the survivor path only, so the killing blow wears nothing and the 10% death penalty
    /// (<see cref="ApplyDeathDurabilityLoss"/>) is untouched. Self damage (environment) counts as taken damage here as it does in
    /// the reference. World thread only; allocation free (one float roll, a stack-allocated slot list, one integer roll on a hit).
    /// </summary>
    private void RollHitTakenDurability(Unit victim)
    {
        if (victim is not Player player || !RollDurabilityChance(player.Inventory.Options))
        {
            return;
        }

        Span<byte> armor = stackalloc byte[InventorySlots.EquipmentEnd];
        int count = player.Inventory.CollectWornArmorWithDurability(armor);
        if (count == 0)
        {
            return;
        }

        player.Inventory.DurabilityPointLossForEquipSlot(armor[Random.Next(0, count - 1)]);
    }

    /// <summary>
    /// Weapon wear on a hit done: after a white swing of a player attacker connects for damage and the victim survives, the
    /// same <c>Items:DurabilityLossChanceDamage</c> roll takes one point off the weapon of the swinging hand (main hand for
    /// <see cref="WeaponAttackType.BaseAttack"/>, off hand for <see cref="WeaponAttackType.OffAttack"/>). vmangos picks a random
    /// slot for a hit done as well (Unit.cpp:1103-1108); the weapon-specific slot is the documented deviation that makes a
    /// weapon wear with the hits it lands (docs/areas/items.md). A missed, dodged or parried swing deals nothing and wears nothing.
    /// World thread only; allocation free.
    /// </summary>
    private void RollHitDoneDurability(MeleeDamageInfo info, uint dealt)
    {
        if (dealt == 0 || info.Attacker is not Player player || ReferenceEquals(info.Attacker, info.Target)
            || !IsAliveState(info.Target) || !RollDurabilityChance(player.Inventory.Options))
        {
            return;
        }

        byte slot = info.AttackType == WeaponAttackType.OffAttack ? InventorySlots.OffHand : InventorySlots.MainHand;
        player.Inventory.DurabilityPointLossForEquipSlot(slot);
    }

    /// <summary>
    /// vmangos roll_chance_f(DurabilityLossChance.Damage). Nothing is rolled (the random source is not advanced) when
    /// durability loss is disabled or the chance is not positive, so those configurations cost nothing per hit.
    /// </summary>
    private bool RollDurabilityChance(ItemMechanicsOptions options) => RollDurabilityChance(options, options.DurabilityLossChanceDamage);

    /// <summary>roll_chance_f(<paramref name="chance"/>) under the global <c>Items:DurabilityLossEnable</c> switch; a chance of 0 or less draws nothing.</summary>
    private bool RollDurabilityChance(ItemMechanicsOptions options, double chance)
    {
        if (!options.DurabilityLossEnable || !(chance > 0d))
        {
            return false;
        }

        return (float)chance > Random.NextFloat(0f, DurabilityChancePercentScale);
    }

    /// <summary>
    /// Wear from defending a white swing, after its damage was dealt, while the player victim still lives: a parry wears the main-hand weapon
    /// (<c>Items:DurabilityLossChanceParry</c>), a block the off-hand shield (<c>Items:DurabilityLossChanceBlock</c>), and damage that absorb
    /// effects took a worn armor piece from the hit-taken pool (<c>Items:DurabilityLossChanceAbsorb</c>). The three settings and their defaults are
    /// the mangos <c>DurabilityLossChance.Parry/Block/Absorb</c> (mangos-classic World.cpp:460-462, mangoszero WorldConfig.cpp:233-235); no
    /// reference core still reads them (vmangos dropped them, World.cpp:554 keeps only .Damage), so which item each one wears is a documented
    /// reconstruction (docs/areas/items.md). A swing that missed, was dodged or was fully blocked does not reach the hit-taken roll, which is
    /// why these exist. World thread only; allocation free.
    /// </summary>
    private void RollDefenseDurability(MeleeDamageInfo info)
    {
        if (info.Target is not Player victim || ReferenceEquals(info.Attacker, info.Target) || !IsAliveState(victim))
        {
            return;
        }

        ItemMechanicsOptions options = victim.Inventory.Options;
        if (info.Outcome == MeleeHitOutcome.Parry)
        {
            if (RollDurabilityChance(options, options.DurabilityLossChanceParry))
            {
                victim.Inventory.DurabilityPointLossForEquipSlot(InventorySlots.MainHand);
            }

            return;
        }

        if (info.Outcome == MeleeHitOutcome.Block && RollDurabilityChance(options, options.DurabilityLossChanceBlock))
        {
            victim.Inventory.DurabilityPointLossForEquipSlot(InventorySlots.OffHand);
        }

        if (info.Absorbed > 0 && RollDurabilityChance(options, options.DurabilityLossChanceAbsorb))
        {
            Span<byte> armor = stackalloc byte[InventorySlots.EquipmentEnd];
            int count = victim.Inventory.CollectWornArmorWithDurability(armor);
            if (count > 0)
            {
                victim.Inventory.DurabilityPointLossForEquipSlot(armor[Random.Next(0, count - 1)]);
            }
        }
    }
}
