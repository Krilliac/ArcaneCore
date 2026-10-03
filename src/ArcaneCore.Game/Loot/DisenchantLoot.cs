using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Disenchanting on one map (vmangos Spell::CheckCast :7376-7392, Spell::EffectDisEnchant SpellEffects.cpp:5059-5073, Player::SendLoot for an item
/// with LOOT_DISENCHANTING Player.cpp:7742-7746, DoLootRelease LootHandler.cpp:543-553): the target item must be the caster's own, carry a
/// disenchant loot id (<c>item_template.DisenchantID</c>) and not be flagged NO_DISENCHANT; the cast binds the item, tries a craft skill-up of the
/// spell, and shows the <c>disenchant_loot_template</c> of the id as temporary loot (wire type 4). Closing the window stores everything left
/// (what does not fit is lost) and destroys the item.
/// <para>
/// vmangos checks the quality (uncommon to epic) and class (weapon or armor) of a disenchantable item once when item templates load
/// (ObjectMgr.cpp:4183-4195, then clears the id); this class trusts the loaded templates and does not repeat that on every cast.
/// </para>
/// </summary>
public sealed class DisenchantLoot(LootService loot) : ILootReleaseHandler
{
    /// <summary>ITEM_FLAG_NO_DISENCHANT (vmangos ItemPrototype.h:79).</summary>
    public const uint ItemFlagNoDisenchant = 0x00008000;

    private readonly HashSet<ObjectGuid> _active = [];

    /// <summary>Items with an open disenchant window (vmangos Item::HasGeneratedLoot while LOOT_DISENCHANTING is open).</summary>
    public int ActiveItems => _active.Count;

    /// <summary>Whether the item can be disenchanted now (the rules of Spell::CheckCast), as CAST_OK or CANT_BE_DISENCHANTED.</summary>
    public SpellCastResult CheckTarget(Player caster, Item? item)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (item is null || _active.Contains(item.Guid) || item.OwnerGuid != caster.Guid)
        {
            return SpellCastResult.CantBeDisenchanted; // missing, in use, or not the caster's own (the trade window)
        }

        return item.Template.DisenchantId == 0 || (item.Template.Flags & ItemFlagNoDisenchant) != 0
            ? SpellCastResult.CantBeDisenchanted
            : SpellCastResult.CastOk;
    }

    /// <summary>
    /// EffectDisEnchant: bind the item, try the craft skill-up of <paramref name="spellId"/> and open the loot window. Nothing happens for an item
    /// without a disenchant loot id (the cast check already refused it).
    /// </summary>
    public LootResult Disenchant(Player player, Item item, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Template.DisenchantId == 0)
        {
            return LootResult.NotLootable;
        }

        item.SetBinding(true);
        player.Skills?.UpdateCraft(spellId);

        LootBag bag = loot.Generate(item.Guid, LootSourceKind.Item, LootType.Disenchanting, LootTableKind.Disenchant, item.Template.DisenchantId, [player]);
        bag.Owner = player.Guid;
        bag.ShareMoney = false;
        bag.ReleaseHandler = this;
        bag.SourceCheck = viewer => viewer.IsAlive && viewer.Inventory.GetItemByGuid(item.Guid) is not null;
        _active.Add(item.Guid);
        LootResult shown = loot.ShowSpecial(player, item, bag);
        if (shown != LootResult.Ok)
        {
            _active.Remove(item.Guid);
        }

        return shown;
    }

    /// <summary>The window closed: every stack still in it goes into the bags (lost without room), then the item is destroyed.</summary>
    public void OnReleased(Player player, LootBag bag)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(bag);
        _active.Remove(bag.Source);
        foreach (LootItem item in bag.Items)
        {
            if (bag.SlotFor(player, item) != LootSlotType.AllowLoot)
            {
                continue;
            }

            if (player.Inventory.AddItem(item.ItemId, item.Count, out _, received: false, created: false, showInChat: true) == InventoryResult.Ok)
            {
                bag.MarkTaken(item, player);
                loot.Quests?.ItemLooted(player, item.ItemId, item.Count);
            }
        }

        loot.RemoveSpecial(bag.Source);
        if (player.Inventory.GetItemByGuid(bag.Source) is { } owned)
        {
            player.Inventory.DestroyItemCount(owned, owned.Count);
        }
    }
}