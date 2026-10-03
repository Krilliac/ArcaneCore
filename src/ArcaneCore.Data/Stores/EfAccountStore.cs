using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// EF Core implementation of <see cref="IAccountStore"/> and <see cref="IAccountAdmin"/>. The events
/// parameter is optional so existing <c>new EfAccountStore(db)</c> call sites keep compiling; DI supplies it.
/// </summary>
public sealed class EfAccountStore(AuthDbContext db, AccountStatusEvents? events = null) : IAccountStore, IAccountAdmin
{
    /// <summary>Ids per query (see <see cref="EfBanStore"/>).</summary>
    private const int ChunkSize = 500;

    public async Task<bool> SetStatusAsync(
        string username, AccountStatus status, int? actorAccountId = null, CancellationToken cancellationToken = default)
    {
        string normalized = username.ToUpperInvariant();
        Account? account = await db.Accounts
            .FirstOrDefaultAsync(a => a.Username == normalized, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return false;
        }

        if (account.Status == status)
        {
            return true; // nothing changed: no write, no event
        }

        account.Status = status;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        events?.Publish(new AccountStatusChange(account.Id, status, actorAccountId));
        return true;
    }

    public async Task<bool> RevokeSessionKeyAsync(int accountId, CancellationToken cancellationToken = default)
    {
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return false;
        }

        account.SessionKey = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyDictionary<int, string>> GetUsernamesAsync(
        IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var names = new Dictionary<int, string>();
        foreach (int[] chunkArray in accountIds.Distinct().Chunk(ChunkSize))
        {
            List<int> chunk = [.. chunkArray];
            foreach (var row in await db.Accounts.AsNoTracking().Where(a => chunk.Contains(a.Id))
                .Select(a => new { a.Id, a.Username }).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                names[row.Id] = row.Username;
            }
        }

        return names;
    }

    public async Task<IReadOnlySet<int>> FindNonActiveAsync(
        IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var found = new HashSet<int>();
        foreach (int[] chunkArray in accountIds.Distinct().Chunk(ChunkSize))
        {
            List<int> chunk = [.. chunkArray]; // List.Contains: an array would bind to the span overload EF cannot translate
            found.UnionWith(await db.Accounts.AsNoTracking()
                .Where(a => chunk.Contains(a.Id) && a.Status != AccountStatus.Active)
                .Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

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

    public async Task<bool> UpdateSecurityAsync(
        string username, AccountSecurity security, CancellationToken cancellationToken = default)
    {
        string normalized = username.ToUpperInvariant();
        Account? account = await db.Accounts
            .FirstOrDefaultAsync(a => a.Username == normalized, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return false;
        }

        account.Security = security;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
