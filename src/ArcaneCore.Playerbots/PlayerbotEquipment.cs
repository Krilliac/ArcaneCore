using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.Protocol;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Out-of-combat upkeep for one managed player, one request per think: bags into empty or smaller bag slots, then the best-scoring gear
/// upgrade from the bags (armor, weapons, shields, rings, trinkets, cloaks, items with stats or random suffixes; vmangos
/// CombatBotBaseAI::EquipOrUseNewItem, CombatBotBaseAI.cpp:2958, and mangoszero's EquipAction), then learnable spell/recipe/mount
/// items in the bags, then ammunition for a bow, gun or crossbow (the AddHunterAmmo choice, :2908, made from what the bags hold),
/// then a talent rank (<see cref="PlayerbotTalents"/>). Every request is the client's own packet through its real handler; legality is the
/// server's (a refused CMSG_AUTOEQUIP_ITEM is not a fault: the item is left alone for a while).
/// </summary>
internal sealed class PlayerbotEquipment(WorldSession session)
{
    private const int MaxItemsScanned = 128;
    private const uint RefusalBackoffMs = 60_000;
    private const uint WeaponBow = 2, WeaponGun = 3, WeaponCrossbow = 18;

    private readonly PlayerbotTalents _talents = new(session);
    private readonly Dictionary<ulong, uint> _refusedUntil = [];

    /// <summary>A worn item at zero durability this long (world time, out of sight of any repair) is reported.</summary>
    internal const uint BrokenGearMs = 20 * 60_000;

    /// <summary>The equipped gear is looked over this often.</summary>
    internal const uint BrokenLookMs = 30_000;

    /// <summary>The same item worn this many times inside <see cref="FlapWindowMs"/> is a swap loop.</summary>
    internal const int FlapLimit = 3;

    internal const uint FlapWindowMs = 600_000;

    private const int EquipLogSize = 8;

    private uint _nextBrokenLookMs;
    private uint _brokenSinceMs;
    private ulong _brokenItemGuid;
    private bool _brokenReported;
    private readonly (ulong Guid, uint AtMs)[] _equipLog = new (ulong, uint)[EquipLogSize];
    private int _equipLogCount;

    internal bool Update(Player player)
    {
        LookForBrokenGear(player);
        if (!player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat
            || session.ManagedBudget is not { Remaining: > 0 })
            return false;

        if (session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting })
            return false;

