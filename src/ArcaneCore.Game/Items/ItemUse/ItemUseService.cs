using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Interrupts;

namespace ArcaneCore.Game.Items.ItemUse;

/// <summary>
/// The item-target restriction of <c>item_required_target</c> (vmangos Item::IsTargetValidForItemUse, Objects/Item.cpp:1011-1026). ArcaneCore does not
/// import that table (classic-db <c>item_required_target</c>, a limit recorded in docs/areas/crafting.md), so the default
/// <see cref="Permissive"/> accepts every target; a store backed by the table plugs in here.
/// </summary>
public interface IItemRequiredTargets
{
    /// <summary>Whether <paramref name="target"/> (the explicit unit target, or the caster for a self cast; may be null) may be the target of <paramref name="item"/>.</summary>
    bool IsValid(Item item, Unit? target);

    /// <summary>Accepts every target: no restriction table is loaded.</summary>
    static IItemRequiredTargets Permissive { get; } = new PermissiveTargets();

    private sealed class PermissiveTargets : IItemRequiredTargets
    {
        public bool IsValid(Item item, Unit? target) => true;
    }
}

/// <summary>
/// CMSG_USE_ITEM (vmangos <c>WorldSession::HandleUseItemOpcode</c>, Handlers/SpellHandler.cpp:36-140, then <c>Player::CastItemUseSpell</c>,
/// Objects/Player.cpp:7362-7396): validate the item and the click, bind it, and cast every ON_USE spell of the item with the item as the cast
/// item. The refusals are SMSG_INVENTORY_CHANGE_FAILURE equip errors, except the target rule and the shapeshift rule, which also send an
/// SMSG_CAST_RESULT.
/// <para>
/// Cast rules that are checks of the cast itself (item in trade, item missing, no charges, the 1.11 consumable refusal) live in
/// <see cref="CastItemCheck"/>; the charge and destroy step in <see cref="ItemSpellCharges"/>; item cooldowns in <see cref="ItemSpellCooldowns"/>.
/// </para>
/// </summary>
/// <param name="spells">The spell system that casts the item spells.</param>
/// <param name="isInTrade">Whether a player has offered an item in an open trade (the economy feature owns trades); null means never.</param>
/// <param name="requiredTargets">The <c>item_required_target</c> rule; null means <see cref="IItemRequiredTargets.Permissive"/>.</param>
public sealed class ItemUseService(SpellSystem spells, Func<Player, Item, bool>? isInTrade = null, IItemRequiredTargets? requiredTargets = null)
{
    private readonly IItemRequiredTargets _requiredTargets = requiredTargets ?? IItemRequiredTargets.Permissive;

    /// <summary>
    /// Register the <see cref="CastItemCheck"/> on <paramref name="spells"/> and return the service. A second call throws.
    /// </summary>
    public static ItemUseService Install(SpellSystem spells, Func<Player, Item, bool>? isInTrade = null, IItemRequiredTargets? requiredTargets = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (spells.CastChecks.OfType<CastItemCheck>().Any())
        {
            throw new InvalidOperationException("the cast item check is already installed");
        }

        spells.RegisterCastCheck(new CastItemCheck(isInTrade));
        return new ItemUseService(spells, isInTrade, requiredTargets);
    }

    /// <summary>Whether <paramref name="item"/> sits in an equipment slot (vmangos Item::IsEquipped: a direct slot below the bags).</summary>
    public static bool IsEquipped(Item item) => item.Container is null && item.Slot < InventorySlots.EquipmentEnd;

    /// <summary>vmangos IsShapeShifted for the use rule: a non-zero form byte (UNIT_FIELD_BYTES_1 byte 2); the form-flag nuance of the druid lane is not read.</summary>
    internal static bool IsShapeShifted(Player player) => player.GetByte(UpdateFields.UnitFieldBytes1, 2) != 0;

