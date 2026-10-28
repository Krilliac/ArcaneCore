using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Where a bot is in its auction house visit.</summary>
internal enum PlayerbotEconomyPhase
{
    Idle,
    Travel,
    OwnerList,
    Listing,
    AwaitSell,
    Buying,
    AwaitBid,
    MailStart,
    AwaitMailList,
    MailWork,
    AwaitMailResult,
    BuyingReagents,
}

/// <summary>
/// One managed bot's use of the faction auction house (docs/areas/playbots-economy.md): every <see cref="PlayerbotEconomyOptions.VisitIntervalMinutes"/>
/// it walks to the auctioneer of its own faction, lists the loot it has no use for, buys one gear upgrade within the visit's budget, then
/// collects what sold, expired or was won from a mailbox. Every action is an ordinary client packet through the world handlers
/// (<see cref="WorldSession.TryManagedAction"/>); replies are read from the bot's own packet queue.
/// <para>
/// Threading and ownership: one instance per <see cref="PlayerbotBrain"/>, used on the world thread only. It holds no reference to the
/// economy's internals: the one fact it reads from the settlement layer is <c>Settlements.CanAct</c>. At most one economy operation that
/// settles asynchronously (a listing, a buyout, a mail take) is outstanding; the bot sends nothing else until its result arrives or the
/// <see cref="OperationTimeoutMs"/> deadline passes (a deadline drops the whole visit). A request the player cannot make (money, space)
/// is checked before it is sent, because the server answers some of them with silence.
/// </para>
/// </summary>
internal sealed partial class PlayerbotEconomy(WorldSession session, PlayerbotOptions options)
{
    /// <summary>The longest one visit lasts (world time) before it is dropped.</summary>
    internal const uint VisitCapMs = 300_000;

    /// <summary>How long an asynchronous result (listing, buyout, mail) is waited for.</summary>
    internal const uint OperationTimeoutMs = 30_000;

    /// <summary>How long the deletion of an emptied letter is waited for (it is cosmetic).</summary>
    internal const uint DeleteTimeoutMs = 10_000;

    /// <summary>How long after a dropped visit the next one is tried (or the interval when that is shorter).</summary>
    internal const uint RetryAfterAbortMs = 300_000;

    /// <summary>How long an auction the house would not sell is left alone.</summary>
    internal const uint RefusedAuctionMs = 600_000;

    /// <summary>How long unread letters the bot does not handle (players' letters, cash on delivery) are not a reason for a visit.</summary>
    internal const uint ForeignMailBackoffMs = 86_400_000;

    /// <summary>The upgrades one visit buys.</summary>
    internal const int MaxPurchasesPerVisit = 1;

    /// <summary>The most letters one visit handles.</summary>
    internal const int MaxLettersPerVisit = 20;

    /// <summary>The reagent auctions one visit buys.</summary>
    internal const int MaxReagentPurchasesPerVisit = 2;

    /// <summary>The reagent searches one visit sends.</summary>
    internal const int MaxReagentSearchesPerVisit = 4;

    private const int MaxRefusedAuctions = 64;
    private const int MaxFailures = 4;
    private const int MaxTravelFailures = 3;

    private readonly PlayerbotEconomyDestinations _destinations = new(session, options);
    private readonly PlayerbotRelistMemory _relist = new();
    private readonly Dictionary<uint, uint> _refusedAuctions = [];

    private PlayerbotEconomyPhase _phase;
    private bool _scheduled;
    private uint _nextVisitAt;
    private uint _nextMailTriggerAt;
    private uint _visitNumber;
    private uint _visitStartedAt;
    private uint _mapId;
    private uint _startMoney;
    private uint _spent;
    private bool _wantList;
    private bool _wantBuy;
    private bool _wantReagents;
    private bool _listingFault;
    private int _failures;
    private int _travelFailures;
    private Random? _random;

    // The auctioneer in use and the listing in progress.
    private ObjectGuid _auctioneer;
    private AuctionHouseEntry? _house;
    private int _slots;
    private readonly Queue<PlayerbotBagItem> _queue = new();
    private uint _moneyBefore;
    private uint _expectedCost;
    private uint _operationDeadline;
    private uint _bidAuction;
    private int _purchases;
    private int _buyAttempts;
    private int _scanStep;
    private readonly List<PlayerbotAuctionRow> _scan = [];

    // The reagent buying: what the bot still lacks, the entries to search in turn, the rows found, and the bid in flight.
    private static readonly IReadOnlyDictionary<uint, uint> NoReagents = new Dictionary<uint, uint>();
    private readonly PlayerbotVendorSupplyNeeds _supply = new();
    private readonly Dictionary<uint, uint> _reagentMissing = [];
    private readonly List<uint> _reagentOrder = [];
    private readonly List<PlayerbotAuctionRow> _reagentScan = [];
    private int _reagentSearches;
    private int _reagentPurchases;
    private int _reagentFailures;
    private PlayerbotEconomyPhase _bidReturn = PlayerbotEconomyPhase.Buying;
    private uint _bidEntry;
    private uint _bidCount;

    // The mailbox work.
    private ObjectGuid _mailbox;
    private readonly List<PlayerbotMailLetter> _letters = [];
    private int _letterIndex;
    private uint _letterMoney;
    private uint _letterItem;
    private bool _letterBlocked;
    private MailAction _awaitedMail;
    private int _foreignUnread;
    private int _handledLetters;

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Vendor;

    /// <summary>
    /// What the bot saves for (<see cref="PlayerbotBudget"/>): a visit spends only the money left after the class spells, the riding
    /// training and the mount the bot is saving for. Null (a bare controller in a test) spends from all the bot's money.
    /// </summary>
    internal PlayerbotBudget? SpendBudget { get; set; }

    /// <summary>The money a visit may spend on purchases: what the budget reserves for higher priorities is kept aside (listing deposits are not purchases).</summary>
    private uint Spendable(Player player) => SpendBudget?.Spendable(PlayerbotSpendKind.Discretionary, player) ?? player.Money;

    internal uint TargetEntry { get; private set; }

    internal PlayerbotEconomyPhase Phase => _phase;

    /// <summary>What the current visit has spent so far, deposits and buyouts (tests, inspection).</summary>
    internal uint Spent => _spent;

    /// <summary>The visit's budget (tests, inspection).</summary>
    internal uint Budget => PlayerbotEconomyPlanner.VisitBudget(_startMoney, options.Economy.BuyUpgradeBudgetFraction);

