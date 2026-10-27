using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
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
using ArcaneCore.World.WorldState;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using System.Numerics;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Bounded ordinary vendor/trainer goals for one managed player: repairs (mangoszero RepairAllAction: any equipped item broken or
/// under a quarter of its durability, at an NPC with the repair flag), food and drink, a hunter's ammunition, gray items, junk
/// when the bags are full, and trainer spells. Every request goes through the real NPC handler. Badly worn gear
/// (<see cref="NeedsRepairTrip"/>) is also a reason to travel to a repair NPC out of sight (<see cref="TravelToRepair"/>), and full bags
/// with something to sell (<see cref="SellTripNeeded"/>) a reason to travel to a vendor.
/// </summary>
internal sealed partial class PlayerbotTownGoals(WorldSession session, PlayerbotOptions options)
{
    private const uint ThinkCooldownMs = 1500;
    private const uint RepairBackoffMs = 60_000;

    /// <summary>How long a refused plan to a visible service NPC is remembered before the walk to it is planned again.</summary>
    internal const uint VisitRefusedMs = 10_000;

    private ObjectGuid _visitRefused;
    private uint _visitRefusedUntil;

    /// <summary>How long a class trainer that had nothing for the bot is not asked again (unless its level changes, it has more money or its class-training spend limit grows).</summary>
    internal const uint TrainerQuoteMemoMs = 5_000;

    private readonly Dictionary<ObjectGuid, (uint Until, uint Level, uint Money, uint Limit)> _noTrainerOffer = [];

    /// <summary>How many times a class trainer's offer was worked out rather than read from the memo (tests).</summary>
    internal int TrainerQuotesAsked { get; private set; }

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
    private bool _cooldownClockedThisThink;
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

    /// <summary>
    /// What the bot may spend (<see cref="PlayerbotBudget"/>); null spends all its money (tests that build a bare controller). Class
    /// training, food, drink and ammunition are bought within it; repairs and selling are not limited by it.
    /// </summary>
    internal PlayerbotBudget? Budget { get; set; }

    /// <summary>The most a class training offer may cost: the money the budget leaves the class spells (all of it without a budget).</summary>
    private uint? ClassTrainingLimit(Player player) => Budget?.Spendable(PlayerbotSpendKind.ClassTraining, player);

    internal bool HasCandidate(Player player)
        => FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer | NpcFlags.Repair) is not null || TripWanted(player) != NpcFlags.None;

    internal static bool HasBrokenWornGear(Player player)
    {
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (slot != InventorySlots.Tabard && slot != InventorySlots.Body
                && player.Inventory.GetItem(InventorySlots.Bag0, slot) is { MaxDurability: > 0, Durability: 0 })
                return true;
        }

        return false;
    }

    internal bool HasUrgentRepairOrSell(Player player)
        => TripWanted(player) != NpcFlags.None || FindNpc(player, NpcFlags.Repair | NpcFlags.Vendor) is not null;

    internal bool Update(Player player, uint elapsedMs)
    {
        if (elapsedMs == 0 || !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat)
            return false;
        // A second Update in one announced think (the goal policy's promotion, then the ladder's rung) does not run the cooldown twice.
        if (!_thinksAreAnnounced || !_cooldownClockedThisThink)
        {
            _cooldownMs = elapsedMs >= _cooldownMs ? 0 : _cooldownMs - elapsedMs;
            _cooldownClockedThisThink = true;
        }
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
                uint? limit = ClassTrainingLimit(player);
                uint spell = _greenTrainerSpells.OrderBy(id => id)
                    .FirstOrDefault(id => trainer.TrainerType switch
                    {
                        TrainerType.Mounts => RidingQuote(player, services, trainer, id) is not null,
                        TrainerType.TradeSkills => TradeSkillQuote(player, services, trainer, id) is not null,
                        _ => services.GetClassTrainerQuote(player, trainer, id, limit) is not null,
                    });
                if (spell != 0)
                {
                    Goal = PlayerbotGoalKind.Train;
                    int known = KnownSpellCount(player);
                    uint money = player.Money;
                    if (!session.TryManagedAction(WorldOpcode.CmsgTrainerBuySpell, TrainerPayload(trainer.Guid, spell))) return true;
                    // The handler runs inside the action: nothing learned and nothing paid is a refusal.
                    if (KnownSpellCount(player) == known && player.Money == money) Refuse(_refusedTrainers, trainer.Guid, RefusedTrainerMs);
                    else Budget?.Invalidate();
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

        NpcFlags wantedTrip = TripWanted(player);
        NpcInfo? npc = _repairSpawn != 0 && wantedTrip == NpcFlags.Repair
            ? FindNpc(player, NpcFlags.Repair)
            : (wantedTrip == NpcFlags.Repair
                ? FindNpc(player, NpcFlags.Repair) ?? FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer)
                : FindNpc(player, NpcFlags.Vendor | NpcFlags.Trainer | NpcFlags.Repair));
        if (npc is null)
            return TravelToRepair(player, services, elapsedMs);
        TargetEntry = npc.Entry;
        Goal = (npc.NpcFlags & NpcFlags.Trainer) != 0
            ? PlayerbotGoalKind.Train : PlayerbotGoalKind.Vendor;

        if (services.InteractableNpc(player, npc.Guid, NpcFlags.None) is null)
        {
            if (player.Map is not { }) return false;
            if (_route is null || _routeTarget != npc.Guid)
            {
                // A plan to this NPC was refused a moment ago (after its fallbacks, 30 to 40 queries): not asked again every think.
                if (_visitRefused == npc.Guid && unchecked((int)(_visitRefusedUntil - session.World.NowMs)) > 0)
                    return TravelToRepair(player, services, elapsedMs);
                PlanOutcome visit = PlayerbotNavigation.PlanExact(player, new(npc.X, npc.Y, npc.Z), options, ref _tileWait,
                    session.World.NowMs, out _route);
                if (visit == PlanOutcome.Pending) return true; // a tile of the way is being read: keep the visit, plan again next think
                if (visit == PlanOutcome.Refused)
                {
                    _visitRefused = npc.Guid;
                    _visitRefusedUntil = unchecked(session.World.NowMs + VisitRefusedMs);
                    return TravelToRepair(player, services, elapsedMs);
                }
                _routeTarget = npc.Guid;
                _visitAnchor = new(player.X, player.Y, player.Z);
                _visitStuckMs = 0;
            }
            // A route that was planned but is not walked (the bot never leaves its place) is given up like a refused plan; before, only the
            // stall watch ended it, 120 s later (GitHub #1476).
            if (VisitStuck(player, elapsedMs))
            {
                uint now = session.World.NowMs;
                _visitRefused = npc.Guid;
                _visitRefusedUntil = unchecked(now + VisitRefusedMs);
                Suspensions?.SuspendEntryEscalating(npc.Entry, now);
                _route = null;
                _routeTarget = default;
                _visitStuckMs = 0;
                PlayerbotMovementControl.Stop(session, player);
                return TravelToRepair(player, services, elapsedMs);
            }
            ClearRepairTrip();
            return PlayerbotNavigation.TryAdvance(session, _route!, options, elapsedMs, session.World.NowMs);
        }

        ClearRepairTrip();

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(session, player)) return true;
        if (_cooldownMs != 0) return true;
        // Throttle service requests, not travel. Motion must keep progressing
        // at the advertised speed until ordinary interaction distance is reached.
        _cooldownMs = ThinkCooldownMs;
        if ((npc.NpcFlags & NpcFlags.Repair) != 0 && TryRepair(player, services, npc))
            return true;
        // Full bags first make room (a purchase would only fail for space).
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TrySellJunk(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyFood(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyAmmo(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyMount(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyEquipmentUpgrade(player, services, npc))
            return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && TryBuyCraftingSupplies(player, services, npc))
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
    private bool TryRepair(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (!RepairWanted(player))
            return false;
        // Not even the cheapest damaged item is affordable: the handler would repair nothing and start the one-minute backoff, and the
        // junk sale that would pay for it (TrySellJunk, forMoney) would wait behind that. Live 2026-10-17: broken-unrepaired at 0 to 4 copper.
        if (CheapestWornRepair(player, services) is { } cheapest && cheapest > player.Money)
            return false;
        Goal = PlayerbotGoalKind.Vendor;
        uint before = EquippedDurability(player);
        ObjectGuid targetItem = TotalWornRepair(player, services) is { } total && total > player.Money
            && CheapestBrokenWornItem(player, services) is { } broken && broken.Cost <= player.Money
            ? broken.Guid : default;
        if (!session.TryManagedAction(WorldOpcode.CmsgRepairItem, RepairPayload(npc.Guid, targetItem)))
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

    /// <summary>
    /// The rest doubles with every round in a row that found nothing to go to (<see cref="RepairTripBackoffMs"/>, twice, four times ...) up
    /// to this. Before, a bot with no way to any repair NPC planned to every spawn of the map again every 30 seconds, for hours: 18
    /// plans of 30 to 40 queries each, a round after round. A plan that succeeds, or the bot being <see cref="RepairBackoffResetYards"/>
    /// from where the last round ended, starts the rests over.
    /// </summary>
    internal const uint RepairTripBackoffMaxMs = 300_000;

    /// <summary>How far from where the last empty round ended the bot has to be for the rest to be over (the way from there may be open).</summary>
    internal const float RepairBackoffResetYards = 300f;

    private int _repairEmptyRounds;
    private Vector3 _repairRestFrom;
    private uint _repairRestMap;

    /// <summary>At most this many repair NPC spawns of the map are kept as trip goals (the nearest first).</summary>
    private const int MaxRepairSpawns = 64;

    /// <summary>
    /// The most repair NPC spawns a route is planned to in one think (<see cref="BeginThink"/> opens it; the brain may call
    /// <see cref="Update"/> twice in one). Live (live-w27-r1): a bot whose start was off the mesh planned to every spawn of the map in
    /// one think, up to 1,740 queries (59 ms for 528 of them), 94 slow thinks in 4 minutes. The rest of the list waits for the next
    /// thinks; with the whole list refused the trip rests (<see cref="RepairTripBackoffMs"/>).
    /// </summary>
    internal const int MaxRepairPlansPerThink = 3;

    /// <summary>
    /// The longest a visit leg (the walk to a service NPC in sight) goes without the bot leaving its place before the NPC is set aside.
    /// Longer than <see cref="RepairTripStuckMs"/>: a visit's route may start by walking away from the NPC (a ramp), which the repair
    /// trip's "closing on it" clock would count as stuck, so this clock counts only not moving at all.
    /// </summary>
    internal const uint VisitStuckMs = 10_000;

    /// <summary>The distance (yards) from its anchor that counts as the visiting bot moving.</summary>
    internal const float VisitProgressYards = 2f;

    private Vector3 _visitAnchor;
    private uint _visitStuckMs;

    /// <summary>Ticks the visit leg's stuck clock once per think (never while a mount step holds the bot); true once <see cref="VisitStuckMs"/> passed without the bot moving <see cref="VisitProgressYards"/>.</summary>
    private bool VisitStuck(Player player, uint elapsedMs)
    {
        OpenThinkIfClockMoved(session.World.NowMs);
        Vector3 here = new(player.X, player.Y, player.Z);
        if (Vector3.DistanceSquared(here, _visitAnchor) >= VisitProgressYards * VisitProgressYards)
        {
            _visitAnchor = here;
            _visitStuckMs = 0;
        }
        else if (!_visitClockedThisThink && !PlayerbotMount.IsBusy(session)) _visitStuckMs += elapsedMs;
        _visitClockedThisThink = true;
        return _visitStuckMs >= VisitStuckMs;
    }

    private bool _visitClockedThisThink;
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
        _visitClockedThisThink = false;
        _cooldownClockedThisThink = false;
        MarkThinkQueries();
    }

    /// <summary>For a caller that does not call <see cref="BeginThink"/>: the world clock moving on opens a new think.</summary>
    private void OpenThinkIfClockMoved(uint now)
    {
        if (_thinksAreAnnounced || now == _thinkStampMs) return;
        _thinkStampMs = now;
        _repairPlansThisThink = 0;
        _repairClockedThisThink = false;
        _visitClockedThisThink = false;
        MarkThinkQueries();
    }

    /// <summary>
    /// The most path queries, and the most query time, the repair plans of one think may spend before the rest of the candidates wait
    /// for the next think (<see cref="RepairQueryBudgetSpent"/>). Live (live-w34-r1, 2026-10-12): 618 of 773 slow bot thinks (&gt;50 ms)
    /// were slowest in the town phase, 114 path queries each on average, 0.6 ms of the phase's time not path search: a plan that is refused
    /// runs its fallbacks (the steep escape, the starts beside the bot, hazard detours), 30 to 40 queries, and
    /// <see cref="MaxRepairPlansPerThink"/> of them made the think 100. A plan that comes cheap (one query) still lets the others follow.
    /// </summary>
    internal const int MaxRepairQueriesPerThink = 16;

    private const long MaxRepairQueryMsPerThink = 5;

    /// <summary>The query time (Stopwatch ticks) a think may spend on repair plans: <see cref="MaxRepairQueryMsPerThink"/> by default; a test sets it to pin one budget without the other depending on the machine load.</summary>
    internal long RepairQueryTicksLimit { get; set; } = MaxRepairQueryMsPerThink * System.Diagnostics.Stopwatch.Frequency / 1000;

    private int _queriesAtThinkStart;
    private long _queryTicksAtThinkStart;

    private void MarkThinkQueries()
    {
        PlayerbotThinkProbe? probe = PlayerbotThinkProbe.Active;
        _queriesAtThinkStart = probe?.Queries ?? 0;
        _queryTicksAtThinkStart = probe?.QueryTicks ?? 0;
    }

    /// <summary>Whether the path queries made since this think began (<see cref="BeginThink"/>) are over the repair budget; never off a think.</summary>
    private bool RepairQueryBudgetSpent()
        => PlayerbotThinkProbe.Active is { } probe
            && (probe.Queries - _queriesAtThinkStart >= MaxRepairQueriesPerThink
                || probe.QueryTicks - _queryTicksAtThinkStart >= RepairQueryTicksLimit);

    private readonly List<(uint Entry, CreatureSpawn Spawn)> _repairSpawns = [];
    private readonly List<(uint Entry, CreatureSpawn Spawn)> _repairServiceSpawns = [];
    private readonly List<(uint Entry, CreatureSpawn Spawn)> _vendorServiceSpawns = [];
    private readonly HashSet<uint> _blockedRepairSpawns = [];
    private (uint MapId, CreatureContent? Content, int Version, int Events, uint Faction, int Ranks) _repairIndex;

    /// <summary>The faction templates of this map's service NPCs the index was built from: whether the bot may use each is part of its cache key (<see cref="RankStamp"/>).</summary>
    private uint[] _repairStampTemplates = [];

    private FactionTemplateCatalog? _repairStampFactions;

    /// <summary>The vendor spawns the last <see cref="RefreshRepairSpawns"/> found usable (a test's view of the index).</summary>
    internal IReadOnlyList<(uint Entry, CreatureSpawn Spawn)> VendorServiceSpawns => _vendorServiceSpawns;
    private PlayerbotRoute? _repairRoute;
    private uint _repairSpawn;
    private uint _repairMissReported;

    /// <summary>A bot this near (yards) the spawn point of the repair NPC it walked to, and seeing no usable NPC, reports it missing (<c>town/service-npc-missing</c>).</summary>
    internal const float RepairArrivedYards = 8f;
    private uint _repairTripBackoffUntil;
    private uint _repairStuckMs;
    private float _repairBest = float.PositiveInfinity;
    private bool _repairToLift; // _repairBest measures to a lift's landing (PlayerbotRoute.ToLiftLanding), not to the NPC

    /// <summary>
    /// The trip worth starting to a service NPC out of sight, as the service it is for (<see cref="NpcFlags.None"/>: none). A repair
    /// (<see cref="NpcFlags.Repair"/>) first: the gear needs it (<see cref="NeedsRepairTrip"/>), the last repair did not come to nothing a
    /// minute ago (<see cref="RepairBackoffMs"/>), and the bot can pay for at least one of its damaged worn items (<see cref="CheapestWornRepair"/>):
    /// with too little money the gear stays as it is, and the question is asked again at the next think. Else a sale
    /// (<see cref="NpcFlags.Vendor"/>): the bags are full and hold something the bot sells (<see cref="SellTripNeeded"/>). Either waits while
    /// the bot rests from an earlier trip that found nothing.
    /// </summary>
    private NpcFlags TripWanted(Player player)
    {
        if (!player.IsAlive) return NpcFlags.None;
        QuestNpcServices? services = session.Services.GetService<QuestNpcFeature>()?.Services;
        bool repairDue = NeedsRepairTrip(player);
        bool sellDue = services is not null && SellTripNeeded(player, services);
        if (!repairDue && !sellDue)
        {
            _repairEmptyRounds = 0; // the gear is fine and the bags are not full (whatever the bot's money): the next trip starts with the short rest again
            return NpcFlags.None;
        }

        uint now = session.World.NowMs;
        bool repair = repairDue && player.Money != 0
            && !(_repairBackoffUntil != 0 && unchecked(now - _repairBackoffUntil) > int.MaxValue);
        if (!repair && !sellDue) return NpcFlags.None;
        if (_repairEmptyRounds > 0)
        {
            // Another map, or far from where the last round found nothing (also once the rest is over): the way from here may be open,
            // so the rests start over.
            if (player.MapId != _repairRestMap
                || Vector3.DistanceSquared(_repairRestFrom, new Vector3(player.X, player.Y, player.Z)) >= RepairBackoffResetYards * RepairBackoffResetYards)
            {
                _repairTripBackoffUntil = 0;
                _repairEmptyRounds = 0;
            }
            else if (_repairTripBackoffUntil != 0 && unchecked(now - _repairTripBackoffUntil) > int.MaxValue) return NpcFlags.None;
        }

        if (repair && services is not null && CheapestWornRepair(player, services) is { } cost && cost <= player.Money) return NpcFlags.Repair;
        return sellDue ? NpcFlags.Vendor : NpcFlags.None;
    }

    /// <summary>
    /// Full bags (no free slot, <see cref="FreeBagSlots"/>) holding something the bot sells (junk, <see cref="FindJunk"/>, or a gray item,
    /// <see cref="SellableGrays"/>): a reason to walk to a vendor out of sight, as badly worn gear is to a repair NPC. Before, junk was sold
    /// only when a vendor happened to be in sight, and a bot with full bags left every corpse it killed unlooted and went on exploring and
    /// killing (bot QA #684, #700, #705, #741, #845, #884 and more: <c>loot-bag-full -> goal=Explore</c> in the trace before the stall).
    /// </summary>
    private bool SellTripNeeded(Player player, QuestNpcServices services)
        => (FreeBagSlots(player) == 0 && (FindJunk(player, services) is not null || SellableGrays(player, services).Any()))
           || (RepairUnaffordable(player, services) && (FindJunk(player, services, forMoney: true) is not null || SellableGrays(player, services).Any()));

    /// <summary>
    /// The gear needs a repair trip (<see cref="NeedsRepairTrip"/>) and the bot cannot pay even its cheapest damaged item's repair
    /// (<see cref="CheapestWornRepair"/>): it sells its junk and grays for the money, whatever room its bags have. Before, a broke bot
    /// waited for money that never came (live 2026-10-17: bot-gear/broken-unrepaired ~2000/h, money 0 to 4 copper, "repair trip due
    /// True"), fighting with a broken weapon and dying more, which broke more gear.
    /// </summary>
    private bool RepairUnaffordable(Player player, QuestNpcServices services)
        => NeedsRepairTrip(player) && CheapestWornRepair(player, services) is { } cost && cost > player.Money;

    /// <summary>
    /// The price of the cheapest damaged worn item's repair, by the server's own pricing (<see cref="IItemService.Repair"/>, asked with
    /// a payment that refuses every price, so nothing is repaired or paid; no reputation discount: the real price is at most this).
    /// Null when nothing worn can be priced (no repair prices loaded: the repair would change nothing).
    /// </summary>
    internal static uint? CheapestWornRepair(Player player, QuestNpcServices services)
    {
        if (CheapestBrokenWornItem(player, services) is { } broken)
            return broken.Cost;
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

    private static (ObjectGuid Guid, uint Cost)? CheapestBrokenWornItem(Player player, QuestNpcServices services)
    {
        if (services.Deps.Items is not { } items) return null;
        (ObjectGuid Guid, uint Cost)? best = null;
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is not { MaxDurability: > 0, Durability: 0 } item)
                continue;
            items.Repair(player, item.Guid, 1f, cost =>
            {
                if (best is null || cost < best.Value.Cost)
                    best = (item.Guid, cost);
                return false;
            });
        }

        return best;
    }

    /// <summary>
    /// Walk toward the nearest repair NPC of the map (a creature_template with UNIT_NPC_FLAG_REPAIR that the bot's faction may approach,
    /// <see cref="PlayerbotTrainerDestinations.CanApproachTrainer"/>) while <see cref="TripWanted"/> names a trip (or toward the nearest vendor, for a sale); once it is in sight the
    /// ordinary visit (<see cref="FindNpc"/>, <see cref="TryRepair"/>: CMSG_REPAIR_ITEM through vmangos HandleRepairItemOpcode, each item
    /// paid for or skipped when unaffordable) takes over. A spawn the walk cannot plan to, or stops closing on for
    /// <see cref="RepairTripStuckMs"/>, is set aside for this trip; with none left the trip rests for <see cref="RepairTripBackoffMs"/>.
    /// True while a movement step was taken.
    /// </summary>
    private bool TravelToRepair(Player player, QuestNpcServices services, uint elapsedMs)
    {
        NpcFlags service = TripWanted(player);
        if (service == NpcFlags.None || player.Map is not { } map)
        {
            ClearRepairTrip();
            return false;
        }

        uint now = session.World.NowMs;
        OpenThinkIfClockMoved(now);
        RefreshRepairSpawns(player, map.MapId, services, service);
        Vector3 here = new(player.X, player.Y, player.Z);
        // The spawn being walked to: closing on it resets the stuck clock; a route walked to its end (a route toward a far goal is
        // one leg of the way, PlayerbotNavigation.TryPlanToward) is planned again from here, and only no closing for
        // RepairTripStuckMs sets the spawn aside.
        if (_repairSpawn != 0 && _repairSpawns.FirstOrDefault(s => s.Spawn.Guid == _repairSpawn) is { Spawn: not null } current
            && SpawnPresent(player, current.Spawn)
            && PlayerbotWorldDestinations.TryResolveSpawnPosition(player, current.Spawn, requireLivePatroller: true, out Vector3 at))
        {
            float left = Vector3.Distance(here, at);
            // The walk to a lift's landing may lead away from the NPC: the stuck clock measures to the landing then, and starts over when it switches.
            bool toLift = _repairRoute is { ToLiftLanding: true };
            float walked = toLift ? Vector3.Distance(here, _repairRoute!.LiftLanding) : left;
            if (toLift != _repairToLift) { _repairToLift = toLift; _repairBest = walked; _repairStuckMs = 0; }
            // Arrived at the repair NPC's own spawn point with no live creature of that entry there: the NPC is not there.
            if (left <= RepairArrivedYards && current.Spawn.MovementType != 2 && _repairMissReported != _repairSpawn
                && !HasVisibleEntry(player, current.Entry)
                && Suspensions?.IsEntrySuspended(current.Entry, now) != true && !PlayerbotNavigationAnomalies.Silenced(player))
            {
                _repairMissReported = _repairSpawn;
                PlayerbotAnomalies.Report(player, "town", "service-npc-missing",
                    $"stands {left:F1} yd from the spawn of {(service == NpcFlags.Repair ? "repair NPC" : "vendor")} {current.Entry} (spawn {_repairSpawn}) and finds no usable service NPC in sight");
                // Set the entry aside, longer each time (SuspendEntryEscalating): a bot that found nobody here walks to the next spawn,
                // not back to this one when the trip rests and the blocked spawns are forgotten (Timbermaw Hold, issues #1478 and #1479).
                Suspensions?.SuspendEntryEscalating(current.Entry, now);
            }
            if (walked < _repairBest - 0.5f) { _repairBest = walked; _repairStuckMs = 0; }
            else if (!_repairClockedThisThink && !PlayerbotMount.IsBusy(session)) _repairStuckMs += elapsedMs; // a second Update of one think does not run the clock twice
            _repairClockedThisThink = true;
            if (_repairStuckMs < RepairTripStuckMs && Suspensions?.IsEntrySuspended(current.Entry, now) != true)
            {
                Goal = PlayerbotGoalKind.Vendor;
                TargetEntry = current.Entry;
                // A leg walked to its end (the motion stops there without completing the route) is followed by the next one.
                if (_repairRoute is null || _repairRoute.Complete || _repairRoute.Points.Count == 0
                    || Vector3.Distance(here, _repairRoute.Points[^1]) < 1f)
                {
                    PlayerbotRoute? walk = null;
                    if (PlayerbotElevator.TryBegin(session, player, at, options, out walk))
                    {
                        _repairRoute = null;
                        return true;
                    }
                    PlanOutcome next = walk is not null
                        ? PlanOutcome.Route
                        : PlayerbotNavigation.PlanToward(player, at, options, ref _tileWait, now, out walk);
                    _repairRoute = walk;
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
        else if (_repairSpawn != 0)
        {
            // The spawn left the world while the bot was walking (its event stopped). It is not missing: do not report it, and do not
            // stand on the empty point.
            _blockedRepairSpawns.Add(_repairSpawn);
            ClearRepairTrip();
        }

        // At most MaxRepairPlansPerThink new candidates are planned to in one think (however many Updates it makes), nearest first;
        // the rest wait for the next think, the blocked set keeping the refused ones out. A refused plan blocks its spawn as ever: a
        // start off the mesh still reaches some spawns (the steep-ground escape depends on the destination), so none is skipped.
        bool tilesPending = false;
        var candidates = new List<(uint Entry, CreatureSpawn Spawn, Vector3 Target)>();
        foreach ((uint entry, CreatureSpawn spawn) in _repairSpawns)
        {
            if (_blockedRepairSpawns.Contains(spawn.Guid) || !SpawnPresent(player, spawn)) continue;
            if (!PlayerbotWorldDestinations.TryResolveSpawnPosition(player, spawn, requireLivePatroller: true, out Vector3 resolved)) continue;
            candidates.Add((entry, spawn, resolved));
        }

        candidates.Sort((a, b) => Vector3.DistanceSquared(here, a.Target).CompareTo(Vector3.DistanceSquared(here, b.Target)));
        foreach ((uint entry, CreatureSpawn spawn, Vector3 target) in candidates)
        {
            if (Suspensions?.IsEntrySuspended(entry, now) == true) continue;
            if (_repairPlansThisThink >= MaxRepairPlansPerThink || _repairPlansThisThink > 0 && (RepairQueryBudgetSpent() || PlayerbotThinkProbe.Active is { RunningMs: >= 20.0 })) return false;
            _repairPlansThisThink++;
            PlayerbotRoute? walk = null;
            if (PlayerbotElevator.TryBegin(session, player, target, options, out walk))
            {
                _repairEmptyRounds = 0;
                _repairRoute = null;
                _repairSpawn = spawn.Guid;
                _repairBest = Vector3.Distance(here, target);
                _repairStuckMs = 0;
                Goal = PlayerbotGoalKind.Vendor;
                TargetEntry = entry;
                return true;
            }
            PlanOutcome planned = walk is not null
                ? PlanOutcome.Route
                : PlayerbotNavigation.PlanToward(player, target, options, ref _tileWait, now, out walk);
            PlayerbotRoute? route = walk;
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

            _repairEmptyRounds = 0;
            _repairRoute = route;
            _repairSpawn = spawn.Guid;
            _repairBest = Vector3.Distance(here, target);
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
        uint rest = (uint)Math.Min((ulong)RepairTripBackoffMs << Math.Min(_repairEmptyRounds, 8), RepairTripBackoffMaxMs);
        if (_repairEmptyRounds < 8) _repairEmptyRounds++;
        _repairRestFrom = here;
        _repairRestMap = map.MapId;
        _repairTripBackoffUntil = Math.Max(1u, unchecked(now + rest));
        return false;
    }

    private static bool HasVisibleEntry(Player player, uint entry)
    {
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (player.Map?.FindObject(guid) is Creature creature && creature.Entry == entry && creature.IsInWorld)
                return true;
        }
        return false;
    }

    private void ClearRepairTrip()
    {
        _repairRoute = null;
        _repairSpawn = 0;
        _repairMissReported = 0;
        _repairStuckMs = 0;
        _repairBest = float.PositiveInfinity;
        _repairToLift = false;
    }

    /// <summary>
    /// Whether <paramref name="spawn"/> may be in the world now. A spawn listed in <c>game_event_creature</c> is there only while its
    /// event says so (the map's spawn gate, the same rule as <see cref="PlayerbotWorldDestinations.IsPresent"/>). No gate means every
    /// spawn is allowed.
    /// </summary>
    private static bool SpawnPresent(Player player, CreatureSpawn spawn) => PlayerbotWorldDestinations.IsPresent(player, spawn);

    /// <summary>
    /// A stamp of the events running now, so a service-spawn index built while an event was off is rebuilt when it starts (and the
    /// reverse). Zero when the event feature is absent: nothing about events can change.
    /// </summary>
    internal static int ActiveEventStamp(IServiceProvider services)
    {
        IReadOnlyCollection<ushort> active = services.GetService<GameEventFeature>()?.ActiveEvents ?? [];
        int stamp = active.Count;
        foreach (ushort id in active) stamp = unchecked((stamp * 397) ^ id);
        return stamp;
    }

    /// <summary>
    /// The spawns of the map's NPCs of <paramref name="service"/> the bot may approach (repair NPCs; or vendors with something on sale, so
    /// that they buy too), kept until the map, the service, the creature content or the running game events change. A spawn a game event
    /// holds back is not in the world (<see cref="SpawnPresent"/>): Javnir Nashak (15012, Harvest Festival) and Professor Thaddeus Paleo
    /// (14847, Darkmoon Faire) stay in <c>creature</c> all year, and walking to the empty point reports <c>town/service-npc-missing</c>.
    /// </summary>
    internal void RefreshRepairSpawns(Player player, uint mapId, QuestNpcServices services, NpcFlags service)
    {
        CreatureContent? content = session.Services.GetService<CreatureWorldFeature>()?.Content;
        if (content is null)
        {
            _repairSpawns.Clear();
            _repairServiceSpawns.Clear();
            _vendorServiceSpawns.Clear();
            return;
        }
        int events = ActiveEventStamp(session.Services);
        var key = (mapId, content, content.DefinitionsVersion, events, player.FactionTemplate, RankStamp(player, services));
        if (_repairIndex != key)
        {
            _repairServiceSpawns.Clear();
            _vendorServiceSpawns.Clear();
            _blockedRepairSpawns.Clear();
            ClearRepairTrip();
            FactionTemplateCatalog? factions = session.Services.GetService<QuestNpcFeature>()?.FactionTemplates;
            Vector3 here = new(player.X, player.Y, player.Z);
            var stampTemplates = new SortedSet<uint>();
            foreach (CreatureTemplate template in content.Templates)
            {
                NpcFlags flags = (NpcFlags)template.NpcFlags;
                bool isRepair = (flags & NpcFlags.Repair) != 0;
                bool isVendor = (flags & NpcFlags.Vendor) != 0 && services.Npcs.VendorItems(template.Entry).Count > 0;
                if (!isRepair && !isVendor) continue;
                IReadOnlyList<CreatureSpawn> spawns = content.GetSpawns(mapId, template.Entry);
                if (spawns.Count == 0) continue;
                // Whether the bot may use this template's NPCs is part of the cache key, usable or not (RankStamp).
                stampTemplates.Add(template.Faction);
                // The NPC must be one the bot can use, not only one that does not attack it: an Unfriendly one refuses the interaction
                // (CanApproachTrainer, above Unfriendly).
                if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, template.Faction, ArcaneCore.Game.Reputation.ReputationRank.Unfriendly))
                    continue;
                foreach (CreatureSpawn spawn in spawns)
                {
                    if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z) || !SpawnPresent(player, spawn))
                        continue;
                    if (isRepair) _repairServiceSpawns.Add((template.Entry, spawn));
                    if (isVendor) _vendorServiceSpawns.Add((template.Entry, spawn));
                }
            }

            TrimServiceSpawns(_repairServiceSpawns, here);
            TrimServiceSpawns(_vendorServiceSpawns, here);
            _repairStampTemplates = [.. stampTemplates];
            _repairStampFactions = factions;
            _repairIndex = (mapId, content, content.DefinitionsVersion, events, player.FactionTemplate, RankStamp(player, services));
        }

        List<(uint Entry, CreatureSpawn Spawn)> source = service == NpcFlags.Repair ? _repairServiceSpawns : _vendorServiceSpawns;
        _repairSpawns.Clear();
        _repairSpawns.AddRange(source);
    }

    /// <summary>
    /// A stamp of which of this map's service NPC faction templates (<see cref="_repairStampTemplates"/>) the bot may use, by the very
    /// test that filters the index (<see cref="PlayerbotTrainerDestinations.CanApproachTrainer"/> above Unfriendly): the index is rebuilt
    /// exactly when an answer changes (a bot that turned Unfriendly with Timbermaw Hold, or earned its way back to Neutral), not on every
    /// rank change. 0 while no index is built. A reaction lookup per distinct template of the map's vendors.
    /// </summary>
    private int RankStamp(Player player, QuestNpcServices services)
    {
        if (_repairStampTemplates.Length == 0) return 0;
        int stamp = 17;
        foreach (uint template in _repairStampTemplates)
            stamp = unchecked((stamp * 31) + (PlayerbotTrainerDestinations.CanApproachTrainer(player, services, _repairStampFactions, template,
                ArcaneCore.Game.Reputation.ReputationRank.Unfriendly) ? 2 : 1));
        return stamp;
    }

    private static void TrimServiceSpawns(List<(uint Entry, CreatureSpawn Spawn)> list, Vector3 here)
    {
        if (list.Count <= MaxRepairSpawns) return;
        list.Sort((a, b) => Vector3.DistanceSquared(here, new Vector3(a.Spawn.X, a.Spawn.Y, a.Spawn.Z))
            .CompareTo(Vector3.DistanceSquared(here, new Vector3(b.Spawn.X, b.Spawn.Y, b.Spawn.Z))));
        list.RemoveRange(MaxRepairSpawns, list.Count - MaxRepairSpawns);
    }

    /// <summary>
    /// The price of repairing everything worn that is damaged, by the server's own pricing (<see cref="IItemService.Repair"/> asked with a
    /// payment that refuses every price, so nothing is repaired or paid; no reputation discount). Null when nothing worn can be priced.
    /// </summary>
    internal static uint? TotalWornRepair(Player player, QuestNpcServices services)
    {
        if (services.Deps.Items is not { } items) return null;
        ulong total = 0;
        bool priced = false;
        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is not { MaxDurability: > 0 } item || item.Durability >= item.MaxDurability)
                continue;
            items.Repair(player, item.Guid, 1f, cost =>
            {
                total += cost;
                priced = true;
                return false;
            });
        }

        return priced ? (uint)Math.Min(total, uint.MaxValue) : null;
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

    /// <summary>A hunter with a bow, gun or crossbow equipped and fewer than <see cref="AmmoLowWater"/> fitting rounds: a bot fact, the same for every vendor.</summary>
    private static bool AmmoLow(Player player)
    {
        if (player.Class != Class.Hunter
            || player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged) is not { } ranged
            || (ItemClass)ranged.Template.Class != ItemClass.Weapon
            || ranged.Template.SubClass is not (WeaponBow or WeaponGun or WeaponCrossbow))
            return false;
        uint carried = 0;
        foreach (Item item in player.Inventory.AllItems)
        {
            if (item.Template.GetInventoryType() == InventoryType.Ammo && player.Inventory.CheckAmmoCompatibility(item.Template))
                carried += item.Count;
        }

        return carried < AmmoLowWater;
    }

    private VendorItem? FindAmmoRow(Player player, QuestNpcServices services, NpcInfo npc)
        => AmmoLow(player) ? FindAmmoRowOf(player, services, npc) : null;

    private VendorItem? FindAmmoRowOf(Player player, QuestNpcServices services, NpcInfo npc)
    {
        return services.Npcs.VendorItems(npc.Entry).Take(64)
            .Select(row => (Row: row, Template: player.Inventory.Templates.Find(row.Item)))
            .Where(pair => !Refused(_refusedPurchases, (npc.Entry, pair.Row.Item)))
            .Where(pair => pair.Template is { } template && template.GetInventoryType() == InventoryType.Ammo
                && player.Inventory.CheckAmmoCompatibility(template)
                && player.Inventory.CanUseAmmo(template.Entry) == ArcaneCore.Game.Items.InventoryResult.Ok
                && services.GetVendorPurchasePrice(player, npc, template.Entry) is { } price && price <= player.Money
                && (Budget is null || Budget.MayAfford(PlayerbotSpendKind.Essentials, price, player)))
            .OrderByDescending(pair => pair.Template!.ItemLevel).ThenBy(pair => pair.Row.Item)
            .Select(pair => pair.Row).FirstOrDefault();
    }

    /// <summary>
    /// The free bag slots a sale of junk leaves the bot (<see cref="FindJunk"/>): it sells while fewer are free. Selling stopped at the
    /// first free slot, and the next corpse filled it again: live bots stood at quest enders with bags full of trade goods
    /// (Stringy Wolf Meat x10 in six slots), every turn-in refused for space (bot-quest/turnin-refused InventoryFull, 44 of 50 in a
    /// night's log, quests 183 and 182 at Talin Keeneye and Senir Whitebeard) and every loot left.
    /// </summary>
    internal const int JunkReserveSlots = 4;

    /// <summary>
    /// With fewer than <see cref="JunkReserveSlots"/> bag slots free, sell the cheapest junk: a white (or gray) item that is not protected and is either not
    /// equipment or equipment that is no upgrade for the bot's build (including what it cannot use at all).
    /// </summary>
    private bool TrySellJunk(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (services.Npcs.VendorItems(npc.Entry).Count == 0
            || (FindJunk(player, services) ?? (RepairUnaffordable(player, services) ? FindJunk(player, services, forMoney: true) : null)) is not { } item)
            return false;
        Goal = PlayerbotGoalKind.Vendor;
        return Sell(npc, item);
    }

    /// <param name="forMoney">Sold for the money a repair needs (<see cref="RepairUnaffordable"/>): whatever room the bags have.</param>
    private Item? FindJunk(Player player, QuestNpcServices services, bool forMoney = false)
    {
        if (!forMoney && FreeBagSlots(player) >= JunkReserveSlots)
            return null;
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        // With no free slot at all, an unbound bind-on-equip green that is no upgrade is junk too (#1442): greens never come off a mob as grays.
        bool full = FreeBagSlots(player) == 0;
        return player.Inventory.AllItems
            .Where(candidate => candidate.BagSlot != InventorySlots.Bag0
                || (candidate.Slot >= InventorySlots.ItemStart && candidate.Slot < InventorySlots.ItemEnd))
            .Where(candidate => (candidate.Template.Quality <= 1 || (full && candidate.Template.Quality == 2)) && candidate.Template.SellPrice > 0
                && !_unsellable.Contains(candidate.Guid)
                && !IsProtected(player, candidate, services, allowUnboundBindOnEquip: full) && IsJunk(player, candidate, weights))
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
        if (npc.TrainerType == TrainerType.Mounts && RidingQuote(player, services, npc, 0) is null)
            return false;
        if (npc.TrainerType == TrainerType.TradeSkills && TradeSkillQuote(player, services, npc, 0) is null)
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

    /// <summary>The visible service NPCs of one <see cref="FindNpc"/> call, with their distance and visible-set order (the sort's tie-break).</summary>
    private readonly List<(float Distance, int Order, NpcInfo Info)> _serviceNpcs = [];

    /// <summary>
    /// The nearest visible NPC with one of the <paramref name="required"/> services that is of use to the bot. Before, every candidate was
    /// asked whether it had anything for the bot (food rows, ammunition, the bags' grays and junk, the trainer's spells) and the nearest
    /// of the useful ones taken: about 14 microseconds and 1.7 KB for each of 50 service NPCs in sight, every think, in a city with
    /// nothing to do. Now the candidates are put in distance order and asked until one says yes, and the facts about the bot that do not
    /// depend on the NPC (<see cref="ServiceNeeds"/>) are worked out once per call. The answer is the same: the first useful NPC in the
    /// same stable distance order.
    /// </summary>
    private NpcInfo? FindNpc(Player player, NpcFlags required)
    {
        if (player.Map is null || session.Services.GetService<QuestNpcFeature>()?.Services is not { } services
            || services.Deps.Creatures is not { } lookup)
            return null;
        uint now = session.World.NowMs;
        _serviceNpcs.Clear();
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (lookup.Find(player, guid, required) is not { IsAlive: true, IsHostile: false, IsInCombat: false } info
                || (info.NpcFlags & required) == 0 || info.IsNotSelectable
                || Suspensions?.IsEntrySuspended(info.Entry, now) == true)
                continue;
            _serviceNpcs.Add((Distance(player, info), _serviceNpcs.Count, info));
        }

        if (_serviceNpcs.Count == 0) return null;
        _serviceNpcs.Sort(static (a, b) => a.Distance.CompareTo(b.Distance) is var order and not 0 ? order : a.Order.CompareTo(b.Order));
        var needs = new ServiceNeeds();
        NpcInfo? found = null;
        NpcInfo? refusedFallback = null;
        foreach ((_, _, NpcInfo info) in _serviceNpcs)
        {
            if (!HasUsefulService(player, services, info, ref needs)) continue;
            if (_visitRefused == info.Guid && unchecked((int)(_visitRefusedUntil - now)) > 0)
            {
                refusedFallback ??= info;
                continue;
            }
            found = info;
            break;
        }

        _serviceNpcs.Clear();
        return found ?? refusedFallback;
    }

    /// <summary>
    /// What a vendor could do for the bot that does not depend on which vendor it is, each worked out the first time it is asked and
    /// kept for the rest of one <see cref="FindNpc"/> call (the bot does not change inside it).
    /// </summary>
    private struct ServiceNeeds
    {
        private byte _repair, _consumables, _ammo, _grays, _junk, _supply;
        private bool _food, _drink;

        /// <summary>The bot's crafting supply needs (<see cref="PlayerbotVendorSupplyNeeds"/>), read from its spellbook once per call.</summary>
        internal PlayerbotVendorSupplyNeeds? Supply(PlayerbotTownGoals goals, Player player)
        {
            if (_supply == 0) _supply = (byte)(goals.SupplyNeeds(player) is not null ? 2 : 1);
            return _supply == 2 ? goals._supplyNeeds : null;
        }

        internal bool Repair(PlayerbotTownGoals goals, Player player, QuestNpcServices services)
        {
            // Unaffordable even for the cheapest damaged item: no reason to stand at a repair NPC (TryRepair would not ask).
            if (_repair == 0) _repair = (byte)(goals.RepairWanted(player)
                && !(CheapestWornRepair(player, services) is { } cheapest && cheapest > player.Money) ? 2 : 1);
            return _repair == 2;
        }

        internal bool Consumables(Player player, SpellSystem spells, out bool food, out bool drink)
        {
            if (_consumables == 0)
            {
                (_food, _drink) = ConsumableNeeds(player, spells);
                _consumables = (byte)(_food || _drink ? 2 : 1);
            }

            food = _food;
            drink = _drink;
            return _consumables == 2;
        }

        internal bool Ammo(Player player)
        {
            if (_ammo == 0) _ammo = (byte)(AmmoLow(player) ? 2 : 1);
            return _ammo == 2;
        }

        internal bool Grays(PlayerbotTownGoals goals, Player player, QuestNpcServices services)
        {
            if (_grays == 0) _grays = (byte)(goals.SellableGrays(player, services).Any() ? 2 : 1);
            return _grays == 2;
        }

        internal bool Junk(PlayerbotTownGoals goals, Player player, QuestNpcServices services)
        {
            if (_junk == 0) _junk = (byte)((goals.FindJunk(player, services)
                ?? (goals.RepairUnaffordable(player, services) ? goals.FindJunk(player, services, forMoney: true) : null)) is not null ? 2 : 1);
            return _junk == 2;
        }
    }

    private bool HasUsefulService(Player player, QuestNpcServices services, NpcInfo npc, ref ServiceNeeds needs)
        // Advisory discovery only; the correlated GREEN list and ordinary buy handler
        // remain the authority for price, prerequisites and teaching/persistence.
        => HasUsefulVendorService(player, services, npc, ref needs)
            || (!Refused(_refusedTrainers, npc.Guid) && (npc.TrainerType == TrainerType.Mounts
                ? RidingQuote(player, services, npc, 0) is not null
                : TrainerHasOffer(player, services, npc)));

    /// <summary>
    /// The riding training this Mounts trainer would teach the bot, when its budget says the money for the training and the mount is
    /// there (<see cref="PlayerbotBudget.TrainingDue"/>) and the trainer is of its race (or it is Exalted): null otherwise.
    /// </summary>
    private ClassTrainerQuote? RidingQuote(Player player, QuestNpcServices services, NpcInfo npc, uint spell)
    {
        if (Budget is not { } budget || !budget.TrainingDue(player)) return null;
        uint free = budget.Spendable(PlayerbotSpendKind.Riding, player);
        return services.GetRidingTrainerQuote(player, npc, spell, free - Math.Min(budget.Riding.MountCost, free));
    }

    private ClassTrainerQuote? TradeSkillQuote(Player player, QuestNpcServices services, NpcInfo npc, uint spell)
    {
        SpellFeature? spellFeature = session.Services.GetService<SpellFeature>();
        SpellStore? store = spellFeature?.System.Store;
        SpellbookCache? spellbook = spellFeature?.Spellbook;
        uint? limit = ClassTrainingLimit(player);
        return services.GetTradeSkillTrainerQuote(
            player,
            npc,
            spell,
            limit,
            learned => PlayerbotCrafting.WantsTradeSkillSpell(player, store, spellbook, learned));
    }

    /// <summary>
    /// The cheapest mount this vendor sells that the bot can use (a riding-skill or mounted-aura item its inventory lets it use), when it
    /// can ride, owns no mount and the budget lets it pay: never dearer than the cheapest the budget planned for.
    /// </summary>
    private VendorItem? FindMountRow(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (Budget is not { } budget || !budget.MountPurchaseDue(player)
            || session.Services.GetService<SpellFeature>()?.System.Store is not { } store) return null;
        uint limit = Math.Min(budget.Spendable(PlayerbotSpendKind.Mount, player), budget.Riding.MountCost == 0 ? uint.MaxValue : budget.Riding.MountCost);
        VendorItem? best = null;
        uint bestPrice = uint.MaxValue;
        PlayerbotServiceIndex? index = PlayerbotServiceIndex.Get(session, services, player);
        uint rankCap = index?.RankForTier(budget.Riding.MountTierIndex) ?? uint.MaxValue, rankFloor = index?.RankFloor(budget.Riding.MountTierIndex) ?? 0;
        foreach (VendorItem row in services.Npcs.VendorItems(npc.Entry).Take(128))
        {
            if (Refused(_refusedPurchases, (npc.Entry, row.Item)) || player.Inventory.Templates.Find(row.Item) is not { } template
                || !PlayerbotMountRules.FitsTier(template, rankFloor, rankCap)
                || !PlayerbotMountRules.IsMountItem(template, store) || player.Inventory.CanUseItem(template) != ArcaneCore.Game.Items.InventoryResult.Ok
                || services.GetVendorPurchasePrice(player, npc, row.Item) is not { } price || price > limit || price >= bestPrice) continue;
            best = row;
            bestPrice = price;
        }

        return best;
    }

    private bool TryBuyMount(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (FindMountRow(player, services, npc) is not { } row) return false;
        Goal = PlayerbotGoalKind.Vendor;
        bool bought = Buy(player, npc, row.Item);
        Budget?.Invalidate();
        return bought;
    }

    private VendorItem? FindEquipmentUpgradeRow(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (player.Money == 0 || FreeBagSlots(player) <= 0)
            return null;

        IReadOnlyList<VendorItem> items = services.Npcs.VendorItems(npc.Entry);
        int limit = Math.Min(items.Count, 64);
        if (limit == 0)
            return null;

        SpellFeature? spellFeature = session.Services.GetService<SpellFeature>();
        SpellStore? store = spellFeature?.System.Store;
        SpellbookCache? spellbook = spellFeature?.Spellbook;
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        VendorItem? best = null;
        float bestScore = 0f;

        for (int i = 0; i < limit; i++)
        {
            VendorItem row = items[i];
            if (Refused(_refusedPurchases, (npc.Entry, row.Item))
                || player.Inventory.GetItemCount(row.Item) > 0
                || player.Inventory.Templates.Find(row.Item) is not { } template
                || services.GetVendorPurchasePrice(player, npc, row.Item) is not { } price
                || price == 0 || price > player.Money
                || (store is not null && PlayerbotMountRules.IsMountItem(template, store))
                || (Budget is not null && !Budget.MayAfford(PlayerbotSpendKind.Discretionary, price, player)))
            {
                continue;
            }

            float score = 0f;
            if (player.Inventory.CanUseItem(template) == ArcaneCore.Game.Items.InventoryResult.Ok
                && PlayerbotItemScore.UpgradeGain(player, template, weights) is { } gain
                && gain > PlayerbotItemScore.MinimumGain)
            {
                score = 1000f + gain;
            }
            else if (PlayerbotEquipment.IsBagUpgrade(player, template))
            {
                score = 500f + template.ContainerSlots;
            }
            else if (store is not null && spellbook is not null && PlayerbotEquipment.CanLearnFromItem(player, template, store, spellbook))
            {
                score = 300f + template.ItemLevel;
            }

            if (score > bestScore)
            {
                best = row;
                bestScore = score;
            }
        }

        return best;
    }

    private bool TryBuyEquipmentUpgrade(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (FindEquipmentUpgradeRow(player, services, npc) is not { } row)
            return false;
        Goal = PlayerbotGoalKind.Vendor;
        bool bought = Buy(player, npc, row.Item);
        Budget?.Invalidate();
        return bought;
    }

    /// <summary>The bot's crafting supply needs, read once per <see cref="FindNpc"/> call or per purchase (<see cref="SupplyNeeds"/>).</summary>
    private readonly PlayerbotVendorSupplyNeeds _supplyNeeds = new();

    /// <summary>
    /// <see cref="_supplyNeeds"/> read from the bot's spellbook as of now; null without a spell store or spellbook (no supply is
    /// needed then, as <see cref="FindCraftingSupplyRow"/> always had it).
    /// </summary>
    private PlayerbotVendorSupplyNeeds? SupplyNeeds(Player player)
    {
        SpellFeature? spellFeature = session.Services.GetService<SpellFeature>();
        SpellStore? store = spellFeature?.System.Store;
        SpellbookCache? spellbook = spellFeature?.Spellbook;
        if (store is null || spellbook is null)
            return null;
        _supplyNeeds.RefreshIfChanged(player, store, spellbook, session.World.NowMs);
        return _supplyNeeds;
    }

    private VendorItem? FindCraftingSupplyRow(Player player, QuestNpcServices services, NpcInfo npc, PlayerbotVendorSupplyNeeds? supply)
    {
        if (player.Money == 0 || FreeBagSlots(player) <= 0)
            return null;

        if (supply is null)
            return null;

        IReadOnlyList<VendorItem> items = services.Npcs.VendorItems(npc.Entry);
        int limit = Math.Min(items.Count, 64);
        for (int i = 0; i < limit; i++)
        {
            VendorItem row = items[i];
            if (Refused(_refusedPurchases, (npc.Entry, row.Item))
                || !supply.Needs(player, row.Item)
                || services.GetVendorPurchasePrice(player, npc, row.Item) is not { } price
                || price > player.Money
                || (Budget is not null && !Budget.MayAfford(PlayerbotSpendKind.Discretionary, price, player)))
            {
                continue;
            }

            return row;
        }

        return null;
    }

    private bool TryBuyCraftingSupplies(Player player, QuestNpcServices services, NpcInfo npc)
    {
        if (player.Money == 0 || FreeBagSlots(player) <= 0
            || FindCraftingSupplyRow(player, services, npc, SupplyNeeds(player)) is not { } row)
            return false;
        Goal = PlayerbotGoalKind.Vendor;
        bool bought = Buy(player, npc, row.Item);
        Budget?.Invalidate();
        return bought;
    }

    /// <summary>
    /// Whether the class or trade-skill trainer has a spell the bot can learn and pay for (<see cref="QuestNpcServices.GetClassTrainerQuote"/>: up to 128
    /// spells described and weighed per trainer). A trainer with nothing is not asked again for <see cref="TrainerQuoteMemoMs"/>, unless the
    /// bot's level changed or it has more money than when it was asked; what else could make a spell learnable (a quest reward, a
    /// skill) is noticed at the next look after that.
    /// </summary>
    private bool TrainerHasOffer(Player player, QuestNpcServices services, NpcInfo npc)
    {
        uint now = session.World.NowMs;
        uint level = player.Level, money = player.Money;
        uint? spendable = ClassTrainingLimit(player);
        uint limit = spendable ?? uint.MaxValue; // the quote is weighed against this limit: a larger one can make a spell affordable
        if (_noTrainerOffer.TryGetValue(npc.Guid, out (uint Until, uint Level, uint Money, uint Limit) memo)
            && memo.Level == level && money <= memo.Money && limit <= memo.Limit && unchecked((int)(memo.Until - now)) > 0)
            return false;
        TrainerQuotesAsked++;
        ClassTrainerQuote? quote = npc.TrainerType == TrainerType.TradeSkills
            ? TradeSkillQuote(player, services, npc, 0)
            : services.GetClassTrainerQuote(player, npc, 0, spendable);
        if (quote is not null)
        {
            _noTrainerOffer.Remove(npc.Guid);
            return true;
        }

        if (_noTrainerOffer.Count >= MaxRefusals) _noTrainerOffer.Clear();
        _noTrainerOffer[npc.Guid] = (unchecked(now + TrainerQuoteMemoMs), level, money, limit);
        return false;
    }

    private bool HasUsefulVendorService(Player player, QuestNpcServices services, NpcInfo npc, ref ServiceNeeds needs)
    {
        if ((npc.NpcFlags & NpcFlags.Repair) != 0 && needs.Repair(this, player, services)) return true;
        if ((npc.NpcFlags & NpcFlags.Vendor) != 0 && services.Npcs.VendorItems(npc.Entry).Count > 0)
        {
            if (session.Services.GetService<SpellFeature>()?.System is { } spells
                && needs.Consumables(player, spells, out bool food, out bool drink)
                && FindConsumableVendorRow(player, services, npc, spells, food, drink) is not null) return true;
            if (needs.Ammo(player) && FindAmmoRowOf(player, services, npc) is not null) return true;
            if (FindMountRow(player, services, npc) is not null) return true;
            if (FindEquipmentUpgradeRow(player, services, npc) is not null) return true;
            if (player.Money != 0 && FreeBagSlots(player) > 0
                && FindCraftingSupplyRow(player, services, npc, needs.Supply(this, player)) is not null) return true;
            if (needs.Grays(this, player, services)) return true;
            if (needs.Junk(this, player, services)) return true;
        }
        return false;
    }

    /// <summary>Whether the bot lacks usable food, and usable drink (a mana user only): bot facts, the same for every vendor.</summary>
    private static (bool Food, bool Drink) ConsumableNeeds(Player player, SpellSystem spells)
        => (!PlayerbotConsumables.HasUsable(player, spells, PlayerbotConsumableKind.Food),
            player.PowerType == PowerType.Mana
            && player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana) > 0
            && !PlayerbotConsumables.HasUsable(player, spells, PlayerbotConsumableKind.Drink));

    private VendorItem? FindConsumableVendorRow(Player player, QuestNpcServices services, NpcInfo npc, SpellSystem spells)
    {
        (bool food, bool drink) = ConsumableNeeds(player, spells);
        return FindConsumableVendorRow(player, services, npc, spells, food, drink);
    }

    private VendorItem? FindConsumableVendorRow(Player player, QuestNpcServices services, NpcInfo npc,
        SpellSystem spells, bool needFood, bool needDrink)
    {
        if (!needFood && !needDrink)
            return null;

        VendorItem[] rows = [.. services.Npcs.VendorItems(npc.Entry).Take(32).Where(row => !Refused(_refusedPurchases, (npc.Entry, row.Item)))];
        foreach (VendorItem row in rows)
        {
            if (TryEligible(row, PlayerbotConsumableKind.Food, needFood, player, services, npc, spells, Budget))
                return row;
        }

        foreach (VendorItem row in rows)
        {
            if (TryEligible(row, PlayerbotConsumableKind.Drink, needDrink, player, services, npc, spells, Budget))
                return row;
        }
        return null;

        static bool TryEligible(VendorItem row, PlayerbotConsumableKind wanted, bool needed, Player player,
            QuestNpcServices services, NpcInfo npc, SpellSystem spells, PlayerbotBudget? budget)
        {
            if (!needed || player.Inventory.Templates.Find(row.Item) is not { } template
                || player.Inventory.CanUseItem(template) != ArcaneCore.Game.Items.InventoryResult.Ok
                || !PlayerbotConsumables.TryClassify(template, spells, out PlayerbotConsumableKind kind)
                || kind != wanted)
                return false;
            return services.GetVendorPurchasePrice(player, npc, row.Item) is { } price
                && (budget is null || budget.MayAfford(PlayerbotSpendKind.Essentials, price, player));
        }
    }

    /// <summary>Whether the bot keeps <paramref name="item"/> whatever a vendor or banker would take (the bank errand asks it, <see cref="PlayerbotBank"/>).</summary>
    internal bool IsProtected(Player player, Item item)
        => session.Services.GetService<QuestNpcFeature>()?.Services is not { } services || IsProtected(player, item, services);

    /// <param name="allowUnboundBindOnEquip">An unbound bind-on-equip item (Bonding 2, not yet soulbound) is not protected by its binding alone.</param>
    private bool IsProtected(Player player, Item item, QuestNpcServices services, bool allowUnboundBindOnEquip = false)
    {
        if (session.Services.GetService<SpellFeature>()?.System is { } spells
            && PlayerbotConsumables.TryClassify(item.Template, spells, out _)) return true;
        IReadOnlyDictionary<uint, QuestStatusData> statuses = services.StateOf(player)?.Quests.Statuses
            ?? new Dictionary<uint, QuestStatusData>();
        bool questRequired = statuses
            .Where(row => row.Value.Status is QuestStatus.Incomplete or QuestStatus.Complete)
            .SelectMany(row => services.Quests.Get(row.Key)?.ReqItemId ?? [])
            .Contains(item.Entry);
        return item.Entry == 117 || item.Template.BagFamily != 0 || item.Template.StartQuest != 0 || questRequired
            || item.Template.FoodType != 0 || item.Template.RequiredSkill != 0
            || (ItemClass)item.Template.Class is ItemClass.Quest or ItemClass.Reagent
            || (item.Template.Bonding != 0 && !(allowUnboundBindOnEquip && item.Template.Bonding == 2 && !item.IsSoulBound));
    }

    private static byte[] BuyPayload(ObjectGuid vendor, uint item)
    {
        var writer = new PacketWriter(14);
        writer.WriteUInt64(vendor.Value); writer.WriteUInt32(item); writer.WriteByte(1); writer.WriteByte(0);
        return writer.ToArray();
    }

    /// <summary>cmsg_repair_item: u64 npc, u64 item (0: everything, vmangos HandleRepairItemOpcode).</summary>
    private static byte[] RepairPayload(ObjectGuid npc, ObjectGuid item = default)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(npc.Value); writer.WriteUInt64(item.Value);
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
