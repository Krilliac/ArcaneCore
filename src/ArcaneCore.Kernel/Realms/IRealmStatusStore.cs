namespace ArcaneCore.Kernel.Realms;

/// <summary>
/// The world daemon's writes to its own realm-list row (vmangos mangosd: Master.cpp:215 clears REALM_FLAG_OFFLINE and zeroes the
/// population when the world starts, Master.cpp:330 sets REALM_FLAG_OFFLINE when it stops, World.cpp:340-353 writes the
/// population as sessions are added), plus the liveness columns that let the logon daemon notice a world that stopped
/// responding (docs/ops/realm-liveness.md). Narrow on purpose: <see cref="IRealmStore"/> stays the read-only realm list of the
/// logon daemon. Every method returns false when no row has the given realm id, and changes only the columns it names.
/// </summary>
public interface IRealmStatusStore
{
    /// <summary>The process is up but not accepting connections: set <see cref="RealmFlags.Offline"/>, <see cref="RealmRuntimeState.Starting"/> and the heartbeat.</summary>
    Task<bool> SetStartingAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default);

    /// <summary>The world serves: clear <see cref="RealmFlags.Offline"/>, set the population to 0, <see cref="RealmRuntimeState.Online"/> and the heartbeat; the other flags are kept.</summary>
    Task<bool> SetOnlineAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default);

    /// <summary>The world serves in another state (<see cref="RealmRuntimeState.Online"/>, <see cref="RealmRuntimeState.Restarting"/> or <see cref="RealmRuntimeState.ShuttingDown"/>): write the state and the heartbeat only.</summary>
    Task<bool> SetStateAsync(int realmId, RealmRuntimeState state, DateTime heartbeatUtc, CancellationToken cancellationToken = default);

    /// <summary>The world is ticking: write the heartbeat only.</summary>
    Task<bool> BeatAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default);

    /// <summary>The world stopped cleanly: set <see cref="RealmFlags.Offline"/> and <see cref="RealmRuntimeState.Stopped"/>; the other flags, the population and the heartbeat are kept.</summary>
    Task<bool> SetStoppedAsync(int realmId, CancellationToken cancellationToken = default);

    /// <summary>Write the population hint shown on the realm-list screen.</summary>
    Task<bool> SetPopulationAsync(int realmId, float population, CancellationToken cancellationToken = default);
}
