using ArcaneCore.Data;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Realm.Resilience;

/// <summary>
/// The auth-database stores as the logon daemon sees them: every call goes through the
/// <see cref="DatabaseGuard"/> of the Auth component (breaker, timeout, bulkhead), and the EF store itself is resolved
/// <i>inside</i> the guarded call (<see cref="GuardedCalls{TStore}"/>), because with MariaDB the <c>DbContext</c>
/// constructor already connects (<c>ServerVersion.AutoDetect</c>) and that failure must count too. A scope that never
/// touches the database never builds a context.
/// </summary>
internal sealed class GuardedAccountStore(GuardedCalls<IAccountStore> calls) : IAccountStore
{
    public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.FindByUsernameAsync(s, ct), username, cancellationToken);

    public Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.CreateAsync(s, ct), account, cancellationToken);

    public Task UpdateCredentialsAsync(string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default)
        => calls.RunAsync(
            static (store, s, ct) => store.UpdateCredentialsAsync(s.Username, s.Salt, s.Verifier, ct),
            (Username: username, Salt: salt, Verifier: verifier),
            cancellationToken);

    public Task UpdateSessionKeyAsync(string username, byte[] sessionKey, CancellationToken cancellationToken = default)
        => calls.RunAsync(
            static (store, s, ct) => store.UpdateSessionKeyAsync(s.Username, s.SessionKey, ct),
            (Username: username, SessionKey: sessionKey),
            cancellationToken);

    public Task UpdateLoginAsync(string username, byte[] sessionKey, string? address, CancellationToken cancellationToken = default)
        => calls.RunAsync(
            static (store, s, ct) => store.UpdateLoginAsync(s.Username, s.SessionKey, s.Address, ct),
            (Username: username, SessionKey: sessionKey, Address: address),
            cancellationToken);

    public Task<bool> UpdateSecurityAsync(string username, AccountSecurity security, CancellationToken cancellationToken = default)
        => calls.RunAsync(
            static (store, s, ct) => store.UpdateSecurityAsync(s.Username, s.Security, ct),
            (Username: username, Security: security),
            cancellationToken);
}

/// <summary>See <see cref="GuardedAccountStore"/>.</summary>
internal sealed class GuardedAccountLoginInfoStore(GuardedCalls<IAccountLoginInfoStore> calls) : IAccountLoginInfoStore
{
    public Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default)
        => calls.RunAsync(
            static (store, s, ct) => store.RecordLoginAsync(s.Username, s.Info, ct),
            (Username: username, Info: info),
            cancellationToken);

    public Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.GetLoginInfoAsync(s, ct), username, cancellationToken);
}

/// <summary>See <see cref="GuardedAccountStore"/>.</summary>
internal sealed class GuardedRealmStore(GuardedCalls<IRealmStore> calls) : IRealmStore
{
    public Task<IReadOnlyList<RealmEntry>> GetRealmsAsync(CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, ct) => store.GetRealmsAsync(ct), cancellationToken);
}

/// <summary>See <see cref="GuardedAccountStore"/>.</summary>
internal sealed class GuardedBanStore(GuardedCalls<IBanStore> calls) : IBanStore
{
    public Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.GetActiveAccountBanAsync(s, ct), accountId, cancellationToken);

    public Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.GetActiveIpBanAsync(s, ct), ip, cancellationToken);

    public Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.FindBannedAccountsAsync(s, ct), accountIds, cancellationToken);

    public Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.FindBannedIpsAsync(s, ct), ips, cancellationToken);

    public Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.BanAccountAsync(s, ct), request, cancellationToken);

    public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.BanIpAsync(s, ct), request, cancellationToken);

    public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default)
        => calls.RunAsync(
            static (store, s, ct) => store.UnbanAccountAsync(s.AccountId, s.Source, s.Message, ct),
            (AccountId: accountId, Source: source, Message: message),
            cancellationToken);

    public Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.UnbanIpAsync(s, ct), ip, cancellationToken);

    public Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.GetHistoryAsync(s, ct), accountId, cancellationToken);

    public Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.FindAccountsWithHistoryAsync(s, ct), accountIds, cancellationToken);

    public Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, ct) => store.ListActiveAccountBansAsync(ct), cancellationToken);

    public Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, s, ct) => store.ListIpBansAsync(s, ct), prefix, cancellationToken);

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
        => calls.RunAsync(static (store, ct) => store.PurgeExpiredAsync(ct), cancellationToken);
}

