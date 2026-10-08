using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Out-of-combat upkeep for one managed player, one request per think: bags into empty bag slots, then the best-scoring gear
/// upgrade from the bags (armor, weapons, shields, rings, trinkets, cloaks, items with stats or random suffixes; vmangos
/// CombatBotBaseAI::EquipOrUseNewItem, CombatBotBaseAI.cpp:2958, and mangoszero's EquipAction), then ammunition for a bow, gun
/// or crossbow (the AddHunterAmmo choice, :2908, made from what the bags hold), then a talent rank
/// (<see cref="PlayerbotTalents"/>). Every request is the client's own packet through its real handler; legality is the
/// server's (a refused CMSG_AUTOEQUIP_ITEM is not a fault: the item is left alone for a while).
/// </summary>
internal sealed class PlayerbotEquipment(WorldSession session)
{
    private const int MaxItemsScanned = 128;
    private const uint RefusalBackoffMs = 60_000;
    private const uint WeaponBow = 2, WeaponGun = 3, WeaponCrossbow = 18;

    private readonly PlayerbotTalents _talents = new(session);
    private readonly Dictionary<ulong, uint> _refusedUntil = [];

    internal bool Update(Player player)
    {
        if (!player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat
            || session.ManagedBudget is not { Remaining: > 0 })
            return false;

        if (session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting })
            return false;

        ForgetExpiredRefusals();
        // Equip first, then talents: a new weapon or piece is worth more than one rank, and both wait for the next think.
        return TryEquipBag(player) || TryEquipUpgrade(player) || TrySetAmmo(player) || _talents.Update(player);
    }

    /// <summary>A general bag from the bags into an empty bag slot (CMSG_AUTOEQUIP_ITEM finds the slot).</summary>
    private bool TryEquipBag(Player player)
    {
        if (!Enumerable.Range(InventorySlots.BagStart, InventorySlots.BagEnd - InventorySlots.BagStart)
                .Any(slot => player.Inventory.GetItem(InventorySlots.Bag0, (byte)slot) is null))
            return false;

        Item? bag = Carried(player)
            .Where(item => item.Template.IsBag() && item.Template.IsGeneralBag() && item.Template.ContainerSlots > 0)
            .OrderByDescending(item => item.Template.ContainerSlots).ThenBy(item => item.Guid.Value)
            .FirstOrDefault(item => player.Inventory.CanEquipItem(InventorySlots.NullSlot, out _, item.Template, item, swap: false) == InventoryResult.Ok);
        return bag is not null && Send(player, bag, InventorySlots.NullSlot);
    }

    /// <summary>The carried item that gains most over what it would replace, worn through the ordinary equip handlers.</summary>
    private bool TryEquipUpgrade(Player player)
    {
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        Func<uint, SpellItemEnchantment?>? enchantments = Enchantments();
        Item? best = null;
        byte bestSlot = InventorySlots.NullSlot;
        float bestGain = PlayerbotItemScore.MinimumGain;
        foreach (Item item in Carried(player))
        {
            ItemTemplate template = item.Template;
            if (template.IsBag() || template.GetInventoryType() is InventoryType.NonEquip or InventoryType.Ammo or InventoryType.Body
                    or InventoryType.Tabard or InventoryType.Quiver
                || (item.MaxDurability > 0 && item.Durability == 0)
                || player.Inventory.CanUseItem(item) != InventoryResult.Ok)
                continue;

            float score = PlayerbotItemScore.Score(item, weights, enchantments);
            if (PlayerbotItemScore.UpgradeGain(player, template, score, weights, enchantments, out byte slot) is not { } gain
                || gain <= bestGain
                || player.Inventory.CanEquipItem(slot, out _, template, item, swap: true) != InventoryResult.Ok)
                continue;

            best = item;
            bestSlot = slot;
            bestGain = gain;
        }

        return best is not null && Send(player, best, bestSlot);
    }

    /// <summary>
    /// The ammunition for an equipped bow, gun or crossbow when none is selected or the selected one ran out: the best fitting
    /// ammo in the bags (highest item level, then damage), through CMSG_SET_AMMO.
    /// </summary>
    private bool TrySetAmmo(Player player)
    {
        if (player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged) is not { } ranged
            || (ItemClass)ranged.Template.Class != ItemClass.Weapon
            || ranged.Template.SubClass is not (WeaponBow or WeaponGun or WeaponCrossbow))
            return false;

        uint current = player.Inventory.AmmoId;
        if (current != 0 && player.Inventory.GetItemCount(current) > 0)
            return false;

        ItemTemplate? ammo = Carried(player).Select(item => item.Template)
            .Where(template => template.GetInventoryType() == InventoryType.Ammo && template.Entry != current
                && !_refusedUntil.ContainsKey(AmmoKey(template.Entry))
                && player.Inventory.CheckAmmoCompatibility(template)
                && player.Inventory.CanUseAmmo(template.Entry) == InventoryResult.Ok)
            .OrderByDescending(template => template.ItemLevel)
            .ThenByDescending(template => template.Damages.Sum(damage => damage.Min + damage.Max))
            .ThenBy(template => template.Entry)
            .FirstOrDefault();
        if (ammo is null)
            return false;

        var writer = new PacketWriter(4);
        writer.WriteUInt32(ammo.Entry);
        if (!session.TryManagedAction(WorldOpcode.CmsgSetAmmo, writer.ToArray()))
            return false;
        if (player.Inventory.AmmoId != ammo.Entry)
            _refusedUntil[AmmoKey(ammo.Entry)] = unchecked(session.World.NowMs + RefusalBackoffMs);
        return true;
    }

    /// <summary>
    /// Wear <paramref name="item"/>: CMSG_AUTOEQUIP_ITEM when the server would pick <paramref name="slot"/> itself (or for
    /// <see cref="InventorySlots.NullSlot"/>), else CMSG_AUTOEQUIP_ITEM_SLOT (the second ring, trinket or hand). An item that
    /// did not move was refused by the server's rules and is skipped until the backoff ends.
    /// </summary>
    private bool Send(Player player, Item item, byte slot)
    {
        bool sent;
        if (slot == InventorySlots.NullSlot || player.Inventory.FindEquipSlot(item.Template, InventorySlots.NullSlot, swap: true) == slot)
        {
            sent = session.TryManagedAction(WorldOpcode.CmsgAutoequipItem, [item.BagSlot, item.Slot]);
        }
        else
        {
            var writer = new PacketWriter(9);
            writer.WriteUInt64(item.Guid.Value);
            writer.WriteByte(slot);
            sent = session.TryManagedAction(WorldOpcode.CmsgAutoequipItemSlot, writer.ToArray());
        }

        if (!sent)
            return false;
        if (!InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot) || item.Inventory is null)
            _refusedUntil[item.Guid.Value] = unchecked(session.World.NowMs + RefusalBackoffMs);
        return true;
    }

    /// <summary>The items in the backpack and bags (not worn, not banked), in a stable order, skipping recent refusals.</summary>
    private IEnumerable<Item> Carried(Player player)
        => player.Inventory.AllItems.Take(MaxItemsScanned)
            .Where(item => InventorySlots.IsInventoryPos(item.BagSlot, item.Slot) && !InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot)
                && !_refusedUntil.ContainsKey(item.Guid.Value))
            .OrderBy(item => item.BagSlot).ThenBy(item => item.Slot).ThenBy(item => item.Entry);

    private Func<uint, SpellItemEnchantment?>? Enchantments()
        => session.Services.GetService<EnchantingFeature>()?.Catalog is { Count: > 0 } catalog ? catalog.Find : null;

    private void ForgetExpiredRefusals()
    {
        uint now = session.World.NowMs;
        foreach ((ulong key, uint until) in _refusedUntil.ToArray())
        {
            if (unchecked(now - until) <= int.MaxValue)
                _refusedUntil.Remove(key);
        }
    }

    // Ammo refusals share the table with item guids; the high bit keeps an entry from ever matching a guid.
    private static ulong AmmoKey(uint entry) => (1UL << 63) | entry;
}
