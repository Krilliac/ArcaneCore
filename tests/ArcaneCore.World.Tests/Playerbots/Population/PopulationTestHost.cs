using System.Collections.Concurrent;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>A tick source with a chosen p95 and sample count; <see cref="Reset"/> empties it and counts the reset.</summary>
internal sealed class FakeTickSource : IPopulationTickSource
{
    private int _samples;
    private double _p95;
    private int _resets;
    private long _total;

    public int Samples => Volatile.Read(ref _samples);

    public long TotalSamples => Interlocked.Read(ref _total);

    public int Resets => Volatile.Read(ref _resets);

    public double P95Ms(int windowTicks) => Volatile.Read(ref _p95);

    public void Reset()
    {
        Volatile.Write(ref _samples, 0);
        Interlocked.Increment(ref _resets);
    }

    /// <summary>The next decision sees <paramref name="samples"/> fresh ticks with this p95 (each call is a new window: the running total grows by that many).</summary>
    public void Set(int samples, double p95Ms)
    {
        Interlocked.Add(ref _total, samples);
        Volatile.Write(ref _p95, p95Ms);
        Volatile.Write(ref _samples, samples);
    }
}

/// <summary>An in-memory <see cref="IPlayerbotPopulationStore"/> with the identity rule of the EF store.</summary>
internal sealed class MemoryPopulationStore : IPlayerbotPopulationStore
{
    private readonly ConcurrentDictionary<Guid, PlayerbotPopulationMember> _items = new();
    private TaskCompletionSource? _loadHeld;
    private TaskCompletionSource _loadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<PlayerbotPopulationMember> Snapshot => [.. _items.Values.OrderBy(m => m.BotId)];

    public TaskCompletionSource HoldNextLoad()
    {
        _loadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _loadHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void CancelHeldLoad() => _loadRelease.TrySetCanceled();

    public void ReleaseHeldLoad() => _loadRelease.TrySetResult();

    public async Task<IReadOnlyList<PlayerbotPopulationMember>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _loadHeld, null) is { } held)
        {
            held.TrySetResult();
            await _loadRelease.Task;
        }
        return Snapshot;
    }

    public Task UpsertAsync(PlayerbotPopulationMember member, CancellationToken cancellationToken = default)
    {
        if (_items.TryGetValue(member.BotId, out PlayerbotPopulationMember? existing) && existing.CharacterId != member.CharacterId)
            throw new InvalidOperationException("The population member identity is already owned by another row.");
        _items[member.BotId] = existing is null ? member : member with { CreatedUnix = existing.CreatedUnix };
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid botId, CancellationToken cancellationToken = default) => Task.FromResult(_items.TryRemove(botId, out _));

    /// <summary>Add a row as if a previous run had written it.</summary>
    public void Seed(PlayerbotPopulationMember member) => _items[member.BotId] = member;
}

/// <summary>A scoped world data store that records whether its scope has ended, so a test can tell a pass-scoped store from a captive one.</summary>
internal sealed class ScopedWorldDataStore : IWorldDataStore, IDisposable
{
    private readonly InMemoryWorldDataStore _inner = new();

    private int _raceClassCalls;

    public bool Disposed { get; private set; }

    /// <summary>How often the store was asked whether a race and class pair is valid (a character creation asks once; the population's cache asks once per race and class).</summary>
    public int RaceClassCalls => Volatile.Read(ref _raceClassCalls);

    public void Dispose() => Disposed = true;

    public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default) => _inner.GetStartPositionAsync(race, cls, cancellationToken);

    public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default) => _inner.GetRaceInfoAsync(race, gender, cancellationToken);

    public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => _inner.GetClassInfoAsync(cls, cancellationToken);

    public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _raceClassCalls);
        return _inner.IsValidRaceClassAsync(race, cls, cancellationToken);
    }
}

