using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
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
        {
            DatabaseOptions options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                throw new InvalidOperationException(
                    "Database:ConnectionString is not configured.");
            }

            switch (options.Provider)
            {
                case DatabaseProvider.MariaDb:
                case DatabaseProvider.MySql:
                    // Pomelo provider serves both MySQL and MariaDB; AutoDetect picks the
                    // correct server-version behavior (requires the server reachable).
                    builder.UseMySql(
                        options.ConnectionString,
                        ServerVersion.AutoDetect(options.ConnectionString));
                    break;

                case DatabaseProvider.PostgreSql:
                    builder.UseNpgsql(options.ConnectionString);
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported provider '{options.Provider}'.");
            }
        });

        services.AddScoped<IAccountStore, EfAccountStore>();
        services.AddScoped<IRealmStore, EfRealmStore>();

        return services;
    }
}
