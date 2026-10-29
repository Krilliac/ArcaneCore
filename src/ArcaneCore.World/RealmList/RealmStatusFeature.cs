using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.RealmList;

/// <summary>
/// Keeps this world's realm-list row in step with the world, as vmangos mangosd does:
/// <list type="bullet">
/// <item>once the world has started (<see cref="WorldLifecycleStage.AfterWorldStart"/>), the Offline flag is cleared and the population
/// set to 0 (Master.cpp:215 <c>realmflags &amp; ~REALM_FLAG_OFFLINE, population = 0</c>);</item>
/// <item>before the world stops (<see cref="WorldLifecycleStage.BeforeWorldStop"/>, which a failed start runs too), the Offline flag is set
/// (Master.cpp:330 <c>realmflags | REALM_FLAG_OFFLINE</c>);</item>
/// <item>when a session is added the population becomes 2 x sessions / <see cref="RealmStatusOptions.PlayerLimit"/> (World.cpp:340-353,
/// in <c>World::AddSession_</c>; vmangos has no timer for it, so neither is there one here).</item>
/// </list>
/// The logon daemon reads the row for every realm-list request, so a world that is down shows offline. A failed write is logged and
/// never fails the start or the stop (vmangos runs these as fire-and-forget statements). Off when <see cref="RealmStatusOptions.Id"/> is 0,
/// when no <see cref="IRealmStatusStore"/> is registered (no auth database), and in gateway worker mode (<c>Cluster:Worker:Enabled</c>),
/// where an active and a standby world share the realm and the gateway owns the client-facing address.
/// </summary>
public sealed class RealmStatusFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RealmStatusFeature> _logger;
    private readonly RealmStatusOptions _options;
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
        if (_options.Id <= 0)
        {
            _logger.LogInformation("World:Realm:Id is 0: this world does not mark its realm online or offline in the realm list");
            return;
        }

        if (_services.GetService<IOptions<GatewayWorkerOptions>>()?.Value is { Enabled: true })
        {
            _logger.LogInformation("Gateway worker mode: the realm-list row of realm {RealmId} is not written by this world", _options.Id);
            return;
        }

        if (_services.GetService<IServiceScopeFactory>() is null || _services.GetService<IWorldLifecycleHooks>() is not { } hooks)
        {
            return;
        }

        using (IServiceScope probe = _services.GetRequiredService<IServiceScopeFactory>().CreateScope())
        {
            if (probe.ServiceProvider.GetService<IRealmStatusStore>() is null)
            {
                return; // no auth database in this host
            }
        }

        Enabled = true;
        _subscriptions.Add(hooks.Register(WorldLifecycleStage.AfterWorldStart, "realm.online", MarkOnlineAsync));
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

    private async Task MarkOnlineAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = false;
        }

        if (await WriteAsync("online", store => store.SetOnlineAsync(_options.Id, cancellationToken)).ConfigureAwait(false) is true)
        {
            _logger.LogInformation("Realm {RealmId} is online in the realm list", _options.Id);
        }
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
        if (await WriteAsync("offline", store => store.SetOfflineAsync(_options.Id, CancellationToken.None)).ConfigureAwait(false) is true)
        {
            _logger.LogInformation("Realm {RealmId} is offline in the realm list", _options.Id);
        }
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
