using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>
/// Pins the DI key under which AddRealmResilience keeps the undecorated stores. The key is a string literal on purpose:
/// it must not drift when the constant moves from the extension class to GuardedCalls.
/// </summary>
public sealed class InnerStoreKeyTests
{
    private const string ExpectedKey = "ArcaneCore.Realm.Resilience.Inner";

    [Fact]
    public void AddRealmResilience_KeepsTheUndecoratedStoresUnderTheUnchangedKey()
    {
        IConfiguration config = new ConfigurationBuilder().Build();
        var account = new InMemoryAccountStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAccountStore>(account);
        services.AddSingleton<IRealmStore>(_ => new InMemoryRealmStore([]));

        services.AddRealmResilience(config);

        Assert.Contains(services, d => d.ServiceType == typeof(IAccountStore) && d.IsKeyedService && Equals(d.ServiceKey, ExpectedKey));
        Assert.Contains(services, d => d.ServiceType == typeof(IRealmStore) && d.IsKeyedService && Equals(d.ServiceKey, ExpectedKey));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IBanStore) && d.IsKeyedService);

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Same(account, provider.GetRequiredKeyedService<IAccountStore>(ExpectedKey));
        Assert.IsType<InMemoryRealmStore>(provider.GetRequiredKeyedService<IRealmStore>(ExpectedKey));
        Assert.Equal("GuardedAccountStore", provider.GetRequiredService<IAccountStore>().GetType().Name);
    }
}
