using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>The percent scale of <c>Items:DurabilityLossChanceDamage</c> (vmangos roll_chance_f: <c>chance > frand(0, 100)</c>).</summary>
    private const float DurabilityChancePercentScale = 100f;

    /// <summary>
    /// Wear on a hit taken (vmangos Unit::DealDamage, "random durability for items (HIT TAKEN)", Unit.cpp:879-884; mangos-classic Unit.cpp:1244):
    /// when a player victim survives a damage event, <c>Items:DurabilityLossChanceDamage</c> percent of the time one of the 19 equipment slots is
    /// rolled with <c>urand(0, EQUIPMENT_SLOT_END - 1)</c> and the item in it loses one point. An empty slot, an item without durability, a
    /// weapon, a ring or a neck piece absorbs the roll, as in both reference cores. Called from <see cref="DealDamage"/> on the survivor path
    /// only, so the killing blow wears nothing and the 10% death penalty (<see cref="ApplyDeathDurability"/>) is untouched. Self damage
    /// (environment) counts as taken damage here as it does in the reference. World thread only; allocation free (one float roll and one
    /// integer roll on a hit, the integer roll drawn after the percent roll).
    /// </summary>
    private void RollHitTakenDurability(Unit victim)
    {
        if (victim is not Player player || !RollDurabilityChance(player.Inventory.Options))
        {
            return;
        }

        player.Inventory.DurabilityPointLossForEquipSlot(RollEquipmentSlot());
    }

    /// <summary>vmangos <c>EquipmentSlots(urand(0, EQUIPMENT_SLOT_END - 1))</c>: one of the 19 equipment slots with equal weight.</summary>
    private byte RollEquipmentSlot() => (byte)Random.Next(0, InventorySlots.EquipmentEnd - 1);

    /// <summary>
    /// Picks one worn armor piece with durability uniformly and takes one point off it. Draws nothing when no armor is worn,
    /// so the caller's chance roll stays the only draw in that case. World thread only; allocation free (stack slot list).
    /// </summary>
    private void WearRandomArmorPiece(Player player)
    {
        Span<byte> armor = stackalloc byte[InventorySlots.EquipmentEnd];
        int count = player.Inventory.CollectWornArmorWithDurability(armor);
        if (count == 0)
        {
            return;
        }

        player.Inventory.DurabilityPointLossForEquipSlot(armor[Random.Next(0, count - 1)]);
    }

    /// <summary>
    /// Wear on a hit done (vmangos Unit::DealDamage "random durability for items (HIT DONE)", Unit.cpp:888-894; mangos-classic Unit.cpp:1228):
    /// after a white swing of a player attacker connects for damage and the victim survives, the same
    /// <c>Items:DurabilityLossChanceDamage</c> roll takes one point off the item in a uniformly rolled one of the 19 equipment slots (the
    /// weapon of the swinging hand is only one of them, 1 in 19). A missed, dodged or parried swing deals nothing and wears nothing. The
    /// reference rolls this in DealDamage for every damage a player deals, spells included; here only the white swing reaches it (the spell
    /// damage path is not wired). World thread only; allocation free.
    /// </summary>
    private void RollHitDoneDurability(MeleeDamageInfo info, uint dealt)
    {
        if (dealt == 0 || info.Attacker is not Player player || ReferenceEquals(info.Attacker, info.Target)
            || !IsAliveState(info.Target) || !RollDurabilityChance(player.Inventory.Options))
        {
            return;
        }

        player.Inventory.DurabilityPointLossForEquipSlot(RollEquipmentSlot());
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
    /// effects took a worn armor piece from the worn-armor pool (<c>Items:DurabilityLossChanceAbsorb</c>). The three settings are the mangos
    /// <c>DurabilityLossChance.Parry/Block/Absorb</c> (mangos-classic World.cpp:460-462, mangoszero WorldConfig.cpp:233-235); no reference core
    /// still reads them (vmangos dropped them, World.cpp:554 keeps only .Damage), so they default to 0 (off, the vmangos behaviour) and which item
    /// each one wears is a documented reconstruction (docs/areas/items.md). A swing that missed, was dodged or was fully blocked does not reach the hit-taken roll, which is
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
            WearRandomArmorPiece(victim);
        }
    }
}
