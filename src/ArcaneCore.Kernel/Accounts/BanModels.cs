using System.Net;

namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// A ban to record against an account (vmangos World::BanAccount, World.cpp:2469-2486). A
/// <see cref="DurationSeconds"/> of 0 is a permanent ban: retail stores bandate == unbandate
/// (AccountMgr.cpp:376,388-390).
/// </summary>
/// <param name="AccountId">The banned account.</param>
/// <param name="DurationSeconds">0 = permanent.</param>
/// <param name="Reason">Free text (retail column is varchar(255)).</param>
/// <param name="Author">Who banned (retail <c>bannedby</c>, varchar(50)).</param>
/// <param name="AuthorAccountId">The banning staff account, never kicked by its own ban (World.cpp:2552-2553).</param>
/// <param name="Realm">Realm id written to <c>account_banned.realm</c>; never filtered on, exactly as retail.</param>
public sealed record BanRequest(
    int AccountId, long DurationSeconds, string Reason, string Author, int? AuthorAccountId = null, int Realm = 1);

/// <summary>An IP ban (vmangos <c>ip_banned</c>, World.cpp:2608). The address is matched exactly.</summary>
public sealed record IpBanRequest(string Ip, long DurationSeconds, string Reason, string Author, int? AuthorAccountId = null);

/// <summary>One row of <c>account_banned</c> (vmangos sql/logon.sql:75-86).</summary>
public sealed record AccountBanRecord(
    int BanId, int AccountId, long BanDate, long UnbanDate, string BannedBy, string Reason, bool Active, int Realm)
{
    /// <summary>Retail encodes a permanent ban as bandate == unbandate (AuthSocket.cpp:464).</summary>
    public bool IsPermanent => BanDate == UnbanDate;
}

/// <summary>One row of <c>ip_banned</c> (vmangos sql/logon.sql:139-146).</summary>
public sealed record IpBanRecord(string Ip, long BanDate, long UnbanDate, string BannedBy, string Reason)
{
    public bool IsPermanent => BanDate == UnbanDate;
}

/// <summary>
/// The one place that decides whether a ban row is in force and what status an account has,
/// shared by the realm daemon, the world daemon and the live re-check so they cannot drift.
/// </summary>
public static class AccountBanEvaluator
{
    /// <summary>
    /// Retail predicate: <c>active = 1 AND (unbandate &gt; now OR bandate = unbandate)</c>
    /// (AuthSocket.cpp:464, WorldSocket.cpp:270-292). All times are unix seconds.
    /// </summary>
    public static bool IsActive(AccountBanRecord ban, long now)
    {
        ArgumentNullException.ThrowIfNull(ban);
        return ban.Active && (ban.UnbanDate > now || ban.BanDate == ban.UnbanDate);
    }

    /// <summary>Retail IP predicate: <c>unbandate &gt; now OR bandate = unbandate</c> (AuthSocket.cpp:338-352).</summary>
    public static bool IsActive(IpBanRecord ban, long now)
    {
        ArgumentNullException.ThrowIfNull(ban);
        return ban.UnbanDate > now || ban.BanDate == ban.UnbanDate;
    }

    /// <summary>
    /// The account's effective status: a non-Active status column is an operator override that is
    /// always honoured; otherwise a permanent ban row means Banned (realm 0x03) and a temporary one
    /// Suspended (realm 0x0C, AuthSocket.cpp:464-476).
    /// </summary>
    public static AccountStatus Effective(AccountStatus column, AccountBanRecord? activeBan)
    {
        if (column != AccountStatus.Active)
        {
            return column;
        }

        if (activeBan is null)
        {
            return AccountStatus.Active;
        }

        return activeBan.IsPermanent ? AccountStatus.Banned : AccountStatus.Suspended;
    }

    /// <summary>
    /// Normalise an address the way the connection limiter does: IPv4-mapped IPv6 becomes IPv4.
    /// Returns null when the text is not an IP address (tests pass a placeholder endpoint).
    /// </summary>
    public static string? NormalizeIp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text.Trim(), out IPAddress? address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    /// <summary>The address of an <c>ip:port</c> / <c>[ipv6]:port</c> endpoint string, normalised; null if unparseable.</summary>
    public static string? AddressOfEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        return IPEndPoint.TryParse(endpoint.Trim(), out IPEndPoint? parsed)
            ? NormalizeIp(parsed.Address.ToString())
            : NormalizeIp(endpoint);
    }
}
