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
    /// written by another process (<c>arcane-account</c>, SQL, the realm daemon) is enforced. Default 60 seconds, the
    /// period of mangosd's own ban-list reload (World.cpp:697 BanListReloadTimer 60; mangosd.conf.dist.in:232-234 ships
    /// 120). vmangos only reloads its IP cache on that timer and never kicks for an externally written account row
    /// (AccountMgr.cpp:317-327), so kicking on the re-check is an ArcaneCore deviation in the safe direction; 0 turns the
    /// re-check off for exact retail behaviour. Each pass is a few indexed queries over the connected account ids.
    /// Bound from Bans:RecheckIntervalSeconds.
    /// </summary>
    public double RecheckIntervalSeconds { get; set; } = DefaultRecheckIntervalSeconds;

    /// <summary>The default of <see cref="RecheckIntervalSeconds"/>: mangosd BanListReloadTimer (World.cpp:697).</summary>
    public const double DefaultRecheckIntervalSeconds = 60;

    /// <summary>
    /// Refuse a <c>.ban</c> whose duration is not a clean <c>1d2h3m4s</c> string. Retail does not: any other
    /// character, or digits without a unit, make TimeStringToSecs return 0, which the command treats as a
    /// PERMANENT ban (Util.cpp:252-275), so a typo bans forever. Bound from Bans:RejectUnparseableDuration; default false.
    /// </summary>
    public bool RejectUnparseableDuration { get; set; }

    /// <summary>
    /// Refuse <c>.ban account</c> / <c>.ban character</c> against an account whose security level is equal to or
    /// higher than the invoker's (an account banning itself is still allowed, as in retail). vmangos has NO such
    /// guard: a game master there can ban an administrator. The strict default closes the one path by which a
    /// compromised low staff account locks out the highest account; set false for exact vmangos parity.
    /// Does not apply to <c>.ban ip</c> (no per-address account list is kept) or to unbans. Bound from
    /// Bans:ProtectHigherSecurity; default true (a deliberate deviation from retail, in the safe direction).
    /// </summary>
    public bool ProtectHigherSecurity { get; set; } = true;

    /// <summary>
    /// The realm id written to <c>account_banned.realm</c> (vmangos <c>realmID</c>); recorded and shown by
    /// <c>.baninfo</c>, never filtered on, exactly as retail. Bound from Bans:RealmId; default 1.
    /// </summary>
    public int RealmId { get; set; } = 1;

    /// <summary>
    /// The most entries one <c>.baninfo</c> history or <c>.banlist</c> reply prints before it ends with a "not shown" line.
    /// Retail prints everything; a long ban history or a one-letter prefix against a large realm would otherwise build and send
    /// one unbounded chat reply (and, for <c>.banlist character</c>, one history query per matching account). For <c>.banlist character</c> the cap also bounds the work: accounts are checked in
    /// batches of 200 (one query each) and the walk stops once one more than the cap has history. 0 restores retail's
    /// unbounded output. Bound from Bans:MaxListedEntries; default 200 (a deliberate deviation, only above that many entries).
    /// </summary>
    public int MaxListedEntries { get; set; } = 200;
}
