using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Economy;

/// <summary>
/// Mail, auction house and player trade (vmangos MailHandler / AuctionHouseHandler /
/// TradeHandler behavior, re-implemented). Every item or money movement is one
/// <see cref="IEconomyStore"/> transaction run by <see cref="EconomySettlements"/>: escrowed
/// items stay in item_instance with owner 0 and exactly one letter/auction reference, so an
/// item exists exactly once whether a commit succeeds, fails, or the process dies.
/// The feature is inert (every request answered with an error) when no economy store is registered.
/// </summary>
public sealed partial class EconomyFeature : IWorldFeature, ICharacterSettlementBarrier, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EconomyFeature> _logger;
    private readonly TimeProvider _clock;
    private readonly Lock _readGate = new();
    private readonly HashSet<Task> _reads = [];
    private readonly CancellationTokenSource _readStop = new();
    private WorldRuntime? _world;
    private ItemsFeature? _items;
    private CharacterDirectory? _directory;
    private Timer? _sweepTimer;
    private Timer? _deliveryTimer;
    private uint _lastMailId;
    private uint _lastAuctionId;
    private uint _lastTextId;
    private bool _stopping;

    public EconomyFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<EconomyFeature> logger,
        TimeProvider? timeProvider = null)
    {
        _services = services;
        _scopes = scopes;
        _logger = logger;
        _clock = timeProvider ?? TimeProvider.System;
        Settlements = new EconomySettlements(scopes, logger, () => Options.SettlementBudget);
    }

    public const string SectionName = "Economy";

    public EconomyOptions Options { get; } = new();

    public EconomySettlements Settlements { get; }

    /// <summary>Whether a store is registered and the startup load succeeded.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Unix seconds.</summary>
    public long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    internal IItemTemplateStore Templates => _items?.Templates ?? ItemTemplateStore.Empty;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the economy feature is already attached");
        }

        _services.GetService<IConfiguration>()?.GetSection(SectionName).Bind(Options);
        _world = world;
        if (_services.GetService<SpellFeature>() is { } spells)
        {
            spells.System.TradeEnchantmentRequest = DeferTradeEnchantment;
            spells.System.TradeItemEnchantmentRequest = DeferTradeItemEnchantment;
        }
        _items = _services.GetService<ItemsFeature>();
        _directory = _services.GetService<CharacterDirectory>();
        Settlements.Attach(world, _services.GetService<CharacterSaveQueue>(), _services.GetService<TeleportFeature>());
        using (IServiceScope scope = _scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IEconomyStore>() is { } store)
            {
                // Startup content load, as the quest feature does: a failure stops attachment.
                // Rows and escrow come from one snapshot. The ID seed is read after it, so every
                // loaded ID is at or below the seed.
                AuctionSnapshot snapshot = store.GetAuctionSnapshotAsync(new AuctionSnapshotFilter()).GetAwaiter().GetResult();
                EconomyIdSeed seed = store.GetIdSeedAsync().GetAwaiter().GetResult();
                _lastMailId = seed.MaxMailId;
                _lastAuctionId = seed.MaxAuctionId;
                _lastTextId = seed.MaxItemTextId;
                foreach (AuctionRecord auction in snapshot.Auctions)
                {
                    if (snapshot.Escrow.TryGetValue(auction.ItemGuid, out ItemInstanceData? item) && EscrowMatches(auction, item))
                    {
                        _auctions[auction.Id] = new AuctionView(auction, item);
                    }
                    else
                    {
                        // Reserved, not dropped: the row is durable, so it is re-read until its escrow item matches.
                        _logger.LogError("auction {Auction} has no matching escrow item {Item}; it stays reserved and unlisted until the item is repaired",
                            auction.Id, auction.ItemGuid);
                        ReserveMismatchedAuction(auction);
                    }
                }

                Enabled = true;
                _logger.LogInformation("Loaded {Auctions} auctions ({Reserved} reserved)", _auctions.Count, _auctionRecoveries.Count);
            }
        }

        world.PlayerLoggedIn += OnPlayerLoggedIn;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.MapCreated += OnMapCreated;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        // Features attach in name order; the NPC services are rebuilt by their own Attach.
        world.Post(SubscribeGossip);
        if (Enabled && Options.ExpirySweepSeconds > 0)
        {
            TimeSpan period = TimeSpan.FromSeconds(Options.ExpirySweepSeconds);
            _sweepTimer = new Timer(_ => world.Post(RunExpirySweep), null, period, period);
        }

        if (Enabled)
        {
            // vmangos MasterPlayer::Update checks the next mail delivery time every world tick; once a second is as fine as the clock.
            _deliveryTimer = new Timer(_ => world.Post(NotifyDeliveredMail), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default)
        => Settlements.WaitForCharacterAsync(characterId, cancellationToken);

    public async Task StopAsync()
    {
        _stopping = true;
        if (_sweepTimer is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        if (_deliveryTimer is { } deliveryTimer)
        {
            await deliveryTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_auctionRecoveryTimer is { } recoveryTimer)
        {
            await recoveryTimer.DisposeAsync().ConfigureAwait(false);
        }

        await _readStop.CancelAsync().ConfigureAwait(false);
        Task[] reads;
        lock (_readGate)
        {
            reads = [.. _reads];
        }

        await Task.WhenAll(reads).ConfigureAwait(false);
        await Settlements.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_world is { } world)
        {
            world.PlayerLoggedIn -= OnPlayerLoggedIn;
            world.PlayerLoggingOut -= OnPlayerLoggingOut;
            world.MapCreated -= OnMapCreated;
        }

        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Wait until every background read and settlement started so far has been observed (tests).</summary>
    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] reads;
            lock (_readGate)
            {
                reads = [.. _reads];
            }

            if (reads.Length == 0 && Settlements.PendingCount == 0)
            {
                return;
            }

            await Task.WhenAll(reads).ConfigureAwait(false);
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private uint NextMailId() => Interlocked.Increment(ref _lastMailId);

    /// <summary>World thread. Internal so tests can observe the allocator after a reseed.</summary>
    internal uint NextAuctionId() => Interlocked.Increment(ref _lastAuctionId);

    /// <summary>The highest IDs handed out or seeded so far, read without allocating. Internal so tests can observe a reseed.</summary>
    internal (uint Mail, uint Auction, uint Text) LastAllocatedIds()
        => (Volatile.Read(ref _lastMailId), Volatile.Read(ref _lastAuctionId), Volatile.Read(ref _lastTextId));

    private uint NextTextId() => Interlocked.Increment(ref _lastTextId);

    /// <summary>A fresh mail id above <paramref name="atLeast"/> (a stored maximum), for a loaded character dump (<c>.pdump load</c>).</summary>
    internal uint ReserveMailId(uint atLeast)
    {
        RaiseTo(ref _lastMailId, atLeast);
        return NextMailId();
    }

    /// <summary>A fresh item text id above <paramref name="atLeast"/>, for a loaded character dump.</summary>
    internal uint ReserveItemTextId(uint atLeast)
    {
        RaiseTo(ref _lastTextId, atLeast);
        return NextTextId();
    }

    private IMailboxAccess? _retailMailboxAccess;

    /// <summary>Test seam: replaces the mailbox check on a running host (a registered <see cref="IMailboxAccess"/> is the production hook).</summary>
    internal IMailboxAccess? MailboxAccessOverride { get; set; }

    private IMailboxAccess MailboxAccess => MailboxAccessOverride ?? _services.GetService<IMailboxAccess>()
        ?? (Options.MailboxAccess == MailboxAccessMode.Permissive
            ? new PermissiveMailboxAccess()
            : _retailMailboxAccess ??= new GameObjectMailboxAccess(() => _services.GetService<GameObjectLootFeature>()));

    private IAuctioneerAccess AuctioneerAccess => _services.GetService<IAuctioneerAccess>()
        ?? new DefaultAuctioneerAccess(_services.GetService<QuestNpcFeature>(), Options);

    private bool CharacterExists(int id) => id > 0 && (_directory is null || _directory.Find(id) is not null);

    private Player? OnlinePlayer(int id) => id > 0 ? _world?.FindOnlinePlayer(ObjectGuid.Player((uint)id)) : null;

    private static int IdOf(Player player) => checked((int)player.Guid.Low);

    private static bool IsAlliance(byte race) => (Race)race is Race.Human or Race.Dwarf or Race.NightElf or Race.Gnome;

    /// <summary>
    /// Run a store read in a fresh scope off the world thread, then continue on the world thread.
    /// Failures are logged and answered by <paramref name="failed"/> on the world thread.
    /// </summary>
    private void Read<T>(Func<IEconomyStore, CancellationToken, Task<T>> read, Action<T> done, Action? failed = null)
    {
        if (!Enabled || _stopping || _world is not { } world)
        {
            failed?.Invoke();
            return;
        }

        Task task = Task.Run(async () =>
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_readStop.Token);
                budget.CancelAfter(Settlements.Budget);
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                T result = await read(scope.ServiceProvider.GetRequiredService<IEconomyStore>(), budget.Token).ConfigureAwait(false);
                world.Post(() => done(result));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "economy read failed");
                if (failed is not null)
                {
                    world.Post(failed);
                }
            }
        });
        lock (_readGate)
        {
            _reads.Add(task);
        }

        task.ContinueWith(t =>
        {
            lock (_readGate)
            {
                _reads.Remove(t);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// World thread: start an economy operation (enabled store required). <paramref name="finished"/>
    /// receives the outcome; its packets and cache updates only run when <c>live</c>.
    /// </summary>
    private bool Start(IReadOnlyList<EconomyActor> actors, IReadOnlyList<EconomyChange> changes, Action<EconomyOutcome> finished,
        Guid? operationId = null)
        => Enabled && !_stopping && Settlements.TryStart(actors, changes, (outcome, live) =>
        {
            if (outcome == EconomyOutcome.Before && changes.Any(c => c is InsertAuction or InsertMail))
            {
                // A refused insert may mean another writer took the ID: raise the allocators past it.
                ReseedIds();
            }

            if (live)
            {
                finished(outcome);
            }
        }, operationId);

    /// <summary>
    /// World thread: re-read the highest IDs in use and raise (never lower) the allocators. Every
    /// allocation and this completion run on the world thread; the compare loop only keeps the
    /// raise monotonic.
    /// </summary>
    private void ReseedIds() => Read((store, ct) => store.GetIdSeedAsync(ct), seed =>
    {
        RaiseTo(ref _lastMailId, seed.MaxMailId);
        RaiseTo(ref _lastAuctionId, seed.MaxAuctionId);
        RaiseTo(ref _lastTextId, seed.MaxItemTextId);
    });

    private static void RaiseTo(ref uint field, uint value)
    {
        uint current = Volatile.Read(ref field);
        while (current < value)
        {
            uint seen = Interlocked.CompareExchange(ref field, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    private void OnPlayerLoggedIn(Player player)
    {
        if (Enabled)
        {
            LoadMailbox(player, sendList: false);
        }
    }

    private void OnPlayerLoggingOut(Player player)
    {
        CancelTrade(player, TradeStatus.TradeCanceled);
        _mailboxes.Remove(IdOf(player));
        _nextMailDelivery.Remove(IdOf(player));
    }

    private readonly HashSet<Map> _maps = [];

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map))
        {
            map.AddUpdater(new TradeMapUpdater(this));
        }
    }

    private void SubscribeGossip()
    {
        if (_services.GetService<QuestNpcFeature>() is { } npcs)
        {
            npcs.Services.ForeignOptionSelected += (player, npc, option) =>
            {
                if (option == ArcaneCore.Game.Npc.GossipOption.Auctioneer && player.Session is WorldSession session)
                {
                    AuctionHello(session, player, npc.Guid);
                }
            };
        }
    }

    /// <summary>World thread: expire auctions from the cache and letters from storage.</summary>
    public void RunExpirySweep()
    {
        if (!Enabled || _stopping)
        {
            return;
        }

        RecoverAuctions();
        ExpireAuctions();
        Read((store, ct) => store.GetExpiredMailsAsync(Now, 50, ct), ExpireMails);
    }
}
