namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// Persistence seam for account and IP bans (vmangos <c>account_banned</c> / <c>ip_banned</c>).
/// The rows are the authority: <see cref="Account.Status"/> stays an operator override on top.
/// All times are unix seconds from the store's <see cref="TimeProvider"/>.
/// </summary>
public interface IBanStore
{
    /// <summary>The newest ban row of the account that is in force now, or null.</summary>
    Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>The IP ban in force for a (normalised) address, or null.</summary>
    Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default);

    /// <summary>Which of <paramref name="accountIds"/> have a ban in force (queried in chunks of 500).</summary>
    Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default);

    /// <summary>Which of <paramref name="ips"/> have an IP ban in force.</summary>
    Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default);

    /// <summary>Insert an active account ban row; raises <see cref="AccountStatusEvents"/> after the commit.</summary>
    Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ban an address. A repeat ban of an address already actively banned keeps the first row (retail's second
    /// INSERT fails on the primary key); an expired row is replaced. Returns true when a row was written.
    /// </summary>
    Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivate every row of the account and write the inactive <c>UNBAN: message</c> audit row
    /// (World.cpp:2461-2467, 2655-2662). Returns true when a row was in force.
    /// </summary>
    Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default);

    /// <summary>Delete the IP ban row (retail has no audit for IP unbans, World.cpp:2641-2645). True if one existed.</summary>
    Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default);

    /// <summary>Every row of an account, oldest first (<c>.baninfo</c>).</summary>
    Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of <paramref name="accountIds"/> have at least one ban row, active or not (<c>.banlist character</c>). The
    /// caller passes a bounded batch: one query per call, so a listing never costs one query per account.
    /// </summary>
    Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default);

    /// <summary>Ban rows in force now (<c>.banlist</c>).</summary>
    Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default);

    /// <summary>IP bans in force whose address starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivate expired account rows and delete expired IP rows (realmd Main.cpp:213-215, mangosd World.cpp:1818-1820).
    /// Returns the number of rows touched.
    /// </summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
