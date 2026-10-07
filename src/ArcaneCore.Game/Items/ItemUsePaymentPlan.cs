using ArcaneCore.Kernel.Items;
using ArcaneCore.Game.Updates;

namespace ArcaneCore.Game.Items;

/// <summary>
/// A pure preview of the persistent item-state payment made by one item use.
/// The snapshots own read-only copies of their charge and enchantment lists.
/// </summary>
public sealed record ItemUsePaymentPlan(
    ItemInstanceData Before,
    ItemInstanceData? After,
    uint DestroyCount)
{
    /// <summary>Build the item-use result without changing the live item.</summary>
    public static ItemUsePaymentPlan Create(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        ItemInstanceData before = Freeze(item.ToData());
        int[] charges = before.Charges.ToArray();
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
            int current = charges.Length > i ? charges[i] : 0;
            if (current != 0)
            {
                current = current > 0 ? current - 1 : current + 1;
                if (item.Template.Stackable < 2)
                {
                    charges[i] = current;
                }
            }

            // The final charged template slot controls expendable deletion.
            withoutCharges = current == 0;
        }

        bool destroy = expendable && withoutCharges;
        if (destroy)
        {
            ItemInstanceData? after = before.Count > 1
                ? Freeze(before with { Count = before.Count - 1, Charges = charges })
                : null;
            return new ItemUsePaymentPlan(before, after, 1);
        }

        ItemInstanceData paid = Freeze(before with { Charges = charges });
        return new ItemUsePaymentPlan(before, paid, 0);
    }

    private static ItemInstanceData Freeze(ItemInstanceData data)
        => data with
        {
            Charges = Array.AsReadOnly(data.Charges.ToArray()),
            Enchantments = Array.AsReadOnly(data.Enchantments.ToArray()),
        };
}
