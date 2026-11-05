using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// A fact that is reported as Skipped, with the variable to set, unless <c>ARCANECORE_TEST_POSTGRES</c> names a PostgreSQL server
/// (a connection string without a database; the fixture creates and drops its own three databases on it).
/// </summary>
public sealed class PostgresClusterFactAttribute : FactAttribute
{
    public PostgresClusterFactAttribute()
    {
        if (ClusterProcessFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A theory that is reported as Skipped, with the variable to set, unless <c>ARCANECORE_TEST_POSTGRES</c> names a PostgreSQL server (see <see cref="PostgresClusterFactAttribute"/>).</summary>
public sealed class PostgresClusterTheoryAttribute : TheoryAttribute
{
    public PostgresClusterTheoryAttribute()
    {
        if (ClusterProcessFixture.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Shape of the cluster a <see cref="ClusterProcessFixture"/> starts.</summary>
public sealed class ClusterOptions
{
    /// <summary>
    /// Fenced world workers to launch (default 1). More than one runs with <c>Cluster:Fencing:Scope=Worker</c>: each worker takes its own shard advisory lock and ownership
    /// row, so they write the one characters database concurrently, and needs <see cref="WorkerMaps"/>.
    /// </summary>
    public int WorkerCount { get; init; } = 1;

    /// <summary>With more than one worker: the maps each worker owns (Cluster:Worker:OwnedMaps, and the gateway entry's Maps); one array per worker, disjoint.</summary>
    public IReadOnlyList<int[]>? WorkerMaps { get; init; }

    /// <summary>With more than one worker: the index of the worker new sessions open on (Gateway:LoginWorker).</summary>
    public int LoginWorkerIndex { get; init; }

    /// <summary>The security level of the account the fixture creates (default Player). A scenario that types GM or administrator commands needs a higher one.</summary>
    public AccountSecurity Security { get; init; } = AccountSecurity.Player;

    /// <summary>
    /// With more than one worker: turns the per-player transfer hosting on (<c>Cluster:Worker:Transfers:Enabled</c> on every worker, <c>Gateway:Transfers:Enabled</c> on the gateway, which then also
    /// reads the characters database). Default false: the cluster is the one the other scenarios run, with no control channel.
    /// </summary>
    public bool Transfers { get; init; }

    /// <summary>
    /// With more than one worker: when set, every other worker is started with <c>Cluster:Worker:AllowInstanceMaps=false</c>, so it never hosts instance copies
    /// of maps outside its list. This does not make the gateway route an instanceable map: the gateway resolves a far teleport (target instance 0) only through
    /// the listed maps, so the instance worker must also list the dungeon map in <see cref="WorkerMaps"/>. Null leaves the default (every worker hosts instances).
    /// </summary>
    public int? InstanceWorkerIndex { get; init; }

    /// <summary>
    /// With more than one worker: the index of the worker that gets a standby process (a second world started with <c>Cluster:Worker:Standby=true</c> that shares the worker's shard) and the whole-World
    /// handoff hosting (<c>Cluster:Worker:Handoff:Enabled</c> on every worker and the standby, <c>Gateway:Handoff:Enabled</c> and the slot's standby entry on the gateway). Null, the default, starts no standby.
    /// There is no supervisor in this fixture, so a retired source is not relaunched.
    /// </summary>
    public int? StandbyWorkerIndex { get; init; }

    /// <summary>
    /// With more than one worker: the index of a worker that <see cref="ClusterProcessFixture.StartAsync(ClusterOptions?, CancellationToken)"/> does not launch; the scenario launches it
    /// with <see cref="ClusterProcessFixture.StartLateWorkerAsync"/>. This is the order a slow host produces by chance: the gateway registers the other links first and a new session
    /// opens on one of them. Null, the default, launches every worker at once.
    /// </summary>
    public int? LateWorkerIndex { get; init; }

    /// <summary>Extra environment variables for the first launch of the gateway only (not for <see cref="ClusterProcessFixture.RestartGatewayAsync"/>); the crash-mid-transfer scenarios use it to hold the coordinator at a stage.</summary>
    public IReadOnlyDictionary<string, string>? FirstGatewayEnvironment { get; init; }

    /// <summary>Copper a new character starts with (<c>CharacterCreation:StartPlayerMoney</c> on every worker); 0, the default, leaves the retail option unset.</summary>
    public long StartPlayerMoney { get; init; }

    /// <summary>
    /// When set, the world database gets an item template with this entry and a <c>playercreateinfo_item</c> row giving one of it to a human warrior (the race and class the scenario
    /// client creates), so a new character owns a persisted <c>item_instance</c> row. Null, the default, seeds nothing.
    /// </summary>
    public uint? StartingItemEntry { get; init; }

    /// <summary>
    /// When set, the world database gets the <c>map_template</c> row of the Deadmines (map 36, a dungeon), so the workers know the map: with an empty <c>map_template</c> a world
    /// knows only the two continents (<c>MapRegistry.DefaultContinents</c>) and refuses any teleport to another map before it reaches the cluster. A non-empty table replaces
    /// that default, so the two continent rows are seeded with it. False, the default, seeds nothing.
    /// </summary>
    public bool SeedDeadminesTemplate { get; init; }

    /// <summary>
    /// With more than one worker: runs the layout of <c>start-cluster.ps1 -Topology Split</c> including its peer link: every world (and the standby) gets <c>Cluster:Peer:BindAddress</c>, its own
    /// <c>Cluster:Peer:Port</c>, the full node map as <c>Cluster:Peer:Endpoints:n:NodeId/Address</c> (the list form the script uses) and the peer link secret in
    /// <c>ARCANECORE_PEER_LINK_SECRET</c> (the run's link secret, as the script reuses it). The node that holds the Social or Economy lease then listens on its peer port and the others dial it.
    /// False, the default, leaves the peer link unconfigured.
    /// </summary>
    public bool PeerLink { get; init; }
}

/// <summary>One launched world worker.</summary>
public sealed record ClusterWorkerInfo(int Index, string NodeId, int ClientPort, int LinkPort, IReadOnlyList<int> OwnedMaps, int PeerPort = 0);

/// <summary>The standby process of one worker's slot (<see cref="ClusterOptions.StandbyWorkerIndex"/>): its own node id and ports, and the index of the worker it stands behind.</summary>
public sealed record ClusterStandbyInfo(int ForWorkerIndex, string NodeId, int ClientPort, int LinkPort, int PeerPort = 0);

/// <summary>
/// The real <c>ArcaneCore.Gateway</c>, the realm daemon and fenced <c>ArcaneCore.World</c> worker(s) as subprocesses of the test process,
/// over three fresh PostgreSQL databases that this fixture creates, initialises and drops. Every process is owned by the fixture and killed
/// (with its process tree) by <see cref="DisposeAsync"/>, which then fails if any of them is still alive. Not a cluster bring-up: nothing
/// outlives the test, no supervisor, no live paths.
/// <para>
/// Secrets: the gateway/worker link secret and the account password are generated per run and reach the children only through their
/// environment (never a command line, never a file). The PostgreSQL connection string is passed the same way and scrubbed from captured output.
/// </para>
/// <para>Thread affinity: none. Public members may be called from any thread; <see cref="DisposeAsync"/> runs once.</para>
/// </summary>
public sealed class ClusterProcessFixture : IAsyncDisposable
{
    public const string PostgresVariable = "ARCANECORE_TEST_POSTGRES";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(30);

    // Inherited configuration that must not leak into a child: anything that binds into the daemons' configuration, and the test secrets.
    private static readonly string[] ScrubbedEnvironmentPrefixes =
    [
        "Cluster__", "Gateway__", "World__", "Auth__", "Database__", "Realm__", "Realms__", "Net__", "Ops__", "Logging__", "ClientData__",
        "DOTNET_gc", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "ARCANECORE_TEST_", "ARCANECORE_CONFIG", "ARCANECORE_PEER_LINK_SECRET",
    ];

    private readonly ClusterOptions _options;
    private readonly List<ChildProcess> _children = [];
    private readonly List<string> _databases = [];
    private readonly List<ClusterWorkerInfo> _workers = [];
    private readonly Dictionary<int, ChildProcess> _workerProcesses = [];
    private readonly List<ChildProcess> _retired = [];
    private readonly string _postgresBase;
    private readonly string _linkSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-cluster-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly object _gate = new();
    private readonly PortReservations _ports = new(PortLockDirectory);
    private Task? _disposeTask;
    private int _realmId;
    private string _authConnection = string.Empty;
    private string _charactersConnection = string.Empty;
    private string _worldConnection = string.Empty;
    private int _realmPort;
    private int _gatewayPort;
    private ClusterStandbyInfo? _standby;
    private ChildProcess? _standbyProcess;
    private ChildProcess? _gatewayProcess;
    private ITestOutputHelper? _output;

    private ClusterProcessFixture(ClusterOptions options)
    {
        _options = options;
        _postgresBase = Environment.GetEnvironmentVariable(PostgresVariable)!.Trim().TrimEnd(';');
        AccountName = "E2E" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)); // uppercase, 11 characters
        AccountPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)); // 16 uppercase hex chars: the client caps passwords at 16 and uppercases them before hashing
    }

    /// <summary>
    /// What the child-log capture replaces with *** before a line is kept (the dump goes to the test output, which CI publishes): the link secret, the account password,
    /// every connection string, and the bare PostgreSQL password on its own, since a driver error can print it without the rest of its connection string.
    /// </summary>
    private string[] Secrets()
    {
        string postgresPassword = string.Empty;
        try
        {
            postgresPassword = new NpgsqlConnectionStringBuilder(_postgresBase).Password ?? string.Empty;
        }
        catch (ArgumentException)
        {
            // Not a parseable connection string: the whole variable is still scrubbed below.
        }

        return [_linkSecret, AccountPassword, _postgresBase, _authConnection, _charactersConnection, _worldConnection, postgresPassword];
    }

    /// <summary>The reason every cluster test is skipped, or null when <c>ARCANECORE_TEST_POSTGRES</c> is set.</summary>
    public static string? SkipReason => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable))
        ? $"Set {PostgresVariable} to a PostgreSQL 16 server connection string (without a database) to run the multi-process cluster tests."
        : null;