    /// <summary>One CMSG_USE_ITEM.</summary>
    /// <param name="player">The using player.</param>
    /// <param name="bag">Bag index (255 = the backpack and equipment).</param>
    /// <param name="slot">Slot in that bag.</param>
    /// <param name="spellIndex">Which of the item's spell slots the client clicked (0-4).</param>
    /// <param name="targets">The client's target block.</param>
    public void UseItem(Player player, byte bag, byte slot, byte spellIndex, SpellCastTargets targets)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(targets);
        PlayerInventory inventory = player.Inventory;
        Item? item = inventory.GetItem(bag, slot);
        if (item is null)
        {
            inventory.SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        Kernel.Items.ItemTemplate proto = item.Template;
        IReadOnlyList<Kernel.Items.ItemSpell> itemSpells = proto.Spells;
        if (spellIndex >= Kernel.Items.ItemTemplate.MaxSpells || spellIndex >= itemSpells.Count
            || itemSpells[spellIndex].SpellId == 0 || itemSpells[spellIndex].Trigger != ItemSpellTriggers.OnUse)
        {
            inventory.SendEquipError(InventoryResult.ItemNotFound, item, null);
            return;
        }

        // Some item classes can be used only in the equipped state.
        if (proto.InventoryType != 0 && !IsEquipped(item))
        {
            inventory.SendEquipError(InventoryResult.ItemNotFound, item, null);
            return;
        }

        InventoryResult usable = inventory.CanUseItem(item);
        if (usable != InventoryResult.Ok)
        {
            inventory.SendEquipError(usable, item, null);
            return;
        }

        // Not allowed from the trade window (cheat way only).
        if (isInTrade?.Invoke(player, item) == true)
        {
            inventory.SendEquipError(InventoryResult.ItemNotFound, item, null);
            return;
        }

        if ((player.UnitFlags & UnitFlags.InCombat) != 0)
        {
            foreach (Kernel.Items.ItemSpell itemSpell in itemSpells)
            {
                if (spells.Store.Get(itemSpell.SpellId) is { } info && info.HasAttribute(SpellAttributesCombat.NotInCombatOnlyPeaceful))
                {
                    inventory.SendEquipError(InventoryResult.NotInCombat, item, null);
                    return;
                }
            }
        }

        // BIND_WHEN_USE, and BIND_WHEN_PICKED_UP / BIND_QUEST_ITEM for a GM-added item that was never bound.
        if ((ItemBonding)proto.Bonding is ItemBonding.WhenUse or ItemBonding.WhenPickedUp or ItemBonding.QuestItem && !item.IsSoulBound)
        {
            item.SetBinding(true);
        }

        // PrepareForSpellSystem: a self cast aims at the player.
        Unit? unitTarget = (targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) != 0
            ? spells.Units.Find(player, targets.Unit)
            : targets.Mask == SpellCastTargetFlags.Self ? player : null;

        SpellCastResult refusal = SpellCastResult.CastOk;
        if (!_requiredTargets.IsValid(item, unitTarget))
        {
            refusal = SpellCastResult.BadTargets;
        }
        else if (IsShapeShifted(player) && !(bag == InventorySlots.Bag0 && slot < InventorySlots.EquipmentEnd))
        {
            // Client patch 1.10.0: all shapeshift forms can use equipped items (the guard is SUPPORTED_CLIENT_BUILD > 1.9.4).
            refusal = SpellCastResult.NoItemsWhileShapeshifted;
        }

        if (refusal != SpellCastResult.CastOk)
        {
            inventory.SendEquipError(InventoryResult.None, item, null);   // frees a grey item after a failed use
            if (spells.Store.Get(itemSpells[spellIndex].SpellId) is { } refused)
            {
                SpellSystem.SendCastResult(player, refused, refusal, triggered: false);
            }

            return;
        }

        CastItemUseSpell(player, item, targets);
    }

    /// <summary>
    /// vmangos Player::CastItemUseSpell: every ON_USE spell of the item is cast with the item as cast item; the first normally, the following
    /// ones triggered ("use triggered flag only for items with many spell casts and for not first cast"). An unknown spell is skipped.
    /// </summary>
    public void CastItemUseSpell(Player player, Item item, SpellCastTargets targets)
    {
        int count = 0;
        foreach (Kernel.Items.ItemSpell itemSpell in item.Template.Spells)
        {
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != ItemSpellTriggers.OnUse || spells.Store.Get(itemSpell.SpellId) is not { } info)
            {
                continue;
            }

            spells.RemoveAurasWithInterruptFlags(player, AuraInterruptMask.ItemUse, 0,
                skipStealth: ((uint)info.AttributesEx & StealthBreakRules.AttributesExAllowWhileStealthed) != 0);
            spells.CastItemSpell(player, item, itemSpell.SpellId, targets, triggered: count > 0);
            count++;
        }
    }
}
