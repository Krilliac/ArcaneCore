using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Game.Crafting.Enchanting;

/// <summary>Item-target rules of a cast (vmangos Item::IsFitToSpellRequirements and the target block of Spell::CheckItems).</summary>
public static class ItemTargetRules
{
    /// <summary>vmangos <c>SPELL_ATTR_HELD_ITEM_ONLY</c> (SpellDefines.h:839): the client picks the main-hand item as the target.</summary>
    public const uint HeldItemOnly = 0x00000200;

    /// <summary>vmangos <c>SPELL_ATTR_EX2_ENCHANT_OWN_ITEM_ONLY</c> (SpellDefines.h:919).</summary>
    public const uint EnchantOwnItemOnly = 0x00002000;

    /// <summary>The Enchant Cloak - Minor Agility data fix (Nostalrius, Item.cpp:979-982): the row says weapon (2) where it means armor (4).</summary>
    public const uint MinorAgilityCloak = 13419;

    private const uint InventoryTypeCloak = 16;

    /// <summary>
    /// vmangos <c>Item::IsFitToSpellRequirements(spell, class, subclass, inventoryType)</c> (Objects/Item.cpp:975-1003): the item class and the subclass
    /// mask of the spell, then (only for a spell that targets an item) the inventory-type mask. -1 is any class, 0 any subclass or inventory type.
    /// </summary>
    public static bool IsFit(SpellInfo spell, ItemTemplate item)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(item);
        if (spell.EquippedItemClass != -1)
        {
            // vmangos lets a cloak through and skips only the class check for everything else, which leaves the row's weapon subclass and
            // inventory masks open to weapons. The spell is Enchant Cloak: a cloak fits, nothing else does.
            if (spell.Id == MinorAgilityCloak)
            {
                return item.InventoryType == InventoryTypeCloak;
            }

            if (spell.EquippedItemClass != (int)item.Class)
            {
                return false;
            }

            if (spell.EquippedItemSubClassMask != 0 && (spell.EquippedItemSubClassMask & (1 << (int)item.SubClass)) == 0)
            {
                return false;
            }
        }

        // Only an item target (TARGET_FLAG_ITEM): other spells' slot rules live in AttributesEx3 and special code.
        return spell.EquippedItemInventoryTypeMask == 0
            || (spell.Targets & (uint)SpellCastTargetFlags.Item) == 0
            || (spell.EquippedItemInventoryTypeMask & (1 << (int)item.InventoryType)) != 0;
    }

    /// <summary>
    /// The item a cast aims at: an item of the caster (<see cref="SpellCastTargetFlags.Item"/>, a GUID) or, through <paramref name="tradeItems"/>, an item the
    /// trading partner offers (<see cref="SpellCastTargetFlags.TradeItem"/>, a trade slot number; vmangos SpellCastTargets::Update, SpellCastTargetsInfo.cpp:119-138).
    /// Null when the cast names none or it is not found.
    /// </summary>
    public static Item? Resolve(Player player, SpellCastTargets targets, Func<Player, SpellCastTargets, Item?>? tradeItems)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(targets);
        if ((targets.Mask & SpellCastTargetFlags.Item) != 0)
        {
            return targets.Item.IsEmpty ? null : player.Inventory.GetItemByGuid(targets.Item);
        }

        return (targets.Mask & SpellCastTargetFlags.TradeItem) != 0 ? tradeItems?.Invoke(player, targets) : null;
    }

    /// <summary>Whether the cast names an item at all (vmangos <c>m_targets.getItemTargetGuid()</c>; a trade item is named by its slot).</summary>
    public static bool HasItemTarget(SpellCastTargets targets)
        => (targets.Mask & SpellCastTargetFlags.Item) != 0 ? !targets.Item.IsEmpty : (targets.Mask & SpellCastTargetFlags.TradeItem) != 0;
}

/// <summary>
/// The item-target block of <c>Spell::CheckItems</c> (Spells/Spell.cpp:7177-7200), after the cast-item checks (Equipment + 10) and before the spell focus
/// (Equipment + 50), so the client sees the retail error when two checks fail at once: a non-player caster cannot target an item
/// (<see cref="SpellCastResult.BadTargets"/>), a missing item is <see cref="SpellCastResult.ItemGone"/>, an item that does not fit the spell's class,
/// subclass and inventory-type masks is <see cref="SpellCastResult.EquippedItemClass"/>, and a held-item spell needs the main-hand item
/// (<see cref="SpellCastResult.MainhandEmpty"/>). A triggered cast reports nothing (<see cref="SpellCastResult.DontReport"/>) unless the target is a trade item.
/// </summary>
/// <param name="tradeItems">Finds the item a trade-slot target names; null: trade items are never found.</param>
public sealed class ItemTargetFitCheck(Func<Player, SpellCastTargets, Item?>? tradeItems = null) : ISpellCastCheck
{
    public const int FitOrder = SpellCastCheckOrder.Equipment + 20;

    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => FitOrder;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        SpellCastTargets targets = context.Targets;
        if (!ItemTargetRules.HasItemTarget(targets))
        {
            return SpellCastResult.CastOk;
        }

        bool quiet = context.Triggered && (targets.Mask & SpellCastTargetFlags.TradeItem) == 0;
        if (context.Caster is not Player player)
        {
            return quiet ? SpellCastResult.DontReport : SpellCastResult.BadTargets;
        }

        if (ItemTargetRules.Resolve(player, targets, tradeItems) is not { } item)
        {
            return quiet ? SpellCastResult.DontReport : SpellCastResult.ItemGone;
        }

        if (!ItemTargetRules.IsFit(context.Spell, item.Template))
        {
            return quiet ? SpellCastResult.DontReport : SpellCastResult.EquippedItemClass;
        }

        if ((((uint)context.Spell.Attributes) & ItemTargetRules.HeldItemOnly) != 0 && !(item.Container is null && item.Slot == InventorySlots.MainHand))
        {
            return quiet ? SpellCastResult.DontReport : SpellCastResult.MainhandEmpty;
        }

        return SpellCastResult.CastOk;
    }
}