    /// <summary>The account the fixture created in the realm's auth database.</summary>
    public string AccountName { get; }

    /// <summary>This run's generated password of <see cref="AccountName"/>.</summary>
    public string AccountPassword { get; }

    /// <summary>The realm daemon's logon listener.</summary>
    public IPEndPoint RealmEndpoint => new(IPAddress.Loopback, _realmPort);

    /// <summary>The gateway's client listener (the address of the realm-list row).</summary>
    public IPEndPoint GatewayEndpoint => new(IPAddress.Loopback, _gatewayPort);

    /// <summary>The launched workers in index order.</summary>
    public IReadOnlyList<ClusterWorkerInfo> Workers => _workers;

    /// <summary>The standby process, or null when <see cref="ClusterOptions.StandbyWorkerIndex"/> is not set.</summary>
    public ClusterStandbyInfo? Standby => _standby;

    /// <summary>Whether the standby process is still running (false when there is none).</summary>
    public bool IsStandbyRunning => _standbyProcess is { HasExited: false };

    /// <summary>
    /// Runs a read-only <c>SELECT count(*)</c> (or any single-bigint query) on the cluster's characters database, for a scenario that asserts on the journal tables
    /// (<c>cluster_transfer_decision</c>, <c>cluster_world_handoff</c>). The statement is the test's own text, never player input.
    /// </summary>
    public async Task<long> CharactersScalarAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        var builder = new NpgsqlConnectionStringBuilder(_charactersConnection) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Runs a read-only multi-row query on the cluster's characters database and maps each row with <paramref name="read"/> (for a scenario that asserts on text columns the scalar form cannot return).
    /// The statement is the test's own text, never player input; the connection is unpooled so the fixture can drop the database.
    /// </summary>
    public async Task<IReadOnlyList<T>> CharactersRowsAsync<T>(string sql, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(read);
        var builder = new NpgsqlConnectionStringBuilder(_charactersConnection) { Pooling = false };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    /// <summary>Launch the cluster. Throws <see cref="InvalidOperationException"/> when <see cref="SkipReason"/> is set; a failed start disposes what it launched.</summary>
    public static Task<ClusterProcessFixture> StartAsync(ClusterOptions? options = null, CancellationToken cancellationToken = default)
        => StartAsync(options, output: null, cancellationToken);

    /// <summary>
    /// Launch the cluster, and when <paramref name="output"/> is set, write every child process's captured output (the last lines of each, secrets scrubbed) to it when the
    /// fixture is disposed. The trx of a CI run keeps the test output but none of the children's stdout, so this is the only record of what the gateway and the workers
    /// did in a failed scenario (why a client was closed, which link a session opened on, a refused claim). xunit 2 gives the fixture no test outcome, so it is written for
    /// every test; the tail is bounded per process.
    /// </summary>
    public static async Task<ClusterProcessFixture> StartAsync(ClusterOptions? options, ITestOutputHelper? output, CancellationToken cancellationToken = default)
    {
        if (SkipReason is { } skip)
        {
            throw new InvalidOperationException(skip);
        }

        options ??= new ClusterOptions();
        Validate(options);
        var fixture = new ClusterProcessFixture(options) { _output = output };
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);
        try
        {
            await fixture.InitializeAsync(startup.Token).ConfigureAwait(false);
            return fixture;
        }
        catch (Exception startFailure)
        {
            try
            {
                await fixture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Cluster startup and cleanup failed.", startFailure, cleanupFailure);
            }

            if (startFailure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Cluster startup exceeded {StartupTimeout.TotalSeconds:0} seconds.", startFailure);
            }

            throw;
        }
    }

