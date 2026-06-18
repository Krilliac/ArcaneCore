using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Tests;

internal sealed class InMemoryAccountStore : IAccountStore
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private int _nextId;

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
}
