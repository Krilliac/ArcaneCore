namespace ArcaneCore.Kernel.Configuration;

/// <summary>How the realm list shows a realm whose world is counting down to a restart while it still serves.</summary>
public enum RestartingRealmDisplay
{
    /// <summary>The realm name turns red and the row stays selectable (<c>RealmFlags.Invalid</c>, 0x01). Not verified in the 1.12.1 client binary.</summary>
    Invalid = 0,

    /// <summary>The realm shows offline and cannot be selected (<c>RealmFlags.Offline</c>, 0x02). Verified in the client script.</summary>
    Offline = 1,
}

/// <summary>
/// How the logon daemon decides, per realm-list request, whether a realm's world is alive (docs/ops/realm-liveness.md). The world
/// writes a state and a heartbeat into its realm row; the logon daemon turns them into the flags it sends.
/// </summary>
[ConfigDaemons(ConfigDaemon.Realm)]
public sealed class RealmLivenessOptions
{
    public const string SectionName = "Realm:Liveness";

    /// <summary>
    /// Seconds without a heartbeat after which a world that claims to be running is shown offline. Default 60. Must be at least three
    /// times <c>World:Realm:HeartbeatIntervalSeconds</c> (a missed beat or two never flips the realm) and above five times
    /// <c>Ops:Watchdog:TickMonitor:HangMs</c> in seconds (a slow tick is a hang for the watchdog long before it is an outage here).
    /// 0 turns the heartbeat check off (the state and the lock level still apply).
    /// </summary>
    public int HeartbeatStaleSeconds { get; set; } = 60;

    /// <summary>
    /// What a realm shows while its world counts down to a restart and still serves: <c>Invalid</c> (red name, still selectable, the
    /// retail-emulator convention, unverified in the 1.12.1 client binary) or <c>Offline</c> (grey, not selectable). Default Invalid.
    /// </summary>
    public RestartingRealmDisplay RestartingAs { get; set; } = RestartingRealmDisplay.Invalid;
}
