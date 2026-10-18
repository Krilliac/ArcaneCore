using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Where a bank visit stands (<see cref="PlayerbotBank.Step"/>).</summary>
internal enum BankStep
{
    /// <summary>Walking to the banker, or waiting for the budget: call again next think.</summary>
    Working,

    /// <summary>Nothing more to do at the bank.</summary>
    Done,

    /// <summary>No banker could be reached.</summary>
    Failed,
}

/// <summary>
/// One managed bot's use of a banker while it stays at its capital home (<see cref="Population.TownStay.Capital"/>): CMSG_BANKER_ACTIVATE
/// opens the bank (<see cref="QuestNpcServices.BankerActivate"/>), CMSG_AUTOBANK_ITEM deposits one item per think
/// (<see cref="PlayerInventory.AutoBankItem"/>), CMSG_BUY_BANK_SLOT buys the next bank bag slot (<see cref="QuestNpcServices.BuyBankSlot"/>):
/// the client's own packets through the ordinary handlers. The bot keeps gear, food, potions, ammunition, bags, quest items, the
/// Hearthstone, grays (the vendor sells them), anything the town controller protects and any trade good or reagent a spell it knows uses
/// (a recipe's materials are the reagents of its spell); it deposits the other trade goods, gems, recipes and stackable curios. A deposit the
/// bank refuses (full) is not repeated. Options: <c>World:Playerbots:Economy:BankDeposit</c> and <c>BuyBankSlots</c> (live).
/// <para>
/// Where the bankers are: the banker spawns of the creature content on the capital's map within <see cref="RadiusYards"/> of the home's
/// centre whose faction the bot may approach (<see cref="PlayerbotTrainerDestinations.CanApproachTrainer"/>), cached per map, centre and
/// content version. A banker in sight is preferred to a spawn.
/// </para>
/// <para>Ownership: one instance per brain, world thread only. It sends at most one request per call.</para>
/// </summary>
internal sealed class PlayerbotBank(WorldSession session, PlayerbotOptions options)
{
    /// <summary>The most items one visit deposits.</summary>
    internal const int MaxDepositsPerVisit = 16;

    /// <summary>A bank slot is bought only when its price is at most this share of the bot's gold.</summary>
    internal const float SlotGoldFraction = 0.4f;

    /// <summary>A bank slot is bought only when the bank has fewer free item slots than this.</summary>
    internal const int SlotFreeSlotsBelow = 5;

    /// <summary>Banker spawns this far (yards, flat) from the capital home's centre are the capital's.</summary>
    internal const float RadiusYards = 400f;

    /// <summary>While at the capital and not visiting, whether a visit has work is asked at most this often (ms): it reads the spellbook.</summary>
    internal const uint CheckIntervalMs = 60_000;

    /// <summary>After a visit ended (done, failed or capped) the next one is at least this far away (ms).</summary>
    internal const uint RestMs = 30 * 60_000;

    /// <summary>The longest one visit lasts (ms), walk included.</summary>
    internal const uint VisitCapMs = 180_000;

    private const int MaxRefusals = 2;

    private readonly PlayerbotEconomyWalker _walker = new(session, options);
    private readonly HashSet<uint> _blocked = [];
    private readonly HashSet<ObjectGuid> _refusedItems = [];
    private readonly List<(uint Entry, uint Spawn, Vector3 Position)> _bankers = [];
    private (uint Map, Vector3 Centre, CreatureContent? Content, int Version, uint Team) _bankersKey = (uint.MaxValue, default, null, -1, 0);
    private bool _opened;
    private bool _slotTried;
    private int _refusals;
    private int _visitDeposits;
    private bool _visiting;
    private bool _checked;
    private uint _visitStartedAt;
    private uint _nextCheckAt;

    /// <summary>Bankers a stalled bot set aside (<see cref="PlayerbotStallWatch"/>; the brain's Vendor goal suspends the banker's entry).</summary>
    internal PlayerbotSuspensions? Suspensions { get; set; }

    /// <summary>The entry of the banker the bot is going to (0 when none).</summary>
    internal uint TargetEntry { get; private set; }

    /// <summary>Whether a visit is under way.</summary>
    internal bool Visiting => _visiting;

