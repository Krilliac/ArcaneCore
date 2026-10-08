using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Bounded ordinary vendor/trainer goals for one managed player: repairs (mangoszero RepairAllAction: any equipped item broken or
/// under a quarter of its durability, at an NPC with the repair flag), food and drink, a hunter's ammunition, gray items, junk
/// when the bags are full, and trainer spells. Every request goes through the real NPC handler.
/// </summary>
internal sealed class PlayerbotTownGoals(WorldSession session, PlayerbotOptions options)
{
    private const uint ThinkCooldownMs = 1500;
    private const uint RepairBackoffMs = 60_000;

    /// <summary>How long a vendor's refusal to sell an item to the bot keeps the bot from asking again.</summary>
    internal const uint RefusedPurchaseMs = 600_000;

    /// <summary>How long a trainer that refused to teach is left alone.</summary>
    internal const uint RefusedTrainerMs = 600_000;

    /// <summary>The most refusals remembered (each kind); the oldest go first.</summary>
    private const int MaxRefusals = 64;

    /// <summary>A hunter restocks when the bags hold fewer rounds than this for the equipped bow, gun or crossbow.</summary>
    internal const uint AmmoLowWater = 200;

    private const uint WeaponBow = 2, WeaponGun = 3, WeaponCrossbow = 18;
    private uint _cooldownMs;
    private uint _repairBackoffUntil;
    private PlayerbotRoute? _route;
    private ObjectGuid _routeTarget;
    private ObjectGuid _trainerTarget;
    private bool _trainerListPending;
    private readonly HashSet<uint> _greenTrainerSpells = [];
    // Requests the server refused: the bot must not repeat them for ever. A vendor refuses to buy a damaged item when no repair
    // price is known for it (PlayerInventory.SellItem, TryAdjustSellPrice): on the live server Ironwander stood at Adlin
    // Pridedrift for hours re-sending CMSG_SELL_ITEM for its damaged gray Frayed Pants, answered SELL_ERR_CANT_SELL_ITEM each time.
    private readonly HashSet<ObjectGuid> _unsellable = [];
    private readonly Dictionary<(uint Vendor, uint Item), uint> _refusedPurchases = [];
    private readonly Dictionary<ObjectGuid, uint> _refusedTrainers = [];

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Explore;

    internal uint TargetEntry { get; private set; }

    internal bool HasCandidate(Player player)
        => FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer | NpcFlags.Repair) is not null;

    internal bool Update(Player player, uint elapsedMs)
    {
        if (elapsedMs == 0 || !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat)
            return false;
        _cooldownMs = elapsedMs >= _cooldownMs ? 0 : _cooldownMs - elapsedMs;
        QuestNpcServices? services = session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services is null)
            return false;

        if (_trainerListPending)
        {
            // Waiting for a response is stationary. Never leave the observer's
            // forward movement running while the interaction timer is held.
            if (!PlayerbotMovementControl.Stop(session, player)) return true;
            if (_cooldownMs != 0) return true;
            _cooldownMs = ThinkCooldownMs;
            ReadTrainerList();
            if (_greenTrainerSpells.Count > 0 && services.InteractableNpc(player, _trainerTarget, NpcFlags.Trainer) is { } trainer)
            {
                if (!PlayerbotMovementControl.Stop(session, player)) return false;
                uint spell = _greenTrainerSpells.OrderBy(id => id)
                    .FirstOrDefault(id => services.GetClassTrainerQuote(player, trainer, id) is not null);
                if (spell != 0)
                {
                    Goal = PlayerbotGoalKind.Train;
                    int known = KnownSpellCount(player);
                    uint money = player.Money;
                    if (!session.TryManagedAction(WorldOpcode.CmsgTrainerBuySpell, TrainerPayload(trainer.Guid, spell))) return true;
                    // The handler runs inside the action: nothing learned and nothing paid is a refusal.
                    if (KnownSpellCount(player) == known && player.Money == money) Refuse(_refusedTrainers, trainer.Guid, RefusedTrainerMs);
                    _trainerListPending = false;
                    _greenTrainerSpells.Clear();
                    return true;
                }
            }
            // Asked for the list and found nothing it would teach: the same question would get the same answer.
            Refuse(_refusedTrainers, _trainerTarget, RefusedTrainerMs);
            _trainerListPending = false;
            _greenTrainerSpells.Clear();
            return false;
        }

        NpcInfo? npc = FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer | NpcFlags.Repair);
        if (npc is null)
            return false;
        TargetEntry = npc.Entry;
        Goal = (npc.NpcFlags & NpcFlags.Trainer) != 0
            ? PlayerbotGoalKind.Train : PlayerbotGoalKind.Vendor;

        if (services.InteractableNpc(player, npc.Guid, NpcFlags.None) is null)
        {
            if (player.Map is not { }) return false;
            if (_route is null || _routeTarget != npc.Guid)
            {
                if (!PlayerbotNavigation.TryPlan(player, new(npc.X, npc.Y, npc.Z), options, out _route)) return false;
                _routeTarget = npc.Guid;
            }
            return PlayerbotNavigation.TryAdvance(session, _route!, options, elapsedMs, session.World.NowMs);
        }

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(session, player)) return true;
        if (_cooldownMs != 0) return true;
        // Throttle service requests, not travel. Motion must keep progressing
        // at the advertised speed until ordinary interaction distance is reached.
        _cooldownMs = ThinkCooldownMs;
        if ((npc.NpcFlags & NpcFlags.Repair) != 0 && TryRepair(player, npc))
            return true;
        // Full bags first make room (a purchase would only fail for space).
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TrySellJunk(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyFood(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyAmmo(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TrySellGray(player, services, npc))
            return true;
        return (npc.NpcFlags & NpcFlags.Trainer) != 0 && !Refused(_refusedTrainers, npc.Guid) && TryTrain(player, services, npc);
    }

    private bool TryBuyFood(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (session.Services.GetService<SpellFeature>()?.System is not { } spells)
            return false;
        VendorItem? vendor = FindConsumableVendorRow(player, services, npc, spells);
        if (vendor is not { } row)
            return false;

        Goal = PlayerbotGoalKind.Vendor;
        return Buy(player, npc, row.Item);
    }

    /// <summary>
    /// CMSG_BUY_ITEM; a purchase that added nothing to the bags was refused (no money, no room, sold out, a reputation or level
    /// gate): the row is not asked for again for <see cref="RefusedPurchaseMs"/>.
    /// </summary>
    private bool Buy(Player player, NpcInfo npc, uint item)
    {
        uint before = player.Inventory.GetItemCount(item);
        if (!session.TryManagedAction(WorldOpcode.CmsgBuyItem, BuyPayload(npc.Guid, item))) return false;
        if (player.Inventory.GetItemCount(item) <= before) Refuse(_refusedPurchases, (npc.Entry, item), RefusedPurchaseMs);
        return true;
    }

    /// <summary>
    /// CMSG_SELL_ITEM; an item still in the bags afterwards was refused by the vendor and is never offered again.
    /// </summary>
    private bool Sell(NpcInfo npc, Item item)
    {
        uint count = item.Count;
        if (!session.TryManagedAction(WorldOpcode.CmsgSellItem, SellPayload(npc.Guid, item.Guid))) return false;
        if (item.Inventory is not null && ReferenceEquals(item.Inventory.GetItemByGuid(item.Guid), item) && item.Count == count
            && _unsellable.Count < MaxRefusals)
            _unsellable.Add(item.Guid);
        return true;
    }

    private bool Refused<TKey>(Dictionary<TKey, uint> refusals, TKey key) where TKey : notnull
    {
        if (!refusals.TryGetValue(key, out uint until)) return false;
        if (unchecked((int)(until - session.World.NowMs)) > 0) return true;
        refusals.Remove(key);
        return false;
    }

    private void Refuse<TKey>(Dictionary<TKey, uint> refusals, TKey key, uint forMs) where TKey : notnull
    {
        uint now = session.World.NowMs;
        foreach (TKey expired in refusals.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            refusals.Remove(expired);
        if (refusals.Count >= MaxRefusals) refusals.Remove(refusals.OrderBy(entry => entry.Value).First().Key);
        refusals[key] = unchecked(now + forMs);
    }

    private int KnownSpellCount(Player player)
        => session.Services.GetService<SpellFeature>()?.Spellbook.GetSpells(player).Count ?? 0;

    /// <summary>
    /// CMSG_REPAIR_ITEM with an empty item guid: repair everything (vmangos HandleRepairItemOpcode, DurabilityRepairAll). When
    /// nothing got better (no repair prices loaded, too little money) repairs pause for a minute instead of repeating.
    /// </summary>
    private bool TryRepair(Player player, NpcInfo npc)
    {
        if (!RepairWanted(player))
            return false;
        Goal = PlayerbotGoalKind.Vendor;
        uint before = EquippedDurability(player);
        if (!session.TryManagedAction(WorldOpcode.CmsgRepairItem, RepairPayload(npc.Guid)))
            return false;
        if (EquippedDurability(player) <= before)
            _repairBackoffUntil = unchecked(session.World.NowMs + RepairBackoffMs);
        return true;
    }

    private bool RepairWanted(Player player)
        => player.Money > 0 && NeedsRepair(player)
            && (_repairBackoffUntil == 0 || unchecked(session.World.NowMs - _repairBackoffUntil) <= int.MaxValue);

    /// <summary>Any worn item broken or under 25% of its durability (mangoszero's playerbot repairs from its "durability" value).</summary>
    internal static bool NeedsRepair(Player player)
    {
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is { MaxDurability: > 0 } item
                && (item.Durability == 0 || item.Durability * 4 < item.MaxDurability))
                return true;
        }

        return false;
    }

    private static uint EquippedDurability(Player player)
    {
        uint total = 0;
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
            total += player.Inventory.GetItem(InventorySlots.Bag0, slot)?.Durability ?? 0;
        return total;
    }

    /// <summary>
    /// A hunter short of rounds for the equipped bow, gun or crossbow buys the best fitting ammunition the vendor sells (vmangos
    /// AddHunterAmmo, CombatBotBaseAI.cpp:2908, creates it instead; a bot buys). One CMSG_BUY_ITEM buys the item's BuyCount.
    /// </summary>
    private bool TryBuyAmmo(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (FindAmmoRow(player, services, npc) is not { } row)
            return false;
        Goal = PlayerbotGoalKind.Vendor;
        return Buy(player, npc, row.Item);
    }

    private VendorItem? FindAmmoRow(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (player.Class != Class.Hunter
            || player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged) is not { } ranged
            || (ItemClass)ranged.Template.Class != ItemClass.Weapon
            || ranged.Template.SubClass is not (WeaponBow or WeaponGun or WeaponCrossbow))
            return null;
        uint carried = 0;
        foreach (Item item in player.Inventory.AllItems)
        {
            if (item.Template.GetInventoryType() == InventoryType.Ammo && player.Inventory.CheckAmmoCompatibility(item.Template))
                carried += item.Count;
        }

        if (carried >= AmmoLowWater)
            return null;
        return services.Npcs.VendorItems(npc.Entry).Take(64)
            .Select(row => (Row: row, Template: player.Inventory.Templates.Find(row.Item)))
            .Where(pair => !Refused(_refusedPurchases, (npc.Entry, pair.Row.Item)))
            .Where(pair => pair.Template is { } template && template.GetInventoryType() == InventoryType.Ammo
                && player.Inventory.CheckAmmoCompatibility(template)
                && player.Inventory.CanUseAmmo(template.Entry) == ArcaneCore.Game.Items.InventoryResult.Ok
                && services.GetVendorPurchasePrice(player, npc, template.Entry) is { } price && price <= player.Money)
            .OrderByDescending(pair => pair.Template!.ItemLevel).ThenBy(pair => pair.Row.Item)
            .Select(pair => pair.Row).FirstOrDefault();
    }

    /// <summary>
    /// With no free bag slot left, sell the cheapest junk: a white (or gray) item that is not protected and is either not
    /// equipment or equipment that is no upgrade for the bot's build (including what it cannot use at all).
    /// </summary>
    private bool TrySellJunk(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (services.Npcs.VendorItems(npc.Entry).Count == 0 || FindJunk(player, services) is not { } item)
            return false;
        Goal = PlayerbotGoalKind.Vendor;
        return Sell(npc, item);
    }

    private Item? FindJunk(Player player, QuestNpcServices services)
    {
        if (FreeBagSlots(player) > 0)
            return null;
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        return player.Inventory.AllItems
            .Where(candidate => candidate.BagSlot != InventorySlots.Bag0
                || (candidate.Slot >= InventorySlots.ItemStart && candidate.Slot < InventorySlots.ItemEnd))
            .Where(candidate => candidate.Template.Quality <= 1 && candidate.Template.SellPrice > 0
                && !_unsellable.Contains(candidate.Guid)
                && !IsProtected(player, candidate, services) && IsJunk(player, candidate, weights))
            .OrderBy(candidate => (ulong)candidate.Template.SellPrice * candidate.Count).ThenBy(candidate => candidate.Guid.Value)
            .FirstOrDefault();
    }

    private static bool IsJunk(Player player, Item item, PlayerbotStatWeights weights)
    {
        if (item.Template.Quality == 0 || item.Template.AllowedEquipSlots(player.Class, canDualWield: true).Length == 0)
            return true;
        return PlayerbotItemScore.UpgradeGain(player, item.Template, PlayerbotItemScore.Score(item, weights, null), weights, null, out _)
            is not { } gain || gain <= PlayerbotItemScore.MinimumGain;
    }

    /// <summary>Free slots a normal item could go into: the backpack plus every general bag worn.</summary>
    internal static int FreeBagSlots(Player player)
    {
        int free = 0;
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is null)
                free++;
        }

        for (byte slot = InventorySlots.BagStart; slot < InventorySlots.BagEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is Container bag && bag.Template.IsGeneralBag())
                free += bag.FreeSlots;
        }

        return free;
    }

    private bool TrySellGray(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (services.Deps.Items is not { } items)
            return false;
        if (services.Npcs.VendorItems(npc.Entry).Count == 0)
            return false;
        Item? item = SellableGrays(player, services).FirstOrDefault();
        if (item is null)
            return false;

        Goal = PlayerbotGoalKind.Vendor;
        return Sell(npc, item);
    }

    /// <summary>The gray items the bot would sell, lowest guid first, without the ones a vendor already refused.</summary>
    private IEnumerable<Item> SellableGrays(Player player, QuestNpcServices services)
        => player.Inventory.AllItems
            .Where(candidate => candidate.BagSlot != InventorySlots.Bag0 || candidate.Slot >= InventorySlots.ItemStart)
            .Where(candidate => candidate.Template.Quality == 0 && candidate.Template.SellPrice > 0
                && !_unsellable.Contains(candidate.Guid))
            .Where(candidate => !IsProtected(player, candidate, services))
            .OrderBy(candidate => candidate.Guid.Value);

    private bool TryTrain(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (services.Deps.Spells is null || services.Npcs.TrainerSpells(npc.Entry).Count == 0)
            return false;
        _trainerTarget = npc.Guid;
        _trainerListPending = session.TryManagedAction(WorldOpcode.CmsgTrainerList, TrainerListPayload(npc.Guid));
        return _trainerListPending;
    }

    private void ReadTrainerList()
    {
        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(WorldOpcode.SmsgTrainerList))
        {
            if (packet.Payload.Length < 16) continue;
            var reader = new PacketReader(packet.Payload);
            if (reader.ReadUInt64() != _trainerTarget.Value) continue;
            _ = reader.ReadUInt32(); uint count = reader.ReadUInt32();
            if (count > 128 || packet.Payload.Length < 16 + (count * 38)) continue;
            for (uint index = 0; index < count; index++)
            {
                uint spell = reader.ReadUInt32(); byte state = reader.ReadByte();
                _ = reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadByte();
                reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32();
                if (state == (byte)TrainerSpellState.Green) _greenTrainerSpells.Add(spell);
            }
        }
    }

    private NpcInfo? FindNpc(Player player, NpcFlags required)
    {
        if (player.Map is null || session.Services.GetService<QuestNpcFeature>()?.Services is not { } services
            || services.Deps.Creatures is not { } lookup)
            return null;
        return player.VisibleObjects.Select(guid => lookup.Find(player, guid))
            .Where(info => info is { IsAlive: true, IsHostile: false, IsInCombat: false }
                && (info.NpcFlags & required) != 0 && !info.IsNotSelectable)
            .Select(info => info!)
            .Where(info => HasUsefulService(player, services, info))
            .OrderBy(info => Distance(player, info))
            .FirstOrDefault(info => (info.NpcFlags & required) != 0);
    }

    private bool HasUsefulService(Player player, QuestNpcServices services, NpcInfo npc)
        // Advisory discovery only; the correlated GREEN list and ordinary buy handler
        // remain the authority for price, prerequisites and teaching/persistence.
        => HasUsefulVendorService(player, services, npc)
            || (!Refused(_refusedTrainers, npc.Guid) && services.GetClassTrainerQuote(player, npc) is not null);

    private bool HasUsefulVendorService(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if ((npc.NpcFlags & NpcFlags.Repair) != 0 && RepairWanted(player)) return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && services.Npcs.VendorItems(npc.Entry).Count > 0)
        {
            if (session.Services.GetService<SpellFeature>()?.System is { } spells
                && FindConsumableVendorRow(player, services, npc, spells) is not null) return true;
            if (FindAmmoRow(player, services, npc) is not null) return true;
            if (SellableGrays(player, services).Any()) return true;
            if (FindJunk(player, services) is not null) return true;
        }
        return false;
    }

    private VendorItem? FindConsumableVendorRow(Player player, QuestNpcServices services, NpcInfo npc,
        SpellSystem spells)
    {
        bool needFood = !PlayerbotConsumables.HasUsable(player, spells, PlayerbotConsumableKind.Food);
        bool needDrink = player.PowerType == PowerType.Mana
            && player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana) > 0
            && !PlayerbotConsumables.HasUsable(player, spells, PlayerbotConsumableKind.Drink);
        if (!needFood && !needDrink)
            return null;

        VendorItem[] rows = [.. services.Npcs.VendorItems(npc.Entry).Take(32).Where(row => !Refused(_refusedPurchases, (npc.Entry, row.Item)))];
        foreach (VendorItem row in rows)
        {
            if (TryEligible(row, PlayerbotConsumableKind.Food, needFood, player, services, npc, spells))
                return row;
        }

        foreach (VendorItem row in rows)
        {
            if (TryEligible(row, PlayerbotConsumableKind.Drink, needDrink, player, services, npc, spells))
                return row;
        }
        return null;

        static bool TryEligible(VendorItem row, PlayerbotConsumableKind wanted, bool needed, Player player,
            QuestNpcServices services, NpcInfo npc, SpellSystem spells)
        {
            if (!needed || player.Inventory.Templates.Find(row.Item) is not { } template
                || player.Inventory.CanUseItem(template) != ArcaneCore.Game.Items.InventoryResult.Ok
                || !PlayerbotConsumables.TryClassify(template, spells, out PlayerbotConsumableKind kind)
                || kind != wanted)
                return false;
            return services.GetVendorPurchasePrice(player, npc, row.Item) is not null;
        }
    }

    private bool IsProtected(Player player, Item item, QuestNpcServices services)
    {
        if (session.Services.GetService<SpellFeature>()?.System is { } spells
            && PlayerbotConsumables.TryClassify(item.Template, spells, out _)) return true;
        bool questRequired = (services.StateOf(player)?.Quests.Statuses ?? new Dictionary<uint, QuestStatusData>())
            .Where(row => row.Value.Status is QuestStatus.Incomplete or QuestStatus.Complete)
            .SelectMany(row => services.Quests.Get(row.Key)?.ReqItemId ?? [])
            .Contains(item.Entry);
        return item.Entry == 117 || item.Template.BagFamily != 0 || item.Template.StartQuest != 0 || questRequired
            || item.Template.FoodType != 0 || item.Template.RequiredSkill != 0
            || (ItemClass)item.Template.Class is ItemClass.Quest or ItemClass.Reagent
            || item.Template.Bonding != 0;
    }

    private static byte[] BuyPayload(ObjectGuid vendor, uint item)
    {
        var writer = new PacketWriter(14);
        writer.WriteUInt64(vendor.Value); writer.WriteUInt32(item); writer.WriteByte(1); writer.WriteByte(0);
        return writer.ToArray();
    }

    /// <summary>cmsg_repair_item: u64 npc, u64 item (0: everything, vmangos HandleRepairItemOpcode).</summary>
    private static byte[] RepairPayload(ObjectGuid npc)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(npc.Value); writer.WriteUInt64(0);
        return writer.ToArray();
    }

    private static byte[] SellPayload(ObjectGuid vendor, ObjectGuid item)
    {
        var writer = new PacketWriter(17);
        writer.WriteUInt64(vendor.Value); writer.WriteUInt64(item.Value); writer.WriteByte(0);
        return writer.ToArray();
    }

    private static byte[] TrainerPayload(ObjectGuid trainer, uint spell)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(trainer.Value); writer.WriteUInt32(spell);
        return writer.ToArray();
    }

    private static byte[] TrainerListPayload(ObjectGuid trainer)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(trainer.Value);
        return writer.ToArray();
    }

    private static float Distance(Player player, NpcInfo npc)
        => MathF.Sqrt(MathF.Pow(player.X - npc.X, 2) + MathF.Pow(player.Y - npc.Y, 2) + MathF.Pow(player.Z - npc.Z, 2));
}
