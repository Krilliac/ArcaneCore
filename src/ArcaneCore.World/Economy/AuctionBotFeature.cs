using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Items;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Economy;

/// <summary>
/// The auction house bot (cMaNGOS AuctionHouseBot, src/game/AuctionHouseBot/AuctionHouseBot.cpp) with MaNGOS Zero's custody ledger
/// (src/game/AuctionHouseBot/Custody*). Off by default (<c>AuctionHouseBot:Enabled</c>). Every <c>UpdateIntervalSeconds</c> it takes one
/// action, cycling sell over each house, then buy over each house, as cMaNGOS Update does with m_houseAction. Every item it creates
/// and every copper it pays or bids is reserved in <see cref="Ledger"/> first; with a custody store (characters <c>ahbot_custody</c>)
/// the reservation is durable before its economy transaction starts, and the transaction carries the row's stable operation id. A
/// row whose outcome is unknown (also after a restart) stays reserved until the economy operation ledger says whether it committed.
/// If the ledger's audit ever finds a broken invariant the bot stops itself.
/// </summary>
public sealed class AuctionBotFeature(IServiceProvider services, ILogger<AuctionBotFeature> logger, TimeProvider? clock = null)
    : IWorldFeature, IAsyncDisposable
{
    /// <summary>Terminal custody rows are kept this long (MaNGOS Zero custody TTL sweep).</summary>
    public const long TerminalRetentionSeconds = 2 * AuctionBotCustodyLedger.SecondsPerDay;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Random _random = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reconciling = new(StringComparer.Ordinal);
    private List<(AuctionBotCustodyRow Row, Action Start)> _awaitingDurable = [];
    private WorldRuntime? _world;
    private EconomyFeature? _economy;
    private ItemsFeature? _items;
    private Timer? _timer;
    private IItemTemplateStore? _poolSource;
    private ArcaneCore.Game.Npc.NpcStore? _vendorSource;
    private LootContent? _lootSource;
    private AuctionBotPlanner? _planner;
    private AuctionBotLootSources? _loot;
    private Dictionary<uint, AuctionBotItemOverride> _overrides = [];
    // Bumped by every Reload and every SetItemData (world thread). A reload applies the rows it read only if no later reload or in-memory
    // change happened since it started: a stale read must not replace a newer override, and reloads must not apply out of order.
    private int _overrideGeneration;
    private bool _persistent;
    private bool _flushing;
    private int _houseAction = -1;

    public AuctionBotOptions Options { get; } = new();

    public AuctionBotCustodyLedger Ledger { get; private set; } = new(new AuctionBotOptions());

    /// <summary>Set when the ledger audit failed: the bot does nothing more until restart.</summary>
    public bool Halted { get; private set; }

    /// <summary>The current planner (null until the item content is loaded).</summary>
    public AuctionBotPlanner? Planner => _planner;

    /// <summary>The <c>ahbot_items</c> rows by item.</summary>
    public IReadOnlyDictionary<uint, AuctionBotItemOverride> Overrides => _overrides;

    /// <summary>Whether custody rows are written to <c>ahbot_custody</c>.</summary>
    public bool Persistent => _persistent;

    /// <summary>Whether ledger changes are still waiting to be written (or being written).</summary>
    public bool HasUnsavedChanges => _persistent && (_flushing || Ledger.HasChanges || _awaitingDurable.Count > 0);

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        BindOptions();
        Ledger = new AuctionBotCustodyLedger(Options);
        _world = world;
        _economy = services.GetService<EconomyFeature>();
        _items = services.GetService<ItemsFeature>();
        if (!Options.Enabled)
        {
            logger.LogInformation("AHBot is disabled (AuctionHouseBot:Enabled)");
            return;
        }

        using (IServiceScope scope = services.CreateScope())
        {
            // Startup load, as the economy feature loads its auctions: rows whose outcome was unknown at shutdown come back Reserved and
            // are rechecked against the economy operation ledger by the first ticks (ReconcilePending).
            if (scope.ServiceProvider.GetService<IAuctionBotCustodyStore>() is { } store)
            {
                IReadOnlyList<AuctionBotCustodyRecord> rows = store.LoadAsync().GetAwaiter().GetResult();
                int skipped = Ledger.Load(rows);
                _persistent = true;
                logger.LogInformation("AHBot custody ledger: {Rows} rows ({Pending} to recheck){Skipped}", Ledger.Rows.Count, Ledger.Pending.Count(),
                    skipped > 0 ? $", {skipped} unreadable rows skipped" : string.Empty);
            }
            else
            {
                logger.LogWarning("AHBot: no custody store; the ledger is kept in memory only");
            }
        }

        LoadOverrides();
        if (_economy is not null)
        {
            _economy.BotBidSettled += OnBotBidSettled;
        }

        logger.LogInformation("AHBot selling items: {Sell}; buying items: {Buy}", Options.SellChance > 0 ? "Enabled" : "Disabled",
            Options.BuyChance > 0 && Options.DailyBuyBudgetCopper > 0 ? "Enabled" : "Disabled");
        TimeSpan period = TimeSpan.FromSeconds(Options.UpdateIntervalSeconds);
        _timer = new Timer(_ => world.Post(Tick), null, period, period);
    }

    public async Task StopAsync()
    {
        if (_timer is { } timer)
        {
            _timer = null;
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        if (_economy is not null)
        {
            _economy.BotBidSettled -= OnBotBidSettled;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>World thread: one bot action (the timer's work; tests call it directly).</summary>
    public void Tick()
    {
        if (!Ready(out EconomyFeature? economy, out AuctionBotPlanner? planner))
        {
            return;
        }

        long now = Now;
        try
        {
            ReconcilePending(economy);
            ReviewStandingBids(economy, now);
            uint[] houseIds = Options.HouseIds();
            int houses = houseIds.Length;
            if (houses > 0)
            {
                if (++_houseAction >= houses * 2)
                {
                    _houseAction = 0;
                }

                uint house = houseIds[_houseAction % houses];
                if (_houseAction < houses)
                {
                    if (planner.Roll(Options.SellChance))
                    {
                        Sell(economy, planner, house, now);
                    }
                }
                else if (planner.Roll(Options.BuyChance))
                {
                    Buy(economy, planner, house, now);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("economy.ahbot-tick-action", ex);
            logger.LogFault("ahbot.tick-action", ex, "AHBot tick action failed");
        }

        try
        {
            Ledger.PruneTerminal(now - TerminalRetentionSeconds);
            Audit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("economy.ahbot-tick-audit", ex);
            logger.LogFault("ahbot.tick-audit", ex, "AHBot custody audit failed");
        }

        try
        {
            Flush();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("economy.ahbot-tick-flush", ex);
            logger.LogFault("ahbot.tick-flush", ex, "AHBot custody flush failed");
        }
    }

    /// <summary>World thread: one sell pass over a house, ignoring the chance roll (tests and GM tools). Returns the listings reserved.</summary>
    public int Sell(uint houseId)
    {
        if (!Ready(out EconomyFeature? economy, out AuctionBotPlanner? planner))
        {
            return 0;
        }

        int started = Sell(economy, planner, houseId, Now);
        Flush();
        return started;
    }

    /// <summary>World thread: one buy pass over a house, ignoring the chance roll (tests and GM tools). Returns the buyouts and bids reserved.</summary>
    public int Buy(uint houseId)
    {
        if (!Ready(out EconomyFeature? economy, out AuctionBotPlanner? planner))
        {
            return 0;
        }

        int started = Buy(economy, planner, houseId, Now);
        Flush();
        return started;
    }

    /// <summary>
    /// World thread: cMaNGOS AuctionHouseBot::Rebuild (<c>.ahbot rebuild [all]</c>): the bot's auctions without a bid (all of them with
    /// <paramref name="all"/>, which sells those with a player bid to that player) expire now, then sell passes refill every house
    /// (without buying) until a pass lists nothing, the room or the day's item budget is spent, or the cMaNGOS pass count is reached.
    /// Returns (auctions expired, listings reserved).
    /// </summary>
    public (int Expired, int Listed) Rebuild(bool all)
    {
        if (!Ready(out EconomyFeature? economy, out AuctionBotPlanner? planner))
        {
            return (0, 0);
        }

        int expired = economy.ExpireBotAuctions(all);
        int listed = 0;
        int passes = (int)(((Options.TimeMaxHours - Options.TimeMinHours) / 2) + Options.TimeMinHours) * 90;
        foreach (uint house in Options.HouseIds())
        {
            for (int i = 0; i < passes; i++)
            {
                int pass = Sell(economy, planner, house, Now);
                listed += pass;
                if (pass == 0)
                {
                    break;
                }
            }
        }

        Flush();
        return (expired, listed);
    }

    /// <summary>
    /// World thread: <c>.ahbot reload</c> (cMaNGOS ReloadAllConfig): re-read the options and the <c>ahbot_items</c> rows and rebuild the
    /// item pool. <c>Enabled</c> and the update interval only change on restart. The options are re-bound at once; the
    /// <c>ahbot_items</c> read runs on the thread pool (the SQLite provider runs its async calls synchronously, so it must not
    /// run on the tick), and the overrides are applied, the pool rebuilt and <paramref name="done"/> called with the option
    /// corrections back on the world thread. <paramref name="done"/> also gets whether the rows read were applied: false when a
    /// <see cref="SetItemData"/> or a newer reload ran since this one started (its rows are then stale and are dropped). Not attached to a world, the
    /// read and the apply run inline.
    /// </summary>
    public void Reload(Action<IReadOnlyList<string>, bool> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        bool enabled = Options.Enabled;
        uint interval = Options.UpdateIntervalSeconds;
        IReadOnlyList<string> fixes = BindOptions();
        Options.Enabled = enabled;
        Options.UpdateIntervalSeconds = interval;
        WorldRuntime? world = _world;
        int generation = Interlocked.Increment(ref _overrideGeneration);

        void Apply(Dictionary<uint, AuctionBotItemOverride>? loaded)
        {
            bool applied = true;
            if (loaded is not null)
            {
                // A SetItemData (or a newer reload) since this reload started changed the overrides: the rows read are stale.
                applied = generation == Volatile.Read(ref _overrideGeneration);
                if (applied)
                {
                    _overrides = loaded;
                }
            }

            _planner = null;
            _loot = null;
            done(fixes, applied);
        }

        if (world is null)
        {
            // Not attached: no world thread to protect, so read and apply here.
            Apply(ReadOverridesAsync().GetAwaiter().GetResult());
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Dictionary<uint, AuctionBotItemOverride>? loaded = await ReadOverridesAsync().ConfigureAwait(false);
                world.Post(() => Apply(loaded));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // world.Post can throw once the world has shut down; the reload is dropped, but not silently.
                FaultLedger.Shared.Record("economy.ahbot-reload-post", ex);
                logger.LogFault("ahbot.reload-post", ex, "AHBot reload could not be applied");
            }
        });
    }

    /// <summary>
    /// World thread: cMaNGOS SetItemData (<c>.ahbot item</c>): set or reset (<paramref name="value"/> null) the <c>ahbot_items</c> row of
    /// an item, in memory at once and in the world database off the thread. AddChance is capped at 100; a min or max amount of 0 is the
    /// stack size; max is at least min. Returns the row kept, or null after a reset.
    /// </summary>
    public AuctionBotItemOverride? SetItemData(ItemTemplate template, AuctionBotItemOverride? value)
    {
        ArgumentNullException.ThrowIfNull(template);
        AuctionBotItemOverride? kept = null;
        Interlocked.Increment(ref _overrideGeneration); // a reload that read the rows before this change must not apply them
        if (value is null)
        {
            _overrides.Remove(template.Entry);
        }
        else
        {
            uint stack = Math.Max(template.Stackable, 1);
            uint min = value.MinAmount == 0 ? stack : value.MinAmount;
            uint max = value.MaxAmount == 0 ? stack : value.MaxAmount;
            kept = new AuctionBotItemOverride(template.Entry, value.Value, Math.Min(value.AddChance, 100), min, Math.Max(max, min));
            _overrides[template.Entry] = kept;
        }

        if (_planner is not null)
        {
            _planner.Overrides = new Dictionary<uint, AuctionBotItemOverride>(_overrides);
        }

        uint entry = template.Entry;
        _ = Task.Run(async () =>
        {
            try
            {
                using IServiceScope scope = services.CreateScope();
                if (scope.ServiceProvider.GetService<IAuctionBotItemStore>() is not { } store)
                {
                    return;
                }

                if (kept is null)
                {
                    await store.DeleteAsync(entry).ConfigureAwait(false);
                }
                else
                {
                    await store.SaveAsync(kept).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("economy.ahbot-save-item", ex);
                logger.LogFault("ahbot.save-item", ex, "AHBot: saving ahbot_items row {Item} failed; the change holds until restart", entry);
            }
        });
        return kept;
    }

    /// <summary>cMaNGOS GetItemData: the row of an item, or its computed value with amounts 0 (not overridden).</summary>
    public AuctionBotItemOverride ItemData(ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return _overrides.TryGetValue(template.Entry, out AuctionBotItemOverride? o) ? o
            : new AuctionBotItemOverride(template.Entry, EnsurePlanner()?.Pricing.BuyoutPerItem(template) ?? 0, 0, 0, 0);
    }

    /// <summary>World thread: cMaNGOS PrepareStatusInfos: the bot's auctions per house and quality.</summary>
    public IReadOnlyList<(uint HouseId, int Count, int[] ByQuality)> Status()
    {
        var result = new List<(uint, int, int[])>();
        if (_economy is not { Enabled: true } economy)
        {
            return result;
        }

        foreach (uint house in Options.HouseIds())
        {
            var byQuality = new int[7];
            int count = 0;
            foreach (AuctionRecord auction in economy.BotAuctions(house))
            {
                count++;
                if (_items?.Templates.Find(auction.ItemEntry) is { Quality: < 7 } t)
                {
                    byQuality[t.Quality]++;
                }
            }

            result.Add((house, count, byQuality));
        }

        return result;
    }

    private bool Ready([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out EconomyFeature? economy,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AuctionBotPlanner? planner)
    {
        economy = _economy;
        planner = null;
        if (!Options.Enabled || Halted || economy is not { Enabled: true })
        {
            return false;
        }

        planner = EnsurePlanner();
        return planner is not null;
    }

    private IReadOnlyList<string> BindOptions()
    {
        services.GetService<IConfiguration>()?.GetSection(AuctionBotOptions.SectionName).Bind(Options);
        IReadOnlyList<string> fixes = Options.Normalize();
        foreach (string fix in fixes)
        {
            logger.LogWarning("AHBot: {Fix}", fix);
        }

        return fixes;
    }

    /// <summary>
    /// Attach time only: WorldHost attaches every feature through FeatureHost.AttachAll before world.Start, and the console "feature attach" only
    /// re-attaches an IDetachableWorldFeature (this one is not), so the world thread is not running. The blocking read of the <c>ahbot_items</c> rows.
    /// </summary>
    private void LoadOverrides()
    {
        if (ReadOverridesAsync().GetAwaiter().GetResult() is { } loaded)
        {
            _overrides = loaded;
        }
    }

    /// <summary>Read the <c>ahbot_items</c> rows: null when there is no store (keep what is held), empty when the read failed.</summary>
    private async Task<Dictionary<uint, AuctionBotItemOverride>?> ReadOverridesAsync()
    {
        try
        {
            using IServiceScope scope = services.CreateScope();
            if (scope.ServiceProvider.GetService<IAuctionBotItemStore>() is not { } store)
            {
                return null;
            }

            IReadOnlyList<AuctionBotItemOverride> rows = await store.LoadAsync().ConfigureAwait(false);
            Dictionary<uint, AuctionBotItemOverride> loaded = rows.Where(o => o.Item != 0).GroupBy(o => o.Item).ToDictionary(g => g.Key, g => g.Last());
            logger.LogInformation("AHBot: {Count} ahbot_items rows", loaded.Count);
            return loaded;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("economy.ahbot-load-overrides", ex);
            logger.LogFault("ahbot.load-overrides", ex, "AHBot: loading ahbot_items failed; no item overrides");
            return [];
        }
    }

    private int Sell(EconomyFeature economy, AuctionBotPlanner planner, uint houseId, long now)
    {
        if (economy.ItemGuids is not { } guids || _items is null)
        {
            return 0;
        }

        // Never reuse an auction id a custody row names (its listing key would be refused forever).
        economy.RaiseAuctionIdTo(Ledger.MaxAuctionId);
        int room = (int)Math.Min(Options.MaxAuctionsPerHouse, int.MaxValue) - economy.BotAuctionCount(houseId);
        int started = 0;
        foreach (AuctionBotSellIntent intent in planner.PlanSell(room, _items.Templates.Find, _loot?.Roll()))
        {
            if (!Invariant.Check(intent.Count > 0, $"AuctionBotFeature.Sell: non-positive item count {intent.Count} for entry {intent.Entry}"))
            {
                continue;
            }

            if (_items.Templates.Find(intent.Entry) is not { } template)
            {
                continue;
            }

            uint auctionId = economy.NextAuctionId();
            ItemInstanceData item = NewItem(guids.Next(), template, intent.Count);
            if (Ledger.TryReserveListing(houseId, auctionId, item.Guid, item.Entry, item.Count, now) is not { } row)
            {
                // The day's item budget is spent (or a guard refused): stop this pass.
                break;
            }

            _inFlight.Add(row.IdemKey);
            StartWhenDurable(row, () => economy.StartBotListing(houseId, auctionId, item, intent.StartBid, intent.Buyout,
                now + (intent.Hours * 3600L), row.OperationId, outcome => Finish(row, outcome)));
            started++;
        }

        return started;
    }

    /// <summary>A new stack of <paramref name="template"/>, with its spell charges and, when enabled, a rolled random property.</summary>
    private ItemInstanceData NewItem(uint guid, ItemTemplate template, uint count)
    {
        var enchantments = new uint[21];
        int property = 0;
        if (Options.RandomProperties && template.RandomProperty != 0 && _items?.RandomProperties is { } source)
        {
            property = source.Generate(template);
            if (property != 0 && source.Find(property) is { } record)
            {
                for (int i = 0; i < ItemRandomPropertyRecord.EnchantSlots; i++)
                {
                    enchantments[(EnchantSlots.Property0 + i) * 3] = i < record.EnchantIds.Count ? record.EnchantIds[i] : 0;
                }
            }
            else
            {
                property = 0;
            }
        }

        return new ItemInstanceData
        {
            Guid = guid,
            Entry = template.Entry,
            Count = count,
            Durability = template.MaxDurability,
            Charges = [.. Enumerable.Range(0, 5).Select(i => i < template.Spells.Count ? template.Spells[i].Charges : 0)],
            Enchantments = enchantments,
            RandomPropertyId = property,
        };
    }

    private int Buy(EconomyFeature economy, AuctionBotPlanner planner, uint houseId, long now)
    {
        if (_items is null)
        {
            return 0;
        }

        Dictionary<uint, AuctionRecord> open = economy.OpenAuctions(houseId).ToDictionary(v => v.Auction.Id, v => v.Auction);
        int started = 0;
        foreach (AuctionBotBuyIntent intent in planner.PlanBuy(open.Values, _items.Templates.Find))
        {
            if (!Invariant.Check(open.TryGetValue(intent.AuctionId, out AuctionRecord? auction) && intent.Price > 0,
                $"AuctionBotFeature.Buy: invalid buy intent for auction {intent.AuctionId} price {intent.Price}") || auction is null)
            {
                continue;
            }

            AuctionBotCustodyRow? row = intent.Bid
                ? Ledger.TryReserveBid(houseId, auction.Id, auction.ItemGuid, auction.ItemEntry, auction.ItemCount, intent.Price, now)
                : Ledger.TryReserveBuyout(houseId, auction.Id, auction.ItemGuid, auction.ItemEntry, auction.ItemCount, intent.Price, now);
            if (row is null)
            {
                continue;
            }

            _inFlight.Add(row.IdemKey);
            AuctionBotCustodyRow reserved = row;
            StartWhenDurable(reserved, intent.Bid
                ? () => economy.StartBotBid(auction.Id, intent.Price, reserved.OperationId, outcome => Finish(reserved, outcome))
                : () => economy.StartBotBuyout(auction.Id, intent.Price, reserved.OperationId, outcome => Finish(reserved, outcome)));
            started++;
        }

        return started;
    }

    /// <summary>
    /// Start a reserved movement once its row is durable (MaNGOS Zero: the custody row is written before the transaction). Without a
    /// custody store it starts at once. If the write fails nothing was started, so the row is rolled back.
    /// </summary>
    private void StartWhenDurable(AuctionBotCustodyRow row, Action start)
    {
        if (!_persistent)
        {
            start();
            return;
        }

        _awaitingDurable.Add((row, start));
    }

    /// <summary>World thread: write the ledger changes off the thread, then start the movements whose rows they made durable.</summary>
    private void Flush()
    {
        if (!_persistent || _flushing || (!Ledger.HasChanges && _awaitingDurable.Count == 0) || _world is not { } world)
        {
            return;
        }

        (IReadOnlyList<AuctionBotCustodyRecord> rows, IReadOnlyList<string> deleted) = Ledger.TakeChanges();
        List<(AuctionBotCustodyRow Row, Action Start)> starts = _awaitingDurable;
        _awaitingDurable = [];
        _flushing = true;
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                using IServiceScope scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAuctionBotCustodyStore>().SaveAsync(rows, deleted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("economy.ahbot-custody-flush", ex);
                failure = ex;
            }

            world.Post(() =>
            {
                _flushing = false;
                if (failure is null)
                {
                    foreach ((AuctionBotCustodyRow row, Action start) in starts)
                    {
                        try
                        {
                            start();
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            FaultLedger.Shared.Record("economy.ahbot-movement-start", ex);
                            logger.LogFault("ahbot.movement-start", ex, "AHBot: starting movement {Key} failed", row.IdemKey);
                            _inFlight.Remove(row.IdemKey);
                            Ledger.Resolve(row.IdemKey, committed: false, Now);
                        }
                    }
                }
                else
                {
                    logger.LogFault("ahbot.custody-flush", failure, "AHBot: writing {Rows} custody rows failed; {Starts} movements are rolled back unstarted", rows.Count, starts.Count);
                    Ledger.Requeue(rows, deleted);
                    foreach ((AuctionBotCustodyRow row, _) in starts)
                    {
                        _inFlight.Remove(row.IdemKey);
                        Ledger.Resolve(row.IdemKey, committed: false, Now);
                    }
                }

                Flush();
            });
        });
    }

    private void Finish(AuctionBotCustodyRow row, EconomyOutcome outcome)
    {
        _inFlight.Remove(row.IdemKey);
        switch (outcome)
        {
            case EconomyOutcome.After:
                Ledger.Resolve(row.IdemKey, committed: true, Now);
                break;
            case EconomyOutcome.Before:
            case EconomyOutcome.NotStarted:
                Ledger.Resolve(row.IdemKey, committed: false, Now);
                break;
            default:
                // Unknown: fail closed. The row keeps its budget and its auction until the store answers (ReconcilePending).
                logger.LogWarning("AHBot: outcome of {Key} is unknown; it stays reserved until reconciled", row.IdemKey);
                break;
        }

        Flush();
    }

    /// <summary>A standing bot bid stopped standing: outbid, bought out or cancelled (released), or won at expiry (settled).</summary>
    private void OnBotBidSettled(uint auctionId, bool won)
    {
        if (Ledger.StandingBid(auctionId) is not { } row)
        {
            return;
        }

        if (won)
        {
            Ledger.SettleBid(row.IdemKey);
        }
        else
        {
            Ledger.ReleaseBid(row.IdemKey, Now);
        }

        Flush();
    }

    /// <summary>
    /// After a restart (or a missed event) a committed bid row may name an auction that moved on: one that still carries this bid stands;
    /// one with another bid, or none, released it; a gone auction is taken as won (the copper stays counted, the safe side).
    /// </summary>
    private void ReviewStandingBids(EconomyFeature economy, long now)
    {
        foreach (AuctionBotCustodyRow row in Ledger.Rows.Where(r => r.Role == AuctionBotCustodyRole.Bid && r.State == AuctionBotCustodyState.TerminalOk).ToList())
        {
            if (economy.FindAuction(row.AuctionId) is not { } auction)
            {
                if (!economy.IsAuctionBusy(row.AuctionId))
                {
                    Ledger.SettleBid(row.IdemKey);
                }
            }
            else if (!(EconomyFeature.HasBotBid(auction) && auction.Bid == row.Amount) && !economy.IsAuctionBusy(row.AuctionId))
            {
                Ledger.ReleaseBid(row.IdemKey, now);
            }
        }
    }

    /// <summary>MaNGOS Zero CustodyReconciler: ask the store whether each reserved row no longer in flight committed.</summary>
    private void ReconcilePending(EconomyFeature economy)
    {
        foreach (AuctionBotCustodyRow row in Ledger.Pending.Where(r => !_inFlight.Contains(r.IdemKey) && !_reconciling.Contains(r.IdemKey)).ToList())
        {
            _reconciling.Add(row.IdemKey);
            economy.ReadOperationCommitted(row.OperationId, committed =>
            {
                _reconciling.Remove(row.IdemKey);
                if (committed is { } known && Ledger.Resolve(row.IdemKey, known, Now))
                {
                    logger.LogInformation("AHBot: reconciled {Key} as {State}", row.IdemKey, known ? "committed" : "not committed");
                    Flush();
                }
            });
        }
    }

    private void Audit()
    {
        IReadOnlyList<string> problems = Ledger.Audit();
        if (problems.Count == 0)
        {
            return;
        }

        Halted = true;
        foreach (string problem in problems)
        {
            logger.LogError("AHBot custody audit failed: {Problem}; the bot is stopped", problem);
        }
    }

    /// <summary>
    /// The items a vendor sells, which <see cref="AuctionBotPricing"/> values at the vendor price: the direct <c>npc_vendor</c> rows plus the shared
    /// <c>npc_vendor_template</c> stock that <c>NpcStore</c> merges into every vendor window.
    /// </summary>
    internal static HashSet<uint> VendorItemSet(ArcaneCore.Game.Npc.NpcStore? npcs)
        => npcs is null ? [] : [.. npcs.Content.VendorItems.Select(v => v.Item), .. npcs.Content.VendorTemplateItems.Select(v => v.Item)];

    /// <summary>
    /// Whether the planner (and with it the pricing built from the vendor set) must be rebuilt: the item store changed, or the NPC store did
    /// (<c>.reload npc_vendor</c> and the other NPC tables swap the whole <c>NpcStore</c>), or there is no planner yet.
    /// </summary>
    internal static bool PlannerIsStale(IItemTemplateStore store, IItemTemplateStore? poolSource, ArcaneCore.Game.Npc.NpcStore? npcs, ArcaneCore.Game.Npc.NpcStore? vendorSource, bool hasPlanner)
        => !hasPlanner || !ReferenceEquals(store, poolSource) || !ReferenceEquals(npcs, vendorSource);

    private AuctionBotPlanner? EnsurePlanner()
    {
        if (_items is null || _items.LoadedStore is not ItemTemplateStore store || ReferenceEquals(store, ItemTemplateStore.Empty))
        {
            // The item content (and the item GUID seed) is not loaded yet; never mint before the allocator is seeded.
            if (_items is not null && ReferenceEquals(_items.LoadedStore, ItemTemplateStore.Empty))
            {
                _items.StartLoadInBackground(); // never inline: SQLite's async calls run synchronously on the tick
            }

            return null;
        }

        ArcaneCore.Game.Npc.NpcStore? npcs = services.GetService<QuestNpcFeature>()?.Services?.Npcs;
        if (PlannerIsStale(store, _poolSource, npcs, _vendorSource, _planner is not null))
        {
            HashSet<uint> vendorItems = VendorItemSet(npcs);
            var pricing = new AuctionBotPricing(Options, vendorItems);
            var pool = new AuctionBotItemPool(store.All, Options, pricing);
            _planner = new AuctionBotPlanner(Options, pricing, pool, _random) { Overrides = new Dictionary<uint, AuctionBotItemOverride>(_overrides) };
            _poolSource = store;
            _vendorSource = npcs;
            _lootSource = null;
            logger.LogInformation("AHBot item pool: {Count} items ({Qualities})", pool.Count,
                string.Join(", ", pool.CountByQuality().OrderBy(p => p.Key).Select(p => $"quality {p.Key}: {p.Value}")));
        }

        if (services.GetService<GameObjectLootFeature>()?.LootContent is { } loot && (!ReferenceEquals(loot, _lootSource) || _loot is null))
        {
            CreatureWorldFeature? creatures = services.GetService<CreatureWorldFeature>();
            _loot = new AuctionBotLootSources(loot, entry => creatures?.Content.FindTemplate(entry)?.Rank, Options, _random);
            _lootSource = loot;
            logger.LogInformation("AHBot loot sources: {Sources}", string.Join(", ", _loot.Sources.Select(s => $"{s.Name}: {s.Tables.Count}")));
        }

        return _planner;
    }
}
