using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Tests;

internal sealed class InMemoryAccountStore : IAccountStore, IAccountAdmin
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private int _nextId;

    /// <summary>Where a real status change is announced (set by the test host; null in plain unit tests).</summary>
    public AccountStatusEvents? Events { get; set; }

    public Task<bool> SetStatusAsync(
        string username, AccountStatus status, int? actorAccountId = null, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(username.ToUpperInvariant(), out Account? account))
        {
            return Task.FromResult(false);
        }

        if (account.Status != status)
        {
            account.Status = status;
            Events?.Publish(new AccountStatusChange(account.Id, status, actorAccountId));
        }

        return Task.FromResult(true);
    }

    public Task<bool> RevokeSessionKeyAsync(int accountId, CancellationToken cancellationToken = default)
    {
        Account? account = _accounts.Values.FirstOrDefault(a => a.Id == accountId);
        if (account is null)
        {
            return Task.FromResult(false);
        }

        account.SessionKey = null;
        return Task.FromResult(true);
    }

    public Task<IReadOnlySet<int>> FindNonActiveAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<int>>(_accounts.Values
            .Where(a => a.Status != AccountStatus.Active && accountIds.Contains(a.Id)).Select(a => a.Id).ToHashSet());

    public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
        => Task.FromResult(_accounts.TryGetValue(username.ToUpperInvariant(), out Account? a) ? a : null);

    public Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default)
    {
        account.Username = account.Username.ToUpperInvariant();
        account.Id = Interlocked.Increment(ref _nextId);
        _accounts[account.Username] = account;
        return Task.FromResult(account);
    }

    public Task UpdateCredentialsAsync(
        string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default)
    {
        Account account = _accounts[username.ToUpperInvariant()];
        account.Salt = salt;
        account.Verifier = verifier;
        return Task.CompletedTask;
    }

    public Task UpdateSessionKeyAsync(
        string username, byte[] sessionKey, CancellationToken cancellationToken = default)
    {
        _accounts[username.ToUpperInvariant()].SessionKey = sessionKey;
        return Task.CompletedTask;
    }

    public Task<bool> UpdateSecurityAsync(
        string username, AccountSecurity security, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(username.ToUpperInvariant(), out Account? account))
        {
            return Task.FromResult(false);
        }

        account.Security = security;
        return Task.FromResult(true);
    }
}
