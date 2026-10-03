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
    private readonly ServiceProvider _services;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = [];
    private readonly Task _acceptLoop;

    private readonly WorldSessionOptions _sessionOptions;
    private readonly ILogger _sessionLogger;

    private WorldTestHost(
        int compressionThreshold, Action<WorldRuntimeOptions>? configure, WorldSessionOptions? sessionOptions, ILogger? sessionLogger,
        BanOptions? banOptions)
    {
        _sessionOptions = sessionOptions ?? new WorldSessionOptions();
        _sessionLogger = sessionLogger ?? NullLogger.Instance;
        Bans = new Bans.InMemoryBanStore(StatusEvents);
        var collection = new ServiceCollection();
        collection.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        collection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
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
        collection.AddSingleton<IAccountDataStore>(AccountData);
        collection.AddSingleton<IWorldDataStore>(WorldData);
        collection.AddSingleton(Directory);
        collection.AddSingleton(_ => ChatCommands.CreateTable());
        collection.AddSingleton<CharacterSaveQueue>();
        collection.AddSingleton<ICharacterSaveQueue>(sp => sp.GetRequiredService<CharacterSaveQueue>());
        collection.AddWorldFeatures();
        WorldTestServices.RegisterAll(collection);
        _services = collection.BuildServiceProvider();

        var options = new WorldRuntimeOptions { TickIntervalMs = 5, UpdateCompressionThreshold = compressionThreshold, AutosaveIntervalMs = 0 };
        configure?.Invoke(options);
        SaveQueue = _services.GetRequiredService<CharacterSaveQueue>();
        World = new WorldRuntime(options, SaveQueue, NullLogger<WorldRuntime>.Instance);
        Opcodes = WorldServiceCollectionExtensions.BuildOpcodeTable();

        _services.AttachWorldFeatures(World);
        SaveQueue.Start();
        World.Start();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

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
        int compressionThreshold = 0, Action<WorldRuntimeOptions>? configure = null, WorldSessionOptions? sessionOptions = null,
        ILogger? sessionLogger = null, BanOptions? banOptions = null)
        => new(compressionThreshold, configure, sessionOptions, sessionLogger, banOptions);

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
        return new WorldTestClient(client);
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

        Task[] sessions;
        lock (_sessions)
        {
            sessions = [.. _sessions];
        }

        await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(10));
        World.Stop();
        await _services.StopWorldFeaturesAsync();
        await SaveQueue.StopAsync();
        World.Dispose();
        await _services.DisposeAsync();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            Task session = Task.Run(async () =>
            {
                using (client)
                await using (NetworkStream stream = client.GetStream())
                await using (AsyncServiceScope scope = _services.CreateAsyncScope())
                {
                    var worldSession = new WorldSession(
                        stream, client.Client.RemoteEndPoint?.ToString() ?? "test", scope.ServiceProvider, Opcodes, World, Registry,
                        _sessionOptions, _sessionLogger);
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

            lock (_sessions)
            {
                _sessions.Add(session);
            }
        }
    }
}
