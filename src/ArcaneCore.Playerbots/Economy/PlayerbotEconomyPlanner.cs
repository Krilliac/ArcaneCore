using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Playerbots;

/// <summary>An item of the bot's general bags as the planner sees it (the world side fills it from the live <c>Item</c>).</summary>
/// <param name="Transferable">The inventory lets the item leave the bot (<c>CanTransferOut</c>: not bound, conjured, timed).</param>
/// <param name="Reserved">The bot keeps it: an item an open quest asks for, a reagent it needs, or an entry it bought lately.</param>
/// <param name="KeptConsumable">Informational: a consumable the bot uses (food, drink, potion). It does not change the surplus rule, which treats every consumable alike: a stack is listed only when the bag's other stacks of the entry already reach the keep count.</param>
internal sealed record PlayerbotBagItem(ObjectGuid Guid, ItemTemplate Template, uint Count, bool Transferable, bool Reserved, bool KeptConsumable = false);

/// <summary>What the bot asks for one item: buyout, start bid and the deposit it pays now.</summary>
internal sealed record PlayerbotListingPlan(uint Buyout, uint StartBid, uint Deposit);

/// <summary>The auction the bot buys out and the gain its slot sees.</summary>
internal sealed record PlayerbotUpgradeChoice(PlayerbotAuctionRow Row, float Gain);

/// <summary>The reagent auction the bot buys out.</summary>
internal sealed record PlayerbotReagentChoice(PlayerbotAuctionRow Row);

/// <summary>
/// Entries the bot bought at the auction house lately, which it does not list again for <see cref="HoldMs"/> (no buy-then-relist loop).
/// A purchase also stays "awaiting collection" until the mail step has taken as many bought items from their letters as were bought
/// (an awaiting count: each purchase adds one, each collected letter takes one), so a letter that was not collected does not make the
/// next visit buy the same reagent again.
/// In memory, bounded to <see cref="MaxEntries"/> (the oldest purchase goes first); lost on restart. World thread only.
/// </summary>
internal sealed class PlayerbotBoughtMemory
{
    internal const int MaxEntries = 64;

    internal const uint HoldMs = 86_400_000;

    private readonly Dictionary<uint, (uint At, long Order, int Awaiting)> _bought = [];
    private long _next;

    /// <summary>How many entries are remembered.</summary>
    internal int Count => _bought.Count;

    /// <summary>Notes a purchase of <paramref name="entry"/> at world time <paramref name="now"/>: (re)starts its hold and adds one to its awaiting-collection count.</summary>
    internal void NoteBought(uint entry, uint now)
    {
        // Entries are otherwise dropped only when queried, and an unqueried one would wrap back into its hold after 2^32 ms (~49.7 days).
        foreach (uint expired in _bought.Where(pair => pair.Key != entry && unchecked(now - pair.Value.At) >= HoldMs).Select(pair => pair.Key).ToArray())
            _bought.Remove(expired);
        int awaiting = _bought.TryGetValue(entry, out (uint At, long Order, int Awaiting) known) ? known.Awaiting : 0;
        if (!_bought.ContainsKey(entry) && _bought.Count >= MaxEntries) _bought.Remove(_bought.MinBy(pair => pair.Value.Order).Key);
        _bought[entry] = (now, _next++, awaiting < int.MaxValue ? awaiting + 1 : awaiting);
    }

    /// <summary>One bought item of <paramref name="entry"/> was taken from its letter: the awaiting count falls by one, not below 0 (the listing hold stays).</summary>
    internal void NoteCollected(uint entry)
    {
        if (_bought.TryGetValue(entry, out (uint At, long Order, int Awaiting) known))
            _bought[entry] = (known.At, known.Order, Math.Max(0, known.Awaiting - 1));
    }