    private static void Validate(ClusterOptions options)
    {
        if (options.WorkerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WorkerCount must be at least 1.");
        }

        if (options.InstanceWorkerIndex is { } instanceWorker && (options.WorkerCount < 2 || instanceWorker < 0 || instanceWorker >= options.WorkerCount))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "InstanceWorkerIndex needs more than one worker and must name one.");
        }

        if (options.WorkerCount > 1)
        {
            if (options.WorkerMaps is null || options.WorkerMaps.Count != options.WorkerCount)
            {
                throw new ArgumentException("More than one worker needs one WorkerMaps entry per worker.", nameof(options));
            }

            int[] all = [.. options.WorkerMaps.SelectMany(maps => maps)];
            if (all.Length == 0 || all.Distinct().Count() != all.Length)
            {
                throw new ArgumentException("WorkerMaps must be non-empty and must not overlap.", nameof(options));
            }

            if (options.LoginWorkerIndex < 0 || options.LoginWorkerIndex >= options.WorkerCount)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "LoginWorkerIndex must name a worker.");
            }

            if (options.StandbyWorkerIndex is { } standby && (standby < 0 || standby >= options.WorkerCount))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "StandbyWorkerIndex must name a worker.");
            }

            if (options.LateWorkerIndex is { } late && (late < 0 || late >= options.WorkerCount || late == options.StandbyWorkerIndex))
            {
                throw new ArgumentOutOfRangeException(nameof(options), "LateWorkerIndex must name a worker without a standby.");
            }
        }
        else if (options.PeerLink)
        {
            throw new ArgumentException("PeerLink needs more than one worker: the peer link joins the workers of a Split layout.", nameof(options));
        }
        else if (options.Transfers || options.StandbyWorkerIndex is not null || options.LateWorkerIndex is not null)
        {
            throw new ArgumentException("Transfers and a standby need more than one worker (the gateway's Workers list names the slots).", nameof(options));
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        string suffix = Guid.NewGuid().ToString("N")[..12];
        _authConnection = Connection("auth", suffix);
        _charactersConnection = Connection("characters", suffix);
        _worldConnection = Connection("world", suffix);

        _realmPort = FreePort();
        _gatewayPort = FreePort();
        for (int index = 0; index < _options.WorkerCount; index++)
        {
            _workers.Add(new ClusterWorkerInfo(
                index, $"e2e-w{index}", FreePort(), FreePort(),
                _options.WorkerCount > 1 ? _options.WorkerMaps![index] : [],
                _options.PeerLink ? FreePort() : 0));
        }

        if (_options.StandbyWorkerIndex is { } standbyFor)
        {
            _standby = new ClusterStandbyInfo(standbyFor, _workers[standbyFor].NodeId + "-standby", FreePort(), FreePort(), _options.PeerLink ? FreePort() : 0);
        }

        await PrepareDatabasesAsync(cancellationToken).ConfigureAwait(false);

        string[] secrets = Secrets();
        _children.Add(ChildProcess.Start("realm", "realm", "ArcaneCore.Realm", Path.Combine(_root, "realm"), RealmEnvironment(), secrets));
        foreach (ClusterWorkerInfo worker in _workers)
        {
            if (worker.Index == _options.LateWorkerIndex)
            {
                continue; // launched by the scenario (StartLateWorkerAsync)
            }

            ChildProcess child = ChildProcess.Start(
                worker.NodeId, "world", "ArcaneCore.World", Path.Combine(_root, worker.NodeId), WorldEnvironment(worker), secrets);
            _children.Add(child);
            _workerProcesses[worker.Index] = child;
        }

        if (_standby is { } standbyInfo)
        {
            _standbyProcess = ChildProcess.Start(
                standbyInfo.NodeId, "world", "ArcaneCore.World", Path.Combine(_root, standbyInfo.NodeId), StandbyEnvironment(standbyInfo), secrets);
            _children.Add(_standbyProcess);
        }

        _gatewayProcess = ChildProcess.Start("gateway", "gateway", "ArcaneCore.Gateway", Path.Combine(_root, "gateway"), GatewayEnvironment(firstLaunch: true), secrets);
        _children.Add(_gatewayProcess);

        var listeners = new List<(string Name, int Port)> { ("realm", _realmPort), ("gateway", _gatewayPort) };
        listeners.AddRange(_workers.Where(worker => worker.Index != _options.LateWorkerIndex).Select(worker => (worker.NodeId + " client listener", worker.ClientPort)));
        if (_standby is { } standbyListener)
        {
            // A standby opens no client listener until it is promoted; its gateway link listener is the readiness signal.
            listeners.Add((standbyListener.NodeId + " link listener", standbyListener.LinkPort));
        }

        foreach ((string name, int port) in listeners)
        {
            await WaitForListenerAsync(name, port, cancellationToken).ConfigureAwait(false);
        }
    }

    private string Connection(string component, string suffix)
    {
        string database = $"arcane_e2e_{component}_{suffix}";
        _databases.Add(database);
        return $"{_postgresBase};Database={database}";
    }

    /// <summary>
    /// Creates and initialises the auth, characters and world schemas in this (the test) process, the way <c>SyntheticArcaneServer</c> does
    /// (AddAuthDatabase, AddCharacterDatabase, AddWorldDatabase, AddWorldDaemon), then seeds the realm row (address = the gateway port) and the account.
    /// </summary>
    private async Task PrepareDatabasesAsync(CancellationToken cancellationToken)
    {
        MockWorldPacks.EnsureRegistered(); // before anything reads the startup code set, as the synthetic server does
        var values = new Dictionary<string, string?>();
        foreach ((string component, string connection) in new[] { ("Auth", _authConnection), ("Characters", _charactersConnection), ("World", _worldConnection) })
        {
            values[$"Database:{component}:Provider"] = nameof(DatabaseProvider.PostgreSql);
            values[$"Database:{component}:ConnectionString"] = connection;
        }

        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_ => configuration);
        services.AddLogging();
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<RealmSeedOptions>(seed => seed.Seed.Add(new RealmSeedEntry
        {
            Name = "ArcaneCore Cluster E2E",
            Address = $"127.0.0.1:{_gatewayPort.ToString(CultureInfo.InvariantCulture)}",
            Type = RealmType.Normal,
            Flags = RealmFlags.None,
        }));
        services.AddAuthDatabase(configuration);
        services.AddCharacterDatabase(configuration);
        services.AddWorldDatabase(configuration);
        services.AddWorldDaemon(configuration);

        ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            await provider.GetRequiredService<AuthDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
            await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
            await provider.GetRequiredService<WorldDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            if (_options.StartingItemEntry is { } startingItem)
            {
                // Human (race 1) warrior (class 1): the character the scenario client creates (ScenarioWire.CharacterCreate).
                await using WorldDbContext world = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorldDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                world.Set<ItemTemplateRow>().Add(new ItemTemplateRow
                {
                    Entry = startingItem, Class = 7, Name = "Crash Test Cloth", DisplayId = 3776, Quality = 1, AllowableClass = -1, AllowableRace = -1, Stackable = 20,
                });
                world.Set<PlayerCreateInfoItemRow>().Add(new PlayerCreateInfoItemRow { Race = 1, Class = 1, ItemId = startingItem, Amount = 1 });
                await world.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_options.SeedDeadminesTemplate)
            {
                // The values of the Data.Tests map_template case (MapDataModuleTests): a 10-player instance whose ghosts enter in Westfall. A non-empty map_template
                // replaces MapRegistry.DefaultContinents, so the continents (common maps, ghost entrance -1, as the default rows) are seeded with it.
                await using WorldDbContext world = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorldDbContext>>().CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                world.Set<MapTemplateRow>().AddRange(
                    new MapTemplateRow { Entry = 0, MapType = 0, GhostEntranceMap = -1, MapName = "Eastern Kingdoms" },
                    new MapTemplateRow { Entry = 1, MapType = 0, GhostEntranceMap = -1, MapName = "Kalimdor" },
                    new MapTemplateRow
                    {
                        Entry = 36, Parent = 0, MapType = 1, PlayerLimit = 10, GhostEntranceMap = 0, GhostEntranceX = -11208.4f, GhostEntranceY = 1672.3f, MapName = "Deadmines",
                    });
                await world.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            AuthDbContext auth = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            // The initializer seeds a row as Stopped (offline in the list). This fixture's row is unmanaged: State Unknown, no Offline flag,
            // so the realm list shows it online whatever the worker has reported yet.
            await auth.Realms.ExecuteUpdateAsync(
                setters => setters.SetProperty(realm => realm.State, RealmRuntimeState.Unknown), cancellationToken).ConfigureAwait(false);
            _realmId = await auth.Realms.Select(realm => realm.Id).SingleAsync(cancellationToken).ConfigureAwait(false);

            (byte[] salt, byte[] verifier) = WowSrp6.CreateCredentials(AccountName, AccountPassword);
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAsync(new Account
            {
                Username = AccountName,
                Salt = salt,
                Verifier = verifier,
                Security = _options.Security,
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            NpgsqlConnection.ClearAllPools(); // no pooled session of this process may keep a database the cleanup drops
        }
    }

    private Dictionary<string, string> CommonEnvironment() => new()
    {
        // Workstation GC keeps three .NET processes small on a shared box (DOTNET_gcServer=0 restores it without a rebuild).
        ["DOTNET_gcServer"] = "0",
        ["Logging__LogLevel__Default"] = "Warning",
        // The cluster's own story at Information: link registrations, session opens and redirects, why the gateway closed a client, why a login was refused.
        // These lines are what the child-log dump (WriteChildLogs) shows for a failed scenario; they are few per test.
        ["Logging__LogLevel__ArcaneCore.Gateway"] = "Information",
        ["Logging__LogLevel__ArcaneCore.World.Cluster"] = "Information",
        ["Logging__LogLevel__ArcaneCore.World.Net"] = "Information",
        ["Ops__Watchdog__Enabled"] = "false",
    };

    private Dictionary<string, string> RealmEnvironment()
    {
        Dictionary<string, string> env = CommonEnvironment();
        env["Auth__BindAddress"] = IPAddress.Loopback.ToString();
        env["Auth__Port"] = _realmPort.ToString(CultureInfo.InvariantCulture);
        env["Auth__AutocreateAccounts"] = "false";
        env["Realm__Console__Enabled"] = "false";
        AddDatabase(env, "Auth", _authConnection);
        return env;
    }

    private Dictionary<string, string> WorldEnvironment(ClusterWorkerInfo worker)
    {
        Dictionary<string, string> env = CommonEnvironment();
        env["World__BindAddress"] = IPAddress.Loopback.ToString();
        env["World__Port"] = worker.ClientPort.ToString(CultureInfo.InvariantCulture);
        env["World__TickIntervalMs"] = "20";
        env["World__AutosaveIntervalMs"] = "0";
        env["World__InstantLogoutSecurity"] = "Player"; // the baseline logout is answered at once instead of after the 20 s delay
        env["World__Console__Enabled"] = "false";
        env["World__RemoteAdmin__Enabled"] = "false";
        env["Cluster__Supervisor__Enabled"] = "false";
        env["Cluster__Fencing__Enabled"] = "true";
        env["Cluster__Fencing__OwnerNode"] = worker.NodeId;
        env["Cluster__Worker__Enabled"] = "true";
        env["Cluster__Worker__BindAddress"] = IPAddress.Loopback.ToString();
        env["Cluster__Worker__Port"] = worker.LinkPort.ToString(CultureInfo.InvariantCulture);
        env["Cluster__Worker__LinkSecret"] = _linkSecret;
        env["Cluster__Worker__NodeId"] = worker.NodeId;
        env["Cluster__Worker__RealmId"] = _realmId.ToString(CultureInfo.InvariantCulture);
        if (_options.WorkerCount > 1)
        {
            env["Cluster__Worker__OwnedMaps"] = string.Join(',', worker.OwnedMaps);
            if (_options.InstanceWorkerIndex is { } instanceWorker && instanceWorker != worker.Index)
            {
                env["Cluster__Worker__AllowInstanceMaps"] = "false";
            }

            // Several workers write one characters database: each takes its own shard lock and ownership scope (design _wf/writer-lock-DESIGN.md, C8), as start-cluster -Topology Split runs them.
            env["Cluster__Fencing__Scope"] = "Worker";
            env["Cluster__Node__Role"] = "MapWorker";
        }

        // The opt-in control channel (design _wf/handoff-control-DESIGN.md section 2.6): both keys stay off unless the scenario asks for them.
        if (_options.StandbyWorkerIndex is not null)
        {
            env["Cluster__Worker__Handoff__Enabled"] = "true";
        }

        if (_options.Transfers)
        {
            env["Cluster__Worker__Transfers__Enabled"] = "true";
        }

        if (_options.PeerLink)
        {
            AddPeerLinkEnvironment(env, worker.PeerPort);
        }

        if (_options.StartPlayerMoney > 0)
        {
            env["CharacterCreation__StartPlayerMoney"] = _options.StartPlayerMoney.ToString(CultureInfo.InvariantCulture);
        }

        AddDatabase(env, "Auth", _authConnection);
        AddDatabase(env, "Characters", _charactersConnection);
        AddDatabase(env, "World", _worldConnection);
        return env;
    }

    /// <summary>
    /// The standby of one worker: the worker's environment with its own node id, owner node and ports, <c>Cluster:Worker:Standby=true</c>, and the worker's shard
    /// (<c>Cluster:Fencing:Shard</c>), so that its promotion takes over that worker's durable scope. It claims nothing and opens no client listener until promoted.
    /// </summary>
    private Dictionary<string, string> StandbyEnvironment(ClusterStandbyInfo standby)
    {
        ClusterWorkerInfo active = _workers[standby.ForWorkerIndex];
        Dictionary<string, string> env = WorldEnvironment(active);
        env["World__Port"] = standby.ClientPort.ToString(CultureInfo.InvariantCulture);
        env["Cluster__Fencing__OwnerNode"] = standby.NodeId;
        env["Cluster__Fencing__Shard"] = active.NodeId;
        env["Cluster__Worker__Port"] = standby.LinkPort.ToString(CultureInfo.InvariantCulture);
        env["Cluster__Worker__NodeId"] = standby.NodeId;
        env["Cluster__Worker__Standby"] = "true";
        if (_options.PeerLink)
        {
            AddPeerLinkEnvironment(env, standby.PeerPort);
        }

        return env;
    }

    /// <summary>
    /// The peer link settings of one world, as start-cluster.ps1 -Topology Split passes them: bind address, this world's own peer port, every world's (and the standby's) node id and
    /// address in the list form (<c>Cluster:Peer:Endpoints:n</c>, which a node id containing a colon needs) and the secret in its environment variable (never a configuration key).
    /// </summary>
    private void AddPeerLinkEnvironment(Dictionary<string, string> env, int ownPeerPort)
    {
        env["Cluster__Peer__BindAddress"] = IPAddress.Loopback.ToString();
        env["Cluster__Peer__Port"] = ownPeerPort.ToString(CultureInfo.InvariantCulture);
        env["Cluster__Peer__HolderPollMs"] = "250"; // a failover test does not wait the production second; the default still applies to every other setting
        env["ARCANECORE_PEER_LINK_SECRET"] = _linkSecret;
        var nodes = new List<(string NodeId, int PeerPort)>(_workers.Select(worker => (worker.NodeId, worker.PeerPort)));
        if (_standby is { } standby)
        {
            nodes.Add((standby.NodeId, standby.PeerPort));
        }

        for (int n = 0; n < nodes.Count; n++)
        {
            string prefix = $"Cluster__Peer__Endpoints__{n.ToString(CultureInfo.InvariantCulture)}__";
            env[prefix + "NodeId"] = nodes[n].NodeId;
            env[prefix + "Address"] = $"{IPAddress.Loopback}:{nodes[n].PeerPort.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    private Dictionary<string, string> GatewayEnvironment(bool firstLaunch)
    {
        Dictionary<string, string> env = CommonEnvironment();
        if (firstLaunch && _options.FirstGatewayEnvironment is { } extra)
        {
            foreach ((string key, string value) in extra)
            {
                env[key] = value;
            }
        }

        env["Gateway__BindAddress"] = IPAddress.Loopback.ToString();
        env["Gateway__Port"] = _gatewayPort.ToString(CultureInfo.InvariantCulture);
        env["Gateway__LinkSecret"] = _linkSecret;
        env["Gateway__NodeId"] = "e2e-gateway";
        env["Gateway__RealmId"] = _realmId.ToString(CultureInfo.InvariantCulture);
        if (_options.WorkerCount == 1)
        {
            env["Gateway__WorkerAddress"] = IPAddress.Loopback.ToString();
            env["Gateway__WorkerPort"] = _workers[0].LinkPort.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            env["Gateway__LoginWorker"] = _workers[_options.LoginWorkerIndex].NodeId;
            foreach (ClusterWorkerInfo worker in _workers)
            {
                string prefix = $"Gateway__Workers__{worker.Index.ToString(CultureInfo.InvariantCulture)}__";
                env[prefix + "NodeId"] = worker.NodeId;
                env[prefix + "Address"] = IPAddress.Loopback.ToString();
                env[prefix + "Port"] = worker.LinkPort.ToString(CultureInfo.InvariantCulture);
                for (int map = 0; map < worker.OwnedMaps.Count; map++)
                {
                    env[$"{prefix}Maps__{map.ToString(CultureInfo.InvariantCulture)}"] = worker.OwnedMaps[map].ToString(CultureInfo.InvariantCulture);
                }

                if (_standby is { } standby && standby.ForWorkerIndex == worker.Index)
                {
                    env[prefix + "StandbyNodeId"] = standby.NodeId;
                    env[prefix + "StandbyAddress"] = IPAddress.Loopback.ToString();
                    env[prefix + "StandbyPort"] = standby.LinkPort.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        AddDatabase(env, "Auth", _authConnection);
        if (_options.Transfers || _standby is not null)
        {
            // The gateway coordinates from the journal in the characters database (read validate-only: it creates and upgrades nothing; the fixture initialised the schema).
            AddDatabase(env, "Characters", _charactersConnection);
            if (_standby is not null)
            {
                env["Gateway__Handoff__Enabled"] = "true";
            }

            if (_options.Transfers)
            {
                env["Gateway__Transfers__Enabled"] = "true";
            }
        }

        return env;
    }

    private static void AddDatabase(Dictionary<string, string> env, string component, string connection)
    {
        env[$"Database__{component}__Provider"] = nameof(DatabaseProvider.PostgreSql);
        env[$"Database__{component}__ConnectionString"] = connection;
    }

    // Fixture ports come from below every ephemeral range in use (Linux default 32768-60999, Windows 49152-65535; a CI node may narrow
    // its range to a few thousand ports). A port the kernel hands out for bind(0) is itself ephemeral: between this probe and the child's
    // bind, any outbound connection (each process's pooled database connections, a gateway's link retries) may take it as its local port,
    // and the child then dies with "Address already in use". Below the ephemeral range only listeners compete, the probe sees them, and the
    // per-process counter (from a random start, so concurrent test processes diverge) never hands one port out twice.
    // The range is disjoint from ArcaneCore.TestSupport.LoopbackPorts (20000-31999), which the in-process test hosts of the other suites
    // use while this suite runs beside them in a gate. The probe cannot hold a port for a child process: between it and the child's bind
    // there are seconds of database preparation and child startup, and a test host that picked the same port in that window took the
    // child's port (the child died at startup with "the world port ... is already in use") and answered the readiness probe in its place.
    private const int PortRangeStart = 12000;
    internal const int PortRangeSize = 8000;
    private static int s_nextPort = RandomNumberGenerator.GetInt32(PortRangeSize);

    // Cross-process reservation: a port is also claimed by an exclusive lock file (FileShare.None, an flock on Linux) in a shared temp directory, held until
    // this fixture is disposed (the OS drops it when the process dies). Concurrent gate runs on one host then never pick the same port, even though the
    // probe cannot bind it for a child. An unusable directory means no reservation (PortReservations.Enabled is false): the listener probe alone decides, as before.
    private static readonly string PortLockDirectory = Path.Combine(Path.GetTempPath(), "arcanecore-cluster-e2e-ports");

    private int FreePort() => PickFreePort(_ports, CanListenEverywhere, () => PortRangeStart + (int)((uint)Interlocked.Increment(ref s_nextPort) % PortRangeSize));

    private static bool CanListenEverywhere(int port) => CanListen(IPAddress.Any, port) && CanListen(IPAddress.Loopback, port);

    /// <summary>
    /// The fixture's port choice, parameterised so a test can pin it to <see cref="PortReservations"/>: takes candidates from <paramref name="next"/> (at most <see cref="PortRangeSize"/> of them),
    /// skips one <paramref name="canListen"/> refuses or <paramref name="ports"/> cannot reserve, and returns the first it reserved.
    /// </summary>
    internal static int PickFreePort(PortReservations ports, Func<int, bool> canListen, Func<int> next)
    {
        for (int attempt = 0; attempt < PortRangeSize; attempt++)
        {
            int port = next();
            if (!canListen(port))
            {
                continue;
            }

            if (ports.TryReserve(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException($"No free port in {PortRangeStart}-{PortRangeStart + PortRangeSize - 1}.");
    }

    private static bool CanListen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task WaitForListenerAsync(string name, int port, CancellationToken cancellationToken)
    {
        while (true)
        {
            ThrowIfAnyChildExited();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ThrowIfAnyChildExited()
    {
        foreach (ChildProcess child in _children)
        {
            // A process the test killed on purpose (KillWorkerAsync, KillGatewayAsync) and has not restarted is expected to be gone.
            if (child.HasExited && !child.KilledByTest)
            {
                throw new InvalidOperationException($"Cluster process '{child.Name}' exited with code {child.ExitCode} before the test finished.{Environment.NewLine}{child.Tail()}");
            }
        }
    }

    /// <summary>The ids of launched processes (or their descendants found by executable path) that are still alive. Empty after a clean <see cref="DisposeAsync"/>.</summary>
    public IReadOnlyList<int> LiveProcessIds()
    {
        var alive = new List<int>();
        foreach (ChildProcess child in _children)
        {
            if (!child.HasExited)
            {
                alive.Add(child.Id);
            }
        }

        foreach (int id in ChildProcess.FindStrayProcesses(Path.Combine(AppContext.BaseDirectory, "daemons"), _startedUtc))
        {
            if (!alive.Contains(id))
            {
                alive.Add(id);
            }
        }

        return alive;
    }

    /// <summary>Whether the world worker with this index is still running.</summary>
    public bool IsWorkerRunning(int index) => _workerProcesses.TryGetValue(index, out ChildProcess? child) && !child.HasExited;

    /// <summary>Kill one world worker (and its process tree) without a graceful stop, as a crash would.</summary>
    public async Task KillWorkerAsync(int index)
    {
        if (!_workerProcesses.TryGetValue(index, out ChildProcess? child))
        {
            throw new ArgumentOutOfRangeException(nameof(index), "No worker with that index.");
        }

        child.KilledByTest = true;
        await child.KillAsync().ConfigureAwait(false);
    }

    /// <summary>Starts the killed world worker with this index again on its original ports, node id and databases, and waits for its client listener.</summary>
    public async Task RestartWorkerAsync(int index, CancellationToken cancellationToken)
    {
        if (!_workerProcesses.TryGetValue(index, out ChildProcess? old))
        {
            throw new ArgumentOutOfRangeException(nameof(index), "No worker with that index.");
        }

        if (!old.HasExited)
        {
            throw new InvalidOperationException($"Worker {index} is still running; kill it before restarting.");
        }

        ClusterWorkerInfo worker = _workers[index];
        _children.Remove(old);
        _retired.Add(old);
        string[] secrets = Secrets();
        ChildProcess child = ChildProcess.Start(
            worker.NodeId, "world", "ArcaneCore.World", Path.Combine(_root, worker.NodeId), WorldEnvironment(worker), secrets);
        _children.Add(child);
        _workerProcesses[index] = child;
        await WaitForListenerAsync(worker.NodeId + " client listener", worker.ClientPort, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Launches the worker named by <see cref="ClusterOptions.LateWorkerIndex"/> (once) and waits for its client listener.</summary>
    public async Task StartLateWorkerAsync(CancellationToken cancellationToken)
    {
        if (_options.LateWorkerIndex is not { } index || _workerProcesses.ContainsKey(index))
        {
            throw new InvalidOperationException("No late worker is configured, or it was already started.");
        }

        ClusterWorkerInfo worker = _workers[index];
        string[] secrets = Secrets();
        ChildProcess child = ChildProcess.Start(
            worker.NodeId, "world", "ArcaneCore.World", Path.Combine(_root, worker.NodeId), WorldEnvironment(worker), secrets);
        _children.Add(child);
        _workerProcesses[index] = child;
        await WaitForListenerAsync(worker.NodeId + " client listener", worker.ClientPort, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes once the running gateway process has logged a line containing <paramref name="fragment"/> (secrets scrubbed; the fixture runs the gateway's
    /// own categories at Information). Lets a scenario wait for an event the gateway only logs instead of sleeping a fixed time.
    /// </summary>
    public Task WaitForGatewayOutputAsync(string fragment, CancellationToken cancellationToken)
    {
        ChildProcess gateway = _gatewayProcess ?? throw new InvalidOperationException("The gateway is not running.");
        return gateway.WaitForLineAsync(fragment, cancellationToken);
    }

    /// <summary>
    /// Starts watching the running gateway's output and completes on the next line containing <paramref name="fragment"/>. Unlike <see cref="WaitForGatewayOutputAsync"/>, a line
    /// already written does not count, so call this before the event: a worker's link registers at startup too, and that line is still in the tail when the worker is restarted.
    /// </summary>
    public Task WaitForNextGatewayOutputAsync(string fragment, CancellationToken cancellationToken)
    {
        ChildProcess gateway = _gatewayProcess ?? throw new InvalidOperationException("The gateway is not running.");
        return gateway.WaitForLineAsync(fragment, cancellationToken, onlyNewLines: true);
    }

    /// <summary>
    /// Completes once the running world worker with this index has logged a line containing <paramref name="fragment"/> (secrets scrubbed; the fixture runs the <c>ArcaneCore.World.Cluster</c>
    /// categories at Information). A line already written counts.
    /// </summary>
    public Task WaitForWorkerOutputAsync(int index, string fragment, CancellationToken cancellationToken)
    {
        if (!_workerProcesses.TryGetValue(index, out ChildProcess? child))
        {
            throw new ArgumentOutOfRangeException(nameof(index), "No worker with that index.");
        }

        return child.WaitForLineAsync(fragment, cancellationToken);
    }

    /// <summary>Kill the gateway (and its process tree) without a graceful stop, as a crash would.</summary>
    public Task KillGatewayAsync()
    {
        ChildProcess gateway = _gatewayProcess ?? throw new InvalidOperationException("The gateway was not started.");
        gateway.KilledByTest = true;
        return gateway.KillAsync();
    }

    /// <summary>Starts the killed gateway again on its original port (without <see cref="ClusterOptions.FirstGatewayEnvironment"/>) and waits for its listener.</summary>
    public async Task RestartGatewayAsync(CancellationToken cancellationToken)
    {
        ChildProcess old = _gatewayProcess ?? throw new InvalidOperationException("The gateway was not started.");
        if (!old.HasExited)
        {
            throw new InvalidOperationException("The gateway is still running; kill it before restarting.");
        }

        _children.Remove(old);
        _retired.Add(old);
        string[] secrets = Secrets();
        _gatewayProcess = ChildProcess.Start("gateway", "gateway", "ArcaneCore.Gateway", Path.Combine(_root, "gateway"), GatewayEnvironment(firstLaunch: false), secrets);
        _children.Add(_gatewayProcess);
        await WaitForListenerAsync("gateway", _gatewayPort, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Logon at the realm daemon, then connect and authenticate at the gateway, as a client does. The processes are still starting
    /// when this is first called, so a refused connection and the gateway's "unavailable" answer (no worker link yet) are retried until
    /// <paramref name="cancellationToken"/> or a child process exit. The caller owns and disposes the client.
    /// </summary>
    public async Task<WorldClient> ConnectGatewayClientAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfAnyChildExited();
            WorldClient? client = null;
            try
            {
                LogonResult logon = await LogonClient.AuthenticateAsync(RealmEndpoint, AccountName, AccountPassword, cancellationToken).ConfigureAwait(false);
                client = await WorldClient.ConnectAsync(GatewayEndpoint, cancellationToken).ConfigureAwait(false);
                byte code = await client.AuthenticateAsync(AccountName, logon.SessionKey, cancellationToken).ConfigureAwait(false);
                if (code == (byte)AuthResponseCode.Ok)
                {
                    WorldClient authenticated = client;
                    client = null;
                    return authenticated;
                }

                if (code != (byte)AuthResponseCode.Unavailable)
                {
                    throw new InvalidOperationException($"The gateway refused the login with response code 0x{code:X2}.");
                }
            }
            catch (Exception ex) when (ex is (IOException and not (MockProtocolException or MockAuthException)) or SocketException && !cancellationToken.IsCancellationRequested)
            {
                // A listener that is not up yet, or a link that dropped mid-handshake. MockProtocolException (a deterministic protocol
                // failure) and MockAuthException (bad credentials) are both IOExceptions but are excluded, so they are not retried: they fail the test at once.
            }
            finally
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Kills every process, drops the databases, removes the temporary directory, then throws if any launched process is still alive.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        foreach (ChildProcess child in Enumerable.Reverse(_children))
        {
            try
            {
                await child.KillAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        WriteChildLogs();

        try
        {
            await DropDatabasesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add(ex); // a handle still open on the directory means a process is still alive
        }

        foreach (ChildProcess child in _children.Concat(_retired))
        {
            child.Dispose();
        }

        _ports.Dispose();

        // The orphan assertion: nothing this fixture launched may outlive it.
        IReadOnlyList<int> alive = LiveProcessIds();
        if (alive.Count > 0)
        {
            failures.Add(new InvalidOperationException($"Cluster processes still alive after disposal: {string.Join(", ", alive)}."));
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Cluster fixture cleanup failed.", failures);
        }
    }

    /// <summary>Writes the captured output of every process this fixture launched (restarted ones included, in launch order) to the test output, when one was given.</summary>
    private void WriteChildLogs()
    {
        if (_output is not { } output)
        {
            return;
        }

        try
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cluster fixture started {_startedUtc:HH:mm:ss.fff} UTC, disposed {DateTime.UtcNow:HH:mm:ss.fff} UTC"));
            foreach (ChildProcess child in _retired.Concat(_children).OrderBy(child => child.Sequence))
            {
                output.WriteLine(child.Tail());
            }
        }
        catch (InvalidOperationException)
        {
            // The test has already finished: xunit no longer accepts output for it.
        }
    }

    private async Task DropDatabasesAsync()
    {
        if (_databases.Count == 0)
        {
            return;
        }

        var admin = new NpgsqlConnectionStringBuilder(_postgresBase) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (string database in _databases)
        {
            // Without WITH (FORCE), which needs privileges a test role often lacks: end this role's sessions, then drop, retrying while the server still reports it in use.
            for (int attempt = 0; ; attempt++)
            {
                await using (NpgsqlCommand terminate = connection.CreateCommand())
                {
                    terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid() AND usename = current_user";
                    terminate.Parameters.AddWithValue("db", database);
                    await terminate.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                try
                {
                    await using NpgsqlCommand drop = connection.CreateCommand();
                    drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\"";
                    await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
                    break;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 40)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>One subprocess: output captured (bounded, secrets scrubbed) and killed with its tree.</summary>
    private sealed class ChildProcess : IDisposable
    {
        // The tail kept per process: enough for a whole scenario at the fixture's log levels (the dump in WriteChildLogs), bounded so a chatty child cannot grow the test output.
        private const int TailLines = 300;
        private const int MaximumLineLength = 400;
        private static int s_sequence;
        private readonly Process _process;
        private readonly string[] _secrets;
        private readonly Queue<string> _tail = new();
        private readonly object _gate = new();
        private readonly List<(string Fragment, TaskCompletionSource Seen)> _watches = [];
        private int _dropped;

        private ChildProcess(string name, Process process, string[] secrets)
        {
            Name = name;
            _process = process;
            _secrets = secrets;
            Sequence = Interlocked.Increment(ref s_sequence);
        }

        internal string Name { get; }

        /// <summary>Launch order across the fixture (a restarted process is a new, later one).</summary>
        internal int Sequence { get; }

        /// <summary>Set by the fixture before a deliberate crash kill, so its exit is not reported as an unexpected one.</summary>
        internal bool KilledByTest { get; set; }

        private int _id;
        private bool _disposed;
        private bool _exitedAtDispose;
        private int _exitCodeAtDispose;

        internal int Id => _id;

        /// <summary>Safe after <see cref="Dispose"/>: Process.Close() resets the Process, so the state is cached before disposing.</summary>
        internal bool HasExited => _disposed ? _exitedAtDispose : _process.HasExited;

        internal int ExitCode => _disposed ? _exitCodeAtDispose : _process.ExitCode;

        /// <summary>Start a daemon from the copy under <c>daemons/&lt;folder&gt;</c> of the test output: its apphost on Windows, <c>dotnet &lt;stem&gt;.dll</c> elsewhere.</summary>
        internal static ChildProcess Start(
            string name, string folder, string stem, string workingDirectory, IReadOnlyDictionary<string, string> environment, string[] secrets)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "daemons", folder);
            Directory.CreateDirectory(workingDirectory);
            var info = new ProcessStartInfo
            {
                // The temp working directory is the host content root, so the daemons skip their shipped appsettings.json and run on options defaults (deliberate isolation).
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string apphost = Path.Combine(directory, stem + ".exe");
            if (OperatingSystem.IsWindows() && File.Exists(apphost))
            {
                info.FileName = apphost;
            }
            else
            {
                string dll = Path.Combine(directory, stem + ".dll");
                if (!File.Exists(dll))
                {
                    throw new FileNotFoundException($"The {name} daemon was not copied to the test output ({dll}); build the test project.", dll);
                }

                string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
                info.FileName = !string.IsNullOrWhiteSpace(host) && File.Exists(host) ? host : "dotnet";
                info.ArgumentList.Add(dll);
            }

            foreach (string key in info.Environment.Keys.ToArray())
            {
                if (ScrubbedEnvironmentPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    info.Environment.Remove(key);
                }
            }

            foreach ((string key, string value) in environment)
            {
                info.Environment[key] = value;
            }

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var child = new ChildProcess(name, process, secrets);
            process.OutputDataReceived += (_, e) => child.Append(e.Data);
            process.ErrorDataReceived += (_, e) => child.Append(e.Data);
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                process.Dispose();
                throw new InvalidOperationException($"The {name} daemon could not be started: {ex.Message}", ex);
            }

            child._id = process.Id;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return child;
        }

        internal async Task KillAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // Already gone between the check and the kill.
            }

            using var timeout = new CancellationTokenSource(KillTimeout);
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Cluster process '{Name}' did not exit within {KillTimeout.TotalSeconds:0} seconds of being killed.");
            }
        }

        /// <summary>
        /// Completes once this process has written a line containing <paramref name="fragment"/> (a line still in the tail counts, and so does any later one; with
        /// <paramref name="onlyNewLines"/> only a later one). The watch is in place when this returns.
        /// </summary>
        internal async Task WaitForLineAsync(string fragment, CancellationToken cancellationToken, bool onlyNewLines = false)
        {
            ArgumentException.ThrowIfNullOrEmpty(fragment);
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            (string Fragment, TaskCompletionSource Seen) watch = (fragment, seen);
            lock (_gate)
            {
                if (!onlyNewLines && _tail.Any(line => line.Contains(fragment, StringComparison.Ordinal)))
                {
                    return;
                }

                _watches.Add(watch);
            }

            try
            {
                await seen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _watches.Remove(watch); // a cancelled wait leaves no watch behind (a matched one was removed by Append already)
                }
            }
        }

        internal string Tail()
        {
            lock (_gate)
            {
                string dropped = _dropped > 0 ? $" ({_dropped} earlier lines dropped)" : string.Empty;
                return $"--- last output of '{Name}' (launch {Sequence}, pid {_id}){dropped}; each line starts with the UTC time it was read ---{Environment.NewLine}{string.Join(Environment.NewLine, _tail)}";
            }
        }

        /// <summary>Processes started after <paramref name="since"/> from a copy under <paramref name="daemonsRoot"/> (Windows apphosts only; elsewhere the launched ids are the whole check).</summary>
        internal static IEnumerable<int> FindStrayProcesses(string daemonsRoot, DateTime since)
        {
            if (!OperatingSystem.IsWindows())
            {
                yield break;
            }

            string root = Path.GetFullPath(daemonsRoot);
            foreach (string stem in new[] { "ArcaneCore.Gateway", "ArcaneCore.Realm", "ArcaneCore.World" })
            {
                foreach (Process candidate in Process.GetProcessesByName(stem))
                {
                    int id = -1;
                    try
                    {
                        string? path = candidate.MainModule?.FileName;
                        if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                            && !candidate.HasExited && candidate.StartTime.ToUniversalTime() >= since)
                        {
                            id = candidate.Id;
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        // Exited, or not ours to inspect: not an orphan of this fixture.
                    }
                    finally
                    {
                        candidate.Dispose();
                    }

                    if (id >= 0)
                    {
                        yield return id;
                    }
                }
            }
        }

        private void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            foreach (string secret in _secrets)
            {
                if (secret.Length > 0)
                {
                    line = line.Replace(secret, "***", StringComparison.Ordinal);
                }
            }

            if (line.Length > MaximumLineLength)
            {
                line = line[..MaximumLineLength];
            }

            line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line;
            lock (_gate)
            {
                for (int index = _watches.Count - 1; index >= 0; index--)
                {
                    if (line.Contains(_watches[index].Fragment, StringComparison.Ordinal))
                    {
                        _watches[index].Seen.TrySetResult();
                        _watches.RemoveAt(index);
                    }
                }

                _tail.Enqueue(line);
                while (_tail.Count > TailLines)
                {
                    _tail.Dequeue();
                    _dropped++;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _exitedAtDispose = _process.HasExited;
                _exitCodeAtDispose = _exitedAtDispose ? _process.ExitCode : 0;
            }
            catch (InvalidOperationException)
            {
                _exitedAtDispose = true; // no longer associated with a process: nothing is running
            }

            _disposed = true;
            _process.Dispose();
        }
    }
}
