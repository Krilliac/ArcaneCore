using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Features;
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
/// and every copper it pays is reserved in <see cref="Ledger"/> first and committed with the row's stable operation id; a row whose
/// outcome is unknown stays reserved until the store says whether it committed. If the ledger's audit ever finds a broken invariant
/// the bot stops itself.
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
    private WorldRuntime? _world;
    private EconomyFeature? _economy;
    private ItemsFeature? _items;
    private Timer? _timer;
    private IItemTemplateStore? _poolSource;
    private AuctionBotPlanner? _planner;
    private int _houseAction = -1;

    public AuctionBotOptions Options { get; } = new();

    public AuctionBotCustodyLedger Ledger { get; private set; } = new(new AuctionBotOptions());

    /// <summary>Set when the ledger audit failed: the bot does nothing more until restart.</summary>
    public bool Halted { get; private set; }

    /// <summary>The current planner (null until the item content is loaded).</summary>
    public AuctionBotPlanner? Planner => _planner;

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(AuctionBotOptions.SectionName).Bind(Options);
        foreach (string fix in Options.Normalize())
        {
            logger.LogWarning("AHBot: {Fix}", fix);
        }

        Ledger = new AuctionBotCustodyLedger(Options);
        _world = world;
        _economy = services.GetService<EconomyFeature>();
        _items = services.GetService<ItemsFeature>();
        if (!Options.Enabled)
        {
            logger.LogInformation("AHBot is disabled (AuctionHouseBot:Enabled)");
            return;
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
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>World thread: one bot action (the timer's work; tests call it directly).</summary>
    public void Tick()
    {
        if (!Options.Enabled || Halted || _economy is not { Enabled: true } economy || EnsurePlanner() is not { } planner)
        {
            return;
        }

        long now = Now;
        ReconcilePending(economy);
        uint[] houseIds = Options.HouseIds();
        int houses = houseIds.Length;
        if (houses == 0)
        {
            return;
        }

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

        Ledger.PruneTerminal(now - TerminalRetentionSeconds);
        Audit();
    }

    /// <summary>World thread: one sell pass over a house, ignoring the chance roll (tests and GM tools).</summary>
    public int Sell(uint houseId)
        => !Options.Enabled || Halted || _economy is not { Enabled: true } economy || EnsurePlanner() is not { } planner
            ? 0 : Sell(economy, planner, houseId, Now);

    /// <summary>World thread: one buy pass over a house, ignoring the chance roll (tests and GM tools).</summary>
    public int Buy(uint houseId)
        => !Options.Enabled || Halted || _economy is not { Enabled: true } economy || EnsurePlanner() is not { } planner
            ? 0 : Buy(economy, planner, houseId, Now);

    private int Sell(EconomyFeature economy, AuctionBotPlanner planner, uint houseId, long now)
    {
        if (economy.ItemGuids is not { } guids || _items is null)
        {
            return 0;
        }

        int room = (int)Math.Min(Options.MaxAuctionsPerHouse, int.MaxValue) - economy.BotAuctionCount(houseId);
        int started = 0;
        foreach (AuctionBotSellIntent intent in planner.PlanSell(room))
        {
            if (_items.Templates.Find(intent.Entry) is not { } template)
            {
                continue;
            }

            uint auctionId = economy.NextAuctionId();
            var item = new ItemInstanceData
            {
                Guid = guids.Next(),
                Entry = intent.Entry,
                Count = intent.Count,
                Durability = template.MaxDurability,
                Charges = [.. Enumerable.Range(0, 5).Select(i => i < template.Spells.Count ? template.Spells[i].Charges : 0)],
                Enchantments = new uint[21],
            };
            if (Ledger.TryReserveListing(houseId, auctionId, item.Guid, item.Entry, item.Count, now) is not { } row)
            {
                // The day's item budget is spent (or a guard refused): stop this pass.
                break;
            }

            _inFlight.Add(row.IdemKey);
            economy.StartBotListing(houseId, auctionId, item, intent.StartBid, intent.Buyout, now + (intent.Hours * 3600L), row.OperationId,
                outcome => Finish(row, outcome));
            started++;
        }

        return started;
    }

    private int Buy(EconomyFeature economy, AuctionBotPlanner planner, uint houseId, long now)
    {
        if (_items is null)
        {
            return 0;
        }

        Dictionary<uint, ArcaneCore.Kernel.Economy.AuctionRecord> open = economy.OpenAuctions(houseId).ToDictionary(v => v.Auction.Id, v => v.Auction);
        int started = 0;
        foreach (AuctionBotBuyIntent intent in planner.PlanBuy(open.Values, _items.Templates.Find))
        {
            ArcaneCore.Kernel.Economy.AuctionRecord auction = open[intent.AuctionId];
            if (Ledger.TryReserveBuyout(houseId, auction.Id, auction.ItemGuid, auction.ItemEntry, auction.ItemCount, intent.Price, now) is not { } row)
            {
                continue;
            }

            _inFlight.Add(row.IdemKey);
            economy.StartBotBuyout(auction.Id, intent.Price, row.OperationId, outcome => Finish(row, outcome));
            started++;
        }

        return started;
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

    private AuctionBotPlanner? EnsurePlanner()
    {
        if (_items is null || _items.LoadedStore is not ItemTemplateStore store || ReferenceEquals(store, ItemTemplateStore.Empty))
        {
            // The item content (and the item GUID seed) is not loaded yet; never mint before the allocator is seeded.
            if (_items is not null && ReferenceEquals(_items.LoadedStore, ItemTemplateStore.Empty))
            {
                _ = _items.EnsureLoadedAsync();
            }

            return null;
        }

        if (!ReferenceEquals(store, _poolSource) || _planner is null)
        {
            HashSet<uint> vendorItems = services.GetService<QuestNpcFeature>()?.Services?.Npcs.Content.VendorItems.Select(v => v.Item).ToHashSet() ?? [];
            var pricing = new AuctionBotPricing(Options, vendorItems);
            var pool = new AuctionBotItemPool(store.All, Options, pricing);
            _planner = new AuctionBotPlanner(Options, pricing, pool, _random);
            _poolSource = store;
            logger.LogInformation("AHBot item pool: {Count} items ({Qualities})", pool.Count,
                string.Join(", ", pool.CountByQuality().OrderBy(p => p.Key).Select(p => $"quality {p.Key}: {p.Value}")));
        }

        return _planner;
    }
}