    internal int ListingsPlaced { get; private set; }

    internal int PurchasesMade { get; private set; }

    /// <summary>Reagent auctions bought over the bot's life (tests, inspection).</summary>
    internal int ReagentPurchasesMade { get; private set; }

    /// <summary>Entries the bot bought lately, which it does not list again.</summary>
    internal PlayerbotBoughtMemory Bought { get; } = new PlayerbotBoughtMemory();

    /// <summary>
    /// Test seam: the per-craft reagent counts of the recipes the bot wants. Null (default) reads them from the bot's spellbook through a
    /// <see cref="ArcaneCore.World.Playerbots.Progression.PlayerbotVendorSupplyNeeds"/>.
    /// </summary>
    internal Func<Player, IReadOnlyDictionary<uint, uint>>? ReagentNeeds { get; set; }

    /// <summary>
    /// Test seam: whether a vendor stocks an item entry (such an entry is never bought at the house). Null (default) asks the world's
    /// <see cref="PlayerbotServiceIndex"/>; when that index is unavailable (no creature content or spell table, no NPC services) every
    /// reagent counts as vendor stocked, so none is bought at the house (fail closed).
    /// </summary>
    internal Func<uint, bool>? VendorStocked { get; set; }

    internal int ItemsCollected { get; private set; }

    /// <summary>The copper the bot took from auction letters over its life (tests, inspection).</summary>
    internal long MoneyCollected { get; private set; }

    internal int VisitsFinished { get; private set; }

    internal PlayerbotRelistMemory Relist => _relist;

    /// <summary>Test seam: the random source of the next visits (default: seeded from the bot's guid and the visit number).</summary>
    internal Func<Random>? RandomSource { get; set; }

    /// <summary>Make the next visit due now (tests, inspection).</summary>
    internal void RequestVisit()
    {
        _scheduled = true;
        _nextVisitAt = session.World.NowMs;
        _nextMailTriggerAt = 0;
    }

    /// <summary>Drop a visit under way (the brain stopped, tests).</summary>
    internal void Reset() => Abort();

    /// <summary>
    /// One think. True when the bot spent it on the visit (walking, a request, waiting for a reply); false when it has nothing to do here or
    /// cannot now (the caller goes on with its other goals).
    /// </summary>
    internal bool Update(Player player, uint elapsedMs)
    {
        PlayerbotEconomyOptions config = options.Economy;
        if (!config.Enabled)
        {
            if (_phase != PlayerbotEconomyPhase.Idle) Abort();
            return false;
        }

        if (elapsedMs == 0) return false;
        if (!player.IsInWorld || !player.IsAlive || (player.Flags & PlayerFlags.Ghost) != 0 || player.Map is null || player.Combat.IsInCombat)
        {
            if (_phase != PlayerbotEconomyPhase.Idle) Abort();
            return false;
        }

        if (IsCasting(player)) return false;
        if (session.Services.GetService<EconomyFeature>() is not { Enabled: true } economy) return false;
        uint now = session.World.NowMs;
        if (_phase == PlayerbotEconomyPhase.Idle)
        {
            if (!TryBegin(player, economy, config, now)) return false;
        }
        else if (Elapsed(now, _visitStartedAt) >= VisitCapMs || player.Map.MapId != _mapId)
        {
            Abort();
            return false;
        }

        // A throttled action is not a failure: the shared per-tick budget is spent; the visit waits for its next turn.
        if (session.ManagedBudget is { Remaining: <= 0 }) return true;
        return Step(player, economy, config, elapsedMs, now);
    }

    // --- visit start -----------------------------------------------------------------------------------------------

    private bool TryBegin(Player player, EconomyFeature economy, PlayerbotEconomyOptions config, uint now)
    {
        uint interval = IntervalMs(config);
        if (!_scheduled)
        {
            _scheduled = true;
            _nextVisitAt = unchecked(now + (uint)((player.Guid.Low * 2654435761UL) % interval));
        }
        else if (unchecked((int)(_nextVisitAt - now)) > (int)interval)
        {
            _nextVisitAt = unchecked(now + interval); // the interval was shortened by a reload
        }

        if (!Reached(now, _nextVisitAt)) return false;
        if (!economy.Settlements.CanAct(session, player)) return false;

        var random = RandomSource?.Invoke() ?? new Random(unchecked((int)(player.Guid.Low * 2654435761u) ^ (int)(_visitNumber * 40503u)));
        IReadOnlyDictionary<uint, uint> wanted = WantedReagents(player);
        IReadOnlyList<PlayerbotBagItem> listables = PlayerbotEconomyPlanner.Listables(BagItems(player, wanted), item => PlayerbotGearScoreSeam.Gain(player, item), _relist);
        bool list = listables.Count > 0 && PlayerbotEconomyPlanner.RollsToList(config, random);
        uint visitMoney = Math.Min(PlayerbotEconomyPlanner.VisitBudget(player.Money, config.BuyUpgradeBudgetFraction), Spendable(player));
        bool buy = visitMoney >= PlayerbotEconomyPlanner.MinimumUpgradeBudget && PlayerbotTownGoals.FreeBagSlots(player) > 0;
        IReadOnlyDictionary<uint, uint> shortfalls = config.BuyReagents && visitMoney >= PlayerbotEconomyPlanner.MinimumUpgradeBudget && PlayerbotTownGoals.FreeBagSlots(player) > 0
            ? WithoutAwaiting(PlayerbotEconomyPlanner.Shortfalls(wanted, entry => player.Inventory.GetItemCount(entry)), now, VendorStockedFor(player))
            : NoReagents;
        bool reagents = shortfalls.Count > 0;
        if (!list && !buy && !reagents && !(Reached(now, _nextMailTriggerAt) && HasUnreadMail(out bool known) && known))
        {
            // Nothing to list or buy and no unread letter: wait a full interval. A mail query the shared budget refused is tried again.
            if (session.ManagedBudget is { Remaining: <= 0 }) return false;
            Schedule(now, interval);
            return false;
        }

        _visitNumber++;
        _random = random;
        _visitStartedAt = now;
        _mapId = player.Map!.MapId;
        _startMoney = player.Money; // listing deposits are bounded by the bot's money, never by a reserve; only purchases are (Spendable)
        _spent = 0;
        _wantList = list;
        _wantBuy = buy;
        _wantReagents = reagents;
        _reagentMissing.Clear();
        _reagentOrder.Clear();
        foreach ((uint entry, uint missing) in shortfalls) _reagentMissing[entry] = missing;
        _reagentOrder.AddRange(_reagentMissing.Keys.Order());
        if (_reagentOrder.Count > 1)
        {
            // The searches are capped, so the start rotates with the visit: later entries get their turn on later visits.
            int start = (int)(_visitNumber % (uint)_reagentOrder.Count);
            if (start > 0)
            {
                List<uint> head = _reagentOrder.GetRange(0, start);
                _reagentOrder.RemoveRange(0, start);
                _reagentOrder.AddRange(head);
            }
        }

        _reagentScan.Clear();
        _reagentSearches = 0;
        _reagentPurchases = 0;
        _reagentFailures = 0;
        _listingFault = false;
        _failures = 0;
        _travelFailures = 0;
        _purchases = 0;
        _buyAttempts = 0;
        _scanStep = 0;
        _scan.Clear();
        _queue.Clear();
        _letters.Clear();
        _foreignUnread = 0;
        _handledLetters = 0;
        _phase = PlayerbotEconomyPhase.Travel;
        return true;
    }

