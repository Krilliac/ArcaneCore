using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A fact that runs only when <see cref="Variable"/> lists the running-bot counts to measure ("50,100,200,400") and the real data is
/// there: <c>ARCANECORE_TEST_TERRAIN_DIR</c> (maps, vmaps, mmaps) and <c>ARCANECORE_TEST_WORLD_DB</c> (a copy of a live world.db).
/// Without them it is reported Skipped, never a silent pass.
/// </summary>
internal sealed class BotTerrainBenchmarkFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_BOT_BENCH";

    public BotTerrainBenchmarkFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable))
            || !File.Exists(Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.WorldVariable) ?? "")
            || !Directory.Exists(Path.Combine(Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable) ?? "", "mmaps")))
        {
            Skip = $"{Variable}, {LiveSnapshotReplayFactAttribute.WorldVariable} and {RealTerrainBotFactAttribute.Variable} are not all set: "
                + "the managed-bot terrain benchmark did NOT run (docs/integration/tick-scaling-20261009.md).";
        }
    }
}

/// <summary>
/// The live stress shape in one process (docs/integration/tick-scaling-20261009.md): managed bots created and started through the
/// ordinary <c>CreateAsync</c>/<c>StartAsync</c> in the ten race/class pairs of the live stress script (both factions, six starting
/// zones on maps 0 and 1), on the real terrain, models and navigation meshes, with the real content of a copied world database
/// (creatures, game objects, quests, NPC services) and a fresh SQLite characters database, on the real-clock 50 ms world loop with
/// the live autosave interval. Not a correctness test: wall time is only reported.
/// <para>
/// For each step of <see cref="BotTerrainBenchmarkFactAttribute.Variable"/> the bots are topped up, left to spread out for
/// <c>ARCANECORE_BOT_BENCH_WARMUP_SECONDS</c> (default 20) and measured for <c>ARCANECORE_BOT_BENCH_HOLD_SECONDS</c> (default 60):
/// tick work (whole <c>RunTick</c>, nearest-rank), its phases, the map sub-phases (packets, heartbeats, each map updater,
/// visibility, values, flush, grid state machine, terrain clean-up, deferred work), grid lifecycle work (grids created / loaded /
/// unloaded and the time their handlers took), the managed-bot feature, the world thread's allocation, GC and the slowest ticks.
/// Optional: <c>ARCANECORE_BOT_BENCH_SETTINGS</c> (a settings file without a <c>Database</c> section: client DBC paths),
/// <c>ARCANECORE_BOT_BENCH_CSV</c> (append one row per step), <c>ARCANECORE_BOT_BENCH_ALLOC_TYPES=1</c> (allocation sample by type),
/// <c>ARCANECORE_BOT_BENCH_LABEL</c> (a label for the rows).
/// </para>
/// </summary>
[Collection("Managed bot scale")]
public sealed class ManagedPlayerbotTerrainBenchmarkTests(ITestOutputHelper output) : IDisposable
{
    private static readonly (byte Race, byte Class)[] RaceClass = [(1, 1), (2, 1), (1, 2), (2, 3), (3, 3), (5, 8), (4, 11), (6, 7), (7, 9), (8, 5)];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-bot-bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { } // a handle still closing; the temp copy is harmless
    }

    [BotTerrainBenchmarkFact]
    public async Task ManagedBots_OnRealTerrainAndContent_TickCostIsReported()
    {
        int[] steps = Environment.GetEnvironmentVariable(BotTerrainBenchmarkFactAttribute.Variable)!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.Parse(s, CultureInfo.InvariantCulture)).Order().ToArray();
        int hold = Seconds("ARCANECORE_BOT_BENCH_HOLD_SECONDS", 60);
        int warmup = Seconds("ARCANECORE_BOT_BENCH_WARMUP_SECONDS", 20);
        string label = Environment.GetEnvironmentVariable("ARCANECORE_BOT_BENCH_LABEL") ?? "run";
        string? csv = Environment.GetEnvironmentVariable("ARCANECORE_BOT_BENCH_CSV");
        string terrain = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;

        Directory.CreateDirectory(_directory);
        string characters = Path.Combine(_directory, "characters.db");
        string world = Path.Combine(_directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.WorldVariable)!, world);

        var builder = new ConfigurationBuilder();
        if (Environment.GetEnvironmentVariable("ARCANECORE_BOT_BENCH_SETTINGS") is { Length: > 0 } settings)
        {
            // Never the live databases: a settings file with connection strings is refused, not overridden key by key.
            Assert.False(new ConfigurationBuilder().AddJsonFile(settings).Build().GetSection("Database").Exists(),
                "the benchmark settings must not carry a Database section");
            builder.AddJsonFile(settings);
        }

        IConfiguration configuration = builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Characters:Provider"] = "Sqlite",
            ["Database:Characters:ConnectionString"] = $"Data Source={characters};Pooling=False",
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={world};Pooling=False",
            ["Database:Auth:Provider"] = "Sqlite",
            ["Database:Auth:ConnectionString"] = $"Data Source={Path.Combine(_directory, "auth.db")};Pooling=False",
        }).Build();
        await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
            await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();

        int max = steps[^1];
        await using WorldTestHost host = WorldTestHost.Start(
            configure: options =>
            {
                options.TickIntervalMs = 50;
                options.AutosaveIntervalMs = 10_000; // the live profile's World:AutosaveIntervalMs
                options.Maps.DataDirectory = terrain;
            },
            configureServices: services =>
            {
                services.AddSingleton(configuration);
                services.AddWorldDatabase(configuration);
                services.AddCharacterDatabase(configuration);
                // The bot registry rows (managed_playerbot) in memory unless ARCANECORE_BOT_BENCH_OWNERS=sqlite: every 5 s the feature
                // rewrites each running bot's row while holding its operations lock, and on SQLite that starves the ramp (about 28 s
                // per bot started past 150 bots on a loaded box). The tick does not touch the rows.
                if (Environment.GetEnvironmentVariable("ARCANECORE_BOT_BENCH_OWNERS") != "sqlite")
                {
                    services.AddSingleton<IManagedPlayerbotStore>(new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore());
                }

                services.AddSingleton<IManagedPlayerbotProvisionStore>(sp =>
                    new ManagedPlayerbotLifecycleTests.MemoryProvisionStore((InMemoryAccountStore)sp.GetRequiredService<IAccountStore>()));
                // The live profile's World:Playerbots (MaxBots raised to the largest step).
                services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
                {
                    Enabled = true, MaxBots = max, MaxRegisteredBots = Math.Max(max, 1), ThinkIntervalMs = 100, MaxActionsPerTick = 12,
                    MaxPathPoints = 128, MaxRouteYards = 2000, MoveSpeed = 7,
                }));
            });
        ManagedPlayerbotFeature bots = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await bots.StartupAsync(default);

        var recorder = new Recorder();
        await host.OnWorldAsync(() =>
        {
            host.World.TickObserver = recorder.Tick;
            host.World.MapUpdateObserver = recorder.Map;
            host.World.FeatureObserver = recorder.Feature;
        });
        output.WriteLine($"label {label}; steps {string.Join(",", steps)}; warmup {warmup} s; hold {hold} s; {Environment.ProcessorCount} logical CPUs; server GC {System.Runtime.GCSettings.IsServerGC}");
        output.WriteLine(Recorder.Header);
        string? progress = Environment.GetEnvironmentVariable("ARCANECORE_BOT_BENCH_PROGRESS");
        void Progress(string line)
        {
            if (!string.IsNullOrEmpty(progress)) File.AppendAllText(progress, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }

        // Every bot is created before any starts (creating is cheap while nothing runs), then each step starts the next ones.
        var created = new List<Guid>(max);
        for (int i = 0; i < max; i++)
        {
            (byte race, byte cls) = RaceClass[i % RaceClass.Length];
            PlayerbotOperationResult result = await bots.CreateAsync(Name(i), race, cls);
            Assert.True(result.Success, $"create {i}: {result.Code}");
            created.Add(result.BotId!.Value);
        }

        Progress($"created {max}");
        int running = 0;
        try
        {
            foreach (int target in steps)
            {
                var ramp = Stopwatch.StartNew();
                for (; running < target; running++)
                {
                    PlayerbotOperationResult started = await bots.StartAsync(created[running].ToString());
                    Assert.True(started.Success, $"start {running}: {started.Code}");
                    if (running % 25 == 24) Progress($"started {running + 1}");
                }

                ramp.Stop();
                Progress($"step {target}: ramp {ramp.Elapsed.TotalSeconds:F0} s; warmup");
                await Task.Delay(TimeSpan.FromSeconds(warmup));
                using AllocationSampler? sampler = Environment.GetEnvironmentVariable("ARCANECORE_BOT_BENCH_ALLOC_TYPES") == "1" ? new AllocationSampler() : null;
                int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
                TimeSpan pauseBefore = GC.GetTotalPauseDuration();
                TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                var wall = Stopwatch.StartNew();
                recorder.Reset();
                await Task.Delay(TimeSpan.FromSeconds(hold));
                StepResult result = recorder.Take();
                double cores = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds / wall.Elapsed.TotalSeconds;
                int runningNow = bots.Snapshot().Count(b => b.State == ManagedPlayerbotState.Running);
                string population = await host.OnWorldAsync(() => string.Join(" ", host.World.Maps
                    .Where(m => m.PlayerCount > 0 || m.Grids.LoadedGridCount > 0)
                    .Select(m => string.Create(CultureInfo.InvariantCulture, $"map{m.MapId}:{m.PlayerCount}p/{m.Grids.LoadedGridCount}g/{m.ObjectCount}o"))));
                string row = result.Row(label, runningNow, cores, GC.CollectionCount(0) - gcBefore[0], GC.CollectionCount(1) - gcBefore[1],
                    GC.CollectionCount(2) - gcBefore[2], (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds, ramp.Elapsed.TotalSeconds);
                output.WriteLine(row);
                Progress(row);
                output.WriteLine("  population " + population);
                foreach (string line in result.Details())
                {
                    output.WriteLine("  " + line);
                }

                if (sampler is not null)
                {
                    foreach ((string type, long bytes) in sampler.Top(25))
                    {
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  alloc-sample {running} bots: {bytes / 1024} KiB {type}"));
                    }
                }

                if (!string.IsNullOrEmpty(csv))
                {
                    if (!File.Exists(csv))
                    {
                        File.AppendAllText(csv, Recorder.Header + Environment.NewLine);
                    }

                    File.AppendAllText(csv, row + Environment.NewLine);
                }

                Assert.True(result.Ticks > 10, "too few ticks recorded");
            }

            Assert.Equal(running, bots.Snapshot().Count(b => b.State == ManagedPlayerbotState.Running));
        }
        finally
        {
            await host.OnWorldAsync(() =>
            {
                host.World.TickObserver = null;
                host.World.MapUpdateObserver = null;
                host.World.FeatureObserver = null;
            });
            await bots.ShutdownBeforeWorldStopAsync();
        }
    }

    private static int Seconds(string variable, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    /// <summary>Distinct letters-only character names: "Bench" + base-26 index ("Benchaaa", "Benchaab", ...).</summary>
    private static string Name(int index)
    {
        char[] suffix = new char[3];
        for (int i = 2; i >= 0; i--)
        {
            suffix[i] = (char)('a' + (index % 26));
            index /= 26;
        }

        return "Bench" + new string(suffix);
    }

    /// <summary>One tick's breakdown (microseconds and bytes), kept for the slowest ticks.</summary>
    private sealed class TickSample
    {
        public long Micros, Bytes, Commands, Maps, Features, BotFeature;
        public long Packets, Heartbeat, Simulation, Visibility, Values, Flush, Cleanup, Grid, Terrain, Deferred;
        public long GridCreateMicros, GridLoadMicros, GridUnloadMicros, GridsCreated, GridsLoaded, GridsUnloaded, NearCheckObjects;
        public long Players, Moved;
        public readonly Dictionary<string, long> Updaters = new(StringComparer.Ordinal);
        public readonly List<string> SlowMaps = [];

        public void Clear()
        {
            Micros = Bytes = Commands = Maps = Features = BotFeature = 0;
            Packets = Heartbeat = Simulation = Visibility = Values = Flush = Cleanup = Grid = Terrain = Deferred = 0;
            GridCreateMicros = GridLoadMicros = GridUnloadMicros = GridsCreated = GridsLoaded = GridsUnloaded = NearCheckObjects = 0;
            Players = Moved = 0;
            Updaters.Clear();
            SlowMaps.Clear();
        }

        public void AddTo(TickSample total)
        {
            total.Micros += Micros; total.Bytes += Bytes; total.Commands += Commands; total.Maps += Maps; total.Features += Features;
            total.BotFeature += BotFeature; total.Packets += Packets; total.Heartbeat += Heartbeat; total.Simulation += Simulation;
            total.Visibility += Visibility; total.Values += Values; total.Flush += Flush; total.Cleanup += Cleanup; total.Grid += Grid;
            total.Terrain += Terrain; total.Deferred += Deferred; total.GridCreateMicros += GridCreateMicros; total.GridLoadMicros += GridLoadMicros;
            total.GridUnloadMicros += GridUnloadMicros; total.GridsCreated += GridsCreated; total.GridsLoaded += GridsLoaded;
            total.GridsUnloaded += GridsUnloaded; total.NearCheckObjects += NearCheckObjects; total.Players += Players; total.Moved += Moved;
            foreach ((string name, long micros) in Updaters)
            {
                total.Updaters[name] = total.Updaters.GetValueOrDefault(name) + micros;
            }
        }

        public string Describe()
            => string.Create(CultureInfo.InvariantCulture,
                $"{Micros / 1000.0:F1} ms: commands {Commands / 1000.0:F1}, maps {Maps / 1000.0:F1} (packets {Packets / 1000.0:F1}, heartbeat {Heartbeat / 1000.0:F1}, "
                + $"updaters {string.Join("/", Updaters.Where(u => u.Value >= 1000).OrderByDescending(u => u.Value).Select(u => $"{u.Key} {u.Value / 1000.0:F1}"))}, "
                + $"visibility {Visibility / 1000.0:F1}, values {Values / 1000.0:F1}, flush {Flush / 1000.0:F1}, grid {Grid / 1000.0:F1}, terrain {Terrain / 1000.0:F1}, deferred {Deferred / 1000.0:F1}; "
                + $"grids +{GridsCreated}c/{GridsLoaded}l -{GridsUnloaded} create {GridCreateMicros / 1000.0:F1} load {GridLoadMicros / 1000.0:F1} unload {GridUnloadMicros / 1000.0:F1}), "
                + $"features {Features / 1000.0:F1} (bots {BotFeature / 1000.0:F1}); {Bytes / 1024} KiB [{string.Join("; ", SlowMaps)}]");
    }

    /// <summary>What one step measured.</summary>
    private sealed record StepResult(int Ticks, long[] Sorted, TickSample Total, long[] MapsSorted, long[] BotSorted,
        long MapsBytes, long FeaturesBytes, long BotBytes, List<TickSample> Slowest)
    {
        public string Row(string label, int bots, double cores, int gen0, int gen1, int gen2, double pauseMs, double rampSeconds)
        {
            double n = Ticks;
            return string.Create(CultureInfo.InvariantCulture,
                $"{label},{bots},{Ticks},{Total.Micros / n / 1000:F3},{Ms(Sorted, 50)},{Ms(Sorted, 95)},{Ms(Sorted, 99)},{Sorted[^1] / 1000.0:F3},"
                + $"{Total.Commands / n / 1000:F3},{Total.Maps / n / 1000:F3},{Ms(MapsSorted, 95)},{Total.Features / n / 1000:F3},{Total.BotFeature / n / 1000:F3},{Ms(BotSorted, 95)},"
                + $"{Total.Packets / n / 1000:F3},{Total.Heartbeat / n / 1000:F3},{(Total.Simulation - Total.Packets - Total.Heartbeat) / n / 1000:F3},"
                + $"{Total.Visibility / n / 1000:F3},{Total.Values / n / 1000:F3},{Total.Flush / n / 1000:F3},{Total.Cleanup / n / 1000:F3},"
                + $"{Total.Grid / n / 1000:F3},{Total.Terrain / n / 1000:F3},{Total.Deferred / n / 1000:F3},"
                + $"{Total.GridsCreated},{Total.GridsLoaded},{Total.GridsUnloaded},{Total.GridCreateMicros / 1000.0:F1},{Total.GridLoadMicros / 1000.0:F1},{Total.GridUnloadMicros / 1000.0:F1},"
                + $"{Total.Bytes / n / 1024:F1},{MapsBytes / n / 1024:F1},{FeaturesBytes / n / 1024:F1},{BotBytes / n / 1024:F1},"
                + $"{gen0},{gen1},{gen2},{pauseMs:F0},{cores:F2},{Total.Players / n:F0},{Total.Moved / n:F0},{rampSeconds:F1}");
        }

        public IEnumerable<string> Details()
        {
            double n = Ticks;
            yield return "map updaters (mean ms/tick): " + string.Join(", ", Total.Updaters.OrderByDescending(u => u.Value)
                .Select(u => string.Create(CultureInfo.InvariantCulture, $"{u.Key} {u.Value / n / 1000:F3}")));
            yield return string.Create(CultureInfo.InvariantCulture, $"grid activity checks visited {Total.NearCheckObjects} active objects");
            foreach (TickSample slow in Slowest)
            {
                yield return "slow tick " + slow.Describe();
            }
        }
    }

    private static string Ms(long[] sorted, double percentile)
        => (TickStats.NearestRank(sorted, percentile) / 1000d).ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>World thread: collects every tick's breakdown; <see cref="Take"/> summarises them (any thread).</summary>
    private sealed class Recorder
    {
        public const string Header = "label,bots,ticks,mean_ms,p50_ms,p95_ms,p99_ms,max_ms,commands_ms,maps_ms,maps_p95_ms,features_ms,botfeature_ms,botfeature_p95_ms,"
            + "packets_ms,heartbeat_ms,updaters_ms,visibility_ms,values_ms,flush_ms,cleanup_ms,grid_ms,terrain_ms,deferred_ms,"
            + "grids_created,grids_loaded,grids_unloaded,grid_create_ms_total,grid_load_ms_total,grid_unload_ms_total,"
            + "alloc_kib,maps_alloc_kib,features_alloc_kib,botfeature_alloc_kib,gen0,gen1,gen2,gc_pause_ms,process_cores,players,moved,ramp_s";

        private const int KeepSlowest = 5;
        private readonly object _gate = new();
        private readonly TickSample _current = new();
        private readonly TickSample _total = new();
        private readonly List<long> _ticks = new(8192);
        private readonly List<long> _maps = new(8192);
        private readonly List<long> _bot = new(8192);
        private readonly List<TickSample> _slowest = [];
        private long _mapsBytes, _featuresBytes, _botBytes;

        public void Map(Map map, MapUpdateDiagnostics d)
        {
            _current.Packets += d.PacketsMicros;
            _current.Heartbeat += d.HeartbeatMicros;
            _current.Simulation += d.SimulationMicros;
            _current.Visibility += d.VisibilityMicros;
            _current.Values += d.ValuesMicros;
            _current.Flush += d.FlushMicros;
            _current.Cleanup += d.CleanupMicros;
            _current.Grid += d.GridMicros;
            _current.Terrain += d.TerrainMicros;
            _current.Deferred += d.DeferredMicros;
            _current.GridsCreated += d.Grids.Created;
            _current.GridsLoaded += d.Grids.Loaded;
            _current.GridsUnloaded += d.Grids.Unloaded;
            _current.GridCreateMicros += d.Grids.CreateMicros;
            _current.GridLoadMicros += d.Grids.LoadMicros;
            _current.GridUnloadMicros += d.Grids.UnloadMicros;
            _current.NearCheckObjects += d.Grids.NearCheckObjects;
            _current.Players += d.Players;
            _current.Moved += d.MovedObjects;
            foreach ((IMapUpdater updater, long micros) in d.Updaters)
            {
                string name = updater.GetType().Name;
                _current.Updaters[name] = _current.Updaters.GetValueOrDefault(name) + micros;
            }

            long total = d.SimulationMicros + d.VisibilityMicros + d.ValuesMicros + d.FlushMicros + d.CleanupMicros;
            if (total >= 20_000)
            {
                _current.SlowMaps.Add(string.Create(CultureInfo.InvariantCulture,
                    $"map {map.MapId} {total / 1000.0:F1} ms sim {d.SimulationMicros / 1000.0:F1} cleanup {d.CleanupMicros / 1000.0:F1} grids +{d.Grids.Created}/{d.Grids.Loaded} -{d.Grids.Unloaded}"));
            }
        }

        public void Feature(string name, long micros, long bytes)
        {
            if (name == nameof(ManagedPlayerbotFeature))
            {
                _current.BotFeature += micros;
                _botBytes += bytes; // world thread only; read under the gate after Reset
            }
        }

        public void Tick(long micros, long bytes, TickPhases phases)
        {
            _current.Micros = micros;
            _current.Bytes = bytes;
            _current.Commands = phases.CommandsMicros;
            _current.Maps = phases.MapsMicros;
            _current.Features = phases.FeaturesMicros;
            lock (_gate)
            {
                _ticks.Add(micros);
                _maps.Add(phases.MapsMicros);
                _bot.Add(_current.BotFeature);
                _mapsBytes += phases.MapsBytes;
                _featuresBytes += phases.FeaturesBytes;
                _current.AddTo(_total);
                if (_slowest.Count < KeepSlowest || micros > _slowest[^1].Micros)
                {
                    var copy = new TickSample();
                    _current.AddTo(copy);
                    copy.SlowMaps.AddRange(_current.SlowMaps);
                    _slowest.Add(copy);
                    _slowest.Sort((a, b) => b.Micros.CompareTo(a.Micros));
                    if (_slowest.Count > KeepSlowest) _slowest.RemoveAt(_slowest.Count - 1);
                }
            }

            _current.Clear();
        }

        public void Reset()
        {
            lock (_gate)
            {
                _ticks.Clear();
                _maps.Clear();
                _bot.Clear();
                _total.Clear();
                _slowest.Clear();
                _mapsBytes = _featuresBytes = 0;
                Interlocked.Exchange(ref _botBytes, 0);
            }
        }

        public StepResult Take()
        {
            lock (_gate)
            {
                var total = new TickSample();
                _total.AddTo(total);
                return new StepResult(_ticks.Count, _ticks.Order().ToArray(), total, _maps.Order().ToArray(), _bot.Order().ToArray(),
                    _mapsBytes, _featuresBytes, Interlocked.Read(ref _botBytes), [.. _slowest]);
            }
        }
    }
}
