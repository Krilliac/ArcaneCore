namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// Transport protections shared by the logon and world listeners (docs/ops/netguard.md). Every
/// limit fails closed: the connection is closed and one rate-limited log line is written; nothing
/// here ever throws into an accept loop or a session. 0 (or 00:00:00) disables a single limit.
/// vmangos has none of these except the pre-auth timeouts (realmd MaxSessionDuration, mangosd
/// Network.TimeoutSecsIfNoAuth), which stay where they are (Auth:MaxSessionDurationSeconds,
/// World:PreAuthTimeout). The rates and the failure budget sit far above anything a retail client
/// does and never affect players behind one NAT address (successes consume nothing). The
/// per-address connection cap (<see cref="MaxConnectionsPerIp"/>, 16) is the one default here that
/// does bound a crowd behind one address; it is a deliberate non-retail default, listed as such in
/// the release-caveat register of docs/guide/operations.md with 0 as the switch that restores retail.
/// </summary>
public sealed class NetProtectionOptions
{
    public const string SectionName = "Net:Protection";

    /// <summary>
    /// Simultaneous connections one client IP address may hold on a listener; 0 disables. Applied
    /// together with the daemon's own cap (Auth:MaxConnectionsPerIp, World:MaxConnectionsPerIp):
    /// when both are set the lower one wins. A retail client holds one connection per daemon.
    /// Deviation from retail (vmangos has no per-address cap), on by default.
    /// </summary>
    public int MaxConnectionsPerIp { get; set; } = 16;

    /// <summary>
    /// How many connections one IP address may open at once before the per-minute rate below
    /// applies (the token bucket's capacity); 0 disables the connection-rate limit. A refused
    /// connection is closed before any session, DI scope or database context exists.
    /// </summary>
    public int ConnectionBurstPerIp { get; set; } = 100;

    /// <summary>
    /// Sustained new connections per minute one IP address may open once its burst is used up
    /// (the token bucket's refill rate). Only read when <see cref="ConnectionBurstPerIp"/> is set.
    /// </summary>
    public int ConnectionsPerMinutePerIp { get; set; } = 300;

    /// <summary>
    /// How many failed authentication attempts (unknown account, wrong proof or digest, banned
    /// account) one IP address may make before further attempts are refused; 0 disables. An
    /// attempt is checked before any database lookup and refused with the connection closed, so a
    /// guessing client costs no query. Successful logins never consume the budget, so players
    /// behind one address are not affected. vmangos realmd has WrongPass.MaxCount (default 10 per
    /// 60 s, LoginThrottle); the same shape, keyed by address.
    /// </summary>
    public int AuthFailureBurstPerIp { get; set; } = 10;

    /// <summary>
    /// How many failed attempts per minute an address gets back once its burst is used up (the
    /// refill rate of the failure budget). Only read when <see cref="AuthFailureBurstPerIp"/> is set.
    /// </summary>
    public int AuthFailuresPerMinutePerIp { get; set; } = 10;

    /// <summary>
    /// Most client addresses the per-address table tracks (rounded up to a power of two). Memory is
    /// fixed at start (about 48 bytes per slot) and nothing is allocated per connection. When the
    /// table is full and no idle slot exists the newcomer is still admitted: the table forgets the
    /// least recently seen address of the probe window (preferring one that is not being limited)
    /// and writes one rate-limited log line. A full table measures less; it never refuses a
    /// connection (the connection caps are the fail-closed limit).
    /// </summary>
    public int MaxTrackedAddresses { get; set; } = 4096;

    /// <summary>
    /// How long after its last connection or attempt an address's slot counts as idle and may be
    /// reused for another address. Must be longer than the time a full burst takes to refill, or a
    /// limited address could be forgotten and start over.
    /// </summary>
    public TimeSpan AddressIdleEviction { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Longest a client may take to deliver the rest of a frame once its first byte has arrived
    /// (the remaining header bytes and the payload); the connection is closed when it expires
    /// (slowloris). 00:00:00 disables. A retail client writes each frame in one send. On the logon
    /// daemon Auth:ReadTimeoutSeconds, when set, takes precedence over this value.
    /// </summary>
    public TimeSpan FrameReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest a logon connection may exist without a successful proof, counted from accept; the
    /// connection is closed when it expires. 00:00:00 disables. Auth:MaxSessionDurationSeconds (300,
    /// vmangos MaxSessionDuration) still bounds the whole connection; this closes an idle or
    /// guessing one much sooner. The world daemon's equivalent is World:PreAuthTimeout (retail).
    /// </summary>
    public TimeSpan LogonUnauthenticatedLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shortest interval between two log lines about the same kind of refusal (refused connection,
    /// refused attempt, full table, frame timeout). Refusals in between are counted and the
    /// count is printed with the next line, so a flood costs one line per interval, never one per
    /// packet. 00:00:00 logs every refusal.
    /// </summary>
    public TimeSpan LogInterval { get; set; } = TimeSpan.FromSeconds(10);
}
