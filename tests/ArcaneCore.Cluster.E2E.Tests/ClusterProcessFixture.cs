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

/// <summary>Shape of the cluster a <see cref="ClusterProcessFixture"/> starts.</summary>
public sealed class ClusterOptions
{
    /// <summary>
    /// Fenced world workers to launch. One is the supported shape today: the characters writer lock is one advisory lock per realm
    /// (s1-per-worker-writer-lock), so a second concurrent worker is refused. Greater than one needs <see cref="WorkerMaps"/>.
    /// </summary>
    public int WorkerCount { get; init; } = 1;

    /// <summary>With more than one worker: the maps each worker owns (Cluster:Worker:OwnedMaps, and the gateway entry's Maps); one array per worker, disjoint.</summary>
    public IReadOnlyList<int[]>? WorkerMaps { get; init; }

    /// <summary>With more than one worker: the index of the worker new sessions open on (Gateway:LoginWorker).</summary>
    public int LoginWorkerIndex { get; init; }
}

/// <summary>One launched world worker.</summary>
public sealed record ClusterWorkerInfo(int Index, string NodeId, int ClientPort, int LinkPort, IReadOnlyList<int> OwnedMaps);

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
        "DOTNET_gc", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "ARCANECORE_TEST_",
    ];

    private readonly ClusterOptions _options;
    private readonly List<ChildProcess> _children = [];
    private readonly List<string> _databases = [];
    private readonly List<ClusterWorkerInfo> _workers = [];
    private readonly Dictionary<int, ChildProcess> _workerProcesses = [];
    private readonly string _postgresBase;
    private readonly string _linkSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-cluster-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly object _gate = new();
    private Task? _disposeTask;
    private int _realmId;
    private string _authConnection = string.Empty;
    private string _charactersConnection = string.Empty;
    private string _worldConnection = string.Empty;
    private int _realmPort;
    private int _gatewayPort;

    private ClusterProcessFixture(ClusterOptions options)
    {
        _options = options;
        _postgresBase = Environment.GetEnvironmentVariable(PostgresVariable)!.Trim().TrimEnd(';');
        AccountName = "E2E" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)); // uppercase, 11 characters
        AccountPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)); // uppercase: the client uppercases it before hashing
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

    /// <summary>Launch the cluster. Throws <see cref="InvalidOperationException"/> when <see cref="SkipReason"/> is set; a failed start disposes what it launched.</summary>
    public static async Task<ClusterProcessFixture> StartAsync(ClusterOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (SkipReason is { } skip)
        {
            throw new InvalidOperationException(skip);
        }

        options ??= new ClusterOptions();
        Validate(options);
        var fixture = new ClusterProcessFixture(options);
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
                _options.WorkerCount > 1 ? _options.WorkerMaps![index] : []));
        }

        await PrepareDatabasesAsync(cancellationToken).ConfigureAwait(false);

        string[] secrets = [_linkSecret, AccountPassword, _postgresBase, _authConnection, _charactersConnection, _worldConnection];
        _children.Add(ChildProcess.Start("realm", "realm", "ArcaneCore.Realm", Path.Combine(_root, "realm"), RealmEnvironment(), secrets));
        foreach (ClusterWorkerInfo worker in _workers)
        {
            ChildProcess child = ChildProcess.Start(
                worker.NodeId, "world", "ArcaneCore.World", Path.Combine(_root, worker.NodeId), WorldEnvironment(worker), secrets);
            _children.Add(child);
            _workerProcesses[worker.Index] = child;
        }

        _children.Add(ChildProcess.Start("gateway", "gateway", "ArcaneCore.Gateway", Path.Combine(_root, "gateway"), GatewayEnvironment(), secrets));

        var listeners = new List<(string Name, int Port)> { ("realm", _realmPort), ("gateway", _gatewayPort) };
        listeners.AddRange(_workers.Select(worker => (worker.NodeId + " client listener", worker.ClientPort)));
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
                Security = AccountSecurity.Player,
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
        }

        AddDatabase(env, "Auth", _authConnection);
        AddDatabase(env, "Characters", _charactersConnection);
        AddDatabase(env, "World", _worldConnection);
        return env;
    }

    private Dictionary<string, string> GatewayEnvironment()
    {
        Dictionary<string, string> env = CommonEnvironment();
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
            }
        }

        AddDatabase(env, "Auth", _authConnection);
        return env;
    }

    private static void AddDatabase(Dictionary<string, string> env, string component, string connection)
    {
        env[$"Database__{component}__Provider"] = nameof(DatabaseProvider.PostgreSql);
        env[$"Database__{component}__ConnectionString"] = connection;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
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
            if (child.HasExited)
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

        await child.KillAsync().ConfigureAwait(false);
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
            catch (Exception ex) when (ex is (IOException and not MockProtocolException) or SocketException && !cancellationToken.IsCancellationRequested)
            {
                // A listener that is not up yet, or a link that dropped mid-handshake. A MockProtocolException (a deterministic protocol
                // failure) and a MockAuthException (bad credentials) are not retried: they fail the test at once.
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

        foreach (ChildProcess child in _children)
        {
            child.Dispose();
        }

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
        private const int TailLines = 60;
        private const int MaximumLineLength = 400;
        private readonly Process _process;
        private readonly string[] _secrets;
        private readonly Queue<string> _tail = new();
        private readonly object _gate = new();

        private ChildProcess(string name, Process process, string[] secrets)
        {
            Name = name;
            _process = process;
            _secrets = secrets;
        }

        internal string Name { get; }

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

        internal string Tail()
        {
            lock (_gate)
            {
                return $"--- last output of '{Name}' ---{Environment.NewLine}{string.Join(Environment.NewLine, _tail)}";
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

            lock (_gate)
            {
                _tail.Enqueue(line);
                while (_tail.Count > TailLines)
                {
                    _tail.Dequeue();
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
