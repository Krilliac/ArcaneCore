using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Combat;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.World;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Social;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.MockClient.Hosting;

/// <summary>
/// A disposable synthetic realm and world backed by the real SQLite stores, daemon
/// features and session handlers. Both listeners are owned IPv4 loopback sockets.
/// </summary>
public sealed class SyntheticArcaneServer : IAsyncDisposable
{
    public const uint JournalQuestId = SyntheticQuestContent.JournalQuestId;
    public const uint NpcQuestId = SyntheticQuestContent.NpcQuestId;
    public const uint RewardQuestId = SyntheticQuestContent.RewardQuestId;
    public const uint NpcEntry = SyntheticQuestContent.NpcEntry;
    public const uint NpcSpawn = SyntheticQuestContent.NpcSpawn;
    public const ulong NpcGuid = ((ulong)0xF130 << 48) | ((ulong)NpcEntry << 24) | NpcSpawn;
    public const uint TargetEntry = SyntheticQuestContent.TargetEntry;
    public const uint FirstTargetSpawn = SyntheticQuestContent.FirstTargetSpawn;
    public const uint SecondTargetSpawn = SyntheticQuestContent.SecondTargetSpawn;
    public const ulong FirstTargetGuid = ((ulong)0xF130 << 48) | ((ulong)TargetEntry << 24) | FirstTargetSpawn;
    public const ulong SecondTargetGuid = ((ulong)0xF130 << 48) | ((ulong)TargetEntry << 24) | SecondTargetSpawn;
    public const uint FixedRewardItem = SyntheticQuestContent.FixedRewardItem;
    public const uint UnchosenRewardItem = SyntheticQuestContent.UnchosenRewardItem;
    public const uint ChosenRewardItem = SyntheticQuestContent.ChosenRewardItem;
    public const uint RewardMoney = SyntheticQuestContent.RewardMoney;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly OwnedFixtureDirectory _directory;
    private readonly TcpListener _realmListener;
    private readonly TcpListener _worldListener;
    private readonly ServiceProvider _services;
    private readonly WorldHost _worldHost;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<TcpClient> _clients = [];
    private readonly List<Task> _sessions = [];
    private Task _startupTask = Task.CompletedTask;
    private Task _guildsLoaded = Task.CompletedTask;
    private Task _realmAcceptLoop = Task.CompletedTask;
    private Task _worldAcceptLoop = Task.CompletedTask;
    private Task? _disposeTask;

    private SyntheticArcaneServer(
        OwnedFixtureDirectory directory, TcpListener realmListener, TcpListener worldListener, ServiceProvider services)
    {
        _directory = directory;
        _realmListener = realmListener;
        _worldListener = worldListener;
        _services = services;
        RealmEndpoint = (IPEndPoint)realmListener.LocalEndpoint;
        WorldEndpoint = (IPEndPoint)worldListener.LocalEndpoint;
        World = services.GetRequiredService<WorldRuntime>();
        // A deterministic roll source changes only the combat roll. Client attacks still
        // pass through CombatHandlers, the map swing loop, health loss and UnitKilled.
        World.MapCreated += static map => map.Combat.Random = SyntheticCombatRandom.Instance;
        _worldHost = services.GetServices<IHostedService>().OfType<WorldHost>().Single();
    }

    public IPEndPoint RealmEndpoint { get; }

    public IPEndPoint WorldEndpoint { get; }

    /// <summary>Create a scope before resolving a database store or context.</summary>
    public IServiceProvider Services => _services;

    public WorldRuntime World { get; }

    /// <summary>The unique fixture directory, removed after sessions and saves drain.</summary>
    public string DataDirectory => _directory.Path;

    /// <summary>Observe actual handler writes after reward settlement and both ordered save queues drain.</summary>
    public async Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await World.InvokeAsync(() => true).WaitAsync(cancellationToken).ConfigureAwait(false);
        await _services.GetRequiredService<QuestNpcFeature>().WaitForSettlementAsync(characterId, cancellationToken)
            .ConfigureAwait(false);
        await _services.GetRequiredService<QuestNpcFeature>().Persistence.FlushCharacterAsync(characterId)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        await _services.GetRequiredService<CharacterSaveQueue>().FlushCharacterAsync(characterId, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<SyntheticArcaneServer> StartAsync(CancellationToken cancellationToken = default)
        => StartAsync(configureServices: null, cancellationToken);

    /// <summary>Configure additional synthetic collaborators before the real world starts.</summary>
    public static async Task<SyntheticArcaneServer> StartAsync(
        Action<IServiceCollection>? configureServices, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);
        CancellationToken startupToken = startup.Token;
        Task<SyntheticArcaneServer> starting = Task.Run(
            () => CreateAndInitializeAsync(configureServices, startupToken), CancellationToken.None);
        try
        {
            return await starting.WaitAsync(startupToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation can win the wait as creation succeeds. Keep ownership of that
            // unpublished result and dispose it as well as observing eventual failures.
            _ = CleanupAbandonedStartupAsync(starting);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Synthetic server startup exceeded 10 seconds.");
            }

            throw;
        }
    }

