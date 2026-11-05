using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Auxiliary;
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
    private ArcaneCore.World.Cluster.Social.RealmAuthorityLeaseKeeper? _leaseKeeper;

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
        if (!Invariant.Check(_world is null, "EconomyFeature.Attach: the economy feature is already attached"))
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
        _leaseKeeper = _services.GetService<ArcaneCore.World.Cluster.Social.RealmAuthorityLeaseKeeper>();
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
        world.MapUnloading += OnMapUnloading;
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
            _deliveryTimer = new Timer(_ => world.Post(() =>
            {
                try
                {
                    NotifyDeliveredMail();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogFault("economy.delivery-timer", ex, "economy mail delivery notification failed");
                }
            }), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    public Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default)
        => Settlements.WaitForCharacterAsync(characterId, cancellationToken);

    public IReadOnlyCollection<int> SettlingCharacters => Settlements.SettlingCharacters;

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
            world.MapUnloading -= OnMapUnloading;
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

    /// <summary>When true (Worker mode) a mail, auction or item text id is never taken from the in-memory counter; an unprimed allocator throws instead.</summary>
    public bool RequireDurableIds { get; set; }

    private void ThrowIfDurableRequired(string scope)
    {
        if (RequireDurableIds)
        {
            throw new IdRangeExhaustedException(
                scope,
                $"Economy id allocator for scope '{scope}' is not primed with a durable block (fail-closed in Worker mode).");
        }
    }

    public DurableIdBlockAllocator? MailIdAllocator { get; set; }

    public DurableIdBlockAllocator? AuctionIdAllocator { get; set; }

    public DurableIdBlockAllocator? ItemTextIdAllocator { get; set; }

    private uint NextMailId()
    {
        if (MailIdAllocator is { } allocator)
        {
            uint next = allocator.Next();
            RaiseTo(ref _lastMailId, next);
            return next;
        }

        ThrowIfDurableRequired(IdRangeScopes.Mail);
        return Interlocked.Increment(ref _lastMailId);
    }

    /// <summary>World thread. Internal so tests can observe the allocator after a reseed.</summary>
    internal uint NextAuctionId()
    {
        if (AuctionIdAllocator is { } allocator)
        {
            uint next = allocator.Next();
            RaiseTo(ref _lastAuctionId, next);
            return next;
        }

        ThrowIfDurableRequired(IdRangeScopes.Auction);
        return Interlocked.Increment(ref _lastAuctionId);
    }

    /// <summary>The highest IDs handed out or seeded so far, read without allocating. Internal so tests can observe a reseed.</summary>
    internal (uint Mail, uint Auction, uint Text) LastAllocatedIds()
        => (Volatile.Read(ref _lastMailId), Volatile.Read(ref _lastAuctionId), Volatile.Read(ref _lastTextId));

    private uint NextTextId()
    {
        if (ItemTextIdAllocator is { } allocator)
        {
            uint next = allocator.Next();
            RaiseTo(ref _lastTextId, next);
            return next;
        }

        ThrowIfDurableRequired(IdRangeScopes.ItemText);
        return Interlocked.Increment(ref _lastTextId);
    }

    /// <summary>A fresh mail id above <paramref name="atLeast"/> (a stored maximum), for a loaded character dump (<c>.pdump load</c>).</summary>
    internal uint ReserveMailId(uint atLeast)
    {
        RaiseTo(ref _lastMailId, atLeast);
        uint id = NextMailId();
        while (id <= atLeast)
        {
            id = NextMailId();
        }

        return id;
    }

    /// <summary>A fresh item text id above <paramref name="atLeast"/>, for a loaded character dump.</summary>
    internal uint ReserveItemTextId(uint atLeast)
    {
        RaiseTo(ref _lastTextId, atLeast);
        uint id = NextTextId();
        while (id <= atLeast)
        {
            id = NextTextId();
        }

        return id;
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

    private static int IdOf(Player player)
    {
        Invariant.Check(player is not null && !player.Guid.IsEmpty && player.Guid.Low > 0, $"EconomyFeature.IdOf: invalid player GUID {player?.Guid}");
        return checked((int)player!.Guid.Low);
    }

    private static bool IsAlliance(byte race) => TeamRules.TeamOf((Race)race) == Team.Alliance;

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
                _logger.LogFault("economy.read", ex, "economy read failed");
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

    /// <summary>How many maps this feature currently owns an updater on (an unloaded map is released).</summary>
    internal int TrackedMapCount => _maps.Count;

    internal void OnMapUnloading(Map map) => _maps.Remove(map);

    internal void OnMapCreated(Map map)
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
                    try
                    {
                        AuctionHello(session, player, npc.Guid);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _logger.LogFault("economy.gossip-auction", ex, "auction hello from gossip failed for player {Player}", player.Guid);
                    }
                }
            };
        }
    }

    /// <summary>World thread: expire auctions from the cache and letters from storage.</summary>
    public void RunExpirySweep()
    {
        // Only the Economy lease holder runs expiry (auctions, letters, recovery): a worker that forwards its requests, or a worker that does not hold the
        // economy lease, would settle the same rows twice (writer-lock design section 2b: the sweep ran on every worker before).
        if (!Enabled || _stopping || !IsEconomyHolder || !HoldsEconomyLease)
        {
            return;
        }

        try
        {
            RecoverAuctions();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("economy.sweep-recover", ex, "auction recovery sweep failed");
        }

        try
        {
            ExpireAuctions();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("economy.sweep-auctions", ex, "auction expiry sweep failed");
        }

        try
        {
            Read((store, ct) => store.GetExpiredMailsAsync(Now, 50, ct), ExpireMails);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("economy.sweep-mails", ex, "mail expiry sweep failed");
        }
    }

    /// <summary>
    /// World thread: mail <paramref name="count"/> of <paramref name="itemEntry"/> to a character from the creature <paramref name="senderEntry"/>
    /// (MAIL_CREATURE), as vmangos BattleGround::SendRewardMarkByMail does for the part of a mark the bags could not take
    /// (BattleGround.cpp:849-882: <c>SendMailTo(..., MAIL_CHECK_MASK_COPIED, 15 * MINUTE, 1 * DAY)</c>: delivered after 15 minutes,
    /// expiring a day after that). The item is created new straight into escrow in one economy operation. <paramref name="finished"/>
    /// gets true once the letter is committed; false when the economy store is off, the template is unknown or the commit failed.
    /// </summary>
    public void SendCreatureItemMail(int receiverId, uint senderEntry, string subject, string body, uint itemEntry, uint count,
        Action<bool>? finished = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        List<MailView>? built = senderEntry == 0 ? null : BuildLetters(receiverId, senderEntry, 0, subject, body, 0, [new ServerMailItem(itemEntry, count)]);
        if (built is not { Count: 1 })
        {
            finished?.Invoke(false);
            return;
        }

        long deliver = Now + BattlegroundMarkMailDelaySeconds;
        MailView letter = built[0] with
        {
            Mail = built[0].Mail with { DeliverTime = deliver, ExpireTime = deliver + BattlegroundMarkMailExpirySeconds },
        };
        var changes = new List<EconomyChange>();
        if (letter.Item is { } escrow) changes.Add(new CreateEscrowItem(escrow));
        changes.Add(new InsertMail(letter.Mail, letter.Body));
        if (!Start([], changes, outcome =>
            {
                bool ok = outcome == EconomyOutcome.After;
                if (ok) Deliver(letter);
                finished?.Invoke(ok);
            }))
        {
            finished?.Invoke(false);
        }
    }

    private const long BattlegroundMarkMailDelaySeconds = 15 * 60;
    private const long BattlegroundMarkMailExpirySeconds = 24 * 60 * 60;

    // ---- Requests from workers that do not hold the Economy lease (M15 S3d, docs/CLUSTERING_DESIGN.md section 8 mail and auctions).
    // The holder owns the only auction cache, the expiry sweep and the fenced store writes. A worker reads through ServeForwardedAsync and sends
    // every mail or auction write as the commit it built for its own participants; the holder validates it against its cache, commits it (the store
    // verifies the lease fence in the same transaction), applies it to the cache and tells the parties. Thread affinity: ServeForwardedAsync may be called
    // from any thread (the peer link); everything that touches the caches runs on the world thread.

    private volatile IEconomyHolderLink? _holderLink;
    private readonly HashSet<Guid> _forwardedDone = [];
    private readonly Queue<Guid> _forwardedDoneOrder = new();

    // Operation ids whose commit is running now (world thread). A resend of one is answered Unknown: the first attempt may still commit.
    private readonly HashSet<Guid> _forwardedInFlight = [];

    /// <summary>Most committed forwarded operation ids remembered, so a retry after a lost answer is answered Ok without a second write.</summary>
    private const int ForwardedDoneLimit = 1024;

    /// <summary>Most auctions one forwarded list answer carries.</summary>
    public const int MaxForwardedAuctions = 4096;

    /// <summary>
    /// The link to the node holding the Economy lease, or null when this node is the holder (the default). A non-null link makes this node a forwarding
    /// worker: it refuses forwarded requests (<see cref="EconomyForwardStatus.NotHolder"/>) and runs no expiry sweep. Settable at any time (a lease change).
    /// </summary>
    public IEconomyHolderLink? HolderLink
    {
        get => _holderLink;
        set => _holderLink = value;
    }

    /// <summary>
    /// Whether this node holds the <see cref="ArcaneCore.Kernel.Cluster.Social.RealmAuthorityScopes.Economy"/> lease: always on a node with no lease keeper (Combined, tests),
    /// on a clustered node only while the keeper reports the lease held (not released, not lost). Gates the expiry sweep and the auction recovery sweep.
    /// </summary>
    public bool HoldsEconomyLease => _leaseKeeper is not { } keeper || keeper.Holds(ArcaneCore.Kernel.Cluster.Social.RealmAuthorityScopes.Economy);

    /// <summary>Whether this node runs the economy singletons (expiry and the forwarded-request service).</summary>
    public bool IsEconomyHolder => _holderLink is null;

    /// <summary>Routes notifications for recipients not hosted here (after a forwarded commit) to their workers; null sends none.</summary>
    public EconomyCommitNotifier? CommitNotifier { get; set; }

    /// <summary>
    /// Serve one worker request on the holder: reads answer from the cache (mail from the store when the character is not online here), writes are validated,
    /// committed and applied. Never throws for a bad request (the answer carries the status); a cancelled <paramref name="cancellationToken"/> stops the wait only,
    /// not a commit already started (the same request sent again learns its outcome).
    /// </summary>
    public Task<EconomyForwardResult> ServeForwardedAsync(EconomyForwardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsEconomyHolder)
        {
            return Task.FromResult(EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.NotHolder));
        }

        if (!Enabled || _stopping || _world is not { } world)
        {
            return Task.FromResult(EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Disabled));
        }

        var done = new TaskCompletionSource<EconomyForwardResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Post(() =>
        {
            try
            {
                ServeForwardedOnWorld(request, world, done);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("economy.forward", ex, "forwarded economy request {Request} failed", request.RequestId);
                done.TrySetResult(EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Failed));
            }
        });
        return done.Task.WaitAsync(cancellationToken);
    }

    private void ServeForwardedOnWorld(EconomyForwardRequest request, WorldRuntime world, TaskCompletionSource<EconomyForwardResult> done)
    {
        if (!IsEconomyHolder || !Enabled || _stopping)
        {
            done.TrySetResult(EconomyForwardResult.Of(request.RequestId,
                IsEconomyHolder ? EconomyForwardStatus.Disabled : EconomyForwardStatus.NotHolder));
            return;
        }

        switch (request.Kind)
        {
            case EconomyForwardKind.AuctionList:
                done.TrySetResult(new EconomyForwardResult(request.RequestId, EconomyForwardStatus.Ok, [],
                    [.. HouseAuctions(request.HouseId, Now)
                        .Where(v => request.AuctionId == 0 || v.Auction.Id == request.AuctionId)
                        .Take(MaxForwardedAuctions)
                        .Select(v => new ForwardedAuction(v.Auction, v.Item))]));
                break;
            case EconomyForwardKind.MailList:
                ServeForwardedMailList(request, done);
                break;
            case EconomyForwardKind.MailWrite:
            case EconomyForwardKind.AuctionWrite:
                ServeForwardedWrite(request, world, done);
                break;
            default:
                done.TrySetResult(EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Invalid));
                break;
        }
    }

    private void ServeForwardedMailList(EconomyForwardRequest request, TaskCompletionSource<EconomyForwardResult> done)
    {
        int characterId = request.CharacterId;
        if (characterId <= 0)
        {
            done.TrySetResult(EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Invalid));
            return;
        }

        if (CachedMail(characterId) is { } cached)
        {
            done.TrySetResult(new EconomyForwardResult(request.RequestId, EconomyForwardStatus.Ok,
                [.. cached.Select(m => new ForwardedMail(m.Mail, m.Item))], []));
            return;
        }

        // Not online here: the store is the only copy, so read it as LoadMailbox does (newest first, with the escrowed items).
        Read(async (store, ct) =>
        {
            IReadOnlyList<MailRecord> mails = await store.GetMailsAsync(characterId, ct).ConfigureAwait(false);
            IReadOnlyDictionary<uint, ItemInstanceData> items = await store
                .GetEscrowItemsAsync(mails.Where(m => m.HasItem).Select(m => m.ItemGuid).ToArray(), ct).ConfigureAwait(false);
            return mails.OrderByDescending(m => m.Id)
                .Select(m => new ForwardedMail(m, m.HasItem ? items.GetValueOrDefault(m.ItemGuid) : null)).ToList();
        }, mails => done.TrySetResult(new EconomyForwardResult(request.RequestId, EconomyForwardStatus.Ok, mails, [])),
            failed: () => done.TrySetResult(EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Failed)));
    }

    private void ServeForwardedWrite(EconomyForwardRequest request, WorldRuntime world, TaskCompletionSource<EconomyForwardResult> done)
    {
        void Answer(EconomyForwardStatus status) => done.TrySetResult(EconomyForwardResult.Of(request.RequestId, status));

        if (request.Commit is not { } commit || commit.OperationId == Guid.Empty || !ForwardedChangesMatch(request.Kind, commit.Changes))
        {
            Answer(EconomyForwardStatus.Invalid);
            return;
        }

        if (_forwardedDone.Contains(commit.OperationId))
        {
            Answer(EconomyForwardStatus.Ok);
            return;
        }

        if (_forwardedInFlight.Contains(commit.OperationId))
        {
            Answer(EconomyForwardStatus.Unknown);
            return;
        }

        var auctionIds = new HashSet<uint>();
        var mailIds = new HashSet<uint>();
        foreach (EconomyChange change in commit.Changes)
        {
            switch (change)
            {
                case InsertAuction insertAuction:
                    auctionIds.Add(insertAuction.Auction.Id);
                    break;
                case UpdateAuction updateAuction:
                    auctionIds.Add(updateAuction.Expected.Id);
                    break;
                case DeleteAuction deleteAuction:
                    auctionIds.Add(deleteAuction.Expected.Id);
                    break;
                case InsertMail insertMail:
                    mailIds.Add(insertMail.Mail.Id);
                    break;
                case UpdateMail updateMail:
                    mailIds.Add(updateMail.Expected.Id);
                    break;
                case DeleteMail deleteMail:
                    mailIds.Add(deleteMail.Expected.Id);
                    break;
            }
        }

        if (auctionIds.Any(_busyAuctions.Contains) || mailIds.Any(_busyMails.Contains))
        {
            RefuseForwarded(request, commit.OperationId, EconomyForwardStatus.Busy, done);
            return;
        }

        // The cache must still hold exactly the rows the worker saw: a stale bid or cancel is refused before anything is written
        // (the store compares the expected rows again inside its transaction).
        foreach (EconomyChange change in commit.Changes)
        {
            bool stale = change switch
            {
                InsertAuction insert => _auctions.ContainsKey(insert.Auction.Id) || _auctionRecoveries.ContainsKey(insert.Auction.Id),
                UpdateAuction update => !CachedAuctionIs(update.Expected),
                DeleteAuction delete => !CachedAuctionIs(delete.Expected),
                _ => false,
            };
            if (stale)
            {
                RefuseForwarded(request, commit.OperationId, EconomyForwardStatus.Conflict, done);
                return;
            }
        }

        _forwardedInFlight.Add(commit.OperationId);

        foreach (uint id in auctionIds)
        {
            _busyAuctions.Add(id);
        }

        foreach (uint id in mailIds)
        {
            _busyMails.Add(id);
        }

        CommitForwarded(request, commit, [.. auctionIds], [.. mailIds], world, done);
    }

    /// <summary>
    /// A write refused from the cache (Busy or Conflict) may be a resend of one that did commit (a holder restart or an evicted replay id), so the store
    /// is asked first: a committed operation is answered Ok, an unreadable store Unknown, and only an operation not in the store gets the refusal.
    /// </summary>
    private void RefuseForwarded(EconomyForwardRequest request, Guid operationId, EconomyForwardStatus refusal, TaskCompletionSource<EconomyForwardResult> done)
        => Read((store, ct) => store.IsCommittedAsync(operationId, ct),
            committed => done.TrySetResult(EconomyForwardResult.Of(request.RequestId, committed ? EconomyForwardStatus.Ok : refusal)),
            failed: () => done.TrySetResult(EconomyForwardResult.Of(request.RequestId, EconomyForwardStatus.Unknown)));

    private bool CachedAuctionIs(AuctionRecord expected)
        => _auctions.TryGetValue(expected.Id, out AuctionView? cached) && cached.Auction == expected && !_auctionRecoveries.ContainsKey(expected.Id);

    /// <summary>
    /// A write kind carries only changes of its class, row updates keep their id, and a new auction brings its escrow item in the same commit
    /// (the cache needs it to list the auction).
    /// </summary>
    private static bool ForwardedChangesMatch(EconomyForwardKind kind, IReadOnlyList<EconomyChange> changes)
    {
        bool auction = changes.Any(c => c is InsertAuction or UpdateAuction or DeleteAuction);
        bool mail = changes.Any(c => c is InsertMail or UpdateMail or ArcaneCore.Kernel.Economy.DeleteMail);
        bool kindFits = kind switch
        {
            EconomyForwardKind.AuctionWrite => auction,
            EconomyForwardKind.MailWrite => mail && !auction,
            _ => false,
        };
        return kindFits
            && changes.All(c => c switch
            {
                UpdateAuction u => u.Updated.Id == u.Expected.Id,
                UpdateMail m => m.Updated.Id == m.Expected.Id,
                InsertAuction i => ForwardedEscrowItem(changes, i.Auction.ItemGuid) is not null,
                _ => true,
            });
    }

    /// <summary>The escrow item a commit creates, mints or moves out of an inventory, by item guid.</summary>
    private static ItemInstanceData? ForwardedEscrowItem(IReadOnlyList<EconomyChange> changes, uint itemGuid)
    {
        foreach (EconomyChange change in changes)
        {
            ItemInstanceData? item = change switch
            {
                EscrowFromInventory fromInventory => fromInventory.Item,
                CreateEscrowItem created => created.Item,
                MintEscrowItem minted => minted.Item,
                _ => null,
            };
            if (item is not null && item.Guid == itemGuid)
            {
                return item;
            }
        }

        return null;
    }

    private void CommitForwarded(EconomyForwardRequest request, EconomyCommitRequest commit, uint[] auctionIds, uint[] mailIds, WorldRuntime world,
        TaskCompletionSource<EconomyForwardResult> done)
    {
        Task task = Task.Run(async () =>
        {
            EconomyOutcome outcome = EconomyOutcome.Unknown;
            EconomyForwardStatus refusal = EconomyForwardStatus.Failed;
            bool replay = false;
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(_readStop.Token);
                budget.CancelAfter(Settlements.Budget);
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                EconomyCommitResult result = await scope.ServiceProvider.GetRequiredService<IEconomyStore>()
                    .CommitAsync(commit, budget.Token).ConfigureAwait(false);
                if (result is EconomyCommitResult.Committed or EconomyCommitResult.AlreadyCommitted)
                {
                    outcome = EconomyOutcome.After;
                    replay = result == EconomyCommitResult.AlreadyCommitted;
                }
                else
                {
                    outcome = EconomyOutcome.Before;
                    refusal = EconomyForwardStatus.Conflict;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("economy.forward-commit", ex, "forwarded economy operation {Operation} from worker {Worker} failed", commit.OperationId, request.WorkerId);
                try
                {
                    using var reconcileBudget = CancellationTokenSource.CreateLinkedTokenSource(_readStop.Token);
                    reconcileBudget.CancelAfter(Settlements.Budget);
                    await using AsyncServiceScope reconcileScope = _scopes.CreateAsyncScope();
                    outcome = await reconcileScope.ServiceProvider.GetRequiredService<IEconomyStore>()
                        .IsCommittedAsync(commit.OperationId, reconcileBudget.Token).ConfigureAwait(false) ? EconomyOutcome.After : EconomyOutcome.Before;
                }
                catch (Exception reconcile) when (reconcile is not OutOfMemoryException)
                {
                    _logger.LogFault("economy.forward-reconcile", reconcile, "could not reconcile forwarded economy operation {Operation}", commit.OperationId);
                    outcome = EconomyOutcome.Unknown;
                }
            }

            world.Post(() => FinishForwarded(request, commit, auctionIds, mailIds, outcome, refusal, replay, done));
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

    /// <summary>World thread: release the rows, reserve what the outcome left uncertain, apply a committed write to the cache and answer the worker.</summary>
    private void FinishForwarded(EconomyForwardRequest request, EconomyCommitRequest commit, uint[] auctionIds, uint[] mailIds, EconomyOutcome outcome,
        EconomyForwardStatus refusal, bool replay, TaskCompletionSource<EconomyForwardResult> done)
    {
        _forwardedInFlight.Remove(commit.OperationId);
        try
        {
            foreach (uint id in mailIds)
            {
                _busyMails.Remove(id);
            }

            foreach (uint id in auctionIds)
            {
                if (outcome == EconomyOutcome.Unknown || (outcome == EconomyOutcome.Before && ChangesExpectedAuction(commit.Changes, id)))
                {
                    // As RunAuctionOperation: the cache may no longer match the row, so the id stays reserved until an authoritative read repairs it.
                    QuarantineAuction(id, commit.Changes);
                }
                else
                {
                    _busyAuctions.Remove(id);
                }
            }

            if (outcome == EconomyOutcome.After)
            {
                if (_forwardedDone.Add(commit.OperationId))
                {
                    _forwardedDoneOrder.Enqueue(commit.OperationId);
                    while (_forwardedDoneOrder.Count > ForwardedDoneLimit)
                    {
                        _forwardedDone.Remove(_forwardedDoneOrder.Dequeue());
                    }
                }

                // A replay of an earlier commit (the store already had it) is answered Ok without touching the cache or the parties: the rows may
                // have changed since, and the first application already notified.
                if (!replay)
                {
                    ApplyForwardedCommit(commit.Changes);
                }
            }
            else if (outcome == EconomyOutcome.Before && commit.Changes.Any(c => c is InsertAuction or InsertMail))
            {
                // As Start: a refused insert may mean the id counters are behind the store.
                ReseedIds();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("economy.forward-apply", ex, "applying forwarded economy operation {Operation} to the cache failed", commit.OperationId);
        }

        EconomyForwardStatus status = outcome switch
        {
            EconomyOutcome.After => EconomyForwardStatus.Ok,
            EconomyOutcome.Unknown => EconomyForwardStatus.Unknown,
            _ => refusal,
        };
        done.TrySetResult(EconomyForwardResult.Of(request.RequestId, status));
    }

    /// <summary>
    /// World thread: apply a committed forwarded write to the holder's caches and tell the parties. A new letter reaches a receiver online here directly and any
    /// other receiver through <see cref="CommitNotifier"/>; a changed bid notifies the outbid bidder and the seller the same way.
    /// </summary>
    private void ApplyForwardedCommit(IReadOnlyList<EconomyChange> changes)
    {
        long now = Now;
        foreach (EconomyChange change in changes)
        {
            switch (change)
            {
                case InsertAuction insertAuction:
                    if (ForwardedEscrowItem(changes, insertAuction.Auction.ItemGuid) is { } escrow)
                    {
                        _auctions[insertAuction.Auction.Id] = new AuctionView(insertAuction.Auction, escrow);
                    }

                    break;
                case UpdateAuction updateAuction:
                    if (_auctions.TryGetValue(updateAuction.Expected.Id, out AuctionView? current))
                    {
                        _auctions[updateAuction.Expected.Id] = current with { Auction = updateAuction.Updated };
                        NotifyForwardedBid(updateAuction, current.Item);
                    }

                    break;
                case DeleteAuction deleteAuction:
                    _auctions.Remove(deleteAuction.Expected.Id);
                    _expiryBackoff.Remove(deleteAuction.Expected.Id);
                    break;
                case InsertMail insertMail:
                    DeliverForwardedLetter(new MailView(insertMail.Mail,
                        insertMail.Mail.HasItem ? ForwardedEscrowItem(changes, insertMail.Mail.ItemGuid) : null), now);
                    break;
                case UpdateMail updateMail:
                    ApplyForwardedLetterUpdate(updateMail, now);
                    break;
                case DeleteMail deleteMail:
                    RemoveMail(deleteMail.Expected.ReceiverId, deleteMail.Expected.Id);
                    break;
            }
        }
    }

    private void ApplyForwardedLetterUpdate(UpdateMail update, long now)
    {
        MailView? existing = _mailboxes.TryGetValue(update.Expected.ReceiverId, out Mailbox? box)
            ? box.Mails.FirstOrDefault(m => m.Mail.Id == update.Expected.Id)
            : null;
        var view = new MailView(update.Updated, update.Updated.HasItem ? existing?.Item : null) { Body = existing?.Body };
        if (update.Updated.ReceiverId != update.Expected.ReceiverId)
        {
            RemoveMail(update.Expected.ReceiverId, update.Expected.Id);
            DeliverForwardedLetter(view, now);
        }
        else
        {
            ReplaceMail(update.Updated.ReceiverId, view);
        }
    }

    private void DeliverForwardedLetter(MailView view, long now)
    {
        int receiver = view.Mail.ReceiverId;
        if (OnlinePlayer(receiver) is not null)
        {
            if (!(_mailboxes.TryGetValue(receiver, out Mailbox? box) && box.Mails.Any(m => m.Mail.Id == view.Mail.Id)))
            {
                Deliver(view);
            }

            return;
        }

        CommitNotifier?.MailCommitted(receiver, view.Mail.DeliverTime, now);
    }

    private void NotifyForwardedBid(UpdateAuction update, ItemInstanceData item)
    {
        AuctionRecord before = update.Expected;
        AuctionRecord after = update.Updated;
        if (after.BidderId == 0 || (after.BidderId == before.BidderId && after.Bid == before.Bid))
        {
            return;
        }

        int randomProperty = item.RandomPropertyId;
        if (before.BidderId != 0 && before.BidderId != after.BidderId)
        {
            // As the local bid path: the outbid notice is built from the auction before the bid (vmangos SendAuctionOutbiddedMail).
            if (OnlinePlayer(before.BidderId) is { } outbid)
            {
                outbid.Session.Send(WorldOpcode.SmsgAuctionBidderNotification, EconomyPackets.BidderNotification(before, won: false, randomProperty));
            }
            else
            {
                CommitNotifier?.Outbid(before, randomProperty);
            }
        }

        if (OnlinePlayer(before.SellerId) is { } seller)
        {
            seller.Session.Send(WorldOpcode.SmsgAuctionOwnerNotification, EconomyPackets.OwnerNotification(after, sold: false, randomProperty));
        }
        else
        {
            CommitNotifier?.AuctionOwnerResult(after, sold: false, randomProperty);
        }
    }
}
