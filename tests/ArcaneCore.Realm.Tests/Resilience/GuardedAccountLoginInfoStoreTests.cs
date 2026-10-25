using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>The login-info store is wrapped by the same breaker as the other auth stores, but is optional.</summary>
public sealed class GuardedAccountLoginInfoStoreTests
{
    private static IConfiguration Config => new ConfigurationBuilder().Build();

    [Fact]
    public async Task RegisteredStore_IsGuarded_AndDelegatesBothCalls()
    {
        var inner = new InMemoryAccountStore();
        await inner.CreateAsync(new Account { Username = "TESTER", Salt = new byte[32], Verifier = new byte[32] });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAccountStore>(inner);
        services.AddSingleton<IRealmStore>(_ => new InMemoryRealmStore([]));
        services.AddSingleton<IAccountLoginInfoStore>(inner);
        services.AddRealmResilience(Config);

        using ServiceProvider provider = services.BuildServiceProvider();
        IAccountLoginInfoStore guarded = provider.GetRequiredService<IAccountLoginInfoStore>();
        Assert.Equal("GuardedAccountLoginInfoStore", guarded.GetType().Name);

        var info = new AccountLoginInfo(new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc), "Win", "x86", "deDE", 5875);
        await guarded.RecordLoginAsync("tester", info);
        Assert.Equal(info, await guarded.GetLoginInfoAsync("TESTER"));
        Assert.Equal(info, await inner.GetLoginInfoAsync("TESTER"));
        Assert.Null(await guarded.GetLoginInfoAsync("NOBODY"));
    }

    [Fact]
    public void UnregisteredStore_IsNotRequired_AndStaysUnregistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IRealmStore>(_ => new InMemoryRealmStore([]));
        services.AddRealmResilience(Config);

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IAccountLoginInfoStore>());
    }
}
