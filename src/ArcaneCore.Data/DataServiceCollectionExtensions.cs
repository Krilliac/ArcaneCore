using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Kernel.WorldData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data;

/// <summary>
/// DI wiring for the data layer. This is the composition-root seam: only the hosts reference
/// the concrete EF stores; the daemons depend on the Kernel interfaces.
/// </summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// The registrations every database shares: the bound <see cref="DatabaseOptions"/> and the one <see cref="SlowTransactionInterceptor"/> and the one <see cref="SlowCommandInterceptor"/>
    /// (singletons, so EF reuses one internal service provider across the contexts' option builds).
    /// </summary>
    private static void AddDatabaseCommon(IServiceCollection services, IConfiguration configuration, DatabaseComponent component)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddSingleton(new SqliteCheckpointComponent(component));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqliteCheckpointService>());
        services.TryAddSingleton(static provider => new SlowTransactionInterceptor(
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SlowTransactionWarnMs,
            (provider.GetService<ILoggerFactory>()?.CreateLogger<SlowTransactionInterceptor>() as ILogger) ?? NullLogger.Instance));
        services.TryAddSingleton(static provider => new SlowCommandInterceptor(
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SlowCommandWarnMs,
            (provider.GetService<ILoggerFactory>()?.CreateLogger<SlowCommandInterceptor>() as ILogger) ?? NullLogger.Instance));
    }

    /// <summary>Accounts and realm list (realm daemon, world daemon, account tool).</summary>
    public static IServiceCollection AddAuthDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabaseCommon(services, configuration, DatabaseComponent.Auth);
        services.AddDbContextPool<AuthDbContext>((provider, builder) =>
            ConfigureProvider(builder, ResolveGuarded(provider, DatabaseComponent.Auth), provider.GetRequiredService<SlowTransactionInterceptor>(),
                slowCommands: provider.GetRequiredService<SlowCommandInterceptor>(), pragmaLogger: PragmaLogger(provider), checkpoint: CheckpointOptions(provider), writeGate: WriteGate(provider)));

        services.AddScoped<IAccountStore, EfAccountStore>();
        services.AddScoped<IRealmStore, EfRealmStore>();
        services.AddScoped<IRealmStatusStore, EfRealmStatusStore>(); // the world daemon's online/offline and population writes
        services.AddSingleton<AuthDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.Auth);
        return services;
    }

    /// <summary>Player characters (world daemon).</summary>
    public static IServiceCollection AddCharacterDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabaseCommon(services, configuration, DatabaseComponent.Characters);
        // Pooled: every save, autosave and store call resolves a context in a fresh scope (CharacterSaveQueue, ~1500 writes/min on live).
        // Unpooled, each one rebuilt its options (WarningsConfiguration and extension copies) and EF's internal service scope, about a fifth
        // of the save path's allocations. The options are built once from the root provider (every input above is a singleton or IOptions),
        // and EF resets a returned context (change tracker, AutoTransactionBehavior the write fence sets, adopted transactions) before reuse.
        services.AddDbContextPool<CharacterDbContext>((provider, builder) =>
            ConfigureProvider(builder, ResolveGuarded(provider, DatabaseComponent.Characters), provider.GetRequiredService<SlowTransactionInterceptor>(),
                provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SqliteWriteTimeoutMs,
                provider.GetRequiredService<SlowCommandInterceptor>(), PragmaLogger(provider), CheckpointOptions(provider), WriteGate(provider)));

        AddCharacterWriteFence(services, configuration);
        services.AddScoped<ICharacterStore, EfCharacterStore>();
        services.AddScoped<IAccountDataStore, EfAccountDataStore>();
        services.AddSingleton<CharacterDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.Characters);
        return services;
    }

    /// <summary>The one key that turns the characters write fence on; the world host's claim and the fence read the same switch.</summary>
    public const string FencingEnabledKey = "Cluster:Fencing:Enabled";

    /// <summary>
    /// Worker mode (<c>Cluster:Worker:Enabled</c>, the gateway topology of docs/areas/gateway.md). A worker's players can be handed to another
    /// world process, so every characters write must carry the ownership epoch: in worker mode the write fence is on whatever
    /// <see cref="FencingEnabledKey"/> says (the world host refuses to start a worker with fencing off).
    /// </summary>
    public const string WorkerEnabledKey = "Cluster:Worker:Enabled";

    /// <summary>
    /// Whether the characters write fence is attached: <see cref="FencingEnabledKey"/> is true, or worker mode (<see cref="WorkerEnabledKey"/>)
    /// is on, which makes fencing mandatory. Combined mode with fencing unset is unfenced, exactly as before.
    /// </summary>
    public static bool IsCharacterWriteFenceRequired(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return IsTrue(configuration[FencingEnabledKey]) || IsTrue(configuration[WorkerEnabledKey]);

        static bool IsTrue(string? value) => bool.TryParse(value, out bool on) && on;
    }

    /// <summary>
    /// Registers the process-wide ownership holder and, when <see cref="IsCharacterWriteFenceRequired"/> (fencing on, or worker mode), attaches the write fence to every
    /// characters context. Off (the default) adds nothing to the context options, and the stores add no transaction and no ownership statement for an untokened save, so the statement kinds are the same as train 16 issued (a CharacterWriteFenceTests case captures them); the world host watches the ownership row every Cluster:Fencing:ClaimWatchIntervalSeconds (default 10 s, independent of autosave) instead, so an unfenced world can keep writing for up to that interval after a fenced world claims the database.
    /// </summary>
    private static void AddCharacterWriteFence(IServiceCollection services, IConfiguration configuration)
    {
        CharacterOwnershipContext holder = services
            .Where(d => d.ServiceType == typeof(CharacterOwnershipContext)).Select(d => d.ImplementationInstance).OfType<CharacterOwnershipContext>().FirstOrDefault()
            ?? new CharacterOwnershipContext();
        services.TryAddSingleton(holder);
        services.TryAddSingleton<ICharacterOwnershipContext>(holder);

        var transactions = new CharacterWriteFenceInterceptor(holder);
        var commands = new CharacterWriteFenceCommandInterceptor();
        services.ConfigureDbContext<CharacterDbContext>(builder =>
        {
            if (IsCharacterWriteFenceRequired(configuration))
            {
                builder.AddInterceptors(transactions, commands);
            }
        });
    }

    /// <summary>Static world content (world daemon, content importer).</summary>
    public static IServiceCollection AddWorldDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabaseCommon(services, configuration, DatabaseComponent.World);
        services.AddDbContextFactory<WorldDbContext>((provider, builder) =>
            ConfigureProvider(builder, ResolveGuarded(provider, DatabaseComponent.World), provider.GetRequiredService<SlowTransactionInterceptor>(),
                slowCommands: provider.GetRequiredService<SlowCommandInterceptor>(), pragmaLogger: PragmaLogger(provider), checkpoint: CheckpointOptions(provider), writeGate: WriteGate(provider)));

        services.AddScoped<IWorldDataStore, EfWorldDataStore>();
        services.AddSingleton<WorldDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.World);
        return services;
    }

    /// <summary>
    /// The SQLite single-writer gate interceptor (always on outside tests that turn it off), or null when it is off. ConfigureProvider attaches it to SQLite contexts only;
    /// the gate itself skips memory, read-only and URI databases.
    /// </summary>
    private static SqliteWriteGateInterceptor? WriteGate(IServiceProvider provider)
    {
        SqliteSingleWriterOptions options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Sqlite.SingleWriter;
        return options.Enabled
            ? SqliteWriteGateInterceptor.For(options, provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(SqliteWriteGateInterceptor).FullName!))
            : null;
    }

    /// <summary>The configured <c>Database:Sqlite:Checkpoint</c> settings the connections of a context are set up with.</summary>
    private static SqliteCheckpointOptions CheckpointOptions(IServiceProvider provider)
        => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Sqlite.Checkpoint;

    /// <summary>
    /// The <c>journal_size_limit</c> to give the connections of a database whose checkpoints <see cref="SqliteCheckpointService"/> owns (which also turns their auto-checkpoint off),
    /// or -1 when SQLite keeps checkpointing itself: no settings given, the checkpointer disabled, or not a plain file-backed SQLite database. Validates the settings when they apply.
    /// </summary>
    private static long CheckpointJournalLimit(DatabaseConnectionOptions options, SqliteCheckpointOptions? checkpoint)
    {
        if (checkpoint is null || SqliteCheckpointTarget.For(options, checkpoint) is null)
        {
            return -1;
        }

        checkpoint.Validate();
        return checkpoint.JournalSizeLimitBytes;
    }

    /// <summary>The logger the SQLite pragma interceptor reports a refused <c>synchronous=NORMAL</c> to (a cached per-category logger, so the interceptor cache key stays stable); null without logging.</summary>
    private static ILogger? PragmaLogger(IServiceProvider provider)
        => provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(SqliteConnectionPragmaInterceptor).FullName!);

    /// <summary>
    /// The component's configured connection options, with the SQLite busy timeout CLAMPED (not rejected) to the database guard's
    /// per-call deadline (<c>Resilience:Database:QueryTimeoutMs</c>) when the guard is registered, enabled and has a deadline.
    /// Why: against a locked file the guard's pessimistic timeout (<see cref="DatabaseGuard.TimeoutStrategyFor"/>) answers on time, but
    /// the abandoned call keeps waiting inside SQLite's busy handler, and closing its connection meanwhile can block until that wait returns,
    /// so a busy timeout longer than the deadline lengthens such a block by the difference. The clamp is silent because the configured value stays
    /// valid on its own (it is simply bounded by the stricter deadline); hosts without a guard keep the configured value.
    /// </summary>
    private static DatabaseConnectionOptions ResolveGuarded(IServiceProvider provider, DatabaseComponent component)
    {
        DatabaseConnectionOptions resolved = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Resolve(component);
        DatabaseResilienceOptions? guard = provider.GetService<IOptions<ResilienceOptions>>()?.Value.Database;
        if (resolved.Provider == DatabaseProvider.Sqlite && guard is { Enabled: true, QueryTimeoutMs: > 0 } && resolved.SqliteBusyTimeoutMs is { } busy && busy > guard.QueryTimeoutMs)
        {
            return new DatabaseConnectionOptions
            {
                Provider = resolved.Provider, ConnectionString = resolved.ConnectionString, SqliteBusyTimeoutMs = guard.QueryTimeoutMs,
                SqliteSynchronous = resolved.SqliteSynchronous,
            };
        }

        return resolved;
    }

    /// <summary>
    /// The SQLite connection string with <c>Default Timeout</c> lowered to the write bound (whole seconds, rounded up; never raised; a Default Timeout of 0, unlimited, is replaced; a bound of 0 leaves it).
    /// It must be in the string, not only set when the connection opens: <c>SqliteConnection.CreateCommand</c> copies DefaultTimeout into the command
    /// when it is created, and EF creates a non-query command (ExecuteUpdate/Delete, ExecuteScalar, raw SQL) before it opens the connection, so a value lowered at
    /// open would leave that first command on the string's 30 s.
    /// </summary>
    private static string WithWriteBound(string connectionString, int writeTimeoutMs)
    {
        if (writeTimeoutMs <= 0)
        {
            return connectionString;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        int seconds = SqliteConnectionPragmaInterceptor.ApplyWriteBound(builder.DefaultTimeout, writeTimeoutMs);
        if (seconds == builder.DefaultTimeout)
        {
            return connectionString;
        }

        builder.DefaultTimeout = seconds;
        return builder.ConnectionString;
    }

    /// <summary>Point a context at its configured engine; <paramref name="slowTransactions"/> and <paramref name="slowCommands"/> (when given) are attached on every provider; <paramref name="sqliteWriteTimeoutMs"/> bounds a SQLite connection's lock waits (0: none).</summary>
    public static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseConnectionOptions options, SlowTransactionInterceptor? slowTransactions = null, int sqliteWriteTimeoutMs = 0, SlowCommandInterceptor? slowCommands = null, ILogger? pragmaLogger = null, SqliteCheckpointOptions? checkpoint = null, SqliteWriteGateInterceptor? writeGate = null)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "No database connection string configured (Database:ConnectionString, or Database:<Auth|Characters|World>:ConnectionString).");
        }

        if (slowTransactions is not null)
        {
            builder.AddInterceptors(slowTransactions);
        }

        if (slowCommands is not null)
        {
            builder.AddInterceptors(slowCommands);
        }

        switch (options.Provider)
        {
            case DatabaseProvider.MariaDb:
            case DatabaseProvider.MySql:
                // Pomelo provider serves both MySQL and MariaDB; AutoDetect picks the
                // correct server-version behavior (requires the server reachable).
                builder.UseMySql(options.ConnectionString, ServerVersion.AutoDetect(options.ConnectionString));
                break;

            case DatabaseProvider.PostgreSql:
                builder.UseNpgsql(options.ConnectionString);
                break;

            case DatabaseProvider.Sqlite:
                builder.UseSqlite(WithWriteBound(options.ConnectionString, sqliteWriteTimeoutMs))
                    .AddInterceptors(SqliteConnectionPragmaInterceptor.For(options.SqliteBusyTimeoutMs ?? SqliteConnectionPragmaInterceptor.DefaultBusyTimeoutMs, sqliteWriteTimeoutMs, options.SqliteSynchronous ?? SqliteSynchronousMode.Full, pragmaLogger, CheckpointJournalLimit(options, checkpoint)));
                if (writeGate is not null)
                {
                    builder.AddInterceptors(writeGate);
                }

                break;

            default:
                throw new InvalidOperationException($"Unsupported provider '{options.Provider}'.");
        }
    }
}
