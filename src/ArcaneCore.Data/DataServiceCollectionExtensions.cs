using ArcaneCore.Data.Characters;
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

/// <summary>DI wiring for the data layer. This is the composition-root seam: only the host
/// references the concrete EF stores; the daemons depend on the Kernel interfaces.</summary>
public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddArcaneCoreData(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddDbContext<ArcaneCoreDbContext>((provider, builder) =>
            ConfigureProvider(builder, provider.GetRequiredService<IOptions<DatabaseOptions>>().Value));

        services.AddScoped<IAccountStore, EfAccountStore>();
        services.AddScoped<IRealmStore, EfRealmStore>();

        return services;
    }

    /// <summary>
    /// Registers the character context and the DB-driven world data (characters,
    /// player_create_info, race_info, class_info) on the same configured database.
    /// </summary>
    public static IServiceCollection AddArcaneCoreCharacterData(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddDbContext<CharacterDbContext>((provider, builder) =>
            ConfigureProvider(builder, provider.GetRequiredService<IOptions<DatabaseOptions>>().Value));

        services.AddScoped<ICharacterStore, EfCharacterStore>();
        services.AddScoped<IWorldDataStore, EfWorldDataStore>();
        services.AddSingleton<CharacterDbInitializer>();

        return services;
    }

    private static void ConfigureProvider(DbContextOptionsBuilder builder, DatabaseOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("Database:ConnectionString is not configured.");
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

            default:
                throw new InvalidOperationException($"Unsupported provider '{options.Provider}'.");
        }
    }
}
