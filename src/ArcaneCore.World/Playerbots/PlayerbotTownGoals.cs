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
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Bounded ordinary vendor/trainer goals for one managed player: repairs (mangoszero RepairAllAction: any equipped item broken or
/// under a quarter of its durability, at an NPC with the repair flag), food and drink, a hunter's ammunition, gray items, junk
/// when the bags are full, and trainer spells. Every request goes through the real NPC handler. Badly worn gear
/// (<see cref="NeedsRepairTrip"/>) is also a reason to travel to a repair NPC out of sight (<see cref="TravelToRepair"/>).
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

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of this controller's plans.</summary>
    private TileWait _tileWait;
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

    /// <summary>NPCs a stalled bot set aside (<see cref="PlayerbotStallWatch"/>).</summary>
    internal PlayerbotSuspensions? Suspensions { get; set; }

    internal uint TargetEntry { get; private set; }

    internal bool HasCandidate(Player player)
        => FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer | NpcFlags.Repair) is not null || RepairTripWanted(player);

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
            return TravelToRepair(player, services, elapsedMs);
        ClearRepairTrip();
        TargetEntry = npc.Entry;
        Goal = (npc.NpcFlags & NpcFlags.Trainer) != 0
            ? PlayerbotGoalKind.Train : PlayerbotGoalKind.Vendor;

        if (services.InteractableNpc(player, npc.Guid, NpcFlags.None) is null)
        {
            if (player.Map is not { }) return false;
            if (_route is null || _routeTarget != npc.Guid)
            {
                PlanOutcome visit = PlayerbotNavigation.PlanExact(player, new(npc.X, npc.Y, npc.Z), options, ref _tileWait,
                    session.World.NowMs, out _route);
                if (visit == PlanOutcome.Pending) return true; // a tile of the way is being read: keep the visit, plan again next think
                if (visit == PlanOutcome.Refused) return false;
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

    /// <summary>
    /// Gear worn badly enough to travel for (<see cref="TravelToRepair"/>): a worn item at 0 durability (vmangos Player::_ApplyItemMods
    /// skips a broken item, so it gives no armor and no stats), or all worn durability together under a fifth of its maximum. Live
    /// 2026-10-11 (analysis bot-xp-analysis.md, 4.1): 1,069 of 2,399 worn durable items of the population bots were at 0, and the median
    /// bot's average durability was 32%, because a repair happened only when a repair NPC was in sight.
    /// </summary>
    internal static bool NeedsRepairTrip(Player player)
    {
        ulong durability = 0, max = 0;
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is not { MaxDurability: > 0 } item) continue;
            if (item.Durability == 0) return true;
            durability += item.Durability;
            max += item.MaxDurability;
        }

        return max > 0 && durability * 5 < max;
    }

    /// <summary>The longest a repair trip goes without getting closer to its NPC before the spawn is set aside.</summary>
    internal const uint RepairTripStuckMs = 5_000;

    /// <summary>How long the bot leaves repair trips alone after one found nothing to go to (no affordable repair, no NPC, no way).</summary>
    internal const uint RepairTripBackoffMs = 30_000;

    /// <summary>At most this many repair NPC spawns of the map are kept as trip goals (the nearest first).</summary>
    private const int MaxRepairSpawns = 64;

    /// <summary>
    /// The most repair NPC spawns a route is planned to in one think (<see cref="BeginThink"/> opens it; the brain may call
    /// <see cref="Update"/> twice in one). Live (live-w27-r1): a bot whose start was off the mesh planned to every spawn of the map in
    /// one think, up to 1,740 queries (59 ms for 528 of them), 94 slow thinks in 4 minutes. The rest of the list waits for the next
    /// thinks; with the whole list refused the trip rests (<see cref="RepairTripBackoffMs"/>).
    /// </summary>
    internal const int MaxRepairPlansPerThink = 3;

    private int _repairPlansThisThink;
    private bool _repairClockedThisThink;
    private bool _thinksAreAnnounced;
    private uint _thinkStampMs;

    /// <summary>
    /// A new think begins: its repair plan budget (<see cref="MaxRepairPlansPerThink"/>) and its one stuck-clock tick are whole again.
    /// A caller that never announces its thinks is not stuck with the first think's budget for life: for it, a think is a world clock
    /// reading (<see cref="OpenThinkIfClockMoved"/>), so the budget and the stuck tick come back whenever the world clock has moved.
    /// </summary>
    internal void BeginThink()
    {
        _thinksAreAnnounced = true;
        _repairPlansThisThink = 0;
        _repairClockedThisThink = false;
    }

    /// <summary>For a caller that does not call <see cref="BeginThink"/>: the world clock moving on opens a new think.</summary>
    private void OpenThinkIfClockMoved(uint now)
    {
        if (_thinksAreAnnounced || now == _thinkStampMs) return;
        _thinkStampMs = now;
        _repairPlansThisThink = 0;
        _repairClockedThisThink = false;
    }

    private readonly List<(uint Entry, CreatureSpawn Spawn)> _repairSpawns = [];
    private readonly HashSet<uint> _blockedRepairSpawns = [];
    private (uint MapId, CreatureContent? Content, int Version) _repairIndex;
    private PlayerbotRoute? _repairRoute;
    private uint _repairSpawn;
    private uint _repairTripBackoffUntil;
    private uint _repairStuckMs;
    private float _repairBest = float.PositiveInfinity;

    /// <summary>
    /// A repair trip is worth starting: the gear needs it (<see cref="NeedsRepairTrip"/>), the last repair did not come to nothing a
    /// minute ago (<see cref="RepairBackoffMs"/>), the bot is not resting from an earlier trip that found nothing, and it can pay for at
    /// least one of its damaged worn items (<see cref="CheapestWornRepair"/>): with too little money the gear stays as it is, and the
    /// question is asked again at the next think.
    /// </summary>
    private bool RepairTripWanted(Player player)
    {
        if (!player.IsAlive || player.Money == 0 || !NeedsRepairTrip(player)) return false;
        uint now = session.World.NowMs;
        if (_repairBackoffUntil != 0 && unchecked(now - _repairBackoffUntil) > int.MaxValue) return false;
        if (_repairTripBackoffUntil != 0 && unchecked(now - _repairTripBackoffUntil) > int.MaxValue) return false;
        return session.Services.GetService<QuestNpcFeature>()?.Services is { } services
            && CheapestWornRepair(player, services) is { } cost && cost <= player.Money;
    }

    /// <summary>
    /// The price of the cheapest damaged worn item's repair, by the server's own pricing (<see cref="IItemService.Repair"/>, asked with
    /// a payment that refuses every price, so nothing is repaired or paid; no reputation discount: the real price is at most this).
    /// Null when nothing worn can be priced (no repair prices loaded: the repair would change nothing).
    /// </summary>
    internal static uint? CheapestWornRepair(Player player, QuestNpcServices services)
    {
        if (services.Deps.Items is not { } items) return null;
        uint? cheapest = null;
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is not { MaxDurability: > 0 } item || item.Durability >= item.MaxDurability)
                continue;
            items.Repair(player, item.Guid, 1f, cost =>
            {
                cheapest = Math.Min(cheapest ?? uint.MaxValue, cost);
                return false;
            });
        }

        return cheapest;
    }

    /// <summary>
    /// Walk toward the nearest repair NPC of the map (a creature_template with UNIT_NPC_FLAG_REPAIR that the bot's faction may approach,
    /// <see cref="PlayerbotTrainerDestinations.CanApproachTrainer"/>) while <see cref="RepairTripWanted"/> holds; once it is in sight the
    /// ordinary visit (<see cref="FindNpc"/>, <see cref="TryRepair"/>: CMSG_REPAIR_ITEM through vmangos HandleRepairItemOpcode, each item
    /// paid for or skipped when unaffordable) takes over. A spawn the walk cannot plan to, or stops closing on for
    /// <see cref="RepairTripStuckMs"/>, is set aside for this trip; with none left the trip rests for <see cref="RepairTripBackoffMs"/>.
    /// True while a movement step was taken.
    /// </summary>
    private bool TravelToRepair(Player player, QuestNpcServices services, uint elapsedMs)
    {
        if (!RepairTripWanted(player) || player.Map is not { } map)
        {
            ClearRepairTrip();
            return false;
        }

        uint now = session.World.NowMs;
        OpenThinkIfClockMoved(now);
        RefreshRepairSpawns(player, map.MapId, services);
        Vector3 here = new(player.X, player.Y, player.Z);
        // The spawn being walked to: closing on it resets the stuck clock; a route walked to its end (a route toward a far goal is
        // one leg of the way, PlayerbotNavigation.TryPlanToward) is planned again from here, and only no closing for
        // RepairTripStuckMs sets the spawn aside.
        if (_repairSpawn != 0 && _repairSpawns.FirstOrDefault(s => s.Spawn.Guid == _repairSpawn) is { Spawn: not null } current)
        {
            Vector3 at = new(current.Spawn.X, current.Spawn.Y, current.Spawn.Z);
            float left = Vector3.Distance(here, at);
            if (left < _repairBest - 0.5f) { _repairBest = left; _repairStuckMs = 0; }
            else if (!_repairClockedThisThink) _repairStuckMs += elapsedMs; // a second Update of one think does not run the clock twice
            _repairClockedThisThink = true;
            if (_repairStuckMs < RepairTripStuckMs && Suspensions?.IsEntrySuspended(current.Entry, now) != true)
            {
                Goal = PlayerbotGoalKind.Vendor;
                TargetEntry = current.Entry;
                // A leg walked to its end (the motion stops there without completing the route) is followed by the next one.
                if (_repairRoute is null || _repairRoute.Complete || _repairRoute.Points.Count == 0
                    || Vector3.Distance(here, _repairRoute.Points[^1]) < 1f)
                {
                    PlanOutcome next = PlayerbotNavigation.PlanToward(player, at, options, ref _tileWait, now, out PlayerbotRoute? leg);
                    _repairRoute = leg;
                    if (next == PlanOutcome.Pending)
                    {
                        // A tile of the next leg is being read: the walk waits, it is not stuck. Give back this think's stuck time.
                        _repairStuckMs = _repairStuckMs > elapsedMs ? _repairStuckMs - elapsedMs : 0;
                        return true;
                    }
                }
                if (_repairRoute is not null && PlayerbotNavigation.TryAdvance(session, _repairRoute, options, elapsedMs, now)) return true;
                _repairRoute = null;
                return false; // planned again at the next think, until the stuck clock runs out
            }

            _blockedRepairSpawns.Add(_repairSpawn);
            ClearRepairTrip();
        }

        // At most MaxRepairPlansPerThink new candidates are planned to in one think (however many Updates it makes), nearest first;
        // the rest wait for the next think, the blocked set keeping the refused ones out. A refused plan blocks its spawn as ever: a
        // start off the mesh still reaches some spawns (the steep-ground escape depends on the destination), so none is skipped.
        bool tilesPending = false;
        foreach ((uint entry, CreatureSpawn spawn) in _repairSpawns.Where(s => !_blockedRepairSpawns.Contains(s.Spawn.Guid))
                     .OrderBy(s => Vector3.DistanceSquared(here, new Vector3(s.Spawn.X, s.Spawn.Y, s.Spawn.Z))).ToArray())
        {
            if (Suspensions?.IsEntrySuspended(entry, now) == true) continue;
            if (_repairPlansThisThink >= MaxRepairPlansPerThink) return false;
            _repairPlansThisThink++;
            PlanOutcome planned = PlayerbotNavigation.PlanToward(player, new Vector3(spawn.X, spawn.Y, spawn.Z), options, ref _tileWait, now,
                out PlayerbotRoute? route);
            if (planned == PlanOutcome.Pending)
            {
                // Not a verdict on this spawn (a tile of the way is being read): leave it unblocked and plan again next think.
                tilesPending = true;
                continue;
            }
            if (planned == PlanOutcome.Refused || route is null)
            {
                _blockedRepairSpawns.Add(spawn.Guid);
                continue;
            }

            _repairRoute = route;
            _repairSpawn = spawn.Guid;
            _repairBest = Vector3.Distance(here, new Vector3(spawn.X, spawn.Y, spawn.Z));
            _repairStuckMs = 0;
            Goal = PlayerbotGoalKind.Vendor;
            TargetEntry = entry;
            if (PlayerbotNavigation.TryAdvance(session, route, options, elapsedMs, now)) return true;
            _blockedRepairSpawns.Add(spawn.Guid);
            ClearRepairTrip();
        }

        // Candidates waiting for tiles are neither walked to nor refused: no rest and nothing cleared, the same ones are planned again.
        if (tilesPending)
        {
            Goal = PlayerbotGoalKind.Vendor;
            return true;
        }

        // Nothing to walk to: rest from trips a while, then look again with every spawn.
        ClearRepairTrip();
        _blockedRepairSpawns.Clear();
        _repairTripBackoffUntil = Math.Max(1u, unchecked(now + RepairTripBackoffMs));
        return false;
    }

    private void ClearRepairTrip()
    {
        _repairRoute = null;
        _repairSpawn = 0;
        _repairStuckMs = 0;
        _repairBest = float.PositiveInfinity;
    }

    /// <summary>The repair NPC spawns of the map the bot may approach, kept until the map or the creature content changes.</summary>
    private void RefreshRepairSpawns(Player player, uint mapId, QuestNpcServices services)
    {
        CreatureContent? content = session.Services.GetService<CreatureWorldFeature>()?.Content;
        if (content is null) { _repairSpawns.Clear(); return; }
        if (_repairIndex.MapId == mapId && ReferenceEquals(_repairIndex.Content, content) && _repairIndex.Version == content.DefinitionsVersion)
            return;
        _repairIndex = (mapId, content, content.DefinitionsVersion);
        _repairSpawns.Clear();
        _blockedRepairSpawns.Clear();
        ClearRepairTrip();
        FactionTemplateCatalog? factions = session.Services.GetService<QuestNpcFeature>()?.FactionTemplates;
        Vector3 here = new(player.X, player.Y, player.Z);
        foreach (CreatureTemplate template in content.Templates)
        {
            if (((NpcFlags)template.NpcFlags & NpcFlags.Repair) == 0
                || !PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, template.Faction)) continue;
            foreach (CreatureSpawn spawn in content.GetSpawns(mapId, template.Entry))
                if (float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z)) _repairSpawns.Add((template.Entry, spawn));
        }

        // Bounded: the nearest of the map's repair NPCs from where the bot was when the index was built.
        if (_repairSpawns.Count > MaxRepairSpawns)
        {
            _repairSpawns.Sort((a, b) => Vector3.DistanceSquared(here, new Vector3(a.Spawn.X, a.Spawn.Y, a.Spawn.Z))
                .CompareTo(Vector3.DistanceSquared(here, new Vector3(b.Spawn.X, b.Spawn.Y, b.Spawn.Z))));
            _repairSpawns.RemoveRange(MaxRepairSpawns, _repairSpawns.Count - MaxRepairSpawns);
        }
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
                && (info.NpcFlags & required) != 0 && !info.IsNotSelectable
                && Suspensions?.IsEntrySuspended(info.Entry, session.World.NowMs) != true)
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
