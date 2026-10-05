using ArcaneCore.Kernel.Items;
using ArcaneCore.Game.Updates;

namespace ArcaneCore.Game.Items;

/// <summary>Atomic item-use helpers shared by the world handler and the spell cast pipeline.</summary>
public sealed partial class PlayerInventory
{
    /// <summary>Whether the item is still the same live object in the requested inventory slot.</summary>
    public bool OwnsItemAt(Item item, byte bag, byte slot)
        => ReferenceEquals(GetItem(bag, slot), item) && ReferenceEquals(item.Inventory, this);

    /// <summary>Apply vmangos Spell::TakeCastItem charge rules after SpellGo/effects.</summary>
    public bool ConsumeItemUse(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ReferenceEquals(item.Inventory, this))
        {
            return false;
        }

        bool expendable = false;
        bool withoutCharges = false;
        for (int i = 0; i < Item.SpellChargeSlots && i < item.Template.Spells.Count; i++)
        {
            ItemSpell proto = item.Template.Spells[i];
            if (proto.SpellId == 0 || proto.Charges == 0)
            {
                continue;
            }

            expendable |= proto.Charges < 0;
            int charges = item.GetInt32(UpdateFields.ItemFieldSpellCharges + i);
            if (charges != 0)
            {
                charges = charges > 0 ? charges - 1 : charges + 1;
                if (item.Template.Stackable < 2)
                {
                    item.SetInt32(UpdateFields.ItemFieldSpellCharges + i, charges);
                }
            }

            // vmangos assigns withoutCharges for each charged slot; the final
            // charged slot controls expendable deletion (Spell.cpp:5011-5040).
            withoutCharges = charges == 0;
        }

        if (expendable && withoutCharges)
        {
            DestroyItemCount(item, 1);
        }

        return true;
    }
}
