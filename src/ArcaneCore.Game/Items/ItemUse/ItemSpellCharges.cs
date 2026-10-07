using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Items.ItemUse;

/// <summary>
/// The item-spell trigger types of <c>item_template.spelltrigger_N</c> (vmangos ItemPrototype.h <c>ItemSpelltriggerType</c>).
/// Only <see cref="OnUse"/> is cast by CMSG_USE_ITEM.
/// </summary>
public static class ItemSpellTriggers
{
    public const uint OnUse = 0;

    public const uint OnEquip = 1;

    public const uint ChanceOnHit = 2;

    public const uint SoulStone = 4;

    public const uint OnNoDelayUse = 5;
}

/// <summary>
/// Spell::TakeCastItem (Spells/Spell.cpp:4991-5048): after a cast from an item, every limited-charge spell of the item loses a charge and a
/// spent expendable item is destroyed (one of the stack).
/// </summary>
public static class ItemSpellCharges
{
    /// <summary>
    /// Run TakeCastItem for <paramref name="cast"/>. Nothing happens without a cast item or a player caster, nor for a triggered cast
    /// unless the target is a trade item (a trade-window item is not modelled, so that exception is not either).
    /// </summary>
    public static void TakeCastItem(SpellCast cast)
    {
        ArgumentNullException.ThrowIfNull(cast);
        if (cast.CastItem is not { } item || cast.Caster is not Player player || cast.IsTriggered)
        {
            return;
        }

        if (TakeCharge(player, item))
        {
            cast.CastItem = null;   // destroying the item involved in the spell must not interrupt it (vmangos ClearCastItem)
        }
    }

    /// <summary>
    /// The charge step of TakeCastItem for one use of <paramref name="item"/> by <paramref name="player"/>: every limited-charge spell
    /// slot loses a charge, and a spent expendable item (negative charges) loses one of its stack. True when the item was destroyed.
    /// Also used for a key that opened a lock from the bags (<see cref="GameObjects.GameObjectMapSystem"/>), which the reference core
    /// opens by casting the key's spell.
    /// </summary>
    public static bool TakeCharge(Player player, Item item)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        bool expendable = false;
        bool withoutCharges = false;
        IReadOnlyList<Kernel.Items.ItemSpell> spells = item.Template.Spells;
        for (int i = 0; i < Item.SpellChargeSlots && i < spells.Count; i++)
        {
            if (spells[i].SpellId == 0 || spells[i].Charges == 0)
            {
                continue;
            }

            if (spells[i].Charges < 0)
            {
                expendable = true;
            }

            int charges = item.GetInt32(UpdateFields.ItemFieldSpellCharges + i);
            if (charges != 0)
            {
                charges = charges > 0 ? charges - 1 : charges + 1;   // abs(charges) is one less after use
                if (item.Template.Stackable < 2)
                {
                    item.SetInt32(UpdateFields.ItemFieldSpellCharges + i, charges);
                }
            }

            withoutCharges = charges == 0;   // vmangos assigns, not accumulates: the last limited slot decides
        }

        if (expendable && withoutCharges)
        {
            player.Inventory.DestroyItemCount(item, 1);
            return true;
        }

        return false;
    }

    /// <summary>The first spell slot of <paramref name="item"/> that has limited charges and none left (vmangos Spell.cpp:7119-7125).</summary>
    public static bool HasNoChargesLeft(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        IReadOnlyList<Kernel.Items.ItemSpell> spells = item.Template.Spells;
        for (int i = 0; i < Item.SpellChargeSlots && i < spells.Count; i++)
        {
            if (spells[i].Charges != 0 && item.GetInt32(UpdateFields.ItemFieldSpellCharges + i) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
