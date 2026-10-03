using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// vmangos Spell::CheckItems for a cast without an item target (Spell.cpp:7208-7236) over Player::HasItemFitToSpellReqirements
/// (Player.cpp:19753-19797): a spell with an equipped item requirement (a fishing pole for fishing, a weapon class for weapon abilities)
/// needs an unbroken fitting item in the right slots: weapons in main hand, off hand and ranged slot, armor in the armor slots plus
/// off hand (shields) and ranged. A non-triggered failure is EQUIPPED_ITEM_CLASS (or the main hand / off hand variants for
/// REQUIRES_MAIN_HAND_WEAPON / REQUIRES_OFFHAND_WEAPON), a triggered one DONT_REPORT. For a main-hand or ranged spell the other
/// attack's weapon does not count (vmangos <c>ignore</c>): an off-hand dagger alone does not satisfy a main-hand ability.
/// <para>
/// Not covered: casts that name an item (enchanting, key use) - their item rules belong to the items area; item classes other than weapon and
/// armor pass (vmangos fails them with a logged error; test content leaves the field at 0 where DBC data has -1, so passing is the safe reading).
/// </para>
/// </summary>
public sealed class EquippedItemCastCheck : ISpellCastCheck
{
    private const uint ItemClassWeapon = 2;

    private const uint ItemClassArmor = 4;

    /// <summary>SPELL_ATTR_EX3_REQUIRES_MAIN_HAND_WEAPON (vmangos SpellDefines.h:955).</summary>
    private const uint RequiresMainHandWeapon = 0x00000400;

    /// <summary>SPELL_ATTR_EX3_REQUIRES_OFFHAND_WEAPON (vmangos SpellDefines.h:972).</summary>
    private const uint RequiresOffhandWeapon = 0x01000000;

    /// <summary>SPELL_ATTR_EX2_AUTO_REPEAT (vmangos SpellDefines.h:911): wands and auto shot.</summary>
    private const uint AutoRepeat = 0x00000020;

    private enum AttackKind
    {
        Base,
        Off,
        Ranged,
    }

    /// <summary>Register the check on <paramref name="spells"/>.</summary>
    public static void Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        spells.RegisterCastCheck(new EquippedItemCastCheck());
    }

    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => SpellCastCheckOrder.Equipment;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        SpellInfo spell = context.Spell;
        if (spell.EquippedItemClass < 0 || context.Caster is not Player player || !context.Targets.Item.IsEmpty)
        {
            return SpellCastResult.CastOk;
        }

        Item? ignore = AttackOf(spell) switch
        {
            AttackKind.Base => WeaponIn(player, InventorySlots.OffHand),
            AttackKind.Off => WeaponIn(player, InventorySlots.MainHand),
            _ => null,
        };
        if (HasFittingItem(player, spell, ignore))
        {
            return SpellCastResult.CastOk;
        }

        if (context.Triggered)
        {
            return SpellCastResult.DontReport;
        }

        if ((spell.AttributesEx3 & RequiresMainHandWeapon) != 0)
        {
            return SpellCastResult.EquippedItemClassMainhand;
        }

        return (spell.AttributesEx3 & RequiresOffhandWeapon) != 0 ? SpellCastResult.EquippedItemClassOffhand : SpellCastResult.EquippedItemClass;
    }

    /// <summary>vmangos SpellEntry::GetWeaponAttackType (SpellEntry.cpp:434-455).</summary>
    private static AttackKind AttackOf(SpellInfo spell) => spell.DamageClass switch
    {
        SpellDamageClass.Melee => (spell.AttributesEx3 & RequiresOffhandWeapon) != 0 ? AttackKind.Off : AttackKind.Base,
        SpellDamageClass.Ranged => AttackKind.Ranged,
        _ => ((uint)spell.AttributesEx2 & AutoRepeat) != 0 ? AttackKind.Ranged : AttackKind.Base,
    };

    private static Item? WeaponIn(Player player, byte slot)
        => player.Inventory.GetItem(InventorySlots.Bag0, slot) is { } item && item.Template.Class == ItemClassWeapon ? item : null;

    /// <summary>vmangos Player::HasItemFitToSpellReqirements.</summary>
    internal static bool HasFittingItem(Player player, SpellInfo spell, Item? ignore)
    {
        switch ((uint)spell.EquippedItemClass)
        {
            case ItemClassWeapon:
                for (byte slot = InventorySlots.MainHand; slot < InventorySlots.Tabard; slot++)
                {
                    if (Fits(player, slot, spell, ignore))
                    {
                        return true;
                    }
                }

                return false;
            case ItemClassArmor:
                for (byte slot = InventorySlots.Head; slot < InventorySlots.MainHand; slot++)
                {
                    if (Fits(player, slot, spell, ignore))
                    {
                        return true;
                    }
                }

                // shields sit in the off hand, some armor subclasses in the ranged slot
                return Fits(player, InventorySlots.OffHand, spell, ignore) || Fits(player, InventorySlots.Ranged, spell, ignore);
            default:
                return true;
        }
    }

    private static bool Fits(Player player, byte slot, SpellInfo spell, Item? ignore)
        => player.Inventory.GetItem(InventorySlots.Bag0, slot) is { } item && !ReferenceEquals(item, ignore) && IsFit(item, spell) && !IsBroken(item);

    /// <summary>vmangos Item::IsFitToSpellRequirements (Item.cpp:975-1003) without the item-target-only inventory type rule.</summary>
    internal static bool IsFit(Item item, SpellInfo spell)
        => spell.EquippedItemClass < 0
            || (item.Template.Class == (uint)spell.EquippedItemClass
                && (spell.EquippedItemSubClassMask == 0 || (spell.EquippedItemSubClassMask & (1 << (int)item.Template.SubClass)) != 0));

    /// <summary>vmangos Item::IsBroken: a durable item at 0 durability.</summary>
    private static bool IsBroken(Item item) => item.MaxDurability > 0 && item.Durability == 0;
}