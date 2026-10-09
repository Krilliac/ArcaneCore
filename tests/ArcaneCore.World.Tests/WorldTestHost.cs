using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Tests;

/// <summary>
/// An in-process world daemon for end-to-end tests: real world thread, real sessions over
/// loopback sockets, in-memory stores. Mirrors <see cref="WorldServiceCollectionExtensions.AddWorldDaemon"/>.
/// </summary>
internal sealed class WorldTestHost : IAsyncDisposable
{
    /// <summary>The game clock the next <see cref="Start"/> installs on the world (null: the real clock).</summary>
    public static readonly AsyncLocal<ArcaneCore.Game.WorldState.Time.IGameTime?> GameTime = new();

    private readonly ServiceProvider _services;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<SessionEntry> _sessions = [];

    // Every client this host handed out. The test holds the only other reference; a client the test drops is otherwise garbage, and the
    // finalizer of its TcpClient closes the socket, which logs the player out in the middle of the test (a GC-timing flake). Keeping them
    // here ties each connection to the host's lifetime; DisposeAsync closes them (a repeated dispose is harmless).
    private readonly List<WorldTestClient> _clients = [];
    private readonly Task _acceptLoop;

    private readonly WorldSessionOptions _sessionOptions;
    private readonly ILogger _sessionLogger;
    private readonly WireOracle? _wireOracle;

    private WorldTestHost(
        int compressionThreshold, Action<WorldRuntimeOptions>? configure, Action<IServiceCollection>? configureServices, WorldSessionOptions? sessionOptions, ILogger? sessionLogger,
        BanOptions? banOptions)
    {
        _sessionOptions = sessionOptions ?? new WorldSessionOptions { PreAuthTimeout = TestPreAuthTimeout };
        _sessionLogger = sessionLogger ?? NullLogger.Instance;
        Bans = new Bans.InMemoryBanStore(StatusEvents);
        var collection = new ServiceCollection();
        collection.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        collection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (Environment.GetEnvironmentVariable("ARCANECORE_TEST_WIRE_ORACLE") == "1")
        {
            var oracle = new WireOracle(recordHistogram: true);
            _wireOracle = oracle;
            collection.AddSingleton<IOutboundPacketObserver>(oracle);
        }
        Accounts.Events = StatusEvents;
        collection.AddSingleton<IAccountStore>(Accounts);
        collection.AddSingleton<IAccountAdmin>(Accounts);
        // Live-ban enforcement (docs/security/live-bans.md): the registry, the status events and the ban rows.
        collection.AddSingleton(Registry);
        collection.AddSingleton(StatusEvents);
        collection.AddSingleton<IBanStore>(Bans);
        if (banOptions is not null)
        {
            collection.AddSingleton(Microsoft.Extensions.Options.Options.Create(banOptions));
        }
        collection.AddSingleton<ICharacterStore>(Characters);
        collection.AddSingleton<ICharacterLifeStore>(Characters);
        collection.AddSingleton<IAccountDataStore>(AccountData);
        collection.AddSingleton<IWorldDataStore>(WorldData);
        collection.AddSingleton(Directory);
        collection.AddSingleton(sp => ChatCommands.CreateTable(sp));
        collection.AddSingleton<CharacterSaveQueue>();
        collection.AddSingleton<ICharacterSaveQueue>(sp => sp.GetRequiredService<CharacterSaveQueue>());
        collection.AddWorldFeatures();
        WorldTestServices.RegisterAll(collection);
        configureServices?.Invoke(collection);
        _services = collection.BuildServiceProvider();

        var options = new WorldRuntimeOptions { TickIntervalMs = 5, UpdateCompressionThreshold = compressionThreshold, AutosaveIntervalMs = 0 };
        configure?.Invoke(options);
        SaveQueue = _services.GetRequiredService<CharacterSaveQueue>();
        World = new WorldRuntime(options, SaveQueue, NullLogger<WorldRuntime>.Instance);
        Opcodes = WorldServiceCollectionExtensions.BuildOpcodeTable();

        // A test that needs the world to run at a chosen instant sets GameTime around Start (async-local, so tests stay isolated); it must be
        // in place before the features attach, because the game event feature reads the clock while it loads.
        if (GameTime.Value is { } gameTime)
        {
            ArcaneCore.Game.WorldState.WorldStateHooks.For(World).Time = gameTime;
        }

        _services.AttachWorldFeatures(World);
        // The harness runs with weather off (vmangos ActivateWeather=0) so an SMSG_WEATHER after every zone
        // entry does not shift the packet sequences of unrelated tests; weather tests switch it on.
        ArcaneCore.Game.WorldState.WorldStateHooks.For(World).WeatherSettings.Enabled = false;
        SaveQueue.Start();
        World.Start();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// The pre-auth deadline of a host started without session options. The retail 10 s (World:PreAuthTimeout) is a wall-clock limit on
    /// the client: a test whose continuation the thread pool held back for 10 s under full-suite load had its connection closed between
    /// SMSG_AUTH_CHALLENGE and CMSG_AUTH_SESSION ("an established connection was aborted"). Here it is only a bound on a hung test; the
    /// deadline itself is tested with its own short value (CodexNetAuthWorldTests).
    /// </summary>
    public static readonly TimeSpan TestPreAuthTimeout = TimeSpan.FromMinutes(2);

    public InMemoryAccountStore Accounts { get; } = new();

    /// <summary>When true, an exception escaping a session is collected in <see cref="SessionFaults"/> instead of failing the host.</summary>
    public bool ExpectSessionFaults { get; set; }

    public List<Exception> SessionFaults { get; } = [];

    /// <summary>In-process ban notifications (the stores publish, the BanEnforcementFeature subscribes).</summary>
    public AccountStatusEvents StatusEvents { get; } = new();

    /// <summary>The ban rows (account_banned / ip_banned equivalent).</summary>
    public Bans.InMemoryBanStore Bans { get; }

    public InMemoryCharacterStore Characters { get; } = new();

    public InMemoryAccountDataStore AccountData { get; } = new();

    public CharacterDirectory Directory { get; } = new();

    public InMemoryWorldDataStore WorldData { get; } = new();

    /// <summary>The host's service provider (singletons registered by the features and by <see cref="IWorldTestServices"/>).</summary>
    public IServiceProvider WorldServices => _services;

    public WorldRuntime World { get; }

    public CharacterSaveQueue SaveQueue { get; }

    public SessionRegistry Registry { get; } = new();

    public OpcodeTable Opcodes { get; }

    public int Port { get; }

    /// <summary>Start a host. Compression is off by default so tests can read update blocks directly.</summary>
    public static WorldTestHost Start(
        int compressionThreshold = 0, Action<WorldRuntimeOptions>? configure = null, Action<IServiceCollection>? configureServices = null,
        WorldSessionOptions? sessionOptions = null, ILogger? sessionLogger = null, BanOptions? banOptions = null)
        => new(compressionThreshold, configure, configureServices, sessionOptions, sessionLogger, banOptions);

    /// <summary>Create an account with a fresh session key (as if it had just logged in at the realm).</summary>
    public async Task<byte[]> AddAccountAsync(string name, AccountSecurity security = AccountSecurity.Player)
    {
        byte[] key = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength);
        await Accounts.CreateAsync(new Account
        {
            Username = name, Salt = new byte[32], Verifier = new byte[32], SessionKey = key, Security = security,
        });
        return key;
    }

    public async Task<WorldTestClient> ConnectAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port);
        var connected = new WorldTestClient(client);
        lock (_clients)
        {
            _clients.Add(connected);
        }

        return connected;
    }

    /// <summary>Connect, authenticate, create a warrior (human unless <paramref name="race"/> says otherwise) and enter the world.</summary>
    public async Task<WorldTestClient> EnterWorldAsync(
        string account, string character, AccountSecurity security = AccountSecurity.Player, byte race = 1)
    {
        byte[] key = await AddAccountAsync(account, security);
        WorldTestClient client = await ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character, race);
        Account stored = (await Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await Characters.GetByAccountAsync(stored.Id)).Single(c => string.Equals(c.Name, character, StringComparison.OrdinalIgnoreCase));
        await client.LoginAsync((ulong)record.Id);
        return client;
    }

    /// <summary>The online player with this name (world thread).</summary>
    public Task<Player> PlayerAsync(string name) => World.InvokeAsync(() => World.FindOnlinePlayer(name)
        ?? throw new InvalidOperationException($"{name} is not online"));

    /// <summary>Read an online player's state on the world thread.</summary>
    public Task<T> PlayerStateAsync<T>(string name, Func<Player, T> read) => World.InvokeAsync(() =>
        read(World.FindOnlinePlayer(name) ?? throw new InvalidOperationException($"{name} is not online")));

    /// <summary>Move an online player without a visibility pass, so no update packets result.</summary>
    public Task PlaceAsync(string name, float x, float y, float z) => World.InvokeAsync(() =>
    {
        Player player = World.FindOnlinePlayer(name) ?? throw new InvalidOperationException($"{name} is not online");
        player.Relocate(x, y, z, player.Orientation, World.NowMs);
        return true;
    });

    /// <summary>Run <paramref name="action"/> on the world thread and wait for it.</summary>
    public Task<T> OnWorldAsync<T>(Func<T> action) => World.InvokeAsync(action);

    public Task OnWorldAsync(Action action) => World.InvokeAsync(() =>
    {
        action();
        return true;
    });

    /// <summary>Wait until <paramref name="condition"/> holds (polling), or fail after 10 s.</summary>
    public static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Wait until <paramref name="condition"/>, evaluated on the world thread, holds.</summary>
    public async Task WaitForWorldAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await World.InvokeAsync(condition))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // listener stopped
        }

        WorldTestClient[] clients;
        lock (_clients)
        {
            clients = [.. _clients];
        }

        foreach (WorldTestClient connected in clients)
        {
            try
            {
                await connected.DisposeAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException)
            {
                // already closed by the test
            }
        }

        SessionEntry[] sessions;
        lock (_sessions)
        {
            sessions = [.. _sessions];
        }

        await DrainSessionsAsync(sessions);
        World.Stop();
        await _services.StopWorldFeaturesAsync();
        await SaveQueue.StopAsync();
        World.Dispose();
        await _services.DisposeAsync();
        _stop.Dispose();
        _wireOracle?.AssertValid();
    }

    /// <summary>
    /// How long the host waits for its sessions to end once their clients are closed. A session's own teardown is bounded: the read loop
    /// ends at once (the client is gone and the host's token cancelled), and <c>DrainWriterAsync</c> gives a writer that cannot finish up
    /// to twice <see cref="WorldSessionOptions.EffectiveWriterDrainBound"/> (5 s each) before abandoning it. The old 10 s wait was exactly
    /// that worst case with no margin, and a teardown the loaded machine stretched past it failed a passing test in its dispose
    /// (PlayerbotGroupPlayerTests, about one full run in ten). This bound only catches a session that never ends.
    /// </summary>
    private TimeSpan SessionTeardownBound => (2 * _sessionOptions.EffectiveWriterDrainBound) + TimeSpan.FromSeconds(20);

    private async Task DrainSessionsAsync(SessionEntry[] sessions)
    {
        try
        {
            await Task.WhenAll(sessions.Select(s => s.Task)).WaitAsync(SessionTeardownBound);
        }
        catch (TimeoutException ex)
        {
            IEnumerable<string> running = sessions.Where(s => !s.Task.IsCompleted).Select(s => s.Session is { } session
                ? $"{session.RemoteEndpoint} account '{session.AccountName}' state {session.State} player {session.Player?.Name ?? "-"}"
                : "a session not yet constructed");
            throw new TimeoutException($"sessions still running {SessionTeardownBound.TotalSeconds:0} s after their clients closed: {string.Join("; ", running)}", ex);
        }
    }

    /// <summary>One accepted connection: its task and, once constructed, its session (for the teardown diagnostic).</summary>
    private sealed class SessionEntry
    {
        public Task Task { get; set; } = Task.CompletedTask;

        public volatile WorldSession? Session;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var entry = new SessionEntry();
            Task session = Task.Run(async () =>
            {
                using (client)
                await using (NetworkStream stream = client.GetStream())
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    var worldSession = new WorldSession(
                        stream, client.Client.RemoteEndPoint?.ToString() ?? "test", scope.ServiceProvider, Opcodes, World, Registry,
                        _sessionOptions, _sessionLogger);
                    entry.Session = worldSession;
                    try
                    {
                        await worldSession.RunAsync(_stop.Token);
                    }
                    catch (Exception ex) when (ExpectSessionFaults)
                    {
                        // The production host (WorldServer.HandleClientAsync) logs a session fault and closes the
                        // connection; a test that provokes one (a store outage) opts in to collecting it.
                        lock (SessionFaults)
                        {
                            SessionFaults.Add(ex);
                        }
                    }
                }
            });

            entry.Task = session;
            lock (_sessions)
            {
                _sessions.Add(entry);
            }
        }
    }
}
