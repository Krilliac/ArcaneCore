using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
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
    ShutdownDrain? drain = null) : IHostedService
{
    private readonly CharacterFencingOptions _fencing = fencingOptions?.Value ?? new();
    private readonly Cluster.Gateway.GatewayWorkerOptions? _worker = workerOptions?.Value;
    private readonly Lock _promoteGate = new();
    private volatile bool _standby = workerOptions?.Value is { Enabled: true, Standby: true };
    private Task? _promotion;
    private volatile bool _standbyStarted;

    /// <summary>Whether a listener will bind <c>World:Port</c> after this host (test seam); null looks for a registered <see cref="Net.WorldServer"/>.</summary>
    internal bool? ListenerExpected { get; init; }
    private readonly IHostApplicationLifetime? _applicationLifetime = applicationLifetime;
    private readonly Guid _incarnation = Guid.NewGuid();
    private CharacterOwnershipToken? _ownership;
    private bool _watchClaims;
    private CancellationTokenSource? _claimWatchStop;
    private Task _claimWatch = Task.CompletedTask;
    private readonly Lock _stopGate = new();
    private Task? _stop;
    private volatile bool _attached; // the features were attached (StartAsync): from then on a stop has a world, features and a save queue to stop

    /// <summary>How often a world running with fencing off looks for a claim (test seam); null uses <see cref="CharacterFencingOptions.ClaimWatchIntervalSeconds"/>.</summary>
    internal TimeSpan? ClaimWatchInterval { get; init; }

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

    /// <summary>This process's ownership incarnation: the one a handoff's epoch takeover hands the characters ownership to.</summary>
    public Guid Incarnation => _incarnation;

    /// <summary>The durable owner node name this world claims under (Cluster:Fencing:OwnerNode).</summary>
    public string OwnerNode => _fencing.OwnerNode;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _fencing.Validate();
        if (_worker is { Enabled: false, Standby: true })
            throw new InvalidOperationException("Cluster:Worker:Standby requires Cluster:Worker:Enabled = true: a standby world is a gateway worker");
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
            // The host never calls StopAsync for a failed start: give back only a lock this start took itself. A lock WorldStartup took stays held
            // until the container disposes it (after the hosted services stop), which in production is the end of the process.
            if (acquiredHere) writerGuard?.Dispose();
            throw;
        }

        saveQueue.FatalFailure += OnFatalSaveFailure;
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

                // Features are singletons, so the scope hands out the same instances. Set first: a feature that attached before one that threw must still stop.
                _attached = true;
                scope.ServiceProvider.AttachWorldFeatures(world);

                if (_fencing.Enabled && scope.ServiceProvider.GetService<IIdRangeReservationStore>() is not null)
                {
                    await PrimeDurableIdAllocatorsAsync(scope.ServiceProvider, scopes, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            logger.LogInformation("Loaded {Count} character names", directory.Count);
            saveQueue.Start();
            world.Start();
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
            if (_attached) await StopAfterFailedStartAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-host.start", ex);
            await StopClaimWatchAsync().ConfigureAwait(false);
            if (_attached) await StopAfterFailedStartAsync().ConfigureAwait(false);
            throw;
        }
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
        if (takenOver.Scope != "world" || takenOver.Incarnation != _incarnation)
            throw new InvalidOperationException("the ownership to promote under was not handed to this world's incarnation");

        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            ICharacterOwnershipStore store = scope.ServiceProvider.GetService<ICharacterOwnershipStore>()
                ?? throw new InvalidOperationException("no characters ownership store is registered; a standby cannot be promoted");
            // Only reads the row: the token must be the database's current active one. Never TryAcquireAsync here, which would claim a released
            // (inactive) row at a new epoch outside the handoff journal and leave it claimed by a standby whose refusal then holds no token.
            CharacterOwnershipToken? current = await store.GetCurrentAsync("world", cancellationToken).ConfigureAwait(false);
            if (current != takenOver)
                throw new InvalidOperationException("the characters database is not owned by this standby under the promoted token; refusing to promote");
        }

        _ownership = takenOver;
        world.CharacterOwnership = takenOver;
        if (ownershipContext is not null)
            ownershipContext.Current = takenOver;
        _standby = false;
        logger.LogInformation("Standby world promoted at ownership epoch {Epoch}: attaching the features and starting the simulation", takenOver.Epoch);

        // As an active world's start does after its ownership claim: load the characters, attach, start, run the after-start work. A failure here
        // leaves a promoted world that owns the database but does not run: the host is stopped (as for a failed start), never re-opened as a standby.
        await AttachAndStartAsync(cancellationToken).ConfigureAwait(false);
        admission?.Open();
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scopes);

        Func<string, uint, CancellationToken, Task<IdRangeBlock>> reserveAsync = async (scopeName, count, ct) =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IIdRangeReservationStore store = scope.ServiceProvider.GetRequiredService<IIdRangeReservationStore>();
            return await store.ReserveBlockAsync(scopeName, count, ct).ConfigureAwait(false);
        };

        if (services.GetService<Items.ItemsFeature>() is { } items)
        {
            DurableIdBlockAllocator itemAlloc = items.GuidAllocator.ConfigureBlockReservation(reserveAsync, blockSize, lowWatermark);
            await itemAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (services.GetService<Social.SocialFeature>() is { } social)
        {
            var groupAlloc = new DurableIdBlockAllocator(IdRangeScopes.Group, blockSize, lowWatermark, reserveAsync);
            await groupAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            social.Context.Groups.IdBlockAllocator = groupAlloc;

            var guildAlloc = new DurableIdBlockAllocator(IdRangeScopes.Guild, blockSize, lowWatermark, reserveAsync);
            await guildAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            social.Context.Guilds.IdBlockAllocator = guildAlloc;
        }

        if (services.GetService<Instances.InstanceFeature>() is { } instances)
        {
            var instanceAlloc = new DurableIdBlockAllocator(IdRangeScopes.Instance, blockSize, lowWatermark, reserveAsync);
            await instanceAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            instances.Instances.IdBlockAllocator = instanceAlloc;
        }

        if (services.GetService<Economy.EconomyFeature>() is { Enabled: true } economy)
        {
            var mailAlloc = new DurableIdBlockAllocator(IdRangeScopes.Mail, blockSize, lowWatermark, reserveAsync);
            await mailAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            economy.MailIdAllocator = mailAlloc;

            var auctionAlloc = new DurableIdBlockAllocator(IdRangeScopes.Auction, blockSize, lowWatermark, reserveAsync);
            await auctionAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            economy.AuctionIdAllocator = auctionAlloc;

            var textAlloc = new DurableIdBlockAllocator(IdRangeScopes.ItemText, blockSize, lowWatermark, reserveAsync);
            await textAlloc.EnsurePrimedAsync(cancellationToken).ConfigureAwait(false);
            economy.ItemTextIdAllocator = textAlloc;
        }
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
        if (_claimWatchStop is not { } watchStop) return;
        await watchStop.CancelAsync().ConfigureAwait(false);
        await _claimWatch.ConfigureAwait(false);
        watchStop.Dispose();
        _claimWatchStop = null;
    }

    /// <summary>
    /// Stop the bots, the world thread and the features, drain the save queue, then give back the ownership. Runs once: a start that failed has already
    /// stopped the world itself (<see cref="StopAfterFailedStartAsync"/>), and the host still stops it when the stop was requested during start
    /// (<see cref="WorldStartup.RunAsync"/>); later calls return the first stop's task.
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
            throw new InvalidOperationException($"the world port {address}:{options.Port} is already in use (another world process?); refusing to start before any playerbot is restored", ex);
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
            if (await store.IsOwnedAsync("world", cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("the characters database is already owned; disabled fencing cannot start a duplicate world writer");
            return;
        }

        _ownership = _fencing is { HasTakeover: true, TakeoverEpoch: { } takeoverEpoch, TakeoverIncarnation: { } takeoverIncarnation }
            ? await store.TryTakeoverAsync(new CharacterOwnershipToken("world", takeoverEpoch,
                takeoverIncarnation), _fencing.OwnerNode, _incarnation, cancellationToken).ConfigureAwait(false)
            : await store.TryAcquireAsync("world", _fencing.OwnerNode, _incarnation, cancellationToken).ConfigureAwait(false);
        if (_ownership is null)
            throw new InvalidOperationException("the characters database is already owned by another world authority");
        world.CharacterOwnership = _ownership;
        if (ownershipContext is not null)
            ownershipContext.Current = _ownership; // the ambient token the characters write fence verifies every unaware store's transaction against
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
                    if (store is not null && await store.IsOwnedAsync("world", stop).ConfigureAwait(false))
                    {
                        saveQueue.FailTerminal(new InvalidOperationException(
                            "the characters database was claimed by a fenced world while this world runs with fencing disabled"));
                        return;
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
