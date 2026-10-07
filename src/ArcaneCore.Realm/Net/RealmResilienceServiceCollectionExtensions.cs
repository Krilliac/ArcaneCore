using ArcaneCore.Data;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// Wires the database guard into the logon daemon (docs/ops/resilience.md). In the <c>ArcaneCore.Realm.Net</c>
/// namespace so <c>Program.cs</c> needs one line and no new using.
/// </summary>
public static class RealmResilienceServiceCollectionExtensions
{
    /// <summary>The key under which the undecorated store registrations are kept.</summary>
    public const string InnerStoreKey = "ArcaneCore.Realm.Resilience.Inner";

    /// <summary>
    /// Register the guard (<see cref="DatabaseResilienceServiceCollectionExtensions.AddDatabaseResilience"/>) and put
    /// the Auth breaker in front of <see cref="IAccountStore"/>, <see cref="IRealmStore"/> and, when registered,
    /// <see cref="IBanStore"/>. Must follow <c>AddAuthDatabase</c>: there is nothing to decorate before it, and the
    /// call says so instead of silently guarding nothing.
    /// </summary>
    public static IServiceCollection AddRealmResilience(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddDatabaseResilience(configuration);

        Decorate<IAccountStore>(services, static (inner, guard) => new GuardedAccountStore(inner, guard), required: true);
        Decorate<IRealmStore>(services, static (inner, guard) => new GuardedRealmStore(inner, guard), required: true);
        Decorate<IBanStore>(services, static (inner, guard) => new GuardedBanStore(inner, guard), required: false);
        return services;
    }

    /// <summary>
    /// Move the last registration of <typeparamref name="TService"/> under <see cref="InnerStoreKey"/> (same lifetime,
    /// same implementation or factory) and register the decorator in its place; the decorator resolves the inner
    /// store lazily from the same scope.
    /// </summary>
    private static void Decorate<TService>(
        IServiceCollection services, Func<Func<TService>, DatabaseGuard, TService> decorate, bool required)
        where TService : class
    {
        ServiceDescriptor? existing = services.LastOrDefault(d => d.ServiceType == typeof(TService) && !d.IsKeyedService);
        if (existing is null)
        {
            if (required)
            {
                throw new InvalidOperationException(
                    $"AddRealmResilience must be called after AddAuthDatabase: no {typeof(TService).Name} is registered to guard.");
            }

            return;
        }

        if (services.Any(d => d.ServiceType == typeof(TService) && d.IsKeyedService && Equals(d.ServiceKey, InnerStoreKey)))
        {
            return; // already decorated
        }

        services.Remove(existing);
        services.Add(existing switch
        {
            { ImplementationInstance: { } instance } => new ServiceDescriptor(typeof(TService), InnerStoreKey, instance),
            { ImplementationFactory: { } factory } => new ServiceDescriptor(typeof(TService), InnerStoreKey, (sp, _) => factory(sp), existing.Lifetime),
            _ => new ServiceDescriptor(typeof(TService), InnerStoreKey, existing.ImplementationType!, existing.Lifetime),
        });

        services.Add(new ServiceDescriptor(
            typeof(TService),
            sp => decorate(() => sp.GetRequiredKeyedService<TService>(InnerStoreKey), sp.GetRequiredService<DatabaseGuard>()),
            existing.Lifetime));
    }
}