        ForgetExpiredRefusals();
        // Equip first, then learnable spell/recipe/mount items, ammo, and talents.
        return TryEquipBag(player) || TryEquipUpgrade(player) || TryLearnFromBagItem(player) || TrySetAmmo(player) || _talents.Update(player);
    }

    /// <summary>Whether <paramref name="candidate"/> is a usable general bag that fills an empty bag slot or replaces a smaller worn general bag.</summary>
    internal static bool IsBagUpgrade(Player player, ItemTemplate candidate)
    {
        if (!candidate.IsBag() || !candidate.IsGeneralBag() || candidate.ContainerSlots == 0
            || player.Inventory.CanUseItem(candidate) != InventoryResult.Ok)
            return false;

        for (byte slot = InventorySlots.BagStart; slot < InventorySlots.BagEnd; slot++)
        {
            Item? worn = player.Inventory.GetItem(InventorySlots.Bag0, slot);
            if (worn is null)
                return true;
            if (worn.Template.IsGeneralBag() && candidate.ContainerSlots > worn.Template.ContainerSlots)
                return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="template"/> is a usable spellbook, recipe, schematic, pattern, plan, formula or mount item that teaches an unknown spell.</summary>
    internal static bool CanLearnFromItem(Player player, ItemTemplate template, SpellStore? store, SpellbookCache? spellbook)
    {
        if (player.Inventory.CanUseItem(template) != InventoryResult.Ok)
            return false;

        if (template.RequiredSkill != 0 && player.Skills is { } skills
            && skills.GetValueBase(template.RequiredSkill) < template.RequiredSkillRank)
            return false;

        bool anyTeachEffect = false;
        foreach (ItemSpell itemSpell in template.Spells)
        {
            if (itemSpell.SpellId == 0 || itemSpell.Trigger != 0)
                continue;

            if (store?.Get(itemSpell.SpellId) is { } useSpell)
            {
                for (int i = 0; i < useSpell.Effects.Count; i++)
                {
                    SpellEffectInfo eff = useSpell.Effects[i];
                    if (eff.Effect != SpellEffectName.LearnSpell || eff.TriggerSpell == 0)
                        continue;

                    anyTeachEffect = true;
                    // A grimoire teaches the pet (TARGET_PET): with no living pet out the cast fails NoPet (live: ~650 Firebolt
                    // 20270 refusals in 10 min from warlock bots).
                    if (eff.TargetA == SpellImplicitTarget.UnitCasterPet && player.GetPet() is not { IsAlive: true })
                        continue;

                    if (spellbook is not null && spellbook.HasSpell(player, eff.TriggerSpell))
                        continue;

                    return true;
                }
            }
            else if ((ItemClass)template.Class == ItemClass.Recipe && spellbook is null)
            {
                return true;
            }
        }

        return !anyTeachEffect && (ItemClass)template.Class == ItemClass.Recipe && store is null;
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

    /// <summary>Use a carried recipe, grimoire, manual or mount item that teaches the bot a new spell (<c>CMSG_USE_ITEM</c>).</summary>
    private bool TryLearnFromBagItem(Player player)
    {
        SpellFeature? spells = session.Services.GetService<SpellFeature>();
        SpellStore? store = spells?.System.Store;
        SpellbookCache? spellbook = spells?.Spellbook;
        if (store is null || spellbook is null)
            return false;

        // The teaching spells have a cast time; a walking bot is refused (Moving).
        if (PlayerbotMotion.IsActive(player))
            return false;

        foreach (Item item in Carried(player))
        {
            if (!CanLearnFromItem(player, item.Template, store, spellbook))
                continue;

            int before = spellbook.GetSpells(player).Count;
            if (!session.TryManagedAction(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(item.BagSlot, item.Slot)))
                return false;
            if (spellbook.GetSpells(player).Count <= before && item.Inventory is not null)
                _refusedUntil[item.Guid.Value] = unchecked(session.World.NowMs + RefusalBackoffMs);
            return true;
        }

        return false;
    }

    /// <summary>The carried item that gains most over what it would replace, worn through the ordinary equip handlers.</summary>
    private bool TryEquipUpgrade(Player player)
    {
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        Func<uint, SpellItemEnchantment?>? enchantments = Enchantments();
        SpellStore? spells = session.Services.GetService<SpellFeature>()?.System.Store;
        Item? best = null;
        byte bestSlot = InventorySlots.NullSlot;
        float bestGain = PlayerbotItemScore.MinimumGain;
        foreach (Item item in Carried(player))
        {
            ItemTemplate template = item.Template;
            if (template.IsBag() || template.GetInventoryType() is InventoryType.NonEquip or InventoryType.Ammo or InventoryType.Body
                    or InventoryType.Tabard or InventoryType.Quiver
                || (item.MaxDurability > 0 && item.Durability == 0)
                || (PlayerbotItemScore.IsUnique(template) && PlayerbotItemScore.HasEquippedEntry(player, template.Entry))
                || player.Inventory.CanUseItem(item) != InventoryResult.Ok)
                continue;

            float score = PlayerbotItemScore.Score(item, weights, enchantments, spells);
            if (PlayerbotItemScore.UpgradeGain(player, template, score, weights, enchantments, spells, out byte slot) is not { } gain
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
        CheckEquipOutcome(player, item, slot);
        return true;
    }

    /// <summary>
    /// What the equip request did to <paramref name="item"/>, chosen for <paramref name="slot"/> (<see cref="InventorySlots.NullSlot"/>:
    /// a bag): still in the bags is refused (backed off, and reported as <c>gear/equip-not-applied</c>: the bot and the server's own
    /// CanEquipItem both judged it wearable), worn is checked for the right slot and for a swap loop.
    /// </summary>
    internal void CheckEquipOutcome(Player player, Item item, byte slot)
    {
        if (!InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot) || item.Inventory is null)
        {
            _refusedUntil[item.Guid.Value] = unchecked(session.World.NowMs + RefusalBackoffMs);
            PlayerbotAnomalies.Report(player, "gear", "equip-not-applied",
                string.Create(CultureInfo.InvariantCulture,
                    $"chose item {item.Entry} for slot {slot}, sent it, and it is still at bag {item.BagSlot} slot {item.Slot}"));
            return;
        }

        CheckEquippedWhere(player, item, slot);
    }

    /// <summary>
    /// The item landed in the equipment, but where the choice said? A named slot must hold it; no named slot (a bag) means a bag slot.
    /// Then the swap-loop check: the same item worn <see cref="FlapLimit"/> times inside <see cref="FlapWindowMs"/>.
    /// </summary>
    private void CheckEquippedWhere(Player player, Item item, byte slot)
    {
        bool right = slot == InventorySlots.NullSlot
            ? item.Slot >= InventorySlots.BagStart && item.Slot < InventorySlots.BagEnd
            : item.Slot == slot;
        if (!right)
        {
            PlayerbotAnomalies.Report(player, "gear", "equip-wrong-slot",
                string.Create(CultureInfo.InvariantCulture,
                    $"item {item.Entry} was meant for slot {(slot == InventorySlots.NullSlot ? "a bag slot" : slot.ToString(CultureInfo.InvariantCulture))} and sits in slot {item.Slot}"));
        }

        uint now = session.World.NowMs;
        _equipLog[_equipLogCount % EquipLogSize] = (item.Guid.Value, now);
        _equipLogCount++;
        int worn = 0;
        for (int index = 0; index < Math.Min(_equipLogCount, EquipLogSize); index++)
        {
            (ulong guid, uint at) = _equipLog[index];
            if (guid == item.Guid.Value && unchecked(now - at) <= FlapWindowMs) worn++;
        }

        if (worn >= FlapLimit)
        {
            for (int index = 0; index < EquipLogSize; index++)
                if (_equipLog[index].Guid == item.Guid.Value) _equipLog[index] = default;
            PlayerbotAnomalies.Report(player, "gear", "equip-flapping",
                string.Create(CultureInfo.InvariantCulture, $"item {item.Entry} worn {worn} times within {FlapWindowMs / 1000} s: two items keep replacing each other"));
        }
    }

    /// <summary>
    /// <c>gear/broken-unrepaired</c>: a worn item at zero durability (it gives nothing) for <see cref="BrokenGearMs"/> of world time while
    /// the bot lives. Looked at every <see cref="BrokenLookMs"/>: a walk over the equipment slots, no allocation. Info: a bot without
    /// the money cannot repair, but the count says how many go about broken.
    /// </summary>
    private void LookForBrokenGear(Player player)
    {
        uint now = session.World.NowMs;
        if (!player.IsInWorld || !player.IsAlive
            || player.Map is not { InstanceId: 0 }
            || player.Map.Template is { IsBattleground: true } or { IsDungeon: true })
        {
            _brokenSinceMs = 0;
            _brokenItemGuid = 0;
            _brokenReported = false;
            return;
        }

        if (unchecked((int)(_nextBrokenLookMs - now)) > 0) return;
        _nextBrokenLookMs = unchecked(now + BrokenLookMs);
        Item? broken = null;
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd && broken is null; slot++)
            if (slot != InventorySlots.Tabard && slot != InventorySlots.Body
                && player.Inventory.GetItem(InventorySlots.Bag0, slot) is { MaxDurability: > 0, Durability: 0 } worn)
                broken = worn;
        if (broken is null)
        {
            _brokenSinceMs = 0;
            _brokenItemGuid = 0;
            _brokenReported = false;
            return;
        }

        if (_brokenSinceMs == 0 || broken.Guid.Value != _brokenItemGuid)
        {
            _brokenItemGuid = broken.Guid.Value;
            _brokenSinceMs = Math.Max(1u, now);
            _brokenReported = false;
            return;
        }

        uint elapsed = unchecked(now - _brokenSinceMs);
        if (_brokenReported || elapsed < BrokenGearMs || elapsed > int.MaxValue) return;
        _brokenReported = true;
        PlayerbotAnomalies.Report(player, "gear", "broken-unrepaired",
            string.Create(CultureInfo.InvariantCulture, $"item {broken.Entry} in slot {broken.Slot} at zero durability for {elapsed} ms of world time (money {player.Money}c, level {player.Level}, repair trip due {PlayerbotTownGoals.NeedsRepairTrip(player)})"),
            ArcaneCore.Kernel.Diagnostics.AnomalySeverity.Info);
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
