using System.Collections.Concurrent;
using System.Net;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Auxiliary;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Cluster.Auxiliary;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Lifecycle;
using ArcaneCore.World.Warden;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// The worker-mode listener (Cluster:Worker): a <see cref="GatewaySessionLinkHost"/> whose sessions are
/// <see cref="RemoteWorldSession"/>s of this world. Registered after <see cref="WorldServer"/>, so it starts after the world
/// host and stops (closing every gateway-backed session and waiting for it) before the world host drains its stores.
/// <para>
/// With a <paramref name="wardenVerifier"/> (registered by <see cref="GatewayWorkerServiceCollectionExtensions.AddGatewayWorker"/>) the
/// gateway owns Warden for its sessions: it sends WardenVerdict frames, the link host checks each against the verifier, and an accepted
/// verdict is applied here (Kick disconnects the session, Ban also bans the account through <see cref="IBanStore"/> for
/// <see cref="WardenOptions.BanSeconds"/>; Log needs nothing: the host logged it).
/// </para>
/// </summary>
public sealed class GatewayWorkerEndpoint(
    IServiceScopeFactory scopes,
    IOptions<GatewayWorkerOptions> options,
    IOptions<WorldSessionOptions> sessionOptions,
    OpcodeTable opcodes,
    WorldRuntime world,
    SessionRegistry registry,
    ILoggerFactory loggers,
    WorkerAdmission? admission = null,
    IOptions<WardenOptions>? wardenOptions = null,
    GatewayWardenManifestVerifier? wardenVerifier = null,
    ServerLifecycleFeature? lifecycle = null,
    WorldLoginAdmission? loginAdmission = null) : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan PendingBanStopWait = TimeSpan.FromSeconds(5);

    /// <summary>How often the worker reports its liveness to every admitted gateway link (also sent once on admission). The gateway's offline threshold is 60 s.</summary>
    internal static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _statusStop = new();
    private Task _statusLoop = Task.CompletedTask;
    private Action<ShutdownMask, bool>? _countdownHandler;
    private int _countdown;                                // written by the world thread, read by the status loop: 0 none, 1 restart, 2 shutdown

    // Ban writes still running; StopAsync waits (bounded) so a ban accepted just before shutdown is not abandoned with the process.
    private readonly ConcurrentDictionary<Task, int> _pendingBans = new();
    private GatewaySessionLinkHost? _host;

    /// <summary>The acceptor of the running listener (null before start): its open sessions.</summary>
    public WorldGatewaySessionAcceptor? Acceptor { get; private set; }

    /// <summary>Whether new sessions are admitted (null: always, as in a fixture that builds the endpoint by hand).</summary>
    public WorkerAdmission? Admission => admission;

    /// <summary>The running link host (null before start).</summary>
    public GatewaySessionLinkHost? Host => _host;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        GatewayWorkerOptions config = options.Value;
        GatewayManifestVersions versions;
        if (config.VersionsFromDatabase)
        {
            // Fail closed: an unreadable or in-progress version throws here and the host does not start a link it cannot vouch for.
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            versions = await GatewayManifestVersions.ReadAsync(
                config, scope.ServiceProvider.GetRequiredService<CharacterDbContext>(), scope.ServiceProvider.GetRequiredService<WorldDbContext>(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            versions = GatewayManifestVersions.Resolve(config, null, null);
        }

        ILogger logger = loggers.CreateLogger<GatewayWorkerEndpoint>();
        Acceptor = new WorldGatewaySessionAcceptor(scopes, opcodes, world, registry, sessionOptions.Value, loggers, admission);
        // Owner epoch: the fenced ownership token's epoch when fencing holds one, else 1 (unfenced single node), as the
        // readiness probe reports it. A change after a takeover makes every older packet stale.
        _host = new GatewaySessionLinkHost(
            config.ToManifest(world.CharacterOwnership?.Incarnation ?? Guid.NewGuid(), versions),
            Acceptor,
            () => world.CharacterOwnership is { Epoch: > 0 } token ? token.Epoch : 1,
            config.LinkSecret,
            new IPEndPoint(IPAddress.Parse(config.BindAddress), config.Port),
            logger,
            wardenVerifier,
            wardenVerifier is null ? null : ApplyWardenVerdict,
            BuildStatus);
        _host.Start();
        if (lifecycle is not null && _countdownHandler is null)
        {
            _countdownHandler = OnCountdownChanged;
            lifecycle.CountdownChanged += _countdownHandler;
        }

        GatewaySessionLinkHost running = _host;
        _statusLoop = Task.Run(() => RunStatusLoopAsync(running, _statusStop.Token), CancellationToken.None);
        logger.LogInformation("Worker mode: gateway link listening on {Endpoint}", _host.BoundEndPoint);
        logger.LogInformation(
            "Worker mode: advertising characters schema {CharactersSchema}, world schema {WorldSchema}, content revision {ContentRevision} (from {Source}); the gateway's Gateway:* labels must equal them",
            versions.CharactersSchemaVersion, versions.WorldSchemaVersion, versions.ContentRevision, config.VersionsFromDatabase ? "the databases" : "the Cluster:Worker labels");
    }

    /// <summary>
    /// This worker's liveness as the gateway's realm writer reads it: the world loop's tick counter (a volatile read), the registered sessions, the
    /// player limit in force and the standby and countdown state. Allocation-free and lock-free; callable from any thread.
    /// </summary>
    internal WorkerStatusReport BuildStatus()
    {
        WorkerStatusFlags flags = WorkerStatusFlags.None;
        if (admission is { ClosedReason: WorkerAdmission.StandbyReason })
        {
            flags |= WorkerStatusFlags.Standby;
        }

        switch (Volatile.Read(ref _countdown))
        {
            case 1:
                flags |= WorkerStatusFlags.Restarting;
                break;
            case 2:
                flags |= WorkerStatusFlags.ShuttingDown;
                break;
        }

        return new WorkerStatusReport(world.TicksStarted, registry.Count, loginAdmission?.AmountLimit ?? 0, flags);
    }

    private void OnCountdownChanged(ShutdownMask mask, bool pending)
        => Volatile.Write(ref _countdown, !pending ? 0 : mask.HasFlag(ShutdownMask.Restart) ? 1 : 2);

    private async Task RunStatusLoopAsync(GatewaySessionLinkHost host, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(StatusInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                host.BroadcastWorkerStatus();
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (lifecycle is not null && _countdownHandler is not null)
        {
            lifecycle.CountdownChanged -= _countdownHandler;
            _countdownHandler = null;
        }

        await _statusStop.CancelAsync().ConfigureAwait(false);
        await _statusLoop.ConfigureAwait(false);
        // Clear the field before disposing so the second call (DisposeAsync after the host's StopAsync) is a no-op
        // for the link host even when the first disposal threw.
        GatewaySessionLinkHost? host = _host;
        _host = null;
        WorldGatewaySessionAcceptor? acceptor = Acceptor;
        await StopInOrderAsync(
            host is null ? null : host.DisposeAsync,
            acceptor is null ? null : acceptor.StopAsync).ConfigureAwait(false);
        Task[] bans = [.. _pendingBans.Keys];
        if (bans.Length > 0)
        {
            try
            {
                await Task.WhenAll(bans).WaitAsync(PendingBanStopWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                loggers.CreateLogger<GatewayWorkerEndpoint>().LogWarning("{Count} Warden ban write(s) still pending at stop; the ban(s) may be lost", bans.Length);
            }
        }
    }

    /// <summary>
    /// Applies a verdict the link host accepted (on the link's receive loop; never blocks). The session is the one the verdict names by
    /// generation and account; a session that is already gone needs nothing. Kick and Ban disconnect it at once (a banned client must not
    /// keep sending opcodes while the ban commits); the ban write runs off this thread.
    /// </summary>
    private void ApplyWardenVerdict(long generation, ClusterWardenVerdict verdict)
    {
        ILogger logger = loggers.CreateLogger<GatewayWorkerEndpoint>();
        RemoteWorldSession? remote = null;
        if (Acceptor?.Sessions.TryGetValue(generation, out remote) != true || remote is null || remote.Session.AccountId != verdict.AccountId)
        {
            logger.LogInformation("Warden verdict for generation {Generation} (account {Account}) has no open session; nothing to apply", generation, verdict.AccountId);
            return;
        }

        WorldSession session = remote.Session;
        logger.LogWarning("Warden (gateway): account {Account} ({AccountId}): {Reason}; action {Action}", session.AccountName, session.AccountId, verdict.Reason, verdict.Action);
        switch (verdict.Action)
        {
            case ClusterWardenAction.Kick:
                session.Kick("Warden: " + verdict.Reason);
                break;
            case ClusterWardenAction.Ban:
                session.Kick("Warden: " + verdict.Reason);
                StartBan(session.AccountId, verdict.Reason);
                break;
        }
    }

    private void StartBan(int accountId, string reason)
    {
        Task write = Task.Run(() => BanAsync(accountId, reason)); // BanAsync records its own failures and never throws
        _pendingBans.TryAdd(write, accountId);
        _ = write.ContinueWith(static (done, state) => ((ConcurrentDictionary<Task, int>)state!).TryRemove(done, out _), _pendingBans,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task BanAsync(int accountId, string reason)
    {
        ILogger logger = loggers.CreateLogger<GatewayWorkerEndpoint>();
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IBanStore>() is not { } bans)
            {
                logger.LogWarning("Warden: ban of account {Account} skipped: no ban store", accountId);
                return;
            }

            long seconds = (wardenOptions?.Value ?? new WardenOptions()).BanSeconds;
            await bans.BanAccountAsync(new BanRequest(accountId, seconds, $"Warden: {reason}", WardenFeature.BanAuthor)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnomalyLedger.Shared.Report("warden", "check-fault", $"account {accountId}", $"ban error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault("warden.ban", ex, "Warden: ban of account {Account} failed", accountId);
        }
    }

    /// <summary>Stop the link host first, then the acceptor; the acceptor is stopped even when the host's disposal throws. If both throw, both exceptions are kept (AggregateException).</summary>
    internal static async Task StopInOrderAsync(Func<ValueTask>? stopHost, Func<Task>? stopAcceptor)
    {
        Exception? hostFailure = null;
        try
        {
            if (stopHost is not null)
            {
                await stopHost().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            hostFailure = ex;
        }

        try
        {
            if (stopAcceptor is not null)
            {
                await stopAcceptor().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && hostFailure is not null)
        {
            throw new AggregateException("the link host and the acceptor both failed to stop", hostFailure, ex);
        }

        if (hostFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(hostFailure).Throw();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}

/// <summary>Registration of worker mode (the one call in <c>AddWorldDaemon</c>).</summary>
public static class GatewayWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="GatewayWorkerOptions"/> and, only when Cluster:Worker:Enabled is true, register the
    /// <see cref="GatewayWorkerEndpoint"/> and make character fencing mandatory (<see cref="Clustering.CharacterFencingOptions.RequiredByWorkerMode"/>).
    /// Off (the default) registers nothing else: Combined mode is unchanged.
    /// Call after <c>AddHostedService&lt;WorldServer&gt;()</c> so the endpoint stops before the world host.
    /// </summary>
    public static IServiceCollection AddGatewayWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(GatewayWorkerOptions.SectionName);
        services.Configure<GatewayWorkerOptions>(section);
        if (section.GetValue<bool>(nameof(GatewayWorkerOptions.Enabled)))
        {
            // Fencing is mandatory in worker mode: the host refuses to start without it, and the characters write fence is attached regardless.
            services.PostConfigure<Clustering.CharacterFencingOptions>(fencing => fencing.RequiredByWorkerMode = true);
            bool standby = section.GetValue<bool>(nameof(GatewayWorkerOptions.Standby));
            services.AddSingleton(new WorkerAdmission(standby ? WorkerAdmission.StandbyReason : null));
            // The gateway runs Warden for the sessions it forwards; the worker verifies its verdicts against the manifest it derives from the same options.
            services.AddSingleton(sp => new GatewayWardenManifestVerifier(GatewayWardenManifestVerifier.BuildManifest(
                sp.GetRequiredService<IOptions<GatewayWorkerOptions>>().Value.RealmId,
                sp.GetService<IOptions<WardenOptions>>()?.Value ?? new WardenOptions())));
            if (standby)
            {
                AddStandby(services);
            }

            services.AddSingleton<GatewayWorkerEndpoint>();
            services.AddHostedService(sp => sp.GetRequiredService<GatewayWorkerEndpoint>());
        }

        return services;
    }

    /// <summary>
    /// Standby (Tier 3b): the world loads its content, maps and caches and links to the gateway, but it is not a writer until it is promoted.
    /// Its schema check runs validate-only (no DDL: the active world runs on that schema), it takes no characters writer file lock and claims no
    /// ownership (the active world holds both; fencing, mandatory in worker mode, refuses its characters writes), and it has no client listener of
    /// its own (World:Port belongs to the active world; clients reach a promoted standby through the gateway only).
    /// </summary>
    private static void AddStandby(IServiceCollection services)
    {
        services.PostConfigure<Data.DatabaseOptions>(database => database.Upgrade.ValidateOnly = true);
        services.RemoveAll<WorldWriterGuard>();
        // Keeps the database options but defers the lock: Acquire (WorldStartup, WorldHost.StartAsync) takes nothing while the active world holds it; WorldHost.PromoteAsync takes it
        // (the realm lock, or in Worker scope the shard lock of the worker it replaces: the standby sets Cluster:Fencing:Shard explicitly, see CharacterFencingOptions.ValidateWorkerScope).
        services.AddSingleton(sp => new WorldWriterGuard(sp.GetService<Microsoft.Extensions.Options.IOptions<Data.DatabaseOptions>>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<WorldWriterGuard>>(), deferUntilPromotion: true,
            lifetime: sp.GetService<IHostApplicationLifetime>(), drain: sp.GetService<ArcaneCore.World.Persistence.ShutdownDrain>(),
            fence: sp.GetService<ArcaneCore.Data.Characters.Ownership.CharacterDatabaseWriterFence>(), // a promoted standby must be able to stop itself on a take-over
            fencingOptions: sp.GetService<Microsoft.Extensions.Options.IOptions<Clustering.CharacterFencingOptions>>(), // Scope=Worker: promotion takes the shard lock, not the realm lock
            workerOptions: sp.GetService<Microsoft.Extensions.Options.IOptions<GatewayWorkerOptions>>()));
        ServiceDescriptor? listener = services.LastOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(WorldServer));
        if (listener is not null)
        {
            services.Remove(listener);
        }
    }
}
