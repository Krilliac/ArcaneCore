using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Items;

/// <summary>
/// "Conjured items disappear if you are logged out for more than 15 minutes" (vmangos Player::_LoadInventory, Player.cpp:15533-15540): an item
/// whose template carries ITEM_FLAG_CONJURED, of any class, is not loaded when the time since the stored logout is above 15 minutes; the next
/// save then drops its row (vmangos deletes it at once). The logout second comes from the rested state (vmangos <c>characters.logout_time</c>).
/// </summary>
public static class ConjuredItems
{
    /// <summary>vmangos <c>15 * MINUTE</c>; strictly more than this many seconds offline drops the items.</summary>
    public const long OfflineLimitSeconds = 15 * 60;

    /// <summary>Whether an item of <paramref name="template"/> vanishes after <paramref name="offlineSeconds"/> logged out.</summary>
    public static bool VanishesAfter(ItemTemplate template, long offlineSeconds)
    {
        ArgumentNullException.ThrowIfNull(template);
        return offlineSeconds > OfflineLimitSeconds && (template.Flags & (uint)ItemTemplateFlags.Conjured) != 0;
    }

    /// <summary>
    /// The stored rows without the conjured items that vanished. A row whose template is unknown is kept (the inventory keeps unloadable rows).
    /// Returns the input itself when nothing vanished.
    /// </summary>
    public static IReadOnlyList<InventoryItemData> WithoutVanished(IReadOnlyList<InventoryItemData> rows, IItemTemplateStore templates, long offlineSeconds)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(templates);
        if (offlineSeconds <= OfflineLimitSeconds || !rows.Any(r => templates.Find(r.Item.Entry) is { } t && VanishesAfter(t, offlineSeconds)))
        {
            return rows;
        }

        return [.. rows.Where(r => templates.Find(r.Item.Entry) is not { } t || !VanishesAfter(t, offlineSeconds))];
    }
}