    private static async Task<SyntheticArcaneServer> CreateAndInitializeAsync(
        Action<IServiceCollection>? configureServices, CancellationToken cancellationToken)
    {
        OwnedFixtureDirectory directory = OwnedFixtureDirectory.Create();
        var realmListener = new TcpListener(IPAddress.Loopback, 0);
        var worldListener = new TcpListener(IPAddress.Loopback, 0);
        ServiceProvider? services = null;
        SyntheticArcaneServer? server = null;
        CancellationTokenRegistration closeListeners = default;
        try
        {
            realmListener.Start();
            worldListener.Start();
            // Register after binding: cancellation immediately closes even a fixture whose
            // service callback or synchronous content loader is still running.
            closeListeners = cancellationToken.Register(() =>
            {
                realmListener.Stop();
                worldListener.Stop();
            });
            cancellationToken.ThrowIfCancellationRequested();
            services = CreateServices(directory.Path,
                (IPEndPoint)realmListener.LocalEndpoint, (IPEndPoint)worldListener.LocalEndpoint, configureServices);
            cancellationToken.ThrowIfCancellationRequested();
            server = new SyntheticArcaneServer(directory, realmListener, worldListener, services);
            server._startupTask = server.InitializeAsync(cancellationToken);
            await server._startupTask.ConfigureAwait(false);
            // Transfer listener ownership from startup cancellation to server disposal.
            // Disposing the registration waits for any cancellation callback already running.
            closeListeners.Dispose();
            closeListeners = default;
            cancellationToken.ThrowIfCancellationRequested();
            return server;
        }
        catch (Exception startFailure)
        {
            realmListener.Stop();
            worldListener.Stop();
            try
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    // This cleanup continues after its deadline too, including eventual
                    // deletion if construction failed after the provider was created.
                    Task cleanup = CleanupUnstartedAsync(services, directory);
                    _ = cleanup.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    await cleanup.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
                }
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Synthetic server startup and cleanup failed.", startFailure, cleanupFailure);
            }

