using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
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

        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema, DatabaseStartup.OptionsFrom(services), logger, cancellationToken).ConfigureAwait(false);

        // Expired bans are cleaned at startup like realmd (Main.cpp:213-215) and mangosd (World.cpp:1818-1820).
        Kernel.Accounts.IBanStore? bans = scope.ServiceProvider.GetService<Kernel.Accounts.IBanStore>();
        if (bans is not null)
        {
            await bans.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        }

        RealmSeedOptions? seed = scope.ServiceProvider.GetService<IOptions<RealmSeedOptions>>()?.Value;
        if (seed is { Seed.Count: > 0 } && !await db.Realms.AnyAsync(cancellationToken).ConfigureAwait(false))
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

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
