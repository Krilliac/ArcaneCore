using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Realm.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>
/// The realm daemon's start-up purge of expired bans (AuthDbInitializer) runs inside the SQLite schema lock's write
/// transaction. The guarded stores (AddRealmResilience) run each call in a DI scope of its own, so a purge through
/// them would wait on another connection for that transaction until the query timeout: the purge must use the
/// bootstrap's own context, and still happen.
/// </summary>
public sealed class GuardedStartupPurgeTests
{
    [Fact]
    public async Task ComposedRealm_Startup_PurgesExpiredBans_OnTheBootstrapConnection()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "auth.db");
        try
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={file}",
                ["Resilience:Database:QueryTimeoutMs"] = "2000",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthDatabase(config);
            services.AddRealmResilience(config);
            await using ServiceProvider provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AuthDbInitializer>().InitializeAsync();

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                db.Set<IpBanRow>().Add(new IpBanRow { Ip = "10.0.0.1", BanDate = 1000, UnbanDate = 2000, BannedBy = "GM", BanReason = "expired" });
                db.Set<IpBanRow>().Add(new IpBanRow { Ip = "10.0.0.2", BanDate = 1000, UnbanDate = 1000, BannedBy = "GM", BanReason = "permanent" });
                await db.SaveChangesAsync();
            }

            // The second start finds the expired row. A purge on a connection of its own would wait for the schema lock's
            // transaction here and fail with TimeoutRejectedException after the 2 s query timeout.
            await provider.GetRequiredService<AuthDbInitializer>().InitializeAsync();

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                Assert.Equal(["10.0.0.2"], await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Set<IpBanRow>().Select(r => r.Ip).ToListAsync());
            }
        }
        finally
        {
            using (var own = new SqliteConnection($"Data Source={file}")) SqliteConnection.ClearPool(own);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 100)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
