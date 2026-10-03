using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>
/// The daemons' start-up honours <c>Database:Upgrade:Policy</c> through the three real initializers, and a refusal
/// is one scrubbed line plus an exit code. MariaDB/PostgreSQL theories run only on hosted CI.
/// </summary>
public sealed class StartupPolicyTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    private static ServiceProvider Services(DatabaseConnectionOptions connection, params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
        };
        foreach ((string key, string value) in extra)
        {
            settings[key] = value;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthDatabase(configuration);
        services.AddCharacterDatabase(configuration);
        services.AddWorldDatabase(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DefaultOptions_CreateTheCurrentSchemaAndSeed_AsBefore(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = Services(connection);

        await services.GetRequiredService<AuthDbInitializer>().InitializeAsync();
        await services.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        await services.GetRequiredService<WorldDbInitializer>().InitializeAsync();

        Assert.Equal(SchemaPolicy.Always, services.GetRequiredService<IOptions<DatabaseOptions>>().Value.Upgrade.Policy);
        await using WorldDbContext world = TestContexts.Create<WorldDbContext>(connection);
        Assert.True(await world.PlayerCreateInfo.AnyAsync()); // the seed still runs
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(world, "world"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CreateOnly_CreatesAnEmptyDatabase_ButRefusesToUpgradeAnExistingOne_WithoutDdl(DatabaseProvider provider)
    {
        (string, string) policy = ("Database:Upgrade:Policy", "CreateOnly");

        DatabaseConnectionOptions fresh = await _databases.CreateAsync(provider);
        await using (ServiceProvider services = Services(fresh, policy))
        {
            await services.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(fresh);
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(db, "characters"));
        }

        DatabaseConnectionOptions legacy = await _databases.CreateAsync(provider);
        await using (CharactersM5Context m5 = TestContexts.Create<CharactersM5Context>(legacy))
        {
            await SchemaBootstrapper.EnsureAsync(m5, CharactersM5Context.Schema);
        }

        await using CharacterDbContext before = TestContexts.Create<CharacterDbContext>(legacy);
        string snapshot = await UpgradeTestSupport.SnapshotAsync(before, CharacterDbContext.Schema);
        await using ServiceProvider behind = Services(legacy, policy);

        SchemaPolicyException ex = await Assert.ThrowsAsync<SchemaPolicyException>(
            () => behind.GetRequiredService<CharacterDbInitializer>().InitializeAsync());

        Assert.Contains("schema version 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"needs schema version {CharacterDbContext.Schema.CurrentVersion}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("arcane-db upgrade", ex.Message, StringComparison.Ordinal);
        Assert.Equal(snapshot, await UpgradeTestSupport.SnapshotAsync(before, CharacterDbContext.Schema));

        // The same database, the historic policy: upgraded as before.
        await using ServiceProvider always = Services(legacy, ("Database:Upgrade:Policy", "Always"));
        await always.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await UpgradeTestSupport.ReadVersionAsync(before, "characters"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Never_RefusesAnEmptyDatabase_AndCreatesNothing(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = Services(connection, ("Database:Upgrade:Policy", "Never"));

        await Assert.ThrowsAsync<SchemaPolicyException>(() => services.GetRequiredService<AuthDbInitializer>().InitializeAsync());

        if (provider == DatabaseProvider.Sqlite)
        {
            Assert.False(File.Exists(UpgradeTestSupport.SqlitePath(connection)));
        }
    }

    [Fact]
    public void Options_BindFromConfiguration_AndInvalidValuesFailWithAClearMessage()
    {
        DatabaseConnectionOptions sqlite = new() { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" };

        using (ServiceProvider ok = Services(sqlite, ("Database:Upgrade:Policy", "createonly"), ("Database:Upgrade:LockTimeoutSeconds", "5")))
        {
            SchemaUpgradeOptions options = DatabaseStartup.OptionsFrom(ok);
            Assert.Equal(SchemaPolicy.CreateOnly, options.Policy);
            Assert.Equal(TimeSpan.FromSeconds(5), options.LockTimeout);
        }

        using (ServiceProvider defaults = Services(sqlite))
        {
            SchemaUpgradeOptions options = DatabaseStartup.OptionsFrom(defaults);
            Assert.Equal(SchemaPolicy.Always, options.Policy);
            Assert.Equal(SchemaBootstrapper.DefaultLockTimeout, options.LockTimeout);
        }

        using (ServiceProvider badPolicy = Services(sqlite, ("Database:Upgrade:Policy", "Sometimes")))
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DatabaseStartup.OptionsFrom(badPolicy));
            Assert.Contains("Policy", ex.ToString(), StringComparison.Ordinal);
        }

        using (ServiceProvider badTimeout = Services(sqlite, ("Database:Upgrade:LockTimeoutSeconds", "0")))
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DatabaseStartup.OptionsFrom(badTimeout));
            Assert.Contains("LockTimeoutSeconds", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARefusal_IsOneScrubbedLineAndAnExitCode_NotACrash()
    {
        DatabaseConnectionOptions sqlite = new()
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=:memory:;Password=hunter2-marker",
        };
        await using ServiceProvider services = Services(sqlite);
        var error = new StringWriter();

        int refused = await DatabaseStartup.InitializeAsync(
            () => throw new SchemaMismatchException("the characters database is at schema version 99 (Password=hunter2-marker) newer than this build.\nrestore a backup"),
            services, error);

        Assert.Equal(DbUpgradeExitCodes.Refused, refused);
        string line = error.ToString();
        Assert.Single(line.TrimEnd().Split('\n'));
        Assert.StartsWith("database schema refused:", line, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-marker", line, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", line, StringComparison.Ordinal); // no stack trace

        var lockError = new StringWriter();
        int timeout = await DatabaseStartup.InitializeAsync(
            () => throw new SchemaMismatchException("timed out waiting for the characters schema lock") { Reason = SchemaMismatchReason.LockTimeout },
            null, lockError);
        Assert.Equal(DbUpgradeExitCodes.LockTimeout, timeout);

        Assert.Equal(DbUpgradeExitCodes.Ok, await DatabaseStartup.InitializeAsync(() => Task.CompletedTask, null, new StringWriter()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseStartup.InitializeAsync(() => throw new InvalidOperationException("not a schema refusal"), null, new StringWriter()));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
