using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data;

/// <summary>
/// Ensures the schema exists and seeds configured realms on startup.
///
/// M1 uses <see cref="DatabaseFacade.EnsureCreatedAsync"/> rather than migrations: the
/// schema is two small tables and must work identically across MariaDB/MySQL/Postgres,
/// where maintaining three migration sets would be premature. Migrations are revisited
/// when the schema grows (recorded in MILESTONE_M1.md).
/// </summary>
public sealed class ArcaneCoreDbInitializer(IServiceProvider services)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = services.CreateScope();
        ArcaneCoreDbContext db = scope.ServiceProvider.GetRequiredService<ArcaneCoreDbContext>();

        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        RealmSeedOptions seed = scope.ServiceProvider
            .GetRequiredService<IOptions<RealmSeedOptions>>().Value;

        if (seed.Seed.Count > 0 && !await db.Realms.AnyAsync(cancellationToken).ConfigureAwait(false))
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
