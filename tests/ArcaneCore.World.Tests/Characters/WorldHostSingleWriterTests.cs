using System.Net;
using System.Net.Sockets;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Schema;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// One world per characters database, and a free port, are checked before the first playerbot row is read or written (the 2026-10 incident: a second world
/// restored 863 bots on the live database and then failed to bind its port). The writer lock is a file lock, so two hosts in one process contend exactly as two processes do.
/// </summary>
public sealed class WorldHostSingleWriterTests
{
    [Fact]
    public async Task ASecondWorldOnTheSameCharactersFile_FailsToStart_BeforeAnyDatabaseRowIsTouched()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        ManagedPlayerbot bot = await SeedDesiredBotAsync(database);

        await using Kit first = Kit.Create(DatabaseOptions(database), fencing: false);
        await first.Host.StartAsync(CancellationToken.None);

        await using Kit second = Kit.Create(DatabaseOptions(database), fencing: false);
        CharacterDatabaseAlreadyOwnedException failure = await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(
            () => second.Host.StartAsync(CancellationToken.None));

        Assert.Contains("already owned by another world process", failure.Message);
        Assert.Contains(database + ".world.lock", failure.Message);
        Assert.Equal(database + ".world.lock", failure.LockPath);
        Assert.False(second.Feature.Attached); // no world feature (so no playerbot restore) ran
        Assert.Equal(0, second.Ownership.IsOwnedCalls); // not even the ownership row was read
        Assert.Equal(0, second.Ownership.AcquireCalls);
        Assert.Equal(bot, await ReadBotAsync(database, bot.BotId)); // Revision and everything else unchanged
    }

    [Fact]
    public async Task TheLockIsHeldAlsoWhenFencingIsOn_AndOutlivesTheHostStop_UntilTheGuardIsDisposed()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");

        await using Kit first = Kit.Create(DatabaseOptions(database), fencing: true);
        await first.Host.StartAsync(CancellationToken.None);
        await using (Kit denied = Kit.Create(DatabaseOptions(database), fencing: true))
            await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(() => denied.Host.StartAsync(CancellationToken.None));

        // The checkpointer's final WAL checkpoint runs after the host stops, so a complete host stop keeps the lock.
        await first.Host.StopAsync(CancellationToken.None);
        await using (Kit duringShutdown = Kit.Create(DatabaseOptions(database), fencing: false))
            await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(() => duringShutdown.Host.StartAsync(CancellationToken.None));

        first.Guard.Dispose(); // the container / process end
        await using Kit afterwards = Kit.Create(DatabaseOptions(database), fencing: false);
        await afterwards.Host.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheProductionStartupOrder_RefusesASecondWorld_BeforeAnySchemaInitializerTouchesTheFile()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        using var first = new WorldWriterGuard(DatabaseOptions(database));
        using var second = new WorldWriterGuard(DatabaseOptions(database));
        int firstInitializations = 0, secondInitializations = 0;
        var firstError = new StringWriter();
        var secondError = new StringWriter();

        int firstCode = await WorldStartup.PrepareDatabasesAsync(Services(first), () => { firstInitializations++; return Task.CompletedTask; }, firstError);
        int secondCode = await WorldStartup.PrepareDatabasesAsync(Services(second), async () =>
        {
            secondInitializations++;
            await File.WriteAllTextAsync(database, "schema bootstrap touched the file");
        }, secondError);

        Assert.Equal(0, firstCode);
        Assert.Equal(1, firstInitializations);
        Assert.NotEqual(0, secondCode);
        Assert.Equal(0, secondInitializations);
        Assert.False(File.Exists(database)); // the file was never opened, created or upgraded
        Assert.Contains("already owned by another world process", secondError.ToString());
        Assert.Contains(database + ".world.lock", secondError.ToString());
    }

    [Fact]
    public async Task TheWorldHostBuiltByAddWorldDaemon_ReceivesTheDatabaseOptions_AndTakesTheLock()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        Microsoft.Extensions.Configuration.IConfiguration configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={database}",
            }).Build();
        await using ServiceProvider provider = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).AddWorldDaemon(configuration).BuildServiceProvider();
        WorldHost host = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorldHost>().Single();

        using var other = new WorldWriterGuard(DatabaseOptions(database));
        other.Acquire(); // another world process holds the characters file

        await Assert.ThrowsAsync<CharacterDatabaseAlreadyOwnedException>(() => host.StartAsync(CancellationToken.None));
        other.Dispose();
        Assert.Null(provider.GetRequiredService<WorldWriterGuard>().LockPath);
        provider.GetRequiredService<WorldWriterGuard>().Acquire();
        Assert.Equal(database + ".world.lock", provider.GetRequiredService<WorldWriterGuard>().LockPath); // the DI guard reads the bound Database section
    }

    [Fact]
    public async Task RunToEnd_ACleanStop_ReturnsTheExitCodeOfTheRestart_AndTheContainerReleasesTheLock()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        IHost host = BuildHost(DatabaseOptions(database), lifetime => new StopsTheApplication(lifetime));
        int initializations = 0;

        int code = await WorldStartup.RunAsync(host, () => { initializations++; return Task.CompletedTask; }, new StringWriter(), () => 2);

        Assert.Equal(2, code); // 2 = restart: an ObjectDisposedException after RunAsync would have lost it
        Assert.Equal(1, initializations);
        using CharacterDatabaseWriterLock released = CharacterDatabaseWriterLock.TryAcquire(DatabaseOptions(database).Value.Resolve(DatabaseComponent.Characters))!;
        Assert.NotNull(released);
    }

    [Fact]
    public async Task RunToEnd_AFailedStart_SurfacesTheOriginalException_AndTheContainerStillReleasesTheLock()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        IHost host = BuildHost(DatabaseOptions(database), _ => new FailsToStart());

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => 0));

        Assert.Equal("the real start failure", failure.Message);
        using CharacterDatabaseWriterLock released = CharacterDatabaseWriterLock.TryAcquire(DatabaseOptions(database).Value.Resolve(DatabaseComponent.Characters))!;
        Assert.NotNull(released);
    }

    [Fact]
    public async Task RunToEnd_ALockThatCannotBeTakenOrCreated_IsOneLineAndTheRefusalCode_NotACrash()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        using var owner = new WorldWriterGuard(DatabaseOptions(database));
        owner.Acquire();
        IHost contended = BuildHost(DatabaseOptions(database), lifetime => new StopsTheApplication(lifetime));
        var contendedError = new StringWriter();
        int contendedCode = await WorldStartup.RunAsync(contended, () => throw new InvalidOperationException("must not initialize"), contendedError, () => 0);
        contended.Dispose();
        Assert.NotEqual(0, contendedCode);
        Assert.Contains("already owned by another world process", contendedError.ToString());

        string unusable = Path.Combine(directory.Path, "other.db");
        Directory.CreateDirectory(unusable + ".world.lock"); // not contention: a directory where the lock file must be
        IHost broken = BuildHost(DatabaseOptions(unusable), lifetime => new StopsTheApplication(lifetime));
        var brokenError = new StringWriter();
        int brokenCode = await WorldStartup.RunAsync(broken, () => throw new InvalidOperationException("must not initialize"), brokenError, () => 0);
        broken.Dispose();
        Assert.Equal(contendedCode, brokenCode);
        Assert.Contains("could not create the characters database writer lock", brokenError.ToString());
        Assert.DoesNotContain("already owned", brokenError.ToString());
    }

    [Fact]
    public async Task TheProbeWorksWithTheProductionWiring_AWorldPortHeldByAnotherListener_RefusesTheStart()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        string database = Path.Combine(directory.Path, "characters.db");
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            int port = ((IPEndPoint)taken.LocalEndpoint).Port;
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={database}",
                ["World:BindAddress"] = "127.0.0.1",
                ["World:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }).Build();
            await using ServiceProvider provider = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).AddWorldDaemon(configuration).BuildServiceProvider();
            WorldHost host = provider.GetServices<IHostedService>().OfType<WorldHost>().Single(); // no ListenerExpected seam: WorldServer is detected

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));

            Assert.Contains($"127.0.0.1:{port}", failure.Message);
            Assert.Contains("already in use", failure.Message);
            Assert.Null(provider.GetRequiredService<WorldWriterGuard>().LockPath); // the start took the free lock itself, so it gave it back
        }
        finally { taken.Stop(); }
    }

    private static IHost BuildHost(IOptions<DatabaseOptions> database, Func<IHostApplicationLifetime, IHostedService> service)
        => new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(database);
            services.AddSingleton<WorldWriterGuard>();
            services.AddSingleton<IHostedService>(sp => service(sp.GetRequiredService<IHostApplicationLifetime>()));
        }).Build();

    private sealed class StopsTheApplication(IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailsToStart : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("the real start failure");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static IServiceProvider Services(WorldWriterGuard guard)
        => new ServiceCollection().AddSingleton(guard).BuildServiceProvider();

    [Fact]
    public async Task AServerDatabase_TakesTheAdvisoryLock_SoAnUnreachableServerFailsTheStartBeforeAnyClaim()
    {
        // A server database is guarded by a session advisory lock on a dedicated connection (CharacterDatabaseWriterLockTests runs the refusal against a real server).
        // Here there is no server: the start must reach for the lock and fail with the driver's own error, not skip it and start a second writer.
        var options = Options.Create(new DatabaseOptions { Provider = DatabaseProvider.MariaDb, ConnectionString = "Server=nowhere.invalid;Database=characters;Connection Timeout=2" });
        await using Kit kit = Kit.Create(options, fencing: false);

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => kit.Host.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task APortAlreadyInUse_FailsTheStart_BeforeAnyWorldFeatureOrBotIsStarted()
    {
        using var directory = new TempDirectory("arcanecore-singlewriter-", sqlite: true);
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            int port = ((IPEndPoint)taken.LocalEndpoint).Port;
            string database = Path.Combine(directory.Path, "characters.db");
            await using Kit kit = Kit.Create(DatabaseOptions(database), fencing: true, world: new WorldOptions { BindAddress = "127.0.0.1", Port = port });

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Host.StartAsync(CancellationToken.None));

            Assert.Contains($"127.0.0.1:{port}", failure.Message);
            Assert.False(kit.Feature.Attached);
            Assert.Equal(0, kit.Ownership.AcquireCalls); // the durable claim was not taken either
            using CharacterDatabaseWriterLock released = CharacterDatabaseWriterLock.TryAcquire(DatabaseOptions(database).Value.Resolve(DatabaseComponent.Characters))!;
            Assert.NotNull(released); // and the failed start gave its file lock back
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public async Task APortHeldByAFixtureThatOwnsItsSockets_IsNotProbed_WhenNoWorldServerIsRegistered()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        try
        {
            int port = ((IPEndPoint)taken.LocalEndpoint).Port;
            await using Kit kit = Kit.Create(null, fencing: false, world: new WorldOptions { BindAddress = "127.0.0.1", Port = port }, listenerExpected: false);
            await kit.Host.StartAsync(CancellationToken.None);
            Assert.True(kit.Feature.Attached);
        }
        finally { taken.Stop(); }
    }

    [Fact]
    public async Task AFreePort_StartsNormally_AndPortZeroIsNotProbed()
    {
        await using Kit free = Kit.Create(null, fencing: false, world: new WorldOptions { BindAddress = "127.0.0.1", Port = 0 });
        await free.Host.StartAsync(CancellationToken.None);
        Assert.True(free.Feature.Attached);
    }

    private static IOptions<DatabaseOptions> DatabaseOptions(string database)
        => Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={database}" });

    private static CharacterDbContext Context(string database)
        => new(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={database}").Options);

    private static async Task<ManagedPlayerbot> SeedDesiredBotAsync(string database)
    {
        await using CharacterDbContext db = Context(database);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        CharacterRecord character = await new ArcaneCore.Data.Stores.EfCharacterStore(db).CreateAsync(
            new CharacterRecord { AccountId = 7, Name = "Singlewriter", Race = 1, Class = 1, Level = 1 });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), character.AccountId, character.Id, "PBSINGLE", true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 100, 100);
        var store = new EfManagedPlayerbotStore(db);
        await store.CreateAsync(bot);
        bot = bot with { Revision = 1, UpdatedUnix = 101 }; // a row another process has already written to
        Assert.True(await store.UpdateAsync(bot, expectedRevision: 0));
        return bot;
    }

    private static async Task<ManagedPlayerbot?> ReadBotAsync(string database, Guid botId)
    {
        await using CharacterDbContext db = Context(database);
        return await new EfManagedPlayerbotStore(db).FindAsync(botId);
    }

    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly WorldRuntime _world;

        private Kit(ServiceProvider provider, WorldRuntime world, WorldHost host, Ownership ownership, Feature feature, WorldWriterGuard guard)
        {
            Guard = guard;
            _provider = provider;
            _world = world;
            Host = host;
            Ownership = ownership;
            Feature = feature;
        }

        public WorldHost Host { get; }
        public WorldWriterGuard Guard { get; }
        public Ownership Ownership { get; }
        public Feature Feature { get; }

        public static Kit Create(IOptions<DatabaseOptions>? database, bool fencing, WorldOptions? world = null, bool listenerExpected = true)
        {
            var guard = new WorldWriterGuard(database);
            var ownership = new Ownership();
            var feature = new Feature();
            var services = new ServiceCollection();
            services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
            services.AddSingleton<ICharacterOwnershipStore>(ownership);
            services.AddSingleton<IWorldFeature>(feature);
            ServiceProvider provider = services.BuildServiceProvider();
            IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
            var runtime = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
            var host = new WorldHost(runtime, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance,
                Options.Create(new CharacterFencingOptions { Enabled = fencing, OwnerNode = "test" }),
                writerGuard: guard, worldOptions: world is null ? null : Options.Create(world)) { ListenerExpected = listenerExpected };
            return new Kit(provider, runtime, host, ownership, feature, guard);
        }

        public async ValueTask DisposeAsync()
        {
            try { await Host.StopAsync(CancellationToken.None); }
            catch (Exception) { }
            Guard.Dispose();
            _world.Dispose();
            await _provider.DisposeAsync();
        }
    }

    private sealed class Feature : IWorldFeature
    {
        public bool Attached { get; private set; }

        public void Attach(WorldRuntime world) => Attached = true;
    }

    private sealed class Ownership : ICharacterOwnershipStore
    {
        private int _isOwnedCalls;
        private int _acquireCalls;
        public int IsOwnedCalls => Volatile.Read(ref _isOwnedCalls);
        public int AcquireCalls => Volatile.Read(ref _acquireCalls);

        public Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _isOwnedCalls);
            return Task.FromResult(false);
        }

        public Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _acquireCalls);
            return Task.FromResult<CharacterOwnershipToken?>(new(scope, 1, incarnation));
        }

        public Task<CharacterOwnershipToken?> GetCurrentAsync(string scope, CancellationToken cancellationToken = default)
            => Task.FromResult<CharacterOwnershipToken?>(null);

        public Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, CancellationToken cancellationToken = default)
            => Task.FromResult<CharacterOwnershipToken?>(new(observed.Scope, observed.Epoch + 1, newIncarnation));

        public Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
