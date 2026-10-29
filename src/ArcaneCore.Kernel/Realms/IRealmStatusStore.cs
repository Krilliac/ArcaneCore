namespace ArcaneCore.Kernel.Realms;

/// <summary>
/// The world daemon's writes to its own realm-list row (vmangos mangosd: Master.cpp:215 clears REALM_FLAG_OFFLINE and zeroes the
/// population when the world starts, Master.cpp:330 sets REALM_FLAG_OFFLINE when it stops, World.cpp:340-353 writes the
/// population as sessions are added). Narrow on purpose: <see cref="IRealmStore"/> stays the read-only realm list of the logon
/// daemon. Every method returns false when no row has the given realm id.
/// </summary>
public interface IRealmStatusStore
{
    /// <summary>The world is up: clear <see cref="RealmFlags.Offline"/> and set the population to 0; the other flags are kept.</summary>
    Task<bool> SetOnlineAsync(int realmId, CancellationToken cancellationToken = default);

    /// <summary>The world is going down: set <see cref="RealmFlags.Offline"/>; the other flags and the population are kept.</summary>
    Task<bool> SetOfflineAsync(int realmId, CancellationToken cancellationToken = default);

    /// <summary>Write the population hint shown on the realm-list screen.</summary>
    Task<bool> SetPopulationAsync(int realmId, float population, CancellationToken cancellationToken = default);
}