/// <summary>
/// A world with managed playerbots, the population feature, one Alliance starter zone (Elwynn Forest, levels 5-10, one graveyard on map 0),
/// a fake tick source and in-memory stores. Passes run when the test says so (<see cref="PlayerbotPopulationFeature.RunPassNowAsync"/>):
/// the interval is the 600 s maximum unless the test lowers it.
/// </summary>
internal sealed class PopulationTestHost : IAsyncDisposable
{
    public const uint Elwynn = 12;

    private PopulationTestHost(WorldTestHost host, PlayerbotOptions options, ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore owners,
        MemoryPopulationStore members, FakeTickSource ticks, List<ScopedWorldDataStore> dataStores)
    {
        Host = host;
        Options = options;
        Owners = owners;
        Members = members;
        Ticks = ticks;
        DataStores = dataStores;
    }

    public WorldTestHost Host { get; }

    public PlayerbotOptions Options { get; }

    public PlayerbotPopulationOptions Population => Options.Population;

    public ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore Owners { get; }

    public MemoryPopulationStore Members { get; }

    public FakeTickSource Ticks { get; }

    /// <summary>Every world data store the host's scopes created (the feature must create them per pass scope and dispose them with it).</summary>
    public List<ScopedWorldDataStore> DataStores { get; }

    /// <summary>The stores that built a race and class cache: one that was asked about all eight races of at least one class.</summary>
    public IReadOnlyList<ScopedWorldDataStore> CacheBuilders
    {
        get
        {
            lock (DataStores) return [.. DataStores.Where(s => s.RaceClassCalls >= 8)];
        }
    }

    public ManagedPlayerbotFeature Bots => Host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();

    public PlayerbotPopulationFeature Feature => Host.WorldServices.GetRequiredService<PlayerbotPopulationFeature>();

    /// <summary>Westfall in the relocation variant (levels 12-20, one graveyard on map 0), the zone an out-levelled Elwynn bot is moved to.</summary>
    public const uint Westfall = 40;

    /// <summary>A capital in the relocation variant: not in the atlas on purpose, a place a bot visits for town and trainer goals.</summary>
    public const uint Stormwind = 1519;