    /// <summary>Items deposited over the bot's life (tests, inspection).</summary>
    internal int Deposited { get; private set; }

    /// <summary>Bank slots bought over the bot's life.</summary>
    internal int SlotsBought { get; private set; }

    /// <summary>Start a new visit.</summary>
    internal void Reset()
    {
        _walker.Clear();
        _blocked.Clear();
        _refusedItems.Clear();
        _opened = false;
        _slotTried = false;
        _refusals = 0;
        _visitDeposits = 0;
        TargetEntry = 0;
    }

    /// <summary>
    /// One think of the bank errand of a bot that stays at its capital home: every <see cref="CheckIntervalMs"/> it asks whether a visit has
    /// work, and while one is under way it walks, opens the bank, deposits and buys a slot. True when it took the think (a step was sent or
    /// the bot is on its way); false otherwise, also when the visit just ended (the next one is <see cref="RestMs"/> away).
    /// </summary>
    internal bool Update(Player player, PlayerbotHomeArea capital, Func<Item, bool> isProtected, uint elapsedMs)
    {
        PlayerbotEconomyOptions config = options.Economy;
        if (!config.BankDeposit && !config.BuyBankSlots)
        {
            End(rest: false);
            return false;
        }

        if (elapsedMs == 0 || !player.IsInWorld || !player.IsAlive || (player.Flags & PlayerFlags.Ghost) != 0 || player.Combat.IsInCombat
            || player.Map is not { } map || map.MapId != capital.MapId)
            return false;

        uint now = session.World.NowMs;
        if (!_visiting)
        {
            // _checked: the first check runs at once (a zero start would read as a future time on a world up for over 2^31 ms).
            if (_checked && unchecked((int)(now - _nextCheckAt)) < 0) return false;
            (_checked, _nextCheckAt) = (true, unchecked(now + CheckIntervalMs));
            if (!HasWork(player, isProtected)) return false;
            Reset();
            _visiting = true;
            _visitStartedAt = now;
        }

        if (unchecked(now - _visitStartedAt) > VisitCapMs)
        {
            End(rest: true);
            return false;
        }

        BankStep step = Step(player, BankersOf(player, capital), isProtected, elapsedMs);
        if (step == BankStep.Working) return true;
        End(rest: true);
        return false;
    }

    private void End(bool rest)
    {
        if (!_visiting) return;
        _visiting = false;
        _walker.Clear();
        TargetEntry = 0;
        if (rest) (_checked, _nextCheckAt) = (true, unchecked(session.World.NowMs + RestMs));
    }

    /// <summary>Whether a visit would do anything: something to deposit, or a slot to buy.</summary>
    internal bool HasWork(Player player, Func<Item, bool> isProtected)
        => (options.Economy.BankDeposit && SelectDeposits(player, NeededReagents(player), isProtected).Count > 0)
            || (options.Economy.BuyBankSlots && WantsSlot(player));

    /// <summary>The items a bank visit would deposit, in bag order (a pure function of the bags).</summary>
    internal static List<Item> SelectDeposits(Player player, IReadOnlySet<uint> neededReagents, Func<Item, bool> isProtected)
    {
        var result = new List<Item>();
        foreach (Item item in player.Inventory.AllItems)
        {
            bool backpack = item.BagSlot == InventorySlots.Bag0 && item.Slot >= InventorySlots.ItemStart && item.Slot < InventorySlots.ItemEnd;
            bool worn = item.BagSlot >= InventorySlots.BagStart && item.BagSlot < InventorySlots.BagEnd;
            if ((backpack || worn) && Depositable(item, neededReagents, isProtected)) result.Add(item);
        }

        return result;
    }

    private static bool Depositable(Item item, IReadOnlySet<uint> neededReagents, Func<Item, bool> isProtected)
    {
        if (item.Entry == PlayerbotHearthstone.ItemEntry || item.IsBag) return false;
        if (item.Template.Quality == 0 || isProtected(item)) return false; // grays are sold, not banked
        return (ItemClass)item.Template.Class switch
        {
            ItemClass.Gem or ItemClass.Recipe => true,
            // Trade goods a recipe the bot knows uses (First Aid's cloth) stay in the bags, like a spell's reagent.
            ItemClass.TradeGoods or ItemClass.Reagent => !neededReagents.Contains(item.Entry),
            ItemClass.Junk => item.Template.Stackable > 1,
            _ => false, // gear, consumables, ammunition, quest items, keys
        };
    }

