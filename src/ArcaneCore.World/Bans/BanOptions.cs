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
    /// Maximum distinct matching character-owner accounts checked by <c>.banlist character</c>.
    /// 0 (default) is unlimited, as vmangos AccountCommands.cpp:835-910. Nonzero values are clamped to 1..500;
    /// truncation prints a notice asking for a narrower prefix. Bounds candidates before checking ban history,
    /// because character and auth rows live in separate databases. Bound from Bans:CharacterListMaxResults.
    /// </summary>
    public int CharacterListMaxResults { get; set; }
}
