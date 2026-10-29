namespace ArcaneCore.World.RealmList;

/// <summary>
/// The world daemon's own row in the realm list (<c>realmlist</c> in the auth database), which <see cref="RealmLivenessService"/> keeps
/// current (offline until the world accepts connections, online while its loop ticks, offline at a clean stop) and
/// <see cref="RealmStatusFeature"/> updates with the population (vmangos Master.cpp:215, :330; World.cpp:340-353).
/// </summary>
public sealed class RealmStatusOptions
{
    public const string SectionName = "World:Realm";

    /// <summary>
    /// The realm-list id this world serves (vmangos RealmID; mangosd.conf.dist.in ships 1). The world clears its Offline flag at
    /// start and sets it at a clean stop, so the realm list shows the realm offline while the world is down. 0 = this world does
    /// not write the realm list (ArcaneCore-specific: vmangos refuses to start without a RealmID, Master.cpp:475-480).
    /// </summary>
    public int Id { get; set; } = 1;

    /// <summary>
    /// The population formula's player limit (vmangos PlayerLimit, default 100, SystemConfig.h:91): the realm list's population
    /// hint is 2 x sessions / PlayerLimit, written when a session is added (World.cpp:340-353). 0 = the population is never written
    /// (vmangos skips it when the limit is 0). ArcaneCore has no login queue, so only the hint reads this value.
    /// </summary>
    public int PlayerLimit { get; set; } = 100;

    /// <summary>
    /// Seconds between the heartbeats this world writes into its realm-list row while its loop is ticking (ArcaneCore-specific; vmangos
    /// has no liveness check). The logon daemon shows the realm offline once the last beat is older than <c>Realm:Liveness:HeartbeatStaleSeconds</c>
    /// (default 60, which must be at least three times this interval). A beat is written only if the world loop started a tick since the last
    /// one, so a hung or stalled loop stops beating while the process is still up. Needs <c>World:Realm:Id</c> above 0.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;
}