    /// <summary>MSG_QUERY_NEXT_MAIL_TIME: whether an unread delivered letter waits. <paramref name="known"/> is false when the query was not sent or not answered.</summary>
    private bool HasUnreadMail(out bool known)
    {
        known = false;
        session.DrainManagedPackets(WorldOpcode.MsgQueryNextMailTime);
        if (!session.TryManagedAction(WorldOpcode.MsgQueryNextMailTime, [])) return false;
        IReadOnlyList<ManagedSessionPacket> replies = session.DrainManagedPackets(WorldOpcode.MsgQueryNextMailTime);
        if (replies.Count == 0 || !TryDecode(() => PlayerbotEconomyWire.DecodeHasUnreadMail(replies[^1].Payload), out bool unread)) return false;
        known = true;
        return unread;
    }

    // --- the visit -------------------------------------------------------------------------------------------------

    private bool Step(Player player, EconomyFeature economy, PlayerbotEconomyOptions config, uint elapsedMs, uint now)
    {
        Goal = PlayerbotGoalKind.Vendor;
        return _phase switch
        {
            PlayerbotEconomyPhase.Travel => StepTravel(player, economy, elapsedMs, now),
            PlayerbotEconomyPhase.OwnerList => StepOwnerList(player, economy, config, now),
            PlayerbotEconomyPhase.Listing => StepListing(player, economy, config, now),
            PlayerbotEconomyPhase.AwaitSell => StepAwaitSell(player, now),
            PlayerbotEconomyPhase.BuyingReagents => StepBuyingReagents(player, economy, now),
            PlayerbotEconomyPhase.Buying => StepBuying(player, economy, config, now),
            PlayerbotEconomyPhase.AwaitBid => StepAwaitBid(player, now),
            PlayerbotEconomyPhase.MailStart => StepMailStart(player, economy, elapsedMs, now),
            PlayerbotEconomyPhase.AwaitMailList => StepAwaitMailList(player, now),
            PlayerbotEconomyPhase.MailWork => StepMailWork(player, now),
            PlayerbotEconomyPhase.AwaitMailResult => StepAwaitMailResult(player, now),
            _ => false,
        };
    }

    private bool StepTravel(Player player, EconomyFeature economy, uint elapsedMs, uint now)
    {
        PlayerbotAuctioneerSighting? seen = _destinations.FindAuctioneer(player, economy);
        if (seen is null)
        {
            if (_destinations.StepToHint(player, economy, elapsedMs, now))
            {
                TargetEntry = _destinations.TargetEntry;
                return true;
            }

            // No faction auctioneer in sight or on the map's hints: the auction part is over; the mailbox may still be here.
            _wantList = false;
            _wantBuy = false;
            _wantReagents = false;
            return ToMail();
        }

        TargetEntry = seen.Npc.Entry;
        if (!seen.Interactable)
        {
            if (_destinations.WalkTo(player, seen.Position, elapsedMs)) return true;
            if (++_travelFailures < MaxTravelFailures) return true;
            Abort();
            return false;
        }

        if (!PlayerbotMovementControl.Stop(session, player)) return true;
        _destinations.Clear();
        _auctioneer = seen.Npc.Guid;
        _house = seen.House;
        _phase = _wantList ? PlayerbotEconomyPhase.OwnerList : AfterListing();
        return true;
    }

    private bool StepOwnerList(Player player, EconomyFeature economy, PlayerbotEconomyOptions config, uint now)
    {
        if (!AuctioneerStillUsable(player, economy)) return Finish(now);
        session.DrainManagedPackets(WorldOpcode.SmsgAuctionOwnerListResult);
        if (!session.TryManagedAction(WorldOpcode.CmsgAuctionListOwnerItems, PlayerbotEconomyWire.ListOwnerItems(_auctioneer.Value, 0))) return Failed();
        IReadOnlyList<ManagedSessionPacket> replies = session.DrainManagedPackets(WorldOpcode.SmsgAuctionOwnerListResult);
        if (replies.Count == 0 || !TryDecode(() => PlayerbotEconomyWire.DecodeAuctionList(replies[^1].Payload), out PlayerbotAuctionPage? page) || page is null)
        {
            // The bot does not know how many listings it already has: it lists nothing this visit (fail closed).
            _phase = AfterListing();
            return true;
        }

        _slots = PlayerbotEconomyPlanner.FreeListingSlots(config, page.Total);
        _queue.Clear();
        foreach (PlayerbotBagItem item in PlayerbotEconomyPlanner.Listables(BagItems(player, WantedReagents(player)), t => PlayerbotGearScoreSeam.Gain(player, t), _relist))
            _queue.Enqueue(item);
        _phase = _slots > 0 && _queue.Count > 0 ? PlayerbotEconomyPhase.Listing : AfterListing();
        return true;
    }