    /// <summary>Whether <paramref name="entry"/> was bought within <see cref="HoldMs"/> of <paramref name="now"/>; an entry past its hold is forgotten.</summary>
    internal bool IsHeld(uint entry, uint now)
    {
        if (!_bought.TryGetValue(entry, out (uint At, long Order, int Awaiting) known)) return false;
        if (unchecked(now - known.At) < HoldMs) return true;
        _bought.Remove(entry);
        return false;
    }

    /// <summary>Whether <paramref name="entry"/> is held (within <see cref="HoldMs"/>) and its awaiting count is above 0; false after the hold, so a lost letter cannot block buying forever.</summary>
    internal bool IsAwaitingCollection(uint entry, uint now) => IsHeld(entry, now) && _bought[entry].Awaiting > 0;
}

/// <summary>
/// The economy bot's decisions, pure and deterministic over plain values: which items to list, how many listings a visit may add, the
/// price and whether it pays, which upgrade to buy within the visit budget. The world side (<see cref="PlayerbotEconomy"/>) only supplies
/// the facts and carries the decisions out through ordinary client packets.
/// </summary>
internal static class PlayerbotEconomyPlanner
{
    /// <summary>The smallest budget (copper) worth a trip for an upgrade alone.</summary>
    internal const uint MinimumUpgradeBudget = 100;

    /// <summary>
    /// Whether an item at (<paramref name="bagSlot"/>, <paramref name="slot"/>) is carried: in the backpack or in an equipped bag
    /// (slots 19..22). Equipped gear, the bags themselves, the bank and the contents of bank bags are not.
    /// </summary>
    internal static bool IsCarried(byte bagSlot, byte slot)
        => bagSlot == InventorySlots.Bag0 ? slot >= InventorySlots.ItemStart && slot < InventorySlots.ItemEnd
            : bagSlot >= InventorySlots.BagStart && bagSlot < InventorySlots.BagEnd;

    /// <summary>The listing slots a bot short of money gets on top of <see cref="PlayerbotEconomyOptions.MaxListingsPerBot"/>.</summary>
    internal const int MoneyGoalExtraListings = 3;

    /// <summary>Whether the bot is short of money: its budget reserves more than it has, or its money is below <paramref name="copperPerLevel"/> times its level.</summary>
    internal static bool IsShortOfMoney(uint money, uint reserved, uint level, int copperPerLevel)
        => reserved > money || (copperPerLevel > 0 && money < (ulong)copperPerLevel * level);