    /// <summary>The reagent items the spells the bot knows use.</summary>
    internal HashSet<uint> NeededReagents(Player player)
    {
        var needed = new HashSet<uint>();
        if (session.Services.GetService<SpellFeature>() is not { } feature) return needed;
        foreach (uint id in feature.Spellbook.GetSpells(player))
        {
            if (feature.System.Store.Get(id) is not { } spell) continue;
            foreach (SpellReagent reagent in spell.Reagents)
                if (reagent.Item > 0) needed.Add((uint)reagent.Item);
        }

        return needed;
    }

    /// <summary>Free item slots in the bank: its 24 main slots and the room in the bank bags.</summary>
    internal static int FreeBankSlots(Player player)
    {
        int free = 0;
        for (byte slot = InventorySlots.BankItemStart; slot < InventorySlots.BankItemEnd; slot++)
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is null) free++;
        for (byte slot = InventorySlots.BankBagStart; slot < InventorySlots.BankBagEnd; slot++)
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is Container bag) free += bag.FreeSlots;
        return free;
    }

    /// <summary>The slot-purchase gate: the bank is nearly full, a slot is left to buy, and its price is a modest share of the gold.</summary>
    internal static bool ShouldBuySlot(uint money, uint price, int freeBankSlots, bool nextSlotExists)
        => nextSlotExists && freeBankSlots < SlotFreeSlotsBelow && price > 0 && price <= money * SlotGoldFraction;

    private bool WantsSlot(Player player)
        => session.Services.GetService<QuestNpcFeature>()?.Services is { } services && services.Deps.Items is { } items
            && player.Inventory.BankBagSlotCount < QuestNpcServices.MaxBankBagSlots
            && items.BankBagSlotPrice((uint)player.Inventory.BankBagSlotCount + 1) is { } price
            && ShouldBuySlot(player.Money, price, FreeBankSlots(player), true);

    /// <summary>
    /// The banker spawns of the capital the bot may approach, nearest the home's centre first (cached until the map, centre, team or creature
    /// content changes).
    /// </summary>
    internal IReadOnlyList<(uint Entry, uint Spawn, Vector3 Position)> BankersOf(Player player, PlayerbotHomeArea capital)
    {
        CreatureContent? content = session.Services.GetService<CreatureWorldFeature>()?.Content;
        var key = (capital.MapId, capital.Centre, content, content?.DefinitionsVersion ?? -1, (uint)player.Team);
        if (key == _bankersKey) return _bankers;
        _bankersKey = key;
        _bankers.Clear();
        if (content is null || session.Services.GetService<QuestNpcFeature>() is not { Services: { } services } npcFeature) return _bankers;
        FactionTemplateCatalog? factions = npcFeature.FactionTemplates;
        foreach (CreatureTemplate template in content.Templates.Where(t => ((NpcFlags)t.NpcFlags & NpcFlags.Banker) != 0).OrderBy(t => t.Entry))
        {
            if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, template.Faction)) continue;
            foreach (CreatureSpawn spawn in content.GetSpawns(capital.MapId, template.Entry))
            {
                if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)) continue;
                float dx = spawn.X - capital.Centre.X, dy = spawn.Y - capital.Centre.Y;
                if ((dx * dx) + (dy * dy) <= RadiusYards * RadiusYards) _bankers.Add((template.Entry, spawn.Guid, new Vector3(spawn.X, spawn.Y, spawn.Z)));
            }
        }

        Vector3 centre = capital.Centre;
        _bankers.Sort((a, b) => Vector2.DistanceSquared(new(a.Position.X, a.Position.Y), new(centre.X, centre.Y))
            .CompareTo(Vector2.DistanceSquared(new(b.Position.X, b.Position.Y), new(centre.X, centre.Y))));
        return _bankers;
    }

    /// <summary>One think of the visit: walk to a banker (in sight, else the nearest spawn), open the bank, deposit, buy a slot.</summary>
    internal BankStep Step(Player player, IReadOnlyList<(uint Entry, uint Spawn, Vector3 Position)> bankers, Func<Item, bool> isProtected, uint elapsedMs)
    {
        if (session.Services.GetService<QuestNpcFeature>()?.Services is not { } services || services.Deps.Creatures is not { } lookup)
            return BankStep.Failed;
        uint now = session.World.NowMs;
        NpcInfo? seen = null;
        NpcInfo? banker = null;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (lookup.Find(player, guid, NpcFlags.Banker) is not { IsAlive: true, IsHostile: false, IsInCombat: false, IsNotSelectable: false } info
                || (info.NpcFlags & NpcFlags.Banker) == 0 || Suspensions?.IsEntrySuspended(info.Entry, now) == true) continue;
            if (services.InteractableNpc(player, guid, NpcFlags.Banker) is not null)
            {
                banker = info;
                break;
            }

            if (seen is null || DistanceSquared(player, info) < DistanceSquared(player, seen)) seen = info;
        }

        if (banker is null)
        {
            _opened = false;
            Vector3 target;
            if (seen is not null && !_blocked.Contains(seen.SpawnId))
            {
                target = new(seen.X, seen.Y, seen.Z);
                TargetEntry = seen.Entry;
                if (_walker.Step(player, target, elapsedMs)) return BankStep.Working;
                _blocked.Add(seen.SpawnId);
                _walker.Clear();
                return BankStep.Working;
            }

            Vector3 here = new(player.X, player.Y, player.Z);
            (uint Entry, uint Spawn, Vector3 Position)? hint = null;
            foreach (var candidate in bankers)
            {
                if (_blocked.Contains(candidate.Spawn) || Suspensions?.IsEntrySuspended(candidate.Entry, now) == true) continue;
                if (hint is null || Vector3.DistanceSquared(here, candidate.Position) < Vector3.DistanceSquared(here, hint.Value.Position)) hint = candidate;
            }

            if (hint is not { } chosen) return BankStep.Failed;
            TargetEntry = chosen.Entry;
            if (!_walker.Step(player, chosen.Position, elapsedMs))
            {
                _blocked.Add(chosen.Spawn);
                _walker.Clear();
            }

            return BankStep.Working;
        }

        TargetEntry = banker.Entry;
        if (!PlayerbotMovementControl.Stop(session, player)) return BankStep.Working;
        if (!_opened)
        {
            _opened = session.TryManagedAction(WorldOpcode.CmsgBankerActivate, PlayerbotNavigation.GuidPayload(banker.Guid.Value));
            return BankStep.Working;
        }

        if (options.Economy.BankDeposit && _visitDeposits < MaxDepositsPerVisit && _refusals < MaxRefusals)
        {
            Item? next = SelectDeposits(player, NeededReagents(player), isProtected).FirstOrDefault(i => !_refusedItems.Contains(i.Guid));
            if (next is not null)
            {
                ObjectGuid guid = next.Guid;
                uint before = next.Count;
                if (!session.TryManagedAction(WorldOpcode.CmsgAutobankItem, [next.BagSlot, next.Slot])) return BankStep.Working;
                Item? after = player.Inventory.GetItemByGuid(guid);
                bool moved = after is null || InventorySlots.IsBankPos(after.BagSlot, after.Slot) || after.Count < before;
                if (moved) { Deposited++; _visitDeposits++; }
                else { _refusedItems.Add(guid); _refusals++; }
                return BankStep.Working;
            }
        }

        if (options.Economy.BuyBankSlots && !_slotTried)
        {
            _slotTried = true;
            if (WantsSlot(player))
            {
                byte count = player.Inventory.BankBagSlotCount;
                if (!session.TryManagedAction(WorldOpcode.CmsgBuyBankSlot, PlayerbotNavigation.GuidPayload(banker.Guid.Value))) _slotTried = false;
                else if (player.Inventory.BankBagSlotCount > count) SlotsBought++;
                return BankStep.Working;
            }
        }

        return BankStep.Done;
    }

    private static float DistanceSquared(Player player, NpcInfo npc)
    {
        float dx = npc.X - player.X, dy = npc.Y - player.Y, dz = npc.Z - player.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