    /// <summary>
    /// Start the host. With <paramref name="withRelocation"/> the relocation is on (<see cref="PopulationRelocation.Teleport"/>) and the area
    /// table also holds Westfall (levels 12-20) and a capital; Westfall is excluded at first so the first bots are created in Elwynn
    /// (the test empties <c>ExcludedZones</c> to open it). <paramref name="configureServices"/> adds services (a placement veto).
    /// </summary>
    public static async Task<PopulationTestHost> StartAsync(Action<PlayerbotOptions>? configure = null, bool manualClock = false,
        bool withRelocation = false, Action<IServiceCollection>? configureServices = null, Func<IWorldDataStore>? dataStore = null)
    {
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var members = new MemoryPopulationStore();
        var ticks = new FakeTickSource();
        var dataStores = new List<ScopedWorldDataStore>();
        var options = new PlayerbotOptions
        {
            Enabled = true, MaxBots = 4, ThinkIntervalMs = 100, MaxActionsPerTick = 8, MaxPathPoints = 64, MaxRouteYards = 200, AllowedMaps = [0, 1],
            Chat = { Enabled = false },
            Population =
            {
                Enabled = true, TargetOnline = 2, IntervalSeconds = 600, WindowTicks = 20, TickP95BudgetMs = 60, ResumeBelowPct = 80,
                // One window confirms and no minimum run time: a pass acts on the p95 the test sets (the hysteresis and anti-churn
                // tests raise them).
                ConfirmWindows = 1, MinRunSeconds = 0,
                MaxStartsPerInterval = 4, MaxParksPerInterval = 1, MaxCreatePerMinute = 10, Factions = PopulationFactions.Alliance,
                Classes = [1], Bands = [new PopulationBand { MinLevel = 1, MaxLevel = 10, Weight = 1 }], Relocation = PopulationRelocation.Off,
            },
        };
        if (withRelocation)
        {
            options.Population.Relocation = PopulationRelocation.Teleport;
            options.Population.ExcludedZones = [Westfall];
        }

        configure?.Invoke(options);
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            configureServices?.Invoke(services);
            if (manualClock) services.AddSingleton<IWorldFeature, ManualClockFeature>();
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IPlayerbotPopulationStore>(members);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Microsoft.Extensions.Options.Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(sp =>
                new ManagedPlayerbotLifecycleTests.MemoryProvisionStore((InMemoryAccountStore)sp.GetRequiredService<IAccountStore>()));
            // Scoped, as in production (EfWorldDataStore over a DbContext): resolving it from the root provider would capture it for good.
            if (dataStore is not null) services.AddScoped(_ => dataStore());
            else services.AddScoped<IWorldDataStore>(_ =>
            {
                var store = new ScopedWorldDataStore();
                lock (dataStores) dataStores.Add(store);
                return store;
            });
        });
        var result = new PopulationTestHost(host, options, owners, members, ticks, dataStores);
        await host.World.InvokeAsync(() =>
        {
            WorldMaps maps = WorldMaps.Of(host.World);
            List<AreaTemplate> areas =
            [
                new AreaTemplate(Elwynn, 0, 0, Elwynn, 0, 0, "Elwynn Forest", AreaTeams.Ally, 0),
                new AreaTemplate(24, 0, Elwynn, 24, 0, 5, "Fargodeep Mine", 0, 0),
                new AreaTemplate(25, 0, Elwynn, 25, 0, 10, "Stormwind Mountains", 0, 0),
            ];
            List<WorldSafeLoc> locations = [new WorldSafeLoc(1, 0, -8913.23f, -133.0f, 81.0f, 0f, "Goldshire")];
            List<GraveyardLink> links = [new GraveyardLink(1, Elwynn, 0)];
            if (withRelocation)
            {
                areas.Add(new AreaTemplate(Westfall, 0, 0, Westfall, 0, 0, "Westfall", AreaTeams.Ally, 0));
                areas.Add(new AreaTemplate(41, 0, Westfall, 41, 0, 12, "Sentinel Hill", 0, 0));
                areas.Add(new AreaTemplate(42, 0, Westfall, 42, 0, 20, "The Dead Acre", 0, 0));
                areas.Add(new AreaTemplate(Stormwind, 0, 0, Stormwind, (uint)AreaFlags.Capital, 0, "Stormwind City", AreaTeams.Ally, 0));
                areas.Add(new AreaTemplate(1520, 0, Stormwind, 1520, 0, 15, "Trade District", 0, 0));
                locations.Add(new WorldSafeLoc(2, 0, -10_653.0f, 1_165.0f, 34.0f, 0f, "Sentinel Hill"));
                links.Add(new GraveyardLink(2, Westfall, 0));
            }

            maps.Terrain.Areas = new AreaTable(areas, maps.Registry);
            WorldGraveyards.Of(host.World).Load(new GraveyardContent(locations, links), null);
            return true;
        });
        await result.Bots.StartupAsync(default);
        result.Feature.TickSource = ticks;
        return result;
    }

    /// <summary>Run one pass and return the status it published.</summary>
    public async Task<PopulationStatus> PassAsync()
    {
        await Feature.RunPassNowAsync();
        return Feature.Status;
    }

    /// <summary>Wait until exactly <paramref name="count"/> bots are running and each stands in the world with no teleport pending.</summary>
    public Task WaitForSettledAsync(int count) => Host.WaitForWorldAsync(() =>
    {
        PlayerbotStatus[] running = [.. Bots.Snapshot().Where(s => s.State == ManagedPlayerbotState.Running)];
        TeleportFeature teleports = Host.WorldServices.GetRequiredService<TeleportFeature>();
        return running.Length == count && running.All(s => Bots.FindSession(s.BotId)?.Player is { IsInWorld: true } player
            && !teleports.Teleports.IsBeingTeleported(player));
    }, $"{count} bots running and settled");

    public int RunningCount => Bots.Snapshot().Count(s => s.State == ManagedPlayerbotState.Running);

    public async ValueTask DisposeAsync()
    {
        try { await Bots.ShutdownBeforeWorldStopAsync(); }
        finally { await Host.DisposeAsync(); }
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