    /// <summary>The most listings one bot may have at once: <see cref="PlayerbotEconomyOptions.MaxListingsPerBot"/> (plus the money goal's extra slots), at most the ceiling.</summary>
    internal static int ListingCap(PlayerbotEconomyOptions options, bool moneyGoal)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxListingsPerBot <= 0) return 0;
        int extra = moneyGoal && options.MoneyGoal ? MoneyGoalExtraListings : 0;
        return (int)Math.Min((long)PlayerbotEconomyOptions.MaxListingsCeiling, (long)options.MaxListingsPerBot + extra);
    }

    /// <summary>Whether a stack of a consumable is surplus: <paramref name="othersOfEntry"/> (the same entry in the bag's other stacks) already reaches <paramref name="keep"/>.</summary>
    /// <remarks>
    /// Only stacks the bot may part with qualify: transferable, not reserved, not empty, above poor quality, with a vendor price, no quest
    /// starter. A consumable the bot uses (<see cref="PlayerbotBagItem.KeptConsumable"/>) is surplus beyond the keep count like any other.
    /// </remarks>
    internal static bool IsSurplusConsumable(PlayerbotBagItem item, uint othersOfEntry, int keep)
    {
        ArgumentNullException.ThrowIfNull(item);
        ItemTemplate template = item.Template;
        return item.Transferable && !item.Reserved && item.Count > 0 && (ItemClass)template.Class == ItemClass.Consumable && template.Quality >= 1
            && template.SellPrice > 0 && template.StartQuest == 0 && othersOfEntry >= keep;
    }

    /// <summary>Entry to missing count (per craft minus carried) for every entry whose carried count is below its per-craft count; zero counts skipped.</summary>
    internal static IReadOnlyDictionary<uint, uint> Shortfalls(IReadOnlyDictionary<uint, uint> perCraft, Func<uint, uint> carried)
    {
        ArgumentNullException.ThrowIfNull(perCraft);
        ArgumentNullException.ThrowIfNull(carried);
        var missing = new Dictionary<uint, uint>();
        foreach ((uint entry, uint count) in perCraft)
        {
            if (count == 0) continue;
            uint have = carried(entry);
            if (have < count) missing[entry] = count - have;
        }

        return missing;
    }

    /// <summary>
    /// How many times the shortfall a reagent row's count may be: a row is eligible only when <c>row.Count &lt;= lacking * ReagentOverbuyFactor</c>.
    /// Bounds the surplus a purchase leaves (which is held back from listing and deposit) so a stack of 20 is never bought for a shortfall of 1.
    /// </summary>
    internal const uint ReagentOverbuyFactor = 2;

    /// <summary>
    /// The reagent auction to buy out, or null: of the rows for a missing entry the bot can pay (a buyout, not its own, within
    /// <paramref name="limit"/>, not refused, not a vendor-stocked item, a count of at most <see cref="ReagentOverbuyFactor"/> times the
    /// shortfall, at most <paramref name="unitCap"/> per unit rounded up; a cap of 0 is never eligible) the best. A row that covers the shortfall beats one that does
    /// not; among covering rows the lower total buyout wins, then the lower price per unit (compared exactly), then the lower auction id;
    /// among non-covering rows the lower price per unit, then the lower buyout, then the lower auction id.
    /// </summary>
    /// <param name="unitCap">The most per unit the bot pays for an item (<see cref="PlayerbotAuctionPricing.BuyCapPerUnit"/>); 0 means never bought.</param>
    /// <param name="isRefused">Auctions the house refused to sell to the bot lately.</param>
    /// <param name="isVendorStocked">Item entries a vendor sells: never bought at the house (the vendor price is the market, and a player could otherwise sell vendor goods to bots above it).</param>
    internal static PlayerbotReagentChoice? ChooseReagent(IEnumerable<PlayerbotAuctionRow> rows, ulong self,
        IReadOnlyDictionary<uint, uint> missing, uint limit, Func<uint, ItemTemplate?> templates, Func<ItemTemplate, uint> unitCap,
        Func<uint, bool>? isRefused = null,
        Func<uint, bool>? isVendorStocked = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(unitCap);
        PlayerbotAuctionRow? best = null;
        bool bestCovers = false;
        foreach (PlayerbotAuctionRow row in rows)
        {
            if (row.Buyout == 0 || row.Count == 0 || row.Buyout > limit || row.Owner == self) continue;
            if (!missing.TryGetValue(row.Entry, out uint lacking) || lacking == 0 || isRefused?.Invoke(row.Id) == true) continue;
            if (isVendorStocked?.Invoke(row.Entry) == true || (ulong)row.Count > (ulong)lacking * ReagentOverbuyFactor) continue;
            if (templates(row.Entry) is not { } template) continue;
            uint cap = unitCap(template);
            if (cap == 0 || (((ulong)row.Buyout + row.Count - 1) / row.Count) > cap) continue;
            bool covers = row.Count >= lacking;
            if (best is { } current && !IsBetterReagent(row, covers, current, bestCovers)) continue;
            best = row;
            bestCovers = covers;
        }

        return best is { } chosen ? new PlayerbotReagentChoice(chosen) : null;
    }

    private static bool IsBetterReagent(PlayerbotAuctionRow row, bool covers, PlayerbotAuctionRow current, bool currentCovers)
    {
        if (covers != currentCovers) return covers;
        ulong mine = (ulong)row.Buyout * current.Count;
        ulong theirs = (ulong)current.Buyout * row.Count;
        if (covers)
        {
            if (row.Buyout != current.Buyout) return row.Buyout < current.Buyout;
            if (mine != theirs) return mine < theirs;
            return row.Id < current.Id;
        }

        if (mine != theirs) return mine < theirs;
        if (row.Buyout != current.Buyout) return row.Buyout < current.Buyout;
        return row.Id < current.Id;
    }

    /// <summary>Whether the bot lists <paramref name="item"/>: vendor trash stays with the vendors (<c>PlayerbotTownGoals</c> sells poor quality items and white junk).</summary>
    /// <param name="upgradeGain">What wearing the item would gain (<c>PlayerbotGearScoreSeam</c>); null when the bot cannot use it.</param>
    /// <remarks>
    /// Listed: an unbound-able (none or bind-on-equip) green, blue or epic weapon or armor that is no upgrade for the bot, and any Trade Goods
    /// item above poor quality. Never listed: poor quality or white items, items that cannot leave the bot, reserved items, quest items and
    /// reagents, bags and ammunition (<c>BagFamily</c>), food, quest starters, profession tools, and items with no vendor price.
    /// </remarks>
    internal static bool IsListable(PlayerbotBagItem item, float? upgradeGain)
    {
        ArgumentNullException.ThrowIfNull(item);
        ItemTemplate template = item.Template;
        if (!item.Transferable || item.Reserved || item.Count == 0 || template.SellPrice == 0 || template.Quality == 0) return false;
        if (template.Bonding is not (0 or 2) || template.BagFamily != 0 || template.StartQuest != 0 || template.FoodType != 0
            || template.RequiredSkill != 0 || (ItemClass)template.Class is ItemClass.Quest or ItemClass.Reagent)
            return false;
        if ((ItemClass)template.Class == ItemClass.TradeGoods) return true;
        return template.Quality is >= 2 and <= 4 && (ItemClass)template.Class is ItemClass.Weapon or ItemClass.Armor
            && (upgradeGain is not { } gain || gain <= PlayerbotItemScore.MinimumGain);
    }

    /// <summary>The items to list, the most valuable stack first (reference price times count), the lowest GUID first among equals; given-up entries left out.</summary>
    internal static IReadOnlyList<PlayerbotBagItem> Listables(IEnumerable<PlayerbotBagItem> bag, Func<ItemTemplate, float?> upgradeGain,
        PlayerbotRelistMemory? memory = null, int consumableKeep = 0)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(upgradeGain);
        List<PlayerbotBagItem> items = [.. bag];
        HashSet<ulong>? surplus = consumableKeep > 0 ? SurplusConsumables(items, consumableKeep) : null;
        return [.. items
            .Where(item => memory?.IsGivenUp(item.Template.Entry) != true
                && (IsListable(item, upgradeGain(item.Template)) || surplus?.Contains(item.Guid.Value) == true))
            .OrderByDescending(item => (ulong)PlayerbotAuctionPricing.ReferencePerUnit(item.Template) * item.Count)
            .ThenBy(item => item.Guid.Value)];
    }

    /// <summary>
    /// The consumable stacks to list as surplus: per entry the stacks that qualify smallest first, each only while the bag's other stacks of
    /// the entry (less what was already taken) still reach <paramref name="keep"/>, so at least <paramref name="keep"/> always stay.
    /// </summary>
    private static HashSet<ulong> SurplusConsumables(List<PlayerbotBagItem> items, int keep)
    {
        var surplus = new HashSet<ulong>();
        var totals = new Dictionary<uint, long>();
        foreach (PlayerbotBagItem item in items) totals[item.Template.Entry] = totals.GetValueOrDefault(item.Template.Entry) + item.Count;
        foreach (IGrouping<uint, PlayerbotBagItem> stacks in items.Where(item => (ItemClass)item.Template.Class == ItemClass.Consumable)
                     .GroupBy(item => item.Template.Entry))
        {
            long held = totals[stacks.Key];
            foreach (PlayerbotBagItem stack in stacks.OrderBy(item => item.Count).ThenBy(item => item.Guid.Value))
            {
                if (!IsSurplusConsumable(stack, (uint)Math.Clamp(held - stack.Count, 0L, uint.MaxValue), keep)) continue;
                surplus.Add(stack.Guid.Value);
                held -= stack.Count;
            }
        }

        return surplus;
    }

    /// <summary>
    /// The visit's one roll: lists at all this visit (0 never, 100 always). A bot on the money goal lists without drawing, whenever listing is
    /// allowed at all.
    /// </summary>
    internal static bool RollsToList(PlayerbotEconomyOptions options, Random random, bool moneyGoal = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);
        if (options.ListChancePerVisit <= 0 || options.MaxListingsPerBot <= 0) return false;
        if (moneyGoal && options.MoneyGoal) return true;
        return options.ListChancePerVisit >= 100 || random.Next(100) < options.ListChancePerVisit;
    }

    /// <summary>The listings the bot may still add given what the house counts as its own (the owner list's total).</summary>
    internal static int FreeListingSlots(PlayerbotEconomyOptions options, uint ownerTotal, bool moneyGoal = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (int)Math.Max(0L, ListingCap(options, moneyGoal) - (long)ownerTotal);
    }

    /// <summary>The listings one visit may add: the free slots when the visit's roll succeeds, else none.</summary>
    internal static int ListingSlots(PlayerbotEconomyOptions options, uint ownerTotal, Random random)
        => RollsToList(options, random) ? FreeListingSlots(options, ownerTotal) : 0;

    /// <summary>What a visit may spend in all: the money held at its start times <see cref="PlayerbotEconomyOptions.BuyUpgradeBudgetFraction"/>, rounded down.</summary>
    internal static uint VisitBudget(uint startMoney, float fraction)
        => !float.IsFinite(fraction) || fraction <= 0f ? 0u : (uint)Math.Min(Math.Floor(startMoney * (double)Math.Min(fraction, 1f)), startMoney);

    /// <summary>What is left of <paramref name="budget"/> after <paramref name="spent"/>.</summary>
    internal static uint Remaining(uint budget, uint spent) => spent >= budget ? 0u : budget - spent;

    /// <summary>
    /// The price plan for listing <paramref name="item"/>, or null when it does not pay or does not fit the budget left (a deposit is spent
    /// money: it counts against the visit's budget).
    /// </summary>
    internal static PlayerbotListingPlan? PlanListing(PlayerbotBagItem item, IReadOnlyList<PlayerbotAuctionRow> competitors, ulong self,
        AuctionHouseEntry house, uint depositMinimum, float depositRate, float cutRate, uint remainingBudget, int markupPercent)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(house);
        uint buyout = PlayerbotAuctionPricing.ListingPrice(item.Template, item.Count, competitors, self, house, cutRate, markupPercent);
        if (buyout == 0) return null;
        uint floor = PlayerbotAuctionPricing.FloorPerUnit(item.Template, house.CutPercent * cutRate, markupPercent);
        uint deposit = PlayerbotAuctionPricing.Deposit(house, item.Template, item.Count, depositMinimum, depositRate);
        if (!PlayerbotAuctionPricing.IsWorthListing(item.Template, item.Count, buyout, deposit, house, cutRate, remainingBudget)) return null;
        return new PlayerbotListingPlan(buyout, PlayerbotAuctionPricing.StartBid(buyout, floor, item.Count), deposit);
    }

    /// <summary>
    /// The auction to buy out: of the rows the bot can pay (a buyout, not its own, within the budget left and the money in hand) the one
    /// whose item gains the most for its slot, then the lower buyout, then the lower auction id. Null when nothing gains.
    /// </summary>
    /// <param name="startMoney">The money held when the visit began (the budget's base).</param>
    /// <param name="money">The money held now.</param>
    /// <param name="spent">What this visit has spent so far (deposits and buyouts).</param>
    /// <param name="gain">The gain of wearing an item; null when the bot cannot use it.</param>
    /// <param name="unitCap">The most per unit the bot pays for an item (<see cref="PlayerbotAuctionPricing.BuyCapPerUnit"/>); 0 means never bought.</param>
    /// <param name="isRefused">Auctions the house refused to sell to the bot lately.</param>
    internal static PlayerbotUpgradeChoice? ChooseUpgrade(IEnumerable<PlayerbotAuctionRow> rows, ulong self, uint startMoney, uint money,
        uint spent, float fraction, Func<uint, ItemTemplate?> templates, Func<ItemTemplate, float?> gain, Func<ItemTemplate, uint> unitCap,
        Func<uint, bool>? isRefused = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(gain);
        ArgumentNullException.ThrowIfNull(unitCap);
        uint limit = Math.Min(Remaining(VisitBudget(startMoney, fraction), spent), money);
        PlayerbotUpgradeChoice? best = null;
        foreach (PlayerbotAuctionRow row in rows)
        {
            if (row.Buyout == 0 || row.Count == 0 || row.Buyout > limit || row.Owner == self || isRefused?.Invoke(row.Id) == true) continue;
            if (templates(row.Entry) is not { } template || gain(template) is not { } value || value <= PlayerbotItemScore.MinimumGain) continue;
            uint cap = unitCap(template);
            if (cap == 0 || ((ulong)row.Buyout + row.Count - 1) / row.Count > cap) continue;
            if (best is null || value > best.Gain
                || (value == best.Gain && (row.Buyout < best.Row.Buyout || (row.Buyout == best.Row.Buyout && row.Id < best.Row.Id))))
                best = new PlayerbotUpgradeChoice(row, value);
        }

        return best;
    }

    /// <summary>The entry and action of an auction house letter's subject <c>"entry:0:action"</c> (<see cref="MailRules.AuctionSubject"/>).</summary>
    internal static bool TryParseAuctionSubject(string subject, out uint entry, out AuctionMailAction action)
    {
        entry = 0;
        action = default;
        string[] parts = (subject ?? string.Empty).Split(':');
        if (parts.Length != 3 || !uint.TryParse(parts[0], out entry) || parts[1] != "0"
            || !int.TryParse(parts[2], out int raw) || !Enum.IsDefined((AuctionMailAction)raw))
            return false;
        action = (AuctionMailAction)raw;
        return true;
    }
}

/// <summary>
/// Which item entries came back expired, so the bot does not list the same unsold thing again and again: an entry that expired
/// <see cref="GiveUpAfter"/> times is given up (a sale forgets it). In memory, bounded to <see cref="MaxEntries"/> entries (the oldest go
/// first); lost on restart, which only lets the bot try once more.
/// </summary>
internal sealed class PlayerbotRelistMemory
{
    internal const int MaxEntries = 64;
    internal const int GiveUpAfter = 2;

    private readonly Dictionary<uint, (int Seen, long Order)> _expiries = [];
    private long _next;

    internal int Count => _expiries.Count;

    internal void NoteExpired(uint entry)
    {
        if (_expiries.TryGetValue(entry, out (int Seen, long Order) known))
        {
            _expiries[entry] = (known.Seen + 1, known.Order);
            return;
        }

        if (_expiries.Count >= MaxEntries) _expiries.Remove(_expiries.MinBy(pair => pair.Value.Order).Key);
        _expiries[entry] = (1, _next++);
    }

    internal void NoteSold(uint entry) => _expiries.Remove(entry);

    internal bool IsGivenUp(uint entry) => _expiries.TryGetValue(entry, out (int Seen, long Order) known) && known.Seen >= GiveUpAfter;
}
