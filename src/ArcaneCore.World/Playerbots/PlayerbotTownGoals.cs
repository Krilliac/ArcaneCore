using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Bounded ordinary vendor/trainer goals for one managed player.</summary>
internal sealed class PlayerbotTownGoals(WorldSession session, PlayerbotOptions options)
{
    private const uint ThinkCooldownMs = 1500;
    private uint _cooldownMs;
    private PlayerbotRoute? _route;
    private ObjectGuid _routeTarget;
    private ObjectGuid _trainerTarget;
    private bool _trainerListPending;
    private readonly HashSet<uint> _greenTrainerSpells = [];

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Explore;

    internal uint TargetEntry { get; private set; }

    internal bool HasCandidate(Player player)
        => FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer) is not null;

    internal bool Update(Player player, uint elapsedMs)
    {
        if (elapsedMs == 0 || !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat)
            return false;
        if (_cooldownMs > elapsedMs)
        {
            _cooldownMs -= elapsedMs;
            return false;
        }

        _cooldownMs = ThinkCooldownMs;
        QuestNpcServices? services = session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services is null)
            return false;

        if (_trainerListPending)
        {
            ReadTrainerList();
            if (_greenTrainerSpells.Count > 0 && services.InteractableNpc(player, _trainerTarget, NpcFlags.Trainer) is { } trainer)
            {
                if (!PlayerbotMovementControl.Stop(session, player)) return false;
                uint spell = services.Npcs.TrainerSpells(trainer.Entry).Where(s => s.SpellCost <= player.Money).Select(s => s.Spell)
                    .FirstOrDefault(_greenTrainerSpells.Contains);
                if (spell != 0)
                {
                    Goal = PlayerbotGoalKind.Train;
                    if (!session.TryManagedAction(WorldOpcode.CmsgTrainerBuySpell, TrainerPayload(trainer.Guid, spell))) return true;
                    _trainerListPending = false;
                    _greenTrainerSpells.Clear();
                    return true;
                }
            }
            _trainerListPending = false;
            _greenTrainerSpells.Clear();
            return false;
        }

        NpcInfo? npc = FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer);
        if (npc is null)
            return false;
        TargetEntry = npc.Entry;

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
        if (!PlayerbotMovementControl.Stop(session, player)) return false;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyFood(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TrySellGray(player, services, npc))
            return true;
        return (npc.NpcFlags & NpcFlags.Trainer) != 0 && TryTrain(player, services, npc);
    }

    private bool TryBuyFood(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (services.Deps.Items is not { } items)
            return false;
        foreach (VendorItem vendor in services.Npcs.VendorItems(npc.Entry).Take(32))
        {
            ItemInfo? info = items.GetItem(vendor.Item);
            if (info is null || player.Money < info.BuyPrice || player.Inventory.GetItemCount(vendor.Item) > 0)
                continue;
            if (info.BuyPrice == 0 || !IsFood(vendor.Item, items))
                continue;

            Goal = PlayerbotGoalKind.Vendor;
            return session.TryManagedAction(WorldOpcode.CmsgBuyItem, BuyPayload(npc.Guid, vendor.Item));
        }

        return false;
    }

    private bool TrySellGray(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (services.Deps.Items is not { } items)
            return false;
        if (services.Npcs.VendorItems(npc.Entry).Count == 0)
            return false;
        Item? item = player.Inventory.AllItems
            .Where(candidate => candidate.BagSlot != InventorySlots.Bag0 || candidate.Slot >= InventorySlots.ItemStart)
            .Where(candidate => !IsProtected(player, candidate, services))
            .Where(candidate => candidate.Template.Quality == 0 && candidate.Template.SellPrice > 0)
            .OrderBy(candidate => candidate.Guid.Value)
            .FirstOrDefault();
        if (item is null)
            return false;

        Goal = PlayerbotGoalKind.Vendor;
        return session.TryManagedAction(WorldOpcode.CmsgSellItem, SellPayload(npc.Guid, item.Guid));
    }

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

    private static bool HasUsefulService(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && services.Npcs.VendorItems(npc.Entry).Count > 0)
        {
            if (services.Deps.Items is { } items && player.Inventory.GetItemCount(117) == 0
                && services.Npcs.VendorItems(npc.Entry).Take(32)
                    .Any(row => IsFood(row.Item, items) && items.GetItem(row.Item) is { BuyPrice: > 0 } item
                        && player.Money >= item.BuyPrice)) return true;
            if (player.Inventory.AllItems.Any(item =>
                (item.BagSlot != InventorySlots.Bag0 || item.Slot >= InventorySlots.ItemStart)
                && item.Template.Quality == 0 && item.Template.SellPrice > 0
                && !IsProtected(player, item, services))) return true;
        }
        if ((npc.NpcFlags & NpcFlags.Trainer) == 0 || services.Deps.Spells is not { } spells
            || npc.TrainerType != TrainerType.Class || npc.TrainerClass != (byte)player.Class) return false;
        // Advisory discovery only; the correlated GREEN list and ordinary buy handler
        // remain the authority for price, prerequisites and teaching/persistence.
        return services.Npcs.TrainerSpells(npc.Entry).Take(128).Any(row =>
            spells.DescribeTrainerSpell(row.Spell) is { } info && info.LearnedSpell != 0
            && !spells.HasSpell(player, info.LearnedSpell)
            && spells.IsSpellFitByClassAndRace(player, info.LearnedSpell)
            && player.Level >= (row.ReqLevel != 0 ? row.ReqLevel : info.SpellLevel)
            && (info.ChainPrev == 0 || spells.HasSpell(player, info.ChainPrev))
            && (info.ChainReq == 0 || spells.HasSpell(player, info.ChainReq))
            && (row.ReqSkill == 0 || spells.GetSkillValueBase(player, row.ReqSkill) >= row.ReqSkillValue));
    }

    // Build-5875 starter food is the only content contract this lane can identify without
    // inventing a food catalog seam. Unknown consumables are never purchased automatically.
    private static bool IsFood(uint entry, IItemService items)
        => entry == 117 && items.GetItem(entry) is not null;

    private static bool IsProtected(Player player, Item item, QuestNpcServices services)
    {
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