    private bool StepListing(Player player, EconomyFeature economy, PlayerbotEconomyOptions config, uint now)
    {
        if (!AuctioneerStillUsable(player, economy)) return Finish(now);
        if (_slots <= 0 || _listingFault || !economy.Settlements.CanAct(session, player) || !TryNextListable(player, out PlayerbotBagItem? item) || item is null)
        {
            _phase = AfterListing();
            return true;
        }

        // The market: every listing of the same item name, to undercut the cheapest buyout and learn the going rate.
        session.DrainManagedPackets(WorldOpcode.SmsgAuctionListResult);
        byte[] search = PlayerbotEconomyWire.ListItems(_auctioneer.Value, 0, item.Template.Name, 0, 0, PlayerbotEconomyWire.Any, item.Template.Class,
            PlayerbotEconomyWire.Any, PlayerbotEconomyWire.Any, usable: false);
        if (!session.TryManagedAction(WorldOpcode.CmsgAuctionListItems, search))
        {
            _queue.Enqueue(item);
            return Failed();
        }

        IReadOnlyList<ManagedSessionPacket> replies = session.DrainManagedPackets(WorldOpcode.SmsgAuctionListResult);
        if (replies.Count == 0 || !TryDecode(() => PlayerbotEconomyWire.DecodeAuctionList(replies[^1].Payload), out PlayerbotAuctionPage? page) || page is null)
        {
            _phase = AfterListing();
            return true;
        }

        PlayerbotListingPlan? plan = PlayerbotEconomyPlanner.PlanListing(item, page.Rows, player.Guid.Value, _house!, economy.Options.AuctionDepositMin,
            economy.Options.AuctionRateDeposit, economy.Options.AuctionRateCut, PlayerbotEconomyPlanner.Remaining(Budget, _spent));
        if (plan is null || plan.Deposit > player.Money) return true;

        session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult);
        _moneyBefore = player.Money;
        _expectedCost = plan.Deposit;
        byte[] sell = PlayerbotEconomyWire.SellItem(_auctioneer.Value, item.Guid.Value, plan.StartBid, plan.Buyout, PlayerbotAuctionPricing.DurationMinutes);
        if (!session.TryManagedAction(WorldOpcode.CmsgAuctionSellItem, sell)) return Failed();
        _slots--;
        if (!player.IsQuestSettlementPending)
        {
            // Dropped or refused at once (a refusal is answered inside the handler): nothing was taken.
            bool itemMissing = false;
            foreach (ManagedSessionPacket packet in session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult))
            {
                if (TryDecode(() => PlayerbotEconomyWire.DecodeCommandResult(packet.Payload), out PlayerbotAuctionResult immediate)
                    && immediate.Action == AuctionAction.Started && immediate.Error == AuctionError.ItemNotFound)
                    itemMissing = true;
            }

