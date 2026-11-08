using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World;

/// <summary>
/// Owns the world simulation's lifecycle. Registered before the listener, so it starts first
/// and stops last: on start the character name cache is filled and the world features are
/// attached; on shutdown every online character is saved, then the save queue drains.
/// </summary>
public sealed class WorldHost(
    WorldRuntime world,
    CharacterSaveQueue saveQueue,
    CharacterDirectory directory,
    IServiceScopeFactory scopes,
    ILogger<WorldHost> logger,
    IOptions<CharacterFencingOptions>? fencingOptions = null,
    IHostApplicationLifetime? applicationLifetime = null,
    CharacterOwnershipContext? ownershipContext = null,
    WorldWriterGuard? writerGuard = null,
    IOptions<WorldOptions>? worldOptions = null,
    IServiceProvider? rootServices = null,
    IOptions<Cluster.Gateway.GatewayWorkerOptions>? workerOptions = null,
    Cluster.Gateway.WorkerAdmission? admission = null,
    ShutdownDrain? drain = null,
    Cluster.Social.ClusterRoleFeatureFilter? roleFilter = null,
    Cluster.Social.RealmAuthorityLeaseKeeper? leaseKeeper = null,
    IOptions<Cluster.Social.ClusterNodeOptions>? nodeOptions = null) : IHostedService
{
    private readonly CharacterFencingOptions _fencing = fencingOptions?.Value ?? new();
    private readonly Cluster.Gateway.GatewayWorkerOptions? _worker = workerOptions?.Value;
    private readonly Lock _promoteGate = new();
    private volatile bool _standby = workerOptions?.Value is { Enabled: true, Standby: true };
    private Task? _promotion;
    private volatile bool _standbyStarted;
    private volatile bool _promotionCompleted;

    /// <summary>Whether a listener will bind <c>World:Port</c> after this host (test seam); null looks for a registered <see cref="Net.WorldServer"/>.</summary>
    internal bool? ListenerExpected { get; init; }
    private readonly IHostApplicationLifetime? _applicationLifetime = applicationLifetime;
    private readonly Guid _incarnation = Guid.NewGuid();
    private CharacterOwnershipToken? _ownership;
    private bool _watchClaims;
    private CancellationTokenSource? _claimWatchStop;
    private Task _claimWatch = Task.CompletedTask;
    private CancellationTokenSource? _heartbeatStop;
    private Task _heartbeat = Task.CompletedTask;
    private readonly Lock _stopGate = new();
    private Task? _stop;
    private volatile bool _attached; // the features were attached (StartAsync): from then on a stop has a world, features and a save queue to stop

    /// <summary>How often a world running with fencing off looks for a claim (test seam); null uses <see cref="CharacterFencingOptions.ClaimWatchIntervalSeconds"/>.</summary>
    internal TimeSpan? ClaimWatchInterval { get; init; }

    /// <summary>How often a Worker-scope world records its heartbeat (test seam); null uses <see cref="CharacterFencingOptions.HeartbeatSeconds"/>.</summary>
    internal TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>Whether this world claims a <c>worker:&lt;shard&gt;</c> ownership row (<c>Cluster:Fencing:Scope=Worker</c>) instead of the realm-wide <c>world</c> row.</summary>
    private bool IsWorkerScope => _fencing.Scope == CharacterFencingScope.Worker;

    /// <summary>The durable scope this world claims: <c>world</c>, or <c>worker:&lt;shard&gt;</c> (shard defaults to <c>Cluster:Worker:NodeId</c>).</summary>
    public string OwnershipScope => _fencing.ResolveScope(_worker?.NodeId ?? string.Empty);

    /// <summary>Runs where the playerbot restore runs, after the world and the save queue have started, with the start's token (test seam).</summary>
    internal Func<CancellationToken, Task>? StartupWorkForTesting { get; init; }

    /// <summary>
    /// True while this world is a standby (Cluster:Worker:Standby, Tier 3b) that was not promoted: the process is up, its container (content
    /// catalogs, maps, caches, the features themselves) is built and its schema validated, and it is linked to the gateway; but it holds no characters
    /// ownership, admits no session, and has not attached its features or started the simulation. Those read the characters database, which the
    /// active world is still writing (state read now would be stale at the handoff) and whose fence refuses a non-owner's transactions, reads
    /// included; <see cref="PromoteAsync"/> runs them under the taken-over ownership.
    /// </summary>
    public bool IsStandby => _standby;

    /// <summary>
    /// True once <see cref="PromoteAsync"/> has finished: the features are attached, the simulation runs and admissions are open. <see cref="IsStandby"/> turns false earlier (at the ownership
    /// takeover, so Describe reports the promotion at once), but the features the transfer participants are built over are not attached until this is true.
    /// </summary>
    public bool PromotionCompleted => _promotionCompleted;

    /// <summary>This process's ownership incarnation: the one a handoff's epoch takeover hands the characters ownership to.</summary>
    public Guid Incarnation => _incarnation;

    /// <summary>
    /// Worker scope with a valid explicit Cluster:Worker:OwnedMaps: the maps this world serves, which a handoff's epoch takeover writes to the shard's ownership row
    /// (the standby serves its configured maps, so the durable row must say the same). Null in Realm scope or when the list is not usable (StartAsync refuses that).
    /// </summary>
    public IReadOnlyCollection<uint>? WorkerOwnedMaps
        => IsWorkerScope && _worker is not null && CharacterFencingOptions.TryGetOwnedMaps(_worker, out IReadOnlyCollection<uint> maps, out _) ? maps : null;

    /// <summary>The durable owner node name this world claims under (Cluster:Fencing:OwnerNode).</summary>
    public string OwnerNode => _fencing.OwnerNode;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _fencing.Validate();
        if (_worker is { Enabled: false, Standby: true })
            throw new InvalidOperationException("Cluster:Worker:Standby requires Cluster:Worker:Enabled = true: a standby world is a gateway worker");
        if (_worker is { Enabled: true })
        {
            if (!_worker.TryGetOwnership(out MapOwnership ownedMaps, out string? ownedError))
                throw new InvalidOperationException($"Cluster:Worker:OwnedMaps is not a map list: {ownedError}");
            world.Ownership = ownedMaps;
            if (!ownedMaps.OwnsEverything)
                logger.LogInformation("Worker owns maps: {Maps}", ownedMaps);
        }

        _fencing.ValidateWorkerScope(_worker, nodeOptions?.Value.Role ?? Cluster.Social.ClusterNodeRole.Combined);

        bool acquiredHere = false;
        try
        {
            // Before any database row is read or written: a second world on the same characters file, or on a taken port, must fail here
            // untouched (the 2026-10 incident restored 863 bots from a process that then failed to bind).
            // Normally already held since WorldStartup took it before the schema initializers (then this is a no-op and the lock stays Program's to release).
            // A standby is not a writer: the active world holds the lock and the ownership, and the standby has no listener of its own.
            if (!_standby)
            {
                acquiredHere = writerGuard?.Acquire() == true;
                ProbeListenPort();
                await AcquireOwnershipAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                logger.LogInformation("Standby world: no ownership claimed, no session admitted, after-start work deferred until promotion");
            }
        }
        catch
        {
            // The host never calls StopAsync for a failed start, except that WorldStartup.RunAsync stops the host after a port-in-use refusal (that stop takes the not-attached branch and does nothing): give back only a lock this start took itself. A lock WorldStartup took stays held
            // until the container disposes it (after the hosted services stop), which in production is the end of the process.
            if (acquiredHere) writerGuard?.Dispose();
            throw;
        }

        saveQueue.FatalFailure += OnFatalSaveFailure;
        if (!_standby && IsWorkerScope && _ownership is { } claimed)
            StartHeartbeat(claimed);
        if (_standby)
        {
            await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
                _ = scope.ServiceProvider.GetServices<IWorldFeature>().Count(); // build the features (singletons) now: promotion only attaches them
            _standbyStarted = true;
            return;
        }

        await AttachAndStartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AttachAndStartAsync(CancellationToken cancellationToken)
    {
        // Started right after the startup claim check, so a claim that lands while the world is still starting (registry deletes, bot restore)
        // is seen within one interval. If the rest of start throws, the host never calls StopAsync, so the watch is stopped here.
        if (!_fencing.Enabled && _watchClaims)
        {
            _claimWatchStop = new CancellationTokenSource();
            _claimWatch = Task.Run(() => WatchForClaimAsync(_claimWatchStop.Token));
        }

        try
        {
            await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
            {
                IReadOnlyList<CharacterIdentity> identities = await scope.ServiceProvider
                    .GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync(cancellationToken).ConfigureAwait(false);
                directory.Load(identities);

                // A node with a cluster role (Cluster:Node:Role other than Combined) first takes the realm singleton leases it can get (before the item load below, whose orphaned-loot sweep runs only for the maintenance holder), then attaches
                // only the features its role runs (the auction bot and AutoBroadcast run only while it holds their lease: a map worker attaches them dormant and they wake on a takeover). Combined attaches everything, as always.
                Func<IWorldFeature, bool>? admit = null;
                Cluster.Social.ClusterNodeRole role = nodeOptions?.Value.Role ?? Cluster.Social.ClusterNodeRole.Combined;
                if (role != Cluster.Social.ClusterNodeRole.Combined && roleFilter is not null && leaseKeeper is not null)
                {
                    await leaseKeeper.AcquireAtStartAsync(_incarnation, cancellationToken).ConfigureAwait(false);
                    // The realm singletons are attached on every map worker, dormant until the node holds their lease (at start or by a run-time takeover).
                    admit = feature => Cluster.Social.ClusterRoleFeatureFilter.AttachesDormant(feature.GetType(), role)
                        || roleFilter.ShouldAttachFeature(feature.GetType(), role, leaseKeeper.NodeId, leaseKeeper.TokenFor, leaseKeeper.UtcNow, _incarnation);
                }

                // Item content before any feature attaches: creatures built while the features attach (script and world-state spawns, the first
                // grid spawns) resolve their creature_equip_template items through it, and an unloaded store would warn and leave the slots empty.
                // A failed load is not fatal here: it is not cached, the next request (a login) retries and fails closed itself.
                if (scope.ServiceProvider.GetService<Items.ItemsFeature>() is { } itemsFeature)
                {
                    try
                    {
                        await itemsFeature.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        logger.LogWarning(ex, "Item templates could not be loaded at startup; they load on first use and creature equipment resolves after that");
                    }
                }

                // The durable id allocators are primed BEFORE the features attach (writer-lock design section 3, point 5): an id taken while a feature attaches
                // must come from a reserved block, not from the seeded-from-max counter, which can collide with a block another worker reserved but has not written yet.
                // Allocators of features that only exist once attached (social, instances) are primed now and handed over right after the attach. A failure here
                // happens before _attached is set, so the start's catch gives the leases back.
                Action? handOverIdAllocators = null;
                if (_fencing.Enabled && scope.ServiceProvider.GetService<IIdRangeReservationStore>() is not null)
                {
                    handOverIdAllocators = await PrepareDurableIdAllocatorsAsync(scope.ServiceProvider, scopes, cancellationToken: cancellationToken, requireDurableIds: IsWorkerScope)
                        .ConfigureAwait(false);
                }
                else if (IsWorkerScope)
                {
                    throw new InvalidOperationException("Cluster:Fencing:Scope=Worker needs the durable id range reservation store: two workers on one database would otherwise hand out the same ids");
                }

                // Features are singletons, so the scope hands out the same instances. Set first: a feature that attached before one that threw must still stop.
                _attached = true;
                scope.ServiceProvider.AttachWorldFeatures(world, admit);
                handOverIdAllocators?.Invoke();
            }

            logger.LogInformation("Loaded {Count} character names", directory.Count);
            saveQueue.Start();
            world.Start();
            if (nodeOptions?.Value.Role is { } startedRole && startedRole != Cluster.Social.ClusterNodeRole.Combined && roleFilter is not null && leaseKeeper is not null)
            {
                LogNodeRole(startedRole, roleFilter, leaseKeeper);
            }

            await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
            {
                if (StartupWorkForTesting is { } startupWork)
                    await startupWork(cancellationToken).ConfigureAwait(false);
                // The features' after-start work (the playerbot restore), registered by the features themselves (docs/design/hot-reload-tiers.md, section 4).
                // A standby runs all of this when it is promoted (PromoteAsync), once it owns the characters database.
                if (scope.ServiceProvider.GetService<WorldLifecycleHooks>() is { } hooks)
                    await RunLifecycleHooksAsync(hooks, WorldLifecycleStage.AfterWorldStart, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A stop requested while the bots are restored (Ctrl-C, StopApplication) cancels this start; WorldStartup turns it into an orderly stop.
            await StopClaimWatchAsync().ConfigureAwait(false);
            await ReleaseLeasesAfterFailedStartAsync().ConfigureAwait(false);
            if (_attached) await StopAfterFailedStartAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-host.start", ex);
            await StopClaimWatchAsync().ConfigureAwait(false);
            await ReleaseLeasesAfterFailedStartAsync().ConfigureAwait(false);
            if (_attached) await StopAfterFailedStartAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A start that failed before the features attached still holds the leases it acquired: give them back. Once features are attached the leases stay held
    /// (their singletons, such as the auction bot timer, may be running) until <see cref="StopCoreAsync"/> has stopped the features and releases them itself.
    /// </summary>
    private async Task ReleaseLeasesAfterFailedStartAsync()
    {
        if (!_attached && leaseKeeper is not null)
        {
            await leaseKeeper.ReleaseAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>One start line for a node with a cluster role: which realm singletons run here and which node holds the others.</summary>
    private void LogNodeRole(Cluster.Social.ClusterNodeRole role, Cluster.Social.ClusterRoleFeatureFilter filter, Cluster.Social.RealmAuthorityLeaseKeeper keeper)
    {
        string Describe(Type feature)
        {
            string scope = Cluster.Social.ClusterRoleFeatureFilter.LeaseScopeFor(feature)!;
            return keeper.TokenFor(scope) is { } token ? $"{feature.Name} attached here (token {token})" : $"{feature.Name} dormant (lease held by another node; it starts here if that node's lease lapses)";
        }

        string instances = keeper.TokenFor(ArcaneCore.Kernel.Cluster.Social.RealmAuthorityScopes.Instances) is { } instancesToken
            ? $"instance raid reset times written here (token {instancesToken})"
            : "instance raid reset times written by another node";
        logger.LogInformation("Cluster node role {Role} as {Node}: {AutoBroadcast}; {AuctionBot}; {Instances}", role, keeper.NodeId,
            Describe(typeof(ServerMail.AutoBroadcastFeature)), Describe(typeof(Economy.AuctionBotFeature)), instances);
    }

    /// <summary>
    /// Promote a standby world (Tier 3b) after a handoff's epoch takeover handed it the characters ownership: <paramref name="takenOver"/> must be the
    /// ownership the database holds now, under this world's <see cref="Incarnation"/>, or nothing changes. Then the token becomes the world's and the
    /// write fence's, new sessions are admitted, and the deferred after-start work (the playerbot restore) runs. Runs once; a second call returns the
    /// first one's task, except that a refused promotion (the world is still a standby) may be retried. A world that is not a standby refuses.
    /// </summary>
    public Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(takenOver);
        lock (_promoteGate)
        {
            // A refused promotion (wrong token, nothing changed: still a standby) does not use up the one promotion.
            if (_promotion is { IsCompleted: true, IsCompletedSuccessfully: false } && _standby)
                _promotion = null;
            if (_promotion is null && !_standby)
                throw new InvalidOperationException("this world is not a standby; only a standby world is promoted");
            return _promotion ??= PromoteCoreAsync(takenOver, cancellationToken);
        }
    }

    private async Task PromoteCoreAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken)
    {
        if (!_standbyStarted)
            throw new InvalidOperationException("the standby world has not started; it cannot be promoted");
        string ownershipScope = OwnershipScope;
        if (takenOver.Scope != ownershipScope || takenOver.Incarnation != _incarnation)
            throw new InvalidOperationException("the ownership to promote under was not handed to this world's incarnation");

        // The process-lifetime writer lock first (SQLite sidecar file, or the MariaDB/MySQL/PostgreSQL advisory lock), before the ownership row is read: while the old
        // world is alive it still holds it, and the promotion is refused with the standby untouched (nothing held, still a standby, retryable).
        bool lockedHere;
        try { lockedHere = writerGuard?.AcquireForPromotion() == true; }
        catch (Data.Characters.Ownership.CharacterDatabaseAlreadyOwnedException ex)
        {
            throw new InvalidOperationException($"the characters database writer lock is held elsewhere (the old world is still running); refusing to promote, the standby stays a standby: {ex.Message}", ex);
        }
        catch (WorkerScopeConfigurationException ex)
        {
            // Worker scope misconfigured (an invalid shard id, or SQLite without AllowSqliteWorkerScope): nothing is held; the standby stays a standby, as for the other lock failures.
            throw new InvalidOperationException($"the worker shard writer lock cannot be taken with this configuration ({ex.Message}); refusing to promote, the standby stays a standby", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DbException)
        {
            // Not contention: the lock could not be created, or the database server could not be asked for it. Nothing is held; the standby stays a standby.
            throw new InvalidOperationException($"the characters database writer lock could not be taken ({ex.GetType().Name}: {ex.Message}); refusing to promote, the standby stays a standby", ex);
        }

        try
        {
            // Worker scope: the realm lock is probed after the shard lock is held (a worker never holds the realm lock); a holder is a Combined world.
            if (IsWorkerScope && writerGuard is not null && !writerGuard.ProbeRealmFree())
                throw new InvalidOperationException("a Combined world holds the characters database realm writer lock; refusing to promote a worker shard beside it, the standby stays a standby");
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ICharacterOwnershipStore store = scope.ServiceProvider.GetService<ICharacterOwnershipStore>()
                ?? throw new InvalidOperationException("no characters ownership store is registered; a standby cannot be promoted");
            // Only reads the row: the token must be the database's current active one. Never TryAcquireAsync here, which would claim a released
            // (inactive) row at a new epoch outside the handoff journal and leave it claimed by a standby whose refusal then holds no token.
            CharacterOwnershipToken? current = await store.GetCurrentAsync(ownershipScope, cancellationToken).ConfigureAwait(false);
            if (current != takenOver)
                throw new InvalidOperationException("the characters database is not owned by this standby under the promoted token; refusing to promote");
        }
        catch
        {
            if (lockedHere) writerGuard?.Dispose(); // a refused promotion holds nothing
            throw;
        }

        _ownership = takenOver;
        world.CharacterOwnership = takenOver;
        if (ownershipContext is not null)
            ownershipContext.Current = takenOver;
        _standby = false;
        if (IsWorkerScope)
            StartHeartbeat(takenOver);
        logger.LogInformation("Standby world promoted at ownership epoch {Epoch}: attaching the features and starting the simulation", takenOver.Epoch);

        // As an active world's start does after its ownership claim: load the characters, attach, start, run the after-start work. A failure here
        // leaves a promoted world that owns the database but does not run: the host is stopped (as for a failed start), never re-opened as a standby.
        await AttachAndStartAsync(cancellationToken).ConfigureAwait(false);
        admission?.Open();
        _promotionCompleted = true;
    }

    /// <summary>
    /// The host never calls <see cref="StopAsync"/> for a start that threw, and <c>Host.RunAsync</c> disposes the container right after. Once the features
    /// are attached the world thread, the save queue and the restored bots may be running, so they are stopped here exactly as <see cref="StopAsync"/> stops
    /// them and every queued character snapshot is written while the container is still alive (2026-10-16: a Ctrl-C during the bot restore left the world
    /// thread and the save queue running into a disposed provider, and the queued snapshots were lost). A failure of this stop is logged and recorded by
    /// <see cref="StopAsync"/>; the caller still sees the start's own exception.
    /// </summary>
    private async Task StopAfterFailedStartAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world.host", ex);
            logger.LogError(ex, "The world stop after a failed start did not complete");
        }
    }

    /// <summary>
    /// Configures and primes durable block-prefetch ID allocators for attached world features using
    /// <see cref="IIdRangeReservationStore"/> so runtime allocations never block the world thread on I/O.
    /// </summary>
    public static async Task PrimeDurableIdAllocatorsAsync(
        IServiceProvider services,
        IServiceScopeFactory scopes,
        uint blockSize = 1024,
        uint lowWatermark = 256,
        CancellationToken cancellationToken = default,
        bool requireDurableIds = false)
        => (await PrepareDurableIdAllocatorsAsync(services, scopes, blockSize, lowWatermark, cancellationToken, requireDurableIds).ConfigureAwait(false)).Invoke();

    /// <summary>
    /// <see cref="PrimeDurableIdAllocatorsAsync"/> split in two so the priming can run before the features attach: this primes every allocator and hands over, immediately,
    /// the ones whose feature exists before it attaches (items, pets, GM tickets, economy; the economy's <c>Enabled</c> is set inside its attach, so it is not a condition).
    /// The returned action hands over the allocators of state that exists only once attached (groups, guilds, petitions, instances); run it right after the attach.
    /// </summary>
    internal static async Task<Action> PrepareDurableIdAllocatorsAsync(
        IServiceProvider services,
        IServiceScopeFactory scopes,
        uint blockSize = 1024,
        uint lowWatermark = 256,
        CancellationToken cancellationToken = default,
        bool requireDurableIds = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scopes);
        Action handOver = static () => { };

        Func<string, uint, CancellationToken, Task<IdRangeBlock>> reserveAsync = async (scopeName, count, ct) =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IIdRangeReservationStore store = scope.ServiceProvider.GetRequiredService<IIdRangeReservationStore>();
            return await store.ReserveBlockAsync(scopeName, count, ct).ConfigureAwait(false);
        };

        if (services.GetService<Items.ItemsFeature>() is { } items)
        {
            items.GuidAllocator.RequireDurableIds = requireDurableIds;
            DurableIdBlockAllocator itemAlloc = items.GuidAllocator.ConfigureBlockReservation(reserveAsync, blockSize, lowWatermark);
            await itemAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
        }

        // Pet numbers, GM tickets and petitions: realm-unique across workers (docs: writer-lock design, section 3). Their rows are inserted
        // by the reservation store on first use, so no released schema module changes.
        if ((services.GetService<Pets.PetsFeature>()?.Service ?? services.GetService<Game.Pets.SummonService>()) is { } pets)
        {
            var petAlloc = new DurableIdBlockAllocator(IdRangeScopes.Pet, blockSize, lowWatermark, reserveAsync);
            await petAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            pets.IdBlockAllocator = petAlloc;
            pets.RequireDurableIds = requireDurableIds;
        }

        if (services.GetService<Gm.Audit.GmAuditFeature>() is { } audit)
        {
            var ticketAlloc = new DurableIdBlockAllocator(IdRangeScopes.GmTicket, blockSize, lowWatermark, reserveAsync);
            await ticketAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            audit.TicketIdAllocator = ticketAlloc;
            audit.RequireDurableIds = requireDurableIds;
        }

        if (services.GetService<Social.SocialFeature>() is { } social)
        {
            var groupAlloc = new DurableIdBlockAllocator(IdRangeScopes.Group, blockSize, lowWatermark, reserveAsync);
            await groupAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);

            var guildAlloc = new DurableIdBlockAllocator(IdRangeScopes.Guild, blockSize, lowWatermark, reserveAsync);
            await guildAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);

            var petitionAlloc = new DurableIdBlockAllocator(IdRangeScopes.Petition, blockSize, lowWatermark, reserveAsync);
            await petitionAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            handOver += () =>
            {
                social.Context.Groups.IdBlockAllocator = groupAlloc;
                social.Context.Groups.RequireDurableIds = requireDurableIds;
                social.Context.Guilds.IdBlockAllocator = guildAlloc;
                social.Context.Guilds.RequireDurableIds = requireDurableIds;
                social.Context.Petitions.IdBlockAllocator = petitionAlloc;
                social.Context.Petitions.RequireDurableIds = requireDurableIds;
            };
        }

        if (services.GetService<Instances.InstanceFeature>() is { } instances)
        {
            var instanceAlloc = new DurableIdBlockAllocator(IdRangeScopes.Instance, blockSize, lowWatermark, reserveAsync);
            await instanceAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            handOver += () =>
            {
                instances.Instances.IdBlockAllocator = instanceAlloc;
                instances.Instances.RequireDurableIds = requireDurableIds;
            };
        }

        if (services.GetService<Economy.EconomyFeature>() is { } economy)
        {
            var mailAlloc = new DurableIdBlockAllocator(IdRangeScopes.Mail, blockSize, lowWatermark, reserveAsync);
            await mailAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            economy.MailIdAllocator = mailAlloc;
            economy.RequireDurableIds = requireDurableIds;

            var auctionAlloc = new DurableIdBlockAllocator(IdRangeScopes.Auction, blockSize, lowWatermark, reserveAsync);
            await auctionAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            economy.AuctionIdAllocator = auctionAlloc;

            var textAlloc = new DurableIdBlockAllocator(IdRangeScopes.ItemText, blockSize, lowWatermark, reserveAsync);
            await textAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            economy.ItemTextIdAllocator = textAlloc;
        }

        return handOver;
    }

    /// <summary>
    /// Run the hooks of <paramref name="stage"/> and throw what a direct call of the one hook would have thrown: a cancellation as an
    /// <see cref="OperationCanceledException"/> (the start is then stopped in order), a single failure as itself, several together.
    /// </summary>
    internal static async Task RunLifecycleHooksAsync(WorldLifecycleHooks hooks, WorldLifecycleStage stage, CancellationToken cancellationToken)
    {
        try
        {
            await hooks.RunAsync(stage, cancellationToken).ConfigureAwait(false);
        }
        catch (AggregateException aggregate)
        {
            FaultLedger.Shared.Record("world.host", aggregate);
            IReadOnlyList<Exception> inner = aggregate.InnerExceptions;
            if (cancellationToken.IsCancellationRequested && inner.All(e => e is OperationCanceledException))
                throw new OperationCanceledException("the world start was cancelled", aggregate, cancellationToken);
            if (inner.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner[0]).Throw();
            throw;
        }
    }

    private async Task StopClaimWatchAsync()
    {
        await StopHeartbeatAsync().ConfigureAwait(false);
        if (_claimWatchStop is not { } watchStop) return;
        await watchStop.CancelAsync().ConfigureAwait(false);
        await _claimWatch.ConfigureAwait(false);
        watchStop.Dispose();
        _claimWatchStop = null;
    }

    /// <summary>
    /// Stop the bots, the world thread and the features, drain the save queue, then give back the ownership. Runs once: a start that failed has already
    /// stopped the world itself (<see cref="StopAfterFailedStartAsync"/>), and the host still stops it when the stop was requested during start
    /// (<see cref="WorldStartup.RunAsync"/>) or the start was refused because its port is in use (nothing was attached, so that stop does nothing); later calls return the first stop's task.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopGate)
        {
            return _stop ??= StopCoreAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        await StopClaimWatchAsync().ConfigureAwait(false);

        if (!_attached)
        {
            // Stopped before the start got as far as the features (a stop requested early in the host's start): nothing runs and nothing was written,
            // so only a durable ownership this start took is given back. The features are never stopped without having been attached.
            await ReleaseOwnershipAsync(cancellationToken, failures).ConfigureAwait(false);
            if (failures.Count > 0) throw new AggregateException("World stop before start failed", failures);
            logger.LogInformation("World stopped before it started");
            return;
        }

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            // The features' before-stop work (the playerbot shutdown, then the managed bots no attached feature stopped), newest first.
            if (scope.ServiceProvider.GetService<WorldLifecycleHooks>() is { } hooks)
                await RunLifecycleHooksAsync(hooks, WorldLifecycleStage.BeforeWorldStop, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-host.stop-playerbots", ex);
            failures.Add(ex);
        }
        finally { world.Stop(); }
        drain?.Features.Begin(); // the feature write queues share this stall-only lane; the save queue's lane is begun separately below
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.StopWorldFeaturesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-host.stop-features", ex);
            failures.Add(ex);
        }

        // After the singletons stopped: another node may take the leases now. Never throws; a lease that cannot be released expires by itself.
        if (leaseKeeper is not null) await leaseKeeper.ReleaseAllAsync().ConfigureAwait(false);

        ReportStopDrain(drain?.Features);
        drain?.Saves.Begin();
        bool queueStopped = false;
        try
        {
            await saveQueue.StopAsync().ConfigureAwait(false);
            queueStopped = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-host.stop-save-queue", ex);
            failures.Add(ex);
        }

        ReportStopDrain(drain?.Saves);

        if (failures.Count == 0 && queueStopped)
        {
            await ReleaseOwnershipAsync(cancellationToken, failures).ConfigureAwait(false);
        }

        // Sessions may still hold open packet traces: stop them and wait (bounded) so their records reach the disk before the process ends.
        // Captures are diagnostics, so this comes after the player saves (a stalled capture disk must not delay them) and the ownership release.
        await Task.Run(() => Net.PacketCapture.DrainAll(TimeSpan.FromSeconds(10), logger)).ConfigureAwait(false);

        // The file lock is not released here: the SQLite checkpointer's final WAL checkpoint runs after this host stops, so the guard is
        // disposed with the host container (Program) or the process, after every hosted service has stopped.
        if (failures.Count == 0) logger.LogInformation("World saved and stopped");
        else logger.LogError("World stop did not complete; durable ownership was retained");
        if (failures.Count > 0) throw new AggregateException("World feature shutdown failed", failures);
    }

    /// <summary>Logs how a shutdown stall lane ended. A refusal is recorded once per refusing queue (its stop report); the host adds no second ledger entry.</summary>
    private void ReportStopDrain(StopDrainBudget? lane)
    {
        if (lane is not null)
        {
            logger.LogInformation("{Drain}", lane.Describe());
        }
    }

    /// <summary>
    /// The listener binds after this host starts (registration order), and the bot restore runs inside this start. A port clash is found here, before any bot is taken over:
    /// bind and release the configured endpoint. This is check-then-bind: it narrows the window, it does not close it (the real bind is still <see cref="Net.WorldServer"/>'s).
    /// Skipped without world options, with port 0 (any free port), and when no <see cref="Net.WorldServer"/> is registered (a fixture that owns the sockets itself binds
    /// before this host starts, so the probe would see its own listener).
    /// </summary>
    private void ProbeListenPort()
    {
        bool expected = ListenerExpected
            ?? rootServices?.GetServices<IHostedService>().OfType<Net.WorldServer>().Any() == true;
        if (!expected || worldOptions?.Value is not { Port: > 0 } options || !IPAddress.TryParse(options.BindAddress, out IPAddress? address)) return;
        var probe = new TcpListener(address, options.Port);
        try { probe.Start(); }
        catch (SocketException ex)
        {
            // Any failed bind ends the start here, before the ownership claim and the bot restore: a clash is "in use", anything else (an address this host does not have, a privileged port) is reported as itself.
            throw Net.WorldBindFailedException.For("world port", $"{address}:{options.Port}", ex, "refusing to start before any playerbot is restored");
        }
        finally { probe.Stop(); }
    }

    private async Task AcquireOwnershipAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        ICharacterOwnershipStore? store = scope.ServiceProvider.GetService<ICharacterOwnershipStore>();
        if (store is null)
        {
            if (_fencing.Enabled)
                throw new InvalidOperationException("character fencing is enabled but no ownership store is registered");
            return;
        }

        if (!_fencing.Enabled)
        {
            _watchClaims = true;
            // Any active row (the world row or a worker shard), not just 'world': an unfenced world must not start beside a fenced worker either.
            if (await store.IsAnyOwnedAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("the characters database is already owned; disabled fencing cannot start a duplicate world writer");
            return;
        }

        if (IsWorkerScope)
        {
            _ownership = await ClaimWorkerScopeAsync(store, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ownership = _fencing is { HasTakeover: true, TakeoverEpoch: { } takeoverEpoch, TakeoverIncarnation: { } takeoverIncarnation }
                ? await store.TryTakeoverAsync(new CharacterOwnershipToken("world", takeoverEpoch,
                    takeoverIncarnation), _fencing.OwnerNode, _incarnation, cancellationToken).ConfigureAwait(false)
                : await store.TryAcquireAsync("world", _fencing.OwnerNode, _incarnation, cancellationToken).ConfigureAwait(false);
            if (_ownership is null)
                throw new InvalidOperationException("the characters database is already owned by another world authority");
        }

        world.CharacterOwnership = _ownership;
        if (ownershipContext is not null)
            ownershipContext.Current = _ownership; // the ambient token the characters write fence verifies every unaware store's transaction against
    }

    /// <summary>
    /// The Worker-scope claim (design _wf/writer-lock-DESIGN.md sections 1 and 5): this process already holds the shard's advisory lock (<see cref="WorldWriterGuard"/>).
    /// Claim the <c>worker:&lt;shard&gt;</c> row with this worker's owned maps (an explicit takeover when configured); if the row is still active under another incarnation
    /// and this process holds the shard lock, that incarnation is dead (the server released its lock), so the row is reclaimed at epoch+1 (the old token is then refused
    /// by the fence). Only after the claim committed is the realm lock probed: a holder is a Combined world, and the claim is released again. Each side of the
    /// Combined/Worker exclusion writes first and checks second, so one of them always sees the other.
    /// </summary>
    private async Task<CharacterOwnershipToken> ClaimWorkerScopeAsync(ICharacterOwnershipStore store, CancellationToken cancellationToken)
    {
        string scope = OwnershipScope;
        if (!CharacterFencingOptions.TryGetOwnedMaps(_worker!, out IReadOnlyCollection<uint> maps, out string? mapsError))
            throw new WorkerScopeConfigurationException($"Cluster:Fencing:Scope=Worker: {mapsError}");

        CharacterOwnershipToken? token;
        if (_fencing is { HasTakeover: true, TakeoverEpoch: { } takeoverEpoch, TakeoverIncarnation: { } takeoverIncarnation })
        {
            // The takeover also writes this worker's owned maps: a handed-over shard must not keep the old owner's map set.
            token = await store.TryTakeoverAsync(new CharacterOwnershipToken(scope, takeoverEpoch, takeoverIncarnation), _fencing.OwnerNode, _incarnation, maps, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            token = await store.TryAcquireAsync(scope, _fencing.OwnerNode, _incarnation, maps, cancellationToken).ConfigureAwait(false);
            if (token is null)
                token = await TryReclaimDeadIncarnationAsync(store, scope, maps, cancellationToken).ConfigureAwait(false);
        }

        if (token is null)
            throw new InvalidOperationException($"the worker scope '{scope}' could not be claimed: a live world owns the same shard, its maps overlap another active worker, or a Combined world owns the characters database");

        bool realmFree;
        try
        {
            realmFree = writerGuard is null || writerGuard.ProbeRealmFree();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The probe itself failed (the server is unreachable): the claim just written must not stay active, so the same contract holds as for a held lock.
            await ReleaseWorkerClaimAsync(store, token, scope).ConfigureAwait(false);
            logger.LogError(ex, "The realm writer lock could not be probed for the worker scope '{Scope}'; the claim was released", scope);
            throw;
        }

        if (!realmFree)
        {
            await ReleaseWorkerClaimAsync(store, token, scope).ConfigureAwait(false);
            throw new InvalidOperationException($"a Combined world holds the characters database realm writer lock; the worker scope '{scope}' claim was released and this worker does not start");
        }

        return token;
    }

    private async Task ReleaseWorkerClaimAsync(ICharacterOwnershipStore store, CharacterOwnershipToken token, string scope)
    {
        try
        {
            await store.ReleaseAsync(token, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-host.release-ownership", ex);
            logger.LogError(ex, "The worker scope '{Scope}' claim could not be released after the realm writer lock check", scope);
        }
    }

    /// <summary>
    /// A restart after a hard kill: the shard row is still active under the dead incarnation. Holding the shard lock is the proof that process is gone, so the row is
    /// reclaimed (CAS on the observed epoch and incarnation). Null when there is nothing to reclaim, the lock is not held (no proof), or the CAS lost a race.
    /// </summary>
    private async Task<CharacterOwnershipToken?> TryReclaimDeadIncarnationAsync(ICharacterOwnershipStore store, string scope, IReadOnlyCollection<uint> maps, CancellationToken cancellationToken)
    {
        if (writerGuard is not { HoldsLock: true })
            return null;
        CharacterOwnershipToken? current = await store.GetCurrentAsync(scope, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Incarnation == _incarnation)
            return null;
        await RefuseReclaimDuringCommittedHandoffAsync(scope, cancellationToken).ConfigureAwait(false);
        CharacterOwnershipToken? reclaimed = await store.TryReclaimAsync(current, _fencing.OwnerNode, _incarnation, maps, cancellationToken).ConfigureAwait(false);
        if (reclaimed is not null)
            logger.LogWarning("Reclaimed the worker scope '{Scope}' from the dead incarnation {OldIncarnation} (epoch {OldEpoch}) at epoch {Epoch}: its writer lock was free, so that process is gone", scope, current.Incarnation, current.Epoch, reclaimed.Epoch);
        return reclaimed;
    }

    /// <summary>
    /// The reclaim guard (handoff-control design, commit C12). Between the epoch takeover and the promotion of the standby, the shard row already names the standby's incarnation but the
    /// old process may still be dead or dying: a relaunched copy of it that holds the shard lock would otherwise read that row as a dead incarnation and take the shard back from the
    /// handoff's target. A committed, unresolved handoff of this scope whose target is another incarnation therefore refuses the reclaim; recovery rolls that handoff forward. Handoffs that are
    /// resolved, aborted, still pre-commit (they never changed the row) or that target this very incarnation do not block. A world without a handoff journal has nothing to consult, and neither has one whose characters database
    /// predates the journal (no <c>cluster_world_handoff</c> table, see <see cref="IsMissingHandoffTable"/>): that is no committed handoff, not a failure.
    /// </summary>
    private async Task RefuseReclaimDuringCommittedHandoffAsync(string scope, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope serviceScope = scopes.CreateAsyncScope();
        if (serviceScope.ServiceProvider.GetService<IWorldHandoffStore>() is not { } journal)
            return;
        IReadOnlyList<WorldHandoffRecord> unresolved;
        try
        {
            unresolved = await journal.GetUnresolvedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingHandoffTable(ex))
        {
            logger.LogInformation("The characters database has no cluster_world_handoff table (an older schema): no committed handoff to guard the reclaim of '{Scope}' against", scope);
            return;
        }

        foreach (WorldHandoffRecord record in unresolved)
        {
            if (record.DecisionState != PlayerTransferDecisionState.Committed
                || record.SourceOwnershipToken.Scope != scope
                || record.TargetOwnershipToken?.Incarnation == _incarnation)
                continue;
            logger.LogWarning("Refusing to reclaim the worker scope '{Scope}': handoff {HandoffId} is committed to another incarnation ({TargetIncarnation}) and not yet resolved", scope, record.HandoffId, record.TargetOwnershipToken?.Incarnation);
            throw new InvalidOperationException($"the worker scope '{scope}' belongs to the committed handoff {record.HandoffId}, which has not been promoted yet; this world cannot reclaim it until recovery resolves that handoff");
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> (or an exception it wraps) is the database saying the <c>cluster_world_handoff</c> table does not exist: SQLite "no such table", MySQL/MariaDB error 1146 "doesn't exist",
    /// PostgreSQL 42P01 "does not exist". Any other failure is a real one and propagates: a guard that fails open on an unreachable journal would let a relaunched copy take the shard back.
    /// </summary>
    internal static bool IsMissingHandoffTable(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException
                && e.Message.Contains("cluster_world_handoff", StringComparison.OrdinalIgnoreCase)
                && (e.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
                    || e.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>Starts recording this worker's heartbeat in its <c>character_ownership_heartbeat</c> row every <see cref="CharacterFencingOptions.HeartbeatSeconds"/> (Worker scope only). A heartbeat the row refuses means the shard was reclaimed or reaped: the save queue goes terminal and the host stops.</summary>
    private void StartHeartbeat(CharacterOwnershipToken token)
    {
        // Not synchronised: start, promote and stop are sequenced by the host lifecycle (StopHeartbeatAsync does its exchange only after both have returned).
        if (_heartbeatStop is not null) return;
        _heartbeatStop = new CancellationTokenSource();
        CancellationToken stop = _heartbeatStop.Token;
        _heartbeat = Task.Run(() => HeartbeatLoopAsync(token, stop));
    }

    private async Task StopHeartbeatAsync()
    {
        if (Interlocked.Exchange(ref _heartbeatStop, null) is not { } stop) return;
        await stop.CancelAsync().ConfigureAwait(false);
        await _heartbeat.ConfigureAwait(false);
        stop.Dispose();
    }

    private async Task HeartbeatLoopAsync(CharacterOwnershipToken token, CancellationToken stop)
    {
        TimeSpan interval = HeartbeatInterval ?? TimeSpan.FromSeconds(_fencing.HeartbeatSeconds);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // Wait first: the claim itself wrote the first heartbeat. A failed beat also waits a full interval before the retry.
                await Task.Delay(interval, stop).ConfigureAwait(false);
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    ICharacterOwnershipStore store = scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>();
                    if (!await store.HeartbeatAsync(token, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stop).ConfigureAwait(false))
                    {
                        if (saveQueue.FailTerminal(new InvalidOperationException(
                            $"the worker scope '{token.Scope}' at epoch {token.Epoch} no longer belongs to this world (it was reclaimed or reaped)")))
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world.host", ex);
                    logger.LogFault(LogLevel.Warning, "world-host.heartbeat", ex, "Could not record the worker ownership heartbeat; will retry next interval");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task ReleaseOwnershipAsync(CancellationToken cancellationToken, List<Exception> failures)
    {
        if (!_fencing.Enabled || _ownership is null) return;
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>()
                .ReleaseAsync(_ownership, cancellationToken).ConfigureAwait(false);
            _ownership = null;
            world.CharacterOwnership = null;
            if (ownershipContext is not null)
                ownershipContext.Current = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-host.release-ownership", ex);
            failures.Add(ex);
        }
    }

    /// <summary>
    /// A world with fencing off writes untokened and does not verify ownership per save (that keeps the save path exactly as it was). Instead it
    /// reads the ownership row once per <see cref="CharacterFencingOptions.ClaimWatchIntervalSeconds"/> (default 10 s, independent of autosave), on the thread pool, and stops for good (the save queue's terminal path, which
    /// requests shutdown) if a fenced world has claimed the database meanwhile.
    /// </summary>
    private async Task WatchForClaimAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    ICharacterOwnershipStore? store = scope.ServiceProvider.GetService<ICharacterOwnershipStore>();
                    if (store is not null && await store.IsAnyOwnedAsync(stop).ConfigureAwait(false))
                    {
                        if (saveQueue.FailTerminal(new InvalidOperationException(
                            "the characters database was claimed by a fenced world while this world runs with fencing disabled")))
                        {
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world.host", ex);
                    logger.LogFault(LogLevel.Warning, "world-host.claim-watch", ex, "Could not read the characters ownership row; will retry next interval");
                }

                // Read first, then wait: a failed read also waits a full interval before the retry.
                await Task.Delay(ClaimWatchInterval ?? TimeSpan.FromSeconds(_fencing.ClaimWatchIntervalSeconds), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world.host", ex);
            logger.LogFault("world-host.claim-watch", ex, "Characters ownership claim watch terminated unexpectedly");
        }
    }

    private void OnFatalSaveFailure(Exception error)
    {
        logger.LogCritical(error, "Character save queue entered terminal failure; requesting world shutdown");
        _applicationLifetime?.StopApplication();
    }
}
