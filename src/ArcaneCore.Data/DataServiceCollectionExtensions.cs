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
    private static void AddDatabaseCommon(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
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
        AddDatabaseCommon(services, configuration);
        services.AddDbContext<AuthDbContext>((provider, builder) =>
            ConfigureProvider(builder, ResolveGuarded(provider, DatabaseComponent.Auth), provider.GetRequiredService<SlowTransactionInterceptor>(),
                slowCommands: provider.GetRequiredService<SlowCommandInterceptor>()));

        services.AddScoped<IAccountStore, EfAccountStore>();
        services.AddScoped<IRealmStore, EfRealmStore>();
        services.AddSingleton<AuthDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.Auth);
        return services;
    }

    /// <summary>Player characters (world daemon).</summary>
    public static IServiceCollection AddCharacterDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabaseCommon(services, configuration);
        services.AddDbContext<CharacterDbContext>((provider, builder) =>
            ConfigureProvider(builder, ResolveGuarded(provider, DatabaseComponent.Characters), provider.GetRequiredService<SlowTransactionInterceptor>(),
                provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.SqliteWriteTimeoutMs,
                provider.GetRequiredService<SlowCommandInterceptor>()));

        services.AddScoped<ICharacterStore, EfCharacterStore>();
        services.AddScoped<IAccountDataStore, EfAccountDataStore>();
        services.AddSingleton<CharacterDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.Characters);
        return services;
    }

    /// <summary>Static world content (world daemon, content importer).</summary>
    public static IServiceCollection AddWorldDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabaseCommon(services, configuration);
        services.AddDbContextFactory<WorldDbContext>((provider, builder) =>
            ConfigureProvider(builder, ResolveGuarded(provider, DatabaseComponent.World), provider.GetRequiredService<SlowTransactionInterceptor>(),
                slowCommands: provider.GetRequiredService<SlowCommandInterceptor>()));

        services.AddScoped<IWorldDataStore, EfWorldDataStore>();
        services.AddSingleton<WorldDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.World);
        return services;
    }

    /// <summary>
    /// The component's configured connection options, with the SQLite busy timeout CLAMPED (not rejected) to the database guard's
    /// per-call deadline (<c>Resilience:Database:QueryTimeoutMs</c>) when the guard is registered, enabled and has a deadline.
    /// Why: against a locked file the guard's pessimistic timeout (<see cref="DatabaseGuard.TimeoutStrategyFor"/>) answers on time, but
    /// the abandoned call keeps waiting inside SQLite's busy handler and disposing its scope blocks until it returns, so a busy timeout
    /// longer than the deadline lengthens every refusal by the difference. The clamp is silent because the configured value stays
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
    public static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseConnectionOptions options, SlowTransactionInterceptor? slowTransactions = null, int sqliteWriteTimeoutMs = 0, SlowCommandInterceptor? slowCommands = null)
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
                    .AddInterceptors(SqliteConnectionPragmaInterceptor.For(options.SqliteBusyTimeoutMs ?? SqliteConnectionPragmaInterceptor.DefaultBusyTimeoutMs, sqliteWriteTimeoutMs));
                break;

            default:
                throw new InvalidOperationException($"Unsupported provider '{options.Provider}'.");
        }
    }
}
