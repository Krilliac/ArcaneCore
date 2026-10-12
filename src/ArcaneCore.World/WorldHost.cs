using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
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
    CharacterOwnershipContext? ownershipContext = null) : IHostedService
{
    private readonly CharacterFencingOptions _fencing = fencingOptions?.Value ?? new();
    private readonly IHostApplicationLifetime? _applicationLifetime = applicationLifetime;
    private readonly Guid _incarnation = Guid.NewGuid();
    private CharacterOwnershipToken? _ownership;
    private bool _watchClaims;
    private CancellationTokenSource? _claimWatchStop;
    private Task _claimWatch = Task.CompletedTask;

    /// <summary>How often a world running with fencing off looks for a claim (test seam); null uses <see cref="CharacterFencingOptions.ClaimWatchIntervalSeconds"/>.</summary>
    internal TimeSpan? ClaimWatchInterval { get; init; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _fencing.Validate();
        await AcquireOwnershipAsync(cancellationToken).ConfigureAwait(false);
        saveQueue.FatalFailure += OnFatalSaveFailure;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            IReadOnlyList<CharacterIdentity> identities = await scope.ServiceProvider
                .GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync(cancellationToken).ConfigureAwait(false);
            directory.Load(identities);

            // Features are singletons, so the scope hands out the same instances.
            scope.ServiceProvider.AttachWorldFeatures(world);
        }

        logger.LogInformation("Loaded {Count} character names", directory.Count);
        saveQueue.Start();
        world.Start();
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            if (scope.ServiceProvider.GetService<Playerbots.ManagedPlayerbotFeature>() is { } playerbots)
                await playerbots.StartupAsync(cancellationToken).ConfigureAwait(false);
        }

        // Last, so a start that throws can never leave the watch running (the host does not call StopAsync after a failed start).
        if (!_fencing.Enabled && _watchClaims)
        {
            _claimWatchStop = new CancellationTokenSource();
            _claimWatch = Task.Run(() => WatchForClaimAsync(_claimWatchStop.Token));
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        if (_claimWatchStop is { } watchStop)
        {
            await watchStop.CancelAsync().ConfigureAwait(false);
            await _claimWatch.ConfigureAwait(false);
            watchStop.Dispose();
            _claimWatchStop = null;
        }

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<Playerbots.ManagedPlayerbotFeature>() is { } playerbots)
                await playerbots.ShutdownBeforeWorldStopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { failures.Add(ex); }
        finally { world.Stop(); }
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.StopWorldFeaturesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { failures.Add(ex); }
        bool queueStopped = false;
        try
        {
            await saveQueue.StopAsync().ConfigureAwait(false);
            queueStopped = true;
        }
        catch (Exception ex) { failures.Add(ex); }

        if (failures.Count == 0 && queueStopped)
        {
            await ReleaseOwnershipAsync(cancellationToken, failures).ConfigureAwait(false);
        }

        if (failures.Count == 0) logger.LogInformation("World saved and stopped");
        else logger.LogError("World stop did not complete; durable ownership was retained");
        if (failures.Count > 0) throw new AggregateException("World feature shutdown failed", failures);
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

        _ownership = _fencing.HasTakeover
            ? await store.TryTakeoverAsync(new CharacterOwnershipToken("world", _fencing.TakeoverEpoch!.Value,
                _fencing.TakeoverIncarnation!.Value), _fencing.OwnerNode, _incarnation, cancellationToken).ConfigureAwait(false)
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
        catch (Exception ex) { failures.Add(ex); }
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
                await Task.Delay(ClaimWatchInterval ?? TimeSpan.FromSeconds(_fencing.ClaimWatchIntervalSeconds), stop).ConfigureAwait(false);
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
                catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && stop.IsCancellationRequested))
                {
                    logger.LogWarning(ex, "Could not read the characters ownership row; will retry next interval");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void OnFatalSaveFailure(Exception error)
    {
        logger.LogCritical(error, "Character save queue entered terminal failure; requesting world shutdown");
        _applicationLifetime?.StopApplication();
    }
}
