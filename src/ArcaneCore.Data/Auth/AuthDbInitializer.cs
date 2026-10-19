using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data.Auth;

/// <summary>Brings the auth schema to the current version and seeds configured realms.</summary>
public sealed class AuthDbInitializer(IServiceProvider services, ILogger<AuthDbInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = services.CreateScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        SchemaUpgradeOptions options = DatabaseStartup.OptionsFrom(services);

        await SchemaBootstrapper.EnsureAsync(
            db,
            AuthDbContext.Schema,
            options,
            logger,
            cancellationToken,
            async ct =>
            {
                // Expired bans are cleaned at startup like realmd (Main.cpp:213-215) and mangosd (World.cpp:1818-1820), on this
                // context: inside the schema lock every write must use the lock's connection, and the IBanStore the container
                // hands out may not (the logon daemon's guarded stores run each call in a DI scope of its own, whose connection
                // would wait for the lock's SQLite write transaction until the query timeout).
                if (scope.ServiceProvider.GetService<Kernel.Accounts.IBanStore>() is not null)
                {
                    await new EfBanStore(db, scope.ServiceProvider.GetService<TimeProvider>()).PurgeExpiredAsync(ct).ConfigureAwait(false);
                }

                RealmSeedOptions? seed = scope.ServiceProvider.GetService<IOptions<RealmSeedOptions>>()?.Value;
                if (seed is { Seed.Count: > 0 } && !await db.Realms.AnyAsync(ct).ConfigureAwait(false))
                {
                    foreach (RealmSeedEntry entry in seed.Seed)
                    {
                        db.Realms.Add(new RealmEntry
                        {
                            Name = entry.Name,
                            Address = entry.Address,
                            Type = entry.Type,
                            Flags = entry.Flags,
                            Population = entry.Population,
                            Category = entry.Category,
                        });
                    }

                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }
}
