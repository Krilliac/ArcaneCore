using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.RealmList;

/// <summary>
/// The population half of the world's realm-list row, and the clean-stop hook, as vmangos mangosd does:
/// <list type="bullet">
/// <item>when a session is added the population becomes 2 x sessions / <see cref="RealmStatusOptions.PlayerLimit"/> (World.cpp:340-353,
/// in <c>World::AddSession_</c>; vmangos has no timer for it, so neither is there one here);</item>
/// <item>before the world stops (<see cref="WorldLifecycleStage.BeforeWorldStop"/>, which a failed start runs too), the pending population write
/// finishes and <see cref="RealmLivenessService"/> marks the row Offline and Stopped (Master.cpp:330 <c>realmflags | REALM_FLAG_OFFLINE</c>).</item>
/// </list>
/// The online mark (vmangos Master.cpp:215) moved to <see cref="RealmLivenessService"/>: it needs the listener bound and the first tick, which
/// the after-start hook could not know. A failed write is logged and never fails the start or the stop (vmangos runs these as fire-and-forget
/// statements). Off whenever <see cref="RealmLivenessService.Enabled"/> is off (<see cref="RealmStatusOptions.Id"/> 0, no auth database,
/// gateway worker mode).
/// </summary>
public sealed class RealmStatusFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RealmStatusFeature> _logger;
    private readonly RealmStatusOptions _options;
    private readonly RealmLivenessService? _liveness;
    private readonly Lock _gate = new();
    private readonly List<IDisposable> _subscriptions = [];
    private float? _pendingPopulation;
    private Task _populationWriter = Task.CompletedTask;
    private bool _stopping;
    private int _warnedMissing;

    public RealmStatusFeature(IServiceProvider services, ILogger<RealmStatusFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = services.GetService<IOptions<RealmStatusOptions>>()?.Value ?? new RealmStatusOptions();
        _liveness = services.GetService<RealmLivenessService>();
    }

    /// <summary>Whether this world writes its realm-list row.</summary>
    public bool Enabled { get; private set; }

    /// <summary>vmangos World.cpp:343-345: <c>popu = activeSessions; popu /= playerLimit; popu *= 2</c>, in that order.</summary>
    public static float Population(int sessions, int playerLimit)
    {
        float population = sessions;
        population /= playerLimit;
        population *= 2;
        return population;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_liveness is not { Enabled: true } || _services.GetService<IWorldLifecycleHooks>() is not { } hooks)
        {
            return; // the service says why (realm id 0, gateway worker, no auth database)
        }

        Enabled = true;
        _subscriptions.Add(hooks.Register(WorldLifecycleStage.BeforeWorldStop, "realm.offline", _ => MarkOfflineAsync()));
        if (_options.PlayerLimit > 0 && _services.GetService<SessionRegistry>() is { } sessions)
        {
            Action<WorldSession> added = _ => RequestPopulation(sessions.Count);
            sessions.Registered += added;
            _subscriptions.Add(new Unsubscribe(() => sessions.Registered -= added));
        }
    }

    public Task StopAsync()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    private async Task MarkOfflineAsync()
    {
        Task writer;
        lock (_gate)
        {
            _stopping = true;
            _pendingPopulation = null;
            writer = _populationWriter;
        }

        await writer.ConfigureAwait(false); // a population write still running finishes before the row goes offline
        await _liveness!.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>A session was added: write the population; requests that arrive while a write runs collapse into the newest value.</summary>
    internal void RequestPopulation(int sessions)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            bool idle = _pendingPopulation is null && _populationWriter.IsCompleted;
            _pendingPopulation = Population(sessions, _options.PlayerLimit);
            if (idle)
            {
                _populationWriter = Task.Run(WritePopulationAsync);
            }
        }
    }

    private async Task WritePopulationAsync()
    {
        while (true)
        {
            float population;
            lock (_gate)
            {
                if (_pendingPopulation is not { } next)
                {
                    return;
                }

                population = next;
                _pendingPopulation = null;
            }

            await WriteAsync("population", store => store.SetPopulationAsync(_options.Id, population, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one store call in its own scope; null when it failed (logged), else whether the realm row exists.</summary>
    private async Task<bool?> WriteAsync(string what, Func<IRealmStatusStore, Task<bool>> write)
    {
        try
        {
            await using AsyncServiceScope scope = _services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            bool found = await write(scope.ServiceProvider.GetRequiredService<IRealmStatusStore>()).ConfigureAwait(false);
            if (!found && Interlocked.Exchange(ref _warnedMissing, 1) == 0)
            {
                // vmangos refuses to start on a RealmID with no realmlist row (Master.cpp:159-164); here the world runs and says so.
                _logger.LogWarning("Realm {RealmId} has no row in the realm list, so its {What} state was not written; set World:Realm:Id to the realm's id", _options.Id, what);
            }

            return found;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("realm.status-write", ex, "Writing the realm-list {What} state of realm {RealmId} failed", what, _options.Id);
            return null;
        }
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