/// <summary>
/// Runs one store call under the Auth <see cref="DatabaseGuard"/>, on the undecorated store
/// (<see cref="InnerStoreKey"/>) of a DI scope of the call's own: the guarded
/// operation creates that scope and disposes it when the store call has ended, never earlier.
/// <para>
/// Why not the caller's scope: with SQLite the guard abandons a call at the deadline while it still runs inside its
/// <c>AuthDbContext</c> (<see cref="DatabaseGuard.TimeoutStrategyFor"/>, pessimistic), and that context is pooled
/// (<c>AddAuthDatabase</c>). Disposing the caller's scope then returned the busy context to the pool, and the next
/// lease got it while the abandoned query was still running: the IP-ban list load that ran out of the query budget
/// handed its context to the single-row read that follows it, EF refused the second operation on the context
/// (<see cref="InvalidOperationException"/>, not a database error) and the logon connection closed without the
/// WOW_FAIL_DB_BUSY answer. A scope owned by the call keeps the context leased until the abandoned call ends, so every
/// later call gets a context of its own, and disposing the caller's scope never waits for an abandoned statement.
/// </para>
/// <para>
/// Each call leases its context from the pool and returns it; separate calls of one session never share a change
/// tracker. Thread-safe; one instance per decorator.
/// </para>
/// </summary>
internal sealed class GuardedCalls<TStore>(IServiceScopeFactory scopes, DatabaseGuard guard)
    where TStore : class
{
    /// <summary>The key under which the undecorated store registrations are kept.</summary>
    public const string InnerStoreKey = "ArcaneCore.Realm.Resilience.Inner";

    public Task<TResult> RunAsync<TState, TResult>(
        Func<TStore, TState, CancellationToken, Task<TResult>> call, TState state, CancellationToken cancellationToken)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static async (s, ct) =>
            {
                await using AsyncServiceScope scope = s.Scopes.CreateAsyncScope();
                return await s.Call(Inner(scope), s.State, ct).ConfigureAwait(false);
            },
            (Scopes: scopes, Call: call, State: state),
            cancellationToken).AsTask();

    public Task RunAsync<TState>(Func<TStore, TState, CancellationToken, Task> call, TState state, CancellationToken cancellationToken)
        => guard.ExecuteAsync(
            DatabaseComponent.Auth,
            static async (s, ct) =>
            {
                await using AsyncServiceScope scope = s.Scopes.CreateAsyncScope();
                await s.Call(Inner(scope), s.State, ct).ConfigureAwait(false);
            },
            (Scopes: scopes, Call: call, State: state),
            cancellationToken).AsTask();

    public Task<TResult> RunAsync<TResult>(Func<TStore, CancellationToken, Task<TResult>> call, CancellationToken cancellationToken)
        => RunAsync(static (store, c, ct) => c(store, ct), call, cancellationToken);

    private static TStore Inner(AsyncServiceScope scope)
        => scope.ServiceProvider.GetRequiredKeyedService<TStore>(InnerStoreKey);
}

/// <summary>
/// Diagnostic classification for guarded auth-database store calls, combining <see cref="DatabaseTransience.DescribeException"/>
/// with the store operation context.
/// </summary>
internal static class GuardedAuthDiagnostics
{
    public static string DescribeStoreFault(string storeOperation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"store={storeOperation} {DatabaseTransience.DescribeException(exception)}";
    }
}
