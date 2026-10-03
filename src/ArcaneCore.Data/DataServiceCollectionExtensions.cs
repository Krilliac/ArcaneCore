using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data;

/// <summary>
/// DI wiring for the data layer. This is the composition-root seam: only the hosts reference
/// the concrete EF stores; the daemons depend on the Kernel interfaces.
/// </summary>
public static class DataServiceCollectionExtensions
{
    /// <summary>Accounts and realm list (realm daemon, world daemon, account tool).</summary>
    public static IServiceCollection AddAuthDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddDbContext<AuthDbContext>((provider, builder) =>
            ConfigureProvider(builder, provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Resolve(DatabaseComponent.Auth)));

        services.AddScoped<IAccountStore, EfAccountStore>();
        services.AddScoped<IRealmStore, EfRealmStore>();
        services.AddSingleton<AuthDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.Auth);
        return services;
    }

    /// <summary>Player characters (world daemon).</summary>
    public static IServiceCollection AddCharacterDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddDbContext<CharacterDbContext>((provider, builder) =>
            ConfigureProvider(builder, provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Resolve(DatabaseComponent.Characters)));

        services.AddScoped<ICharacterStore, EfCharacterStore>();
        services.AddScoped<IAccountDataStore, EfAccountDataStore>();
        services.AddSingleton<CharacterDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.Characters);
        return services;
    }

    /// <summary>Static world content (world daemon, content importer).</summary>
    public static IServiceCollection AddWorldDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddDbContext<WorldDbContext>((provider, builder) =>
            ConfigureProvider(builder, provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Resolve(DatabaseComponent.World)));

        services.AddScoped<IWorldDataStore, EfWorldDataStore>();
        services.AddSingleton<WorldDbInitializer>();
        DataModules.AddServices(services, DatabaseComponent.World);
        return services;
    }

    /// <summary>Point a context at its configured engine.</summary>
    public static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseConnectionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "No database connection string configured (Database:ConnectionString, or Database:<Auth|Characters|World>:ConnectionString).");
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
                builder.UseSqlite(options.ConnectionString);
                break;

            default:
                throw new InvalidOperationException($"Unsupported provider '{options.Provider}'.");
        }
    }
}
