using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>EF Core implementation of <see cref="IAccountStore"/>.</summary>
public sealed class EfAccountStore(ArcaneCoreDbContext db) : IAccountStore
{
    public async Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        string normalized = username.ToUpperInvariant();
        return await db.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Username == normalized, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default)
    {
        account.Username = account.Username.ToUpperInvariant();
        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return account;
    }

    public async Task UpdateCredentialsAsync(
        string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default)
    {
        string normalized = username.ToUpperInvariant();
        Account account = await db.Accounts
            .FirstOrDefaultAsync(a => a.Username == normalized, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"account '{normalized}' does not exist");

        account.Salt = salt;
        account.Verifier = verifier;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSessionKeyAsync(
        string username, byte[] sessionKey, CancellationToken cancellationToken = default)
    {
        string normalized = username.ToUpperInvariant();
        Account account = await db.Accounts
            .FirstOrDefaultAsync(a => a.Username == normalized, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"account '{normalized}' does not exist");

        account.SessionKey = sessionKey;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