            if (!session.IsInChaosProbe)
            {
                if (itemMissing)
                    PlayerbotAnomalies.Report(player, "economy", "item-not-found", $"auction listing item {item.Template.Entry} (guid {item.Guid}) not found by auction house");
                PlayerbotAnomalies.Report(player, "economy", "ah-post-failed", $"auction listing for item {item.Template.Entry} (guid {item.Guid}) was rejected immediately");
                PlayerbotAnomalies.Report(player, "economy", "sell-failed", $"auction listing for item {item.Template.Entry} (guid {item.Guid}) was rejected immediately");
            }
            return true;
        }

        _operationDeadline = unchecked(now + OperationTimeoutMs);
        _phase = PlayerbotEconomyPhase.AwaitSell;
        return true;
    }

    private bool StepAwaitSell(Player player, uint now)
    {
        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult))
        {
            if (!TryDecode(() => PlayerbotEconomyWire.DecodeCommandResult(packet.Payload), out PlayerbotAuctionResult result)
                || result.Action != AuctionAction.Started)
                continue;
            if (result.Error == AuctionError.Ok)
            {
                ListingsPlaced++;
                uint paid = _moneyBefore >= player.Money ? _moneyBefore - player.Money : 0;
                _spent = unchecked(_spent + paid);
                // The deposit is the one thing a listing costs the bot now; any other amount means the pricing assumption failed.
                if (player.Money > _moneyBefore || paid != _expectedCost)
                {
                    _listingFault = true;
                    if (!session.IsInChaosProbe)
                        PlayerbotAnomalies.Report(player, "economy", "money-invalid",
                            $"auction listing deposit mismatch (before={_moneyBefore}, after={player.Money}, paid={paid}, expected={_expectedCost})",
                            AnomalySeverity.Error);
                }
            }
            else if (!session.IsInChaosProbe)
            {
                if (result.Error == AuctionError.ItemNotFound)
                    PlayerbotAnomalies.Report(player, "economy", "item-not-found", $"auction listing failed with ItemNotFound (auction={result.AuctionId})");
                PlayerbotAnomalies.Report(player, "economy", "ah-post-failed", $"auction listing failed with {result.Error} (auction={result.AuctionId})");
                PlayerbotAnomalies.Report(player, "economy", "sell-failed", $"auction listing failed with {result.Error} (auction={result.AuctionId})");
            }

            _phase = PlayerbotEconomyPhase.Listing;
            return true;
        }

        if (!player.IsQuestSettlementPending)
        {
            // The settlement ended without a result (an unknown outcome): count what left the purse and carry on.
            _spent = unchecked(_spent + (_moneyBefore >= player.Money ? _moneyBefore - player.Money : 0));
            if (!session.IsInChaosProbe)
            {
                PlayerbotAnomalies.Report(player, "economy", "ah-post-failed", "auction listing settlement ended without a command result");
                PlayerbotAnomalies.Report(player, "economy", "sell-failed", "auction listing settlement ended without a command result");
            }
            _phase = PlayerbotEconomyPhase.Listing;
            return true;
        }

        if (Reached(now, _operationDeadline))
        {
            if (!session.IsInChaosProbe)
            {
                PlayerbotAnomalies.Report(player, "economy", "ah-post-failed", "auction listing timed out waiting for settlement");
                PlayerbotAnomalies.Report(player, "economy", "sell-failed", "auction listing timed out waiting for settlement");
            }
            Abort();
            return false;
        }

        return true;
    }

    private bool StepBuying(Player player, EconomyFeature economy, PlayerbotEconomyOptions config, uint now)
    {
        if (!AuctioneerStillUsable(player, economy)) return Finish(now);
        uint remaining = PlayerbotEconomyPlanner.Remaining(Budget, _spent);
        if (_purchases >= MaxPurchasesPerVisit || _buyAttempts >= 2 || remaining < PlayerbotEconomyPlanner.MinimumUpgradeBudget
            || PlayerbotTownGoals.FreeBagSlots(player) == 0 || !economy.Settlements.CanAct(session, player))
        {
            _phase = MailPhase();
            return true;
        }

        if (_scanStep < 2)
        {
            // The cheapest page of usable green-or-better weapons, then of armor (the house lists by buyout, 50 a page).
            uint itemClass = (uint)(_scanStep == 0 ? ItemClass.Weapon : ItemClass.Armor);
            session.DrainManagedPackets(WorldOpcode.SmsgAuctionListResult);
            byte[] search = PlayerbotEconomyWire.ListItems(_auctioneer.Value, 0, string.Empty, 0, 0, PlayerbotEconomyWire.Any, itemClass,
                PlayerbotEconomyWire.Any, 2, usable: true);
            if (!session.TryManagedAction(WorldOpcode.CmsgAuctionListItems, search)) return Failed();
            IReadOnlyList<ManagedSessionPacket> replies = session.DrainManagedPackets(WorldOpcode.SmsgAuctionListResult);
            if (replies.Count > 0 && TryDecode(() => PlayerbotEconomyWire.DecodeAuctionList(replies[^1].Payload), out PlayerbotAuctionPage? page) && page is not null)
                _scan.AddRange(page.Rows);
            _scanStep++;
            return true;
        }

        PlayerbotUpgradeChoice? choice = PlayerbotEconomyPlanner.ChooseUpgrade(_scan.DistinctBy(row => row.Id), player.Guid.Value, _startMoney, Math.Min(player.Money, Spendable(player)),
            _spent, config.BuyUpgradeBudgetFraction, entry => player.Inventory.Templates.Find(entry), template => PlayerbotGearScoreSeam.Gain(player, template),
            IsRefused);
        if (choice is null)
        {
            _phase = MailPhase();
            return true;
        }

        _buyAttempts++;
        _bidReturn = PlayerbotEconomyPhase.Buying;
        _bidAuction = choice.Row.Id;
        _moneyBefore = player.Money;
        _expectedCost = choice.Row.Buyout;
        session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult);
        if (!session.TryManagedAction(WorldOpcode.CmsgAuctionPlaceBid, PlayerbotEconomyWire.PlaceBid(_auctioneer.Value, choice.Row.Id, choice.Row.Buyout)))
        {
            Refuse(choice.Row.Id);
            return Failed();
        }

        if (!player.IsQuestSettlementPending)
        {
            // The bid settled at once. Money gone means the buyout went through (book it as a purchase); money unchanged means the house
            // answered with an error or with silence (an unaffordable or foreign-account bid): leave the auction alone.
            session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult);
            if (player.Money < _moneyBefore) BookBuyout(_moneyBefore - player.Money);
            else Refuse(choice.Row.Id);
            return true;
        }

        _operationDeadline = unchecked(now + OperationTimeoutMs);
        _phase = PlayerbotEconomyPhase.AwaitBid;
        return true;
    }

    private bool StepAwaitBid(Player player, uint now)
    {
        bool reagent = _bidReturn == PlayerbotEconomyPhase.BuyingReagents;
        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult))
        {
            if (!TryDecode(() => PlayerbotEconomyWire.DecodeCommandResult(packet.Payload), out PlayerbotAuctionResult result)
                || result.Action != AuctionAction.BidPlaced || result.AuctionId != _bidAuction)
                continue;
            if (result.Error == AuctionError.Ok)
            {
                uint paid = _moneyBefore >= player.Money ? _moneyBefore - player.Money : 0;
                BookBuyout(paid);

                if ((player.Money > _moneyBefore || paid != _expectedCost) && !session.IsInChaosProbe)
                {
                    PlayerbotAnomalies.Report(player, "economy", "money-invalid",
                        $"auction buyout cost mismatch for auction {_bidAuction} (before={_moneyBefore}, after={player.Money}, paid={paid}, expected={_expectedCost})",
                        AnomalySeverity.Error);
                }
            }
            else
            {
                NoteBidFailed();
            }

            _phase = _bidReturn;
            return true;
        }

        if (!player.IsQuestSettlementPending)
        {
            // The settlement finished without a result packet. Money gone means the buyout went through: book it as a purchase
            // (a reagent must not be bought again by the next visit); no money gone means the house did not take the bid.
            uint paid = _moneyBefore >= player.Money ? _moneyBefore - player.Money : 0;
            if (paid > 0) BookBuyout(paid);
            else NoteBidFailed();

            _phase = _bidReturn;
            return true;
        }

        if (Reached(now, _operationDeadline))
        {
            // An unknown outcome of a reagent bid must not let the next visit buy the same reagent again (the hold lapses after HoldMs).
            if (reagent) Bought.NoteBought(_bidEntry, now);
            Abort();
            return false;
        }

        return true;
    }

    /// <summary>
    /// A buyout the bot paid for (<paramref name="paid"/> copper left its purse): count the spend, then book a reagent as bought (so a later
    /// visit does not buy it again) or a gear upgrade as a purchase. Shared by the confirmed buyout, the settlement that ended without a result,
    /// and the bid that settled at once without a pending settlement.
    /// </summary>
    private void BookBuyout(uint paid)
    {
        _spent = unchecked(_spent + paid);
        if (_bidReturn == PlayerbotEconomyPhase.BuyingReagents)
        {
            NoteReagentBought();
        }
        else
        {
            _purchases++;
            PurchasesMade++;
        }
    }

    /// <summary>A bid the house did not take: the auction is left alone for a while, and a reagent bid counts toward the visit's two failed tries.</summary>
    private void NoteBidFailed()
    {
        Refuse(_bidAuction);
        if (_bidReturn == PlayerbotEconomyPhase.BuyingReagents) _reagentFailures++;
    }

    /// <summary>A reagent buyout went through: count it, hold the entry against listing, and take it off what the bot still lacks.</summary>
    private void NoteReagentBought()
    {
        _reagentPurchases++;
        ReagentPurchasesMade++;
        Bought.NoteBought(_bidEntry, session.World.NowMs);
        if (_reagentMissing.TryGetValue(_bidEntry, out uint lacking)) _reagentMissing[_bidEntry] = lacking > _bidCount ? lacking - _bidCount : 0;
        _reagentScan.RemoveAll(row => row.Id == _bidAuction);
    }

    // --- the reagents ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The reagent phase: search the house for each reagent the bot lacks (one search a think, at most <see cref="MaxReagentSearchesPerVisit"/>
    /// a visit), then buy out the best auction within the cap per unit and the visit's budget, up to <see cref="MaxReagentPurchasesPerVisit"/>.
    /// </summary>
    private bool StepBuyingReagents(Player player, EconomyFeature economy, uint now)
    {
        if (!AuctioneerStillUsable(player, economy)) return Finish(now);
        if (!options.Economy.BuyReagents)
        {
            // A live reload turned reagent buying off: leave the phase at once.
            _phase = AfterReagents();
            return true;
        }

        uint limit = Math.Min(Math.Min(PlayerbotEconomyPlanner.Remaining(Budget, _spent), player.Money), Spendable(player));
        if (_reagentPurchases >= MaxReagentPurchasesPerVisit || _reagentFailures >= 2 || limit == 0
            || PlayerbotTownGoals.FreeBagSlots(player) == 0 || !economy.Settlements.CanAct(session, player))
        {
            _phase = AfterReagents();
            return true;
        }

        if (_reagentSearches < Math.Min(_reagentOrder.Count, MaxReagentSearchesPerVisit))
        {
            uint entry = _reagentOrder[_reagentSearches];
            if (!Bought.IsAwaitingCollection(entry, now) && player.Inventory.Templates.Find(entry) is { } wanted)
            {
                session.DrainManagedPackets(WorldOpcode.SmsgAuctionListResult);
                byte[] search = PlayerbotEconomyWire.ListItems(_auctioneer.Value, 0, wanted.Name, 0, 0, PlayerbotEconomyWire.Any, wanted.Class,
                    PlayerbotEconomyWire.Any, PlayerbotEconomyWire.Any, usable: false);
                if (!session.TryManagedAction(WorldOpcode.CmsgAuctionListItems, search)) return Failed();
                IReadOnlyList<ManagedSessionPacket> replies = session.DrainManagedPackets(WorldOpcode.SmsgAuctionListResult);
                if (replies.Count > 0 && TryDecode(() => PlayerbotEconomyWire.DecodeAuctionList(replies[^1].Payload), out PlayerbotAuctionPage? page) && page is not null)
                    _reagentScan.AddRange(page.Rows);
            }

            _reagentSearches++;
            return true;
        }

        PlayerbotReagentChoice? choice = PlayerbotEconomyPlanner.ChooseReagent(_reagentScan.DistinctBy(row => row.Id), player.Guid.Value, _reagentMissing, limit,
            entry => player.Inventory.Templates.Find(entry),
            template => PlayerbotEconomyPlanner.ReagentUnitCap(template, options.Economy.ReagentSellMultiple, options.Economy.ReagentAllowanceCopper),
            IsRefused, VendorStockedFor(player));
        if (choice is null)
        {
            _phase = AfterReagents();
            return true;
        }

        _bidReturn = PlayerbotEconomyPhase.BuyingReagents;
        _bidAuction = choice.Row.Id;
        _bidEntry = choice.Row.Entry;
        _bidCount = choice.Row.Count;
        _moneyBefore = player.Money;
        _expectedCost = choice.Row.Buyout;
        session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult);
        if (!session.TryManagedAction(WorldOpcode.CmsgAuctionPlaceBid, PlayerbotEconomyWire.PlaceBid(_auctioneer.Value, choice.Row.Id, choice.Row.Buyout)))
        {
            NoteBidFailed();
            return Failed();
        }

        if (!player.IsQuestSettlementPending)
        {
            // The bid settled at once. Money gone means the buyout went through (book it, or the next visit buys the reagent again);
            // money unchanged means the house answered with an error or with silence: leave the auction alone.
            session.DrainManagedPackets(WorldOpcode.SmsgAuctionCommandResult);
            if (player.Money < _moneyBefore) BookBuyout(_moneyBefore - player.Money);
            else NoteBidFailed();
            return true;
        }

        _operationDeadline = unchecked(now + OperationTimeoutMs);
        _phase = PlayerbotEconomyPhase.AwaitBid;
        return true;
    }

    /// <summary>The per-craft reagent counts of the recipes the bot wants: the test seam, else its spellbook; none without a spell system.</summary>
    private IReadOnlyDictionary<uint, uint> WantedReagents(Player player)
    {
        if (ReagentNeeds is { } seam) return seam(player);
        if (session.Services.GetService<SpellFeature>() is not { } feature) return NoReagents;
        _supply.RefreshIfChanged(player, feature.System.Store, feature.Spellbook, session.World.NowMs);
        return _supply.Reagents;
    }

    // --- the mailbox -----------------------------------------------------------------------------------------------

    private bool StepMailStart(Player player, EconomyFeature economy, uint elapsedMs, uint now)
    {
        PlayerbotMailboxSighting? mailbox = _destinations.FindMailbox(player);
        if (mailbox is null) return Finish(now);
        if (!mailbox.Interactable)
        {
            if (_destinations.WalkTo(player, mailbox.Position, elapsedMs)) return true;
            if (++_travelFailures < MaxTravelFailures) return true;
            return Finish(now);
        }

        if (!PlayerbotMovementControl.Stop(session, player)) return true;
        _destinations.Clear();
        if (!economy.Settlements.CanAct(session, player)) return true;
        _mailbox = mailbox.Guid;
        session.DrainManagedPackets(WorldOpcode.SmsgMailListResult);
        if (!session.TryManagedAction(WorldOpcode.CmsgGetMailList, PlayerbotEconomyWire.GetMailList(_mailbox.Value))) return Failed();
        _operationDeadline = unchecked(now + OperationTimeoutMs);
        _phase = PlayerbotEconomyPhase.AwaitMailList;
        return true;
    }

    private bool StepAwaitMailList(Player player, uint now)
    {
        IReadOnlyList<ManagedSessionPacket> replies = session.DrainManagedPackets(WorldOpcode.SmsgMailListResult);
        if (replies.Count > 0)
        {
            if (!TryDecode(() => PlayerbotEconomyWire.DecodeMailList(replies[^1].Payload), out IReadOnlyList<PlayerbotMailLetter>? letters) || letters is null)
                return Finish(now);
            _letters.Clear();
            _letterIndex = 0;
            foreach (PlayerbotMailLetter letter in letters)
            {
                // Only the auction house's own letters without cash on delivery are the bot's business: a sale's money, an expired listing's
                // item, a won auction's item. A player's letter, a creature's and any cash on delivery are left exactly as they are.
                if (letter.Type == MailMessageType.Auction && letter.Cod == 0)
                {
                    if (_letters.Count < MaxLettersPerVisit) _letters.Add(letter);
                }
                else if (!letter.IsRead)
                {
                    _foreignUnread++;
                }
            }

            LoadLetter();
            _phase = PlayerbotEconomyPhase.MailWork;
            return true;
        }

        if (Reached(now, _operationDeadline)) return Finish(now);
        return true;
    }

    private bool StepMailWork(Player player, uint now)
    {
        if (_letterIndex >= _letters.Count || _handledLetters >= MaxLettersPerVisit) return Finish(now);
        if (!_mailbox.IsEmpty && _destinations.FindMailbox(player) is not { Interactable: true }) return Finish(now);
        PlayerbotMailLetter letter = _letters[_letterIndex];
        MailAction? action = null;
        WorldOpcode opcode = default;
        if (!_letterBlocked && _letterMoney > 0)
        {
            action = MailAction.MoneyTaken;
            opcode = WorldOpcode.CmsgMailTakeMoney;
        }
        else if (!_letterBlocked && _letterItem > 0)
        {
            if (PlayerbotTownGoals.FreeBagSlots(player) == 0)
            {
                _letterBlocked = true;
                return true;
            }

            action = MailAction.ItemTaken;
            opcode = WorldOpcode.CmsgMailTakeItem;
        }
        else if (!_letterBlocked)
        {
            action = MailAction.Deleted;
            opcode = WorldOpcode.CmsgMailDelete;
            NoteLetter(letter);
        }

        if (action is not { } awaited)
        {
            NextLetter();
            return true;
        }

        session.DrainManagedPackets(WorldOpcode.SmsgSendMailResult);
        if (!session.TryManagedAction(opcode, PlayerbotEconomyWire.MailCommand(_mailbox.Value, letter.Id)))
        {
            _letterBlocked = true;
            return Failed();
        }

        _awaitedMail = awaited;
        _moneyBefore = player.Money;
        _operationDeadline = unchecked(now + (awaited == MailAction.Deleted ? DeleteTimeoutMs : OperationTimeoutMs));
        _phase = PlayerbotEconomyPhase.AwaitMailResult;
        return true;
    }

    private bool StepAwaitMailResult(Player player, uint now)
    {
        PlayerbotMailLetter letter = _letters[_letterIndex];
        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(WorldOpcode.SmsgSendMailResult))
        {
            if (!TryDecode(() => PlayerbotEconomyWire.DecodeMailResult(packet.Payload), out PlayerbotMailResult result)
                || result.MailId != letter.Id || result.Action != _awaitedMail)
                continue;
            bool ok = result.Result == MailResult.Ok;
            switch (_awaitedMail)
            {
                case MailAction.MoneyTaken:
                    if (ok)
                    {
                        MoneyCollected += _letterMoney;
                        _letterMoney = 0;
                        if (player.Money < _moneyBefore && !session.IsInChaosProbe)
                        {
                            PlayerbotAnomalies.Report(player, "economy", "money-invalid",
                                $"mail money take decreased player money (before={_moneyBefore}, after={player.Money})",
                                AnomalySeverity.Error);
                        }
                    }
                    else
                    {
                        _letterBlocked = true;
                        if (!session.IsInChaosProbe)
                            PlayerbotAnomalies.Report(player, "economy", "sell-failed",
                                $"taking auction mail money for letter {letter.Id} failed with {result.Result}");
                    }

                    break;
                case MailAction.ItemTaken:
                    if (ok)
                    {
                        _letterItem = 0;
                        ItemsCollected++;
                        if (PlayerbotEconomyPlanner.TryParseAuctionSubject(letter.Subject, out uint takenEntry, out AuctionMailAction takenAction)
                            && takenAction == AuctionMailAction.Won)
                            Bought.NoteCollected(takenEntry);
                    }
                    else
                    {
                        _letterBlocked = true;
                        if (!session.IsInChaosProbe)
                        {
                            if (result.Result == MailResult.InternalError)
                                PlayerbotAnomalies.Report(player, "economy", "item-not-found",
                                    $"auction mail item {letter.ItemEntry} in letter {letter.Id} could not be taken ({result.Result})");
                            PlayerbotAnomalies.Report(player, "economy", "sell-failed",
                                $"taking auction mail item {letter.ItemEntry} in letter {letter.Id} failed with {result.Result}");
                        }
                    }

                    break;
                default:
                    NextLetter();
                    _phase = PlayerbotEconomyPhase.MailWork;
                    return true;
            }

            _phase = PlayerbotEconomyPhase.MailWork;
            return true;
        }

        if (Reached(now, _operationDeadline))
        {
            if (_awaitedMail == MailAction.Deleted)
            {
                NextLetter();
                _phase = PlayerbotEconomyPhase.MailWork;
                return true;
            }

            Abort();
            return false;
        }

        return true;
    }

    private void LoadLetter()
    {
        _letterBlocked = false;
        if (_letterIndex < _letters.Count)
        {
            _letterMoney = _letters[_letterIndex].Money;
            _letterItem = _letters[_letterIndex].ItemEntry;
        }
    }

    private void NextLetter()
    {
        _letterIndex++;
        _handledLetters++;
        LoadLetter();
    }

    /// <summary>An expired letter counts against the item's entry (two expiries and the bot stops listing it); a sale forgets it.</summary>
    private void NoteLetter(PlayerbotMailLetter letter)
    {
        if (!PlayerbotEconomyPlanner.TryParseAuctionSubject(letter.Subject, out uint entry, out AuctionMailAction action)) return;
        if (action == AuctionMailAction.Expired) _relist.NoteExpired(entry);
        else if (action == AuctionMailAction.Successful) _relist.NoteSold(entry);
    }

    // --- shared helpers --------------------------------------------------------------------------------------------

    private PlayerbotEconomyPhase AfterListing() => _wantReagents ? PlayerbotEconomyPhase.BuyingReagents : AfterReagents();

    private PlayerbotEconomyPhase AfterReagents() => _wantBuy ? PlayerbotEconomyPhase.Buying : MailPhase();

    private PlayerbotEconomyPhase MailPhase()
    {
        _travelFailures = 0;
        return PlayerbotEconomyPhase.MailStart;
    }

    private bool ToMail()
    {
        _destinations.Clear();
        _phase = MailPhase();
        return true;
    }

    private bool AuctioneerStillUsable(Player player, EconomyFeature economy)
        => _destinations.FindAuctioneer(player, economy) is { Interactable: true } seen
            && seen.Npc.Guid == _auctioneer;

    /// <summary>The next queued item that is still in the bags in the same amount, or false when the queue is empty.</summary>
    private bool TryNextListable(Player player, out PlayerbotBagItem? item)
    {
        while (_queue.TryDequeue(out PlayerbotBagItem? candidate))
        {
            if (player.Inventory.GetItemByGuid(candidate.Guid) is { } live && live.Count == candidate.Count && live.Entry == candidate.Template.Entry)
            {
                item = candidate;
                return true;
            }

            if (!session.IsInChaosProbe)
                PlayerbotAnomalies.Report(player, "economy", "item-not-found",
                    $"queued auction item {candidate.Template.Entry} (guid {candidate.Guid}, count {candidate.Count}) missing or changed in bags");
        }

        item = null;
        return false;
    }

    /// <summary>
    /// The shortfalls without the entries the bot bought and has not yet collected from their letters (it must not buy them again) and
    /// without the entries a vendor stocks (never bought at the house, so no visit is started for them).
    /// </summary>
    private IReadOnlyDictionary<uint, uint> WithoutAwaiting(IReadOnlyDictionary<uint, uint> shortfalls, uint now, Func<uint, bool> isVendorStocked)
    {
        if (shortfalls.Count == 0) return shortfalls;
        var kept = new Dictionary<uint, uint>(shortfalls.Count);
        foreach ((uint entry, uint missing) in shortfalls)
            if (!Bought.IsAwaitingCollection(entry, now) && !isVendorStocked(entry)) kept[entry] = missing;
        return kept;
    }

    /// <summary>
    /// The vendor-stocked predicate: the <see cref="VendorStocked"/> seam, else the world's service index (resolved once for the returned
    /// predicate). Fail closed: without the index, or with an index that lists no vendor-stocked item at all (not built or not loaded),
    /// every entry counts as vendor stocked.
    /// </summary>
    private Func<uint, bool> VendorStockedFor(Player player)
    {
        if (VendorStocked is { } seam) return seam;
        QuestNpcServices? services = session.Services.GetService<QuestNpcFeature>()?.Services;
        PlayerbotServiceIndex? index = services is null ? null : PlayerbotServiceIndex.Get(session, services, player);
        return index is null || index.VendorStockedCount == 0 ? static _ => true : index.IsVendorStocked;
    }

    private IEnumerable<PlayerbotBagItem> BagItems(Player player, IReadOnlyDictionary<uint, uint> wanted)
    {
        // What the bot keeps whatever its price: food, drink and potions it uses, and what an open quest asks for (the reservations of
        // PlayerbotTownGoals.IsProtected, read here from the same sources).
        QuestNpcServices? services = session.Services.GetService<QuestNpcFeature>()?.Services;
        SpellSystem? spells = session.Services.GetService<SpellFeature>()?.System;
        uint now = session.World.NowMs;
        HashSet<uint> required = services is null
            ? []
            : [.. (services.StateOf(player)?.Quests.Statuses ?? new Dictionary<uint, QuestStatusData>())
                .Where(row => row.Value.Status is QuestStatus.Incomplete or QuestStatus.Complete)
                .SelectMany(row => services.Quests.Get(row.Key)?.ReqItemId ?? [])];
        foreach (Item item in player.Inventory.AllItems)
        {
            if (item.BagSlot == InventorySlots.Bag0 && (item.Slot < InventorySlots.ItemStart || item.Slot >= InventorySlots.ItemEnd)) continue;
            if (item.Template.Quality == 0) continue;
            bool reserved = required.Contains(item.Entry) || wanted.ContainsKey(item.Entry) || Bought.IsHeld(item.Entry, now)
                || (spells is not null && PlayerbotConsumables.TryClassify(item.Template, spells, out _));
            yield return new PlayerbotBagItem(item.Guid, item.Template, item.Count, player.Inventory.CanTransferOut(item) == ArcaneCore.Game.Items.InventoryResult.Ok, reserved);
        }
    }

    private bool IsRefused(uint auctionId)
    {
        if (!_refusedAuctions.TryGetValue(auctionId, out uint until)) return false;
        if (!Reached(session.World.NowMs, until)) return true;
        _refusedAuctions.Remove(auctionId);
        return false;
    }

    private void Refuse(uint auctionId)
    {
        uint now = session.World.NowMs;
        foreach (uint expired in _refusedAuctions.Where(pair => Reached(now, pair.Value)).Select(pair => pair.Key).ToArray())
            _refusedAuctions.Remove(expired);
        if (_refusedAuctions.Count >= MaxRefusedAuctions) _refusedAuctions.Remove(_refusedAuctions.MinBy(pair => pair.Value).Key);
        _refusedAuctions[auctionId] = unchecked(now + RefusedAuctionMs);
    }

    private bool IsCasting(Player player)
        => session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting };

    private bool Failed()
    {
        if (++_failures < MaxFailures) return true;
        Abort();
        return false;
    }

    /// <summary>The visit's work is done: the next one is a full interval away; unread letters left alone do not bring the bot back for a day.</summary>
    private bool Finish(uint now)
    {
        if (_foreignUnread > 0) _nextMailTriggerAt = unchecked(now + ForeignMailBackoffMs);
        VisitsFinished++;
        Schedule(now, IntervalMs(options.Economy));
        Clear();
        return false;
    }

    /// <summary>The visit is dropped (combat, death, a timeout, a fault): sooner retry than after a finished visit.</summary>
    private void Abort()
    {
        uint interval = IntervalMs(options.Economy);
        Schedule(session.World.NowMs, Math.Min(interval, RetryAfterAbortMs));
        Clear();
    }

    private void Schedule(uint now, uint afterMs)
    {
        _scheduled = true;
        _nextVisitAt = unchecked(now + afterMs);
    }

    private void Clear()
    {
        _phase = PlayerbotEconomyPhase.Idle;
        _destinations.Clear();
        _queue.Clear();
        _scan.Clear();
        _reagentScan.Clear();
        _reagentMissing.Clear();
        _reagentOrder.Clear();
        _letters.Clear();
        _auctioneer = default;
        _mailbox = default;
        _house = null;
        TargetEntry = 0;
    }

    private static uint IntervalMs(PlayerbotEconomyOptions config) => (uint)Math.Clamp(config.VisitIntervalMinutes, 1, PlayerbotEconomyOptions.MaxVisitIntervalMinutes) * 60_000u;

    private static bool Reached(uint now, uint at) => unchecked((int)(now - at)) >= 0;

    private static uint Elapsed(uint now, uint since) => unchecked(now - since);

    private static bool TryDecode<T>(Func<T> decode, out T? value)
    {
        try
        {
            value = decode();
            return true;
        }
        catch (FormatException e)
        {
            FaultLedger.Shared.Record("playerbot.economy-wire", e);
            AnomalyLedger.Shared.Report("bot-economy", "wire-decode-failed", "economy-wire", e.Message);
            value = default;
            return false;
        }
    }
}
