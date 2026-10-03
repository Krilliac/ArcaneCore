namespace ArcaneCore.World.Bans;

/// <summary>
/// The <c>Bans</c> configuration section (docs/security/live-bans.md). Every deviation from vmangos behaviour
/// is behind a switch that defaults to retail. Behaviour retail mandates (kick on <c>.ban</c>, refusal at logon
/// and world authentication, IP-ban refusal) has no switch.
/// </summary>
public sealed class BanOptions
{
    public const string SectionName = "Bans";

    /// <summary>
    /// After a live ban the stored session key is nulled, so a world reconnect with the old key is refused
    /// (it then answers UnknownAccount instead of AUTH_BANNED). Retail keeps the key and relies on the ban
    /// check (WorldSocket.cpp:287-290, 333-345). Bound from Bans:RevokeSessionKeyOnBan; default false.
    /// </summary>
    public bool RevokeSessionKeyOnBan { get; set; }

    /// <summary>
    /// How often connected sessions are re-checked against the ban rows, IP bans and the status column, so a ban
    /// written by another process is enforced. 0 (the default) is retail: vmangos never kicks for an externally
    /// written row; mangosd only reloads its IP cache (AccountMgr.cpp:317-327, World.cpp:697 BanListReloadTimer 60,
    /// mangosd.conf.dist.in:232-234 says 120). A very large realm should keep this at tens of seconds: each pass
    /// is a few indexed queries over the connected account ids. Bound from Bans:RecheckIntervalSeconds.
    /// </summary>
    public double RecheckIntervalSeconds { get; set; }
}
