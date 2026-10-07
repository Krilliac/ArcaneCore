using ArcaneCore.Data;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;

namespace ArcaneCore.Realm.Resilience;

/// <summary>
/// The auth-database stores as the logon daemon sees them: every call goes through the
/// <see cref="DatabaseGuard"/> of the Auth component (breaker, timeout, bulkhead), and the EF store itself is resolved
/// <i>inside</i> the guarded call, because with MariaDB the <c>DbContext</c> constructor already connects
/// (<c>ServerVersion.AutoDetect</c>) and that failure must count too. Scoped like the stores they wrap; a scope that
/// never touches the database never builds a context.
/// </summary>
internal sealed class GuardedAccountStore(Func<IAccountStore> inner, DatabaseGuard guard) : IAccountStore
{
    public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<Account?>(s.Inner().FindByUsernameAsync(s.Username, ct)),
            (Inner: inner, Username: username),
            cancellationToken).AsTask();

    public Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<Account>(s.Inner().CreateAsync(s.Account, ct)),
            (Inner: inner, Account: account),
            cancellationToken).AsTask();

    public Task UpdateCredentialsAsync(string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask(s.Inner().UpdateCredentialsAsync(s.Username, s.Salt, s.Verifier, ct)),
            (Inner: inner, Username: username, Salt: salt, Verifier: verifier),
            cancellationToken).AsTask();

    public Task UpdateSessionKeyAsync(string username, byte[] sessionKey, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask(s.Inner().UpdateSessionKeyAsync(s.Username, s.SessionKey, ct)),
            (Inner: inner, Username: username, SessionKey: sessionKey),
            cancellationToken).AsTask();

    public Task<bool> UpdateSecurityAsync(string username, AccountSecurity security, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<bool>(s.Inner().UpdateSecurityAsync(s.Username, s.Security, ct)),
            (Inner: inner, Username: username, Security: security),
            cancellationToken).AsTask();
}

/// <summary>See <see cref="GuardedAccountStore"/>.</summary>
internal sealed class GuardedRealmStore(Func<IRealmStore> inner, DatabaseGuard guard) : IRealmStore
{
    public Task<IReadOnlyList<RealmEntry>> GetRealmsAsync(CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IReadOnlyList<RealmEntry>>(s().GetRealmsAsync(ct)),
            inner,
            cancellationToken).AsTask();
}

/// <summary>See <see cref="GuardedAccountStore"/>.</summary>
internal sealed class GuardedBanStore(Func<IBanStore> inner, DatabaseGuard guard) : IBanStore
{
    public Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<AccountBanRecord?>(s.Inner().GetActiveAccountBanAsync(s.AccountId, ct)),
            (Inner: inner, AccountId: accountId),
            cancellationToken).AsTask();

    public Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IpBanRecord?>(s.Inner().GetActiveIpBanAsync(s.Ip, ct)),
            (Inner: inner, Ip: ip),
            cancellationToken).AsTask();

    public Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IReadOnlySet<int>>(s.Inner().FindBannedAccountsAsync(s.Ids, ct)),
            (Inner: inner, Ids: accountIds),
            cancellationToken).AsTask();

    public Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IReadOnlySet<string>>(s.Inner().FindBannedIpsAsync(s.Ips, ct)),
            (Inner: inner, Ips: ips),
            cancellationToken).AsTask();

    public Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask(s.Inner().BanAccountAsync(s.Request, ct)),
            (Inner: inner, Request: request),
            cancellationToken).AsTask();

    public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<bool>(s.Inner().BanIpAsync(s.Request, ct)),
            (Inner: inner, Request: request),
            cancellationToken).AsTask();

    public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<bool>(s.Inner().UnbanAccountAsync(s.AccountId, s.Source, s.Message, ct)),
            (Inner: inner, AccountId: accountId, Source: source, Message: message),
            cancellationToken).AsTask();

    public Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<bool>(s.Inner().UnbanIpAsync(s.Ip, ct)),
            (Inner: inner, Ip: ip),
            cancellationToken).AsTask();

    public Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IReadOnlyList<AccountBanRecord>>(s.Inner().GetHistoryAsync(s.AccountId, ct)),
            (Inner: inner, AccountId: accountId),
            cancellationToken).AsTask();

    public Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IReadOnlySet<int>>(s.Inner().FindAccountsWithHistoryAsync(s.Ids, ct)),
            (Inner: inner, Ids: accountIds),
            cancellationToken).AsTask();

    public Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IReadOnlyList<AccountBanRecord>>(s().ListActiveAccountBansAsync(ct)),
            inner,
            cancellationToken).AsTask();

    public Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<IReadOnlyList<IpBanRecord>>(s.Inner().ListIpBansAsync(s.Prefix, ct)),
            (Inner: inner, Prefix: prefix),
            cancellationToken).AsTask();

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static (s, ct) => new ValueTask<int>(s().PurgeExpiredAsync(ct)),
            inner,
            cancellationToken).AsTask();
}
