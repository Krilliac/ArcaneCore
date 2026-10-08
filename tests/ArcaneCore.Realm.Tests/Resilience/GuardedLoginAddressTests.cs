using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Realm.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>
/// The realm daemon's account store is the resilience decorator over the EF store (Program.cs composes
/// AddAuthDatabase + AddRealmResilience). A successful proof's address must reach <c>account.LastIp</c>
/// through it, or IP_LOCK would compare against an address that is never written.
/// </summary>
public sealed class GuardedLoginAddressTests
{
    [Fact]
    public async Task ComposedRealmStore_UpdateLogin_PersistsSessionKeyAndAddress()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-lastip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "auth.db");
        try
        {
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={file}",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthDatabase(config);
            services.AddRealmResilience(config);
            await using ServiceProvider provider = services.BuildServiceProvider();
            await provider.GetRequiredService<AuthDbInitializer>().InitializeAsync();

            byte[] key = Enumerable.Range(1, 40).Select(i => (byte)i).ToArray();
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                IAccountStore store = scope.ServiceProvider.GetRequiredService<IAccountStore>();
                Assert.Equal("GuardedAccountStore", store.GetType().Name);
                await store.CreateAsync(new Account { Username = "LOCKED", Salt = new byte[32], Verifier = new byte[32] });
                await store.UpdateLoginAsync("LOCKED", key, "10.1.2.3");
            }

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                Account stored = (await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindByUsernameAsync("LOCKED"))!;
                Assert.Equal(key, stored.SessionKey);
                Assert.Equal("10.1.2.3", stored.LastIp);
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