            throw;
        }
        finally
        {
            closeListeners.Dispose();
        }
    }

    private static async Task CleanupAbandonedStartupAsync(Task<SyntheticArcaneServer> starting)
    {
        try
        {
            SyntheticArcaneServer server = await starting.ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The original caller already received a startup cancellation or deadline
            // failure. Creation and server disposal retain their owned cleanup tasks.
        }
    }

    private static async Task CleanupUnstartedAsync(ServiceProvider? services, OwnedFixtureDirectory directory)
    {
        try
        {
            if (services is not null)
            {
                await services.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            directory.Delete();
        }
    }

    /// <summary>Store only a synthetic SRP salt and verifier; realm authentication writes the session key.</summary>
    public async Task<int> AddAccountAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (username.Length > 16 || username.Any(c => c is < '!' or > '~'))
        {
            throw new ArgumentException("Synthetic account names must contain 1–16 printable ASCII characters.", nameof(username));
        }

        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        string normalized = username.ToUpperInvariant();
        // Reproducible fixture credentials, never used outside this owned synthetic server.
        byte[] salt = SHA256.HashData(Encoding.ASCII.GetBytes("ArcaneCore.MockClient:" + normalized));
        byte[] verifier = WowSrp6.ToFixedLittleEndian(
            WowSrp6.ComputeVerifier(salt, normalized, password), WowSrp6.KeyLength);
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        Account account = await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAsync(new Account
        {
            Username = normalized,
            Salt = salt,
            Verifier = verifier,
            Security = AccountSecurity.Player,
        }, cancellationToken).ConfigureAwait(false);
        return account.Id;
    }

    public ValueTask DisposeAsync()
    {
        Task cleanup;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                _stop.Cancel();
                _realmListener.Stop();
                _worldListener.Stop();
                _disposeTask = Task.Run(DisposeCoreAsync, CancellationToken.None);
                _ = _disposeTask.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            cleanup = _disposeTask;
        }

        // A deadline failure is reported to the caller while the same owned cleanup task
        // continues draining; it never disposes stores underneath unfinished saves.
        return new ValueTask(cleanup.WaitAsync(ShutdownTimeout));
    }

    private static ServiceProvider CreateServices(
        string directory, IPEndPoint realmEndpoint, IPEndPoint worldEndpoint, Action<IServiceCollection>? configureServices)
    {
        var values = new Dictionary<string, string?>
        {
            ["Auth:BindAddress"] = IPAddress.Loopback.ToString(),
            ["Auth:Port"] = realmEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Auth:AutocreateAccounts"] = "false",
            ["World:BindAddress"] = IPAddress.Loopback.ToString(),
            ["World:Port"] = worldEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["World:TickIntervalMs"] = "5",
            ["World:AutosaveIntervalMs"] = "0",
            ["World:UpdateCompressionThreshold"] = "0",
            ["World:InstantLogoutSecurity"] = "Player",
            ["World:Motd"] = "ArcaneCore synthetic mock client fixture.",
            ["Quests:OrdinaryRewardQuestIds:0"] = RewardQuestId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (string component in new[] { "Auth", "Characters", "World" })
        {
            values[$"Database:{component}:Provider"] = nameof(DatabaseProvider.Sqlite);
            values[$"Database:{component}:ConnectionString"] = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, component.ToLowerInvariant() + ".db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();
        }

        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_ => configuration);
        // Keep the fixture silent without providers, while allowing a test to capture
        // real feature/session failures through its configured logging provider.
        services.AddLogging();
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.Configure<RealmSeedOptions>(options => options.Seed.Add(new RealmSeedEntry
        {
            Name = "ArcaneCore Synthetic",
            Address = worldEndpoint.ToString(),
            Type = RealmType.Normal,
        }));
        services.AddAuthDatabase(configuration);
        services.AddCharacterDatabase(configuration);
        services.AddWorldDatabase(configuration);
        services.AddWorldDaemon(configuration);
        // Synthetic immutable factions make the nearby quest giver neutral to the human
        // player without loading any client DBC assets or external world content.
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
        ]));
        // The daemon listener cannot expose its ephemeral endpoint or await all sessions.
        // Retain WorldHost and replace only that listener with this fixture's owned sockets.
        ServiceDescriptor worldServer = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(WorldServer));
        services.Remove(worldServer);
        configureServices?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _services.GetRequiredService<AuthDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _services.GetRequiredService<CharacterDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _services.GetRequiredService<WorldDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            WorldDbContext content = scope.ServiceProvider.GetRequiredService<WorldDbContext>();
            await SyntheticQuestContent.SeedAsync(content, SyntheticQuestProfile.SelfTest, cancellationToken).ConfigureAwait(false);
        }

        await _worldHost.StartAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Social guild loading posts to the simulation thread. Complete it while the
        // simulation is running so that it cannot post its installation after shutdown.
        _guildsLoaded = _services.GetRequiredService<SocialFeature>().GuildsLoaded;
        await _guildsLoaded.WaitAsync(cancellationToken).ConfigureAwait(false);
        _realmAcceptLoop = AcceptLoopAsync(_realmListener, isRealm: true);
        _worldAcceptLoop = AcceptLoopAsync(_worldListener, isRealm: false);
        // Confirm the simulation thread is processing commands before publishing endpoints.
        await World.InvokeAsync(() => true).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class SyntheticCombatRandom : ICombatRandom
    {
        internal static readonly SyntheticCombatRandom Instance = new();

        public int Next(int minInclusive, int maxInclusive) => maxInclusive;

        public float NextFloat(float min, float max) => max;
    }

    private async Task AcceptLoopAsync(TcpListener listener, bool isRealm)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        client.Dispose();
                        return;
                    }

                    _clients.Add(client);
                    _sessions.Add(Task.Run(() => RunSessionAsync(client, isRealm), CancellationToken.None));
                }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested
            && ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Both listener cancellation and explicit listener stop are expected at teardown.
        }
    }

    private async Task RunSessionAsync(TcpClient client, bool isRealm)
    {
        try
        {
            client.NoDelay = true;
            string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "loopback";
            using (client)
            await using (NetworkStream stream = client.GetStream())
            await using (AsyncServiceScope scope = _services.CreateAsyncScope())
            {
                IServiceProvider services = scope.ServiceProvider;
                if (isRealm)
                {
                    var session = new LogonSession(stream,
                        services.GetRequiredService<IAccountStore>(), services.GetRequiredService<IRealmStore>(),
                        services.GetRequiredService<IOptions<AuthOptions>>().Value,
                        NullLogger<LogonSession>.Instance, endpoint);
                    await session.RunAsync(_stop.Token).ConfigureAwait(false);
                }
                else
                {
                    var session = new WorldSession(stream, endpoint, services,
                        services.GetRequiredService<OpcodeTable>(), World,
                        services.GetRequiredService<SessionRegistry>(),
                        services.GetRequiredService<IOptions<WorldSessionOptions>>().Value,
                        services.GetRequiredService<ILogger<WorldSession>>());
                    await session.RunAsync(_stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException or SocketException
            || (_stop.IsCancellationRequested && ex is ObjectDisposedException))
        {
            // Expected disconnects are not fixture failures; unexpected faults remain tracked.
        }
        finally
        {
            client.Dispose();
            lock (_gate)
            {
                _clients.Remove(client);
            }
        }
    }

    private async Task DisposeCoreAsync()
    {
        var failures = new List<Exception>();
        TcpClient[] clients;
        lock (_gate)
        {
            clients = [.. _clients];
        }

        foreach (TcpClient client in clients)
        {
            client.Dispose();
        }

        try
        {
            await _startupTask.ConfigureAwait(false);
        }
        catch
        {
            // StartAsync reports its original failure; cleanup still handles partial startup.
        }

        try
        {
            await Task.WhenAll(_realmAcceptLoop, _worldAcceptLoop).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        Task[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
        }

        try
        {
            await Task.WhenAll(sessions).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            // WorldHost stops the simulation, calls feature shutdown, and drains the
            // character save queue, including disconnect work already posted to the world.
            await _worldHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await _services.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        _stop.Dispose();
        try
        {
            _directory.Delete();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Synthetic server cleanup failed.", failures);
        }
    }
}
