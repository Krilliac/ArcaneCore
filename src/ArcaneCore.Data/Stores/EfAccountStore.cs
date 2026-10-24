using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// EF Core implementation of <see cref="IAccountStore"/> and <see cref="IAccountAdmin"/>. The events
/// parameter is optional so existing <c>new EfAccountStore(db)</c> call sites keep compiling; DI supplies it.
/// </summary>
public sealed class EfAccountStore(AuthDbContext db, AccountStatusEvents? events = null) : IAccountStore, IAccountAdmin, IAccountLoginSecurityStore, IAccountLoginInfoStore
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
        try
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return account;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
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

    public async Task UpdateLoginAsync(string username, byte[] sessionKey, string? address,
        CancellationToken cancellationToken = default)
    {
        string normalized = username.ToUpperInvariant();
        Account account = await db.Accounts.FirstOrDefaultAsync(a => a.Username == normalized, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException($"account '{normalized}' does not exist");
        account.SessionKey = sessionKey;
        if (address is not null) account.LastIp = address;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        string normalized = username.ToUpperInvariant();
        // One UPDATE statement; an unknown account matches no row. The column holds UTC and providers reject other kinds on write.
        DateTime? when = info.LastLoginUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
        await db.Accounts.Where(a => a.Username == normalized)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.LastLoginUtc, when)
                .SetProperty(a => a.LastOs, info.Os)
                .SetProperty(a => a.LastPlatform, info.Platform)
                .SetProperty(a => a.LastLocale, info.Locale)
                .SetProperty(a => a.LastBuild, info.Build), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default)
    {
        string normalized = username.ToUpperInvariant();
        var row = await db.Accounts.AsNoTracking()
            .Where(a => a.Username == normalized)
            .Select(a => new { a.LastLoginUtc, a.LastOs, a.LastPlatform, a.LastLocale, a.LastBuild })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        // Providers hand back an Unspecified kind; the column always holds UTC.
        DateTime? when = row.LastLoginUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
        return new AccountLoginInfo(when, row.LastOs, row.LastPlatform, row.LastLocale, row.LastBuild);
    }

    public async Task<bool> SetAsync(string username, AccountLockFlags flags, string securityInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityInfo);
        string normalized = username.ToUpperInvariant();
        Account? account = await db.Accounts.FirstOrDefaultAsync(a => a.Username == normalized, cancellationToken)
            .ConfigureAwait(false);
        if (account is null) return false;
        if (!AccountLoginSecurityPolicy.IsValid(flags, securityInfo))
            throw new ArgumentException("Invalid account login security settings", nameof(flags));
        account.LockFlags = flags;
        account.SecurityInfo = securityInfo;
        account.SessionKey = null; // a changed factor must invalidate a previously issued world key
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
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
