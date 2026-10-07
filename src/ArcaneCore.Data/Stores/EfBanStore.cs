using System.Data.Common;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>
/// EF Core implementation of <see cref="IBanStore"/> with the retail row semantics (vmangos
/// World.cpp:2461-2486, 2585-2665; AccountMgr.cpp:371-410). Times come from the injected
/// <see cref="TimeProvider"/>, not the database clock, so SQLite, MariaDB and PostgreSQL agree and
/// expiry is testable. Events are raised after the commit and never fail the mutation.
/// </summary>
public sealed class EfBanStore(AuthDbContext db, TimeProvider? clock = null, AccountStatusEvents? events = null) : IBanStore
{
    /// <summary>Ids per query: keeps the IN list under every provider's parameter limit.</summary>
    internal const int ChunkSize = 500;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public async Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default)
    {
        long now = Now;
        List<AccountBanRow> rows = await db.Set<AccountBanRow>().AsNoTracking()
            .Where(r => r.AccountId == accountId && r.Active && (r.UnbanDate > now || r.BanDate == r.UnbanDate))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Retail takes an arbitrary row (LIMIT 1); take the most severe: permanent first, then the latest expiry.
        return rows.OrderByDescending(r => r.BanDate == r.UnbanDate).ThenByDescending(r => r.UnbanDate)
            .Select(r => r.ToRecord()).FirstOrDefault();
    }

    public async Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default)
    {
        string key = Normalize(ip);
        long now = Now;
        IpBanRow? row = await db.Set<IpBanRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Ip == key && (r.UnbanDate > now || r.BanDate == r.UnbanDate), cancellationToken)
            .ConfigureAwait(false);
        return row?.ToRecord();
    }

    public async Task<IReadOnlySet<int>> FindBannedAccountsAsync(
        IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        long now = Now;
        var banned = new HashSet<int>();
        foreach (int[] chunkArray in accountIds.Distinct().Chunk(ChunkSize))
        {
            List<int> chunk = [.. chunkArray]; // List.Contains: an array binds to the ReadOnlySpan overload, which EF cannot translate
            List<int> hit = await db.Set<AccountBanRow>().AsNoTracking()
                .Where(r => chunk.Contains(r.AccountId) && r.Active && (r.UnbanDate > now || r.BanDate == r.UnbanDate))
                .Select(r => r.AccountId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
            banned.UnionWith(hit);
        }

        return banned;
    }

    public async Task<IReadOnlySet<string>> FindBannedIpsAsync(
        IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ips);
        long now = Now;
        var banned = new HashSet<string>(StringComparer.Ordinal);
        foreach (string[] chunkArray in ips.Select(Normalize).Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
        {
            List<string> chunk = [.. chunkArray];
            List<string> hit = await db.Set<IpBanRow>().AsNoTracking()
                .Where(r => chunk.Contains(r.Ip) && (r.UnbanDate > now || r.BanDate == r.UnbanDate))
                .Select(r => r.Ip).ToListAsync(cancellationToken).ConfigureAwait(false);
            banned.UnionWith(hit);
        }

        return banned;
    }

    public async Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        long now = Now;
        db.Set<AccountBanRow>().Add(new AccountBanRow
        {
            AccountId = request.AccountId,
            BanDate = now,
            UnbanDate = now + Math.Max(0, request.DurationSeconds), // 0 = permanent: bandate == unbandate
            BannedBy = Truncate(request.Author, 50),
            BanReason = Truncate(request.Reason, 255),
            Active = true,
            Realm = request.Realm,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (events is not null)
        {
            bool permanent = await EffectiveBanIsPermanentAsync(request.AccountId, request.DurationSeconds <= 0).ConfigureAwait(false);
            events.Publish(new AccountStatusChange(
                request.AccountId,
                permanent ? AccountStatus.Banned : AccountStatus.Suspended,
                request.AuthorAccountId));
        }
    }

    /// <summary>
    /// Retail keeps every row (World.cpp:2476 inserts, nothing is deactivated), so an older, stricter ban can remain the
    /// effective one: the published status is that of the strictest ban in force, not of the request. The read follows
    /// the commit and must not fail the mutation, so a failed read falls back to the request's own kind.
    /// </summary>
    private async Task<bool> EffectiveBanIsPermanentAsync(int accountId, bool requestIsPermanent)
    {
        try
        {
            AccountBanRecord? effective = await GetActiveAccountBanAsync(accountId).ConfigureAwait(false);
            return effective?.IsPermanent ?? requestIsPermanent;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return requestIsPermanent;
        }
    }

    public async Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string ip = Normalize(request.Ip);
        long now = Now;
        bool wrote;
        try
        {
            wrote = await TryWriteIpBanAsync(request, ip, now, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two callers inserted the same address at once: MariaDB raises 1062, PostgreSQL aborts the
            // statement (25P02 if it were inside a shared transaction), SQLite a constraint error. Each
            // SaveChanges owns its transaction, so the connection is clean; start from a clean tracker,
            // re-read, and accept a concurrently inserted row that is in force.
            db.ChangeTracker.Clear();
            IpBanRow? existing = await db.Set<IpBanRow>().AsNoTracking()
                .FirstOrDefaultAsync(r => r.Ip == ip, cancellationToken).ConfigureAwait(false);
            if (existing is null || !AccountBanEvaluator.IsActive(existing.ToRecord(), Now))
            {
                throw;
            }

            wrote = false;
        }

        if (wrote)
        {
            events?.Publish(new IpBanChange(ip, request.AuthorAccountId));
        }

        return wrote;
    }

    private async Task<bool> TryWriteIpBanAsync(IpBanRequest request, string ip, long now, CancellationToken ct)
    {
        IpBanRow? existing = await db.Set<IpBanRow>().FirstOrDefaultAsync(r => r.Ip == ip, ct).ConfigureAwait(false);
        long unban = now + Math.Max(0, request.DurationSeconds);
        if (existing is null)
        {
            db.Set<IpBanRow>().Add(new IpBanRow
            {
                Ip = ip,
                BanDate = now,
                UnbanDate = unban,
                BannedBy = Truncate(request.Author, 50),
                BanReason = Truncate(request.Reason, 255),
            });
        }
        else if (AccountBanEvaluator.IsActive(existing.ToRecord(), now))
        {
            return false; // retail: the second INSERT fails on the primary key; the first row stays
        }
        else
        {
            existing.BanDate = now;
            existing.UnbanDate = unban;
            existing.BannedBy = Truncate(request.Author, 50);
            existing.BanReason = Truncate(request.Reason, 255);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UnbanAccountAsync(
        int accountId, string source, string message, CancellationToken cancellationToken = default)
    {
        long now = Now;
        int deactivated;

        // The deactivation and its audit row are one transaction: a failed audit insert leaves the bans in force, and
        // Active is published only once both are committed.
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            deactivated = await db.Set<AccountBanRow>().Where(r => r.AccountId == accountId && r.Active)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Active, false), cancellationToken).ConfigureAwait(false);

            // Retail WarnAccount writes the audit row even when nothing was active: inactive, unbandate = bandate + 1.
            db.ChangeTracker.Clear();
            db.Set<AccountBanRow>().Add(new AccountBanRow
            {
                AccountId = accountId,
                BanDate = now,
                UnbanDate = now + 1,
                BannedBy = Truncate(source, 50),
                BanReason = Truncate("UNBAN: " + message, 255),
                Active = false,
                Realm = 0,
            });
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                db.ChangeTracker.Clear(); // the rolled-back audit row must not be retried by a later save on this context
                throw;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (deactivated > 0)
        {
            events?.Publish(new AccountStatusChange(accountId, AccountStatus.Active));
        }

        return deactivated > 0;
    }

    public async Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default)
    {
        string key = Normalize(ip);
        int deleted = await db.Set<IpBanRow>().Where(r => r.Ip == key).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0;
    }

    public async Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default)
    {
        List<AccountBanRow> rows = await db.Set<AccountBanRow>().AsNoTracking()
            .Where(r => r.AccountId == accountId).OrderBy(r => r.BanDate).ThenBy(r => r.BanId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToRecord())];
    }

    public async Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(
        IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        var found = new HashSet<int>();
        foreach (int[] chunkArray in accountIds.Distinct().Chunk(ChunkSize))
        {
            List<int> chunk = [.. chunkArray]; // List.Contains: see FindBannedAccountsAsync
            found.UnionWith(await db.Set<AccountBanRow>().AsNoTracking()
                .Where(r => chunk.Contains(r.AccountId)).Select(r => r.AccountId).Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    public async Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default)
    {
        long now = Now;
        List<AccountBanRow> rows = await db.Set<AccountBanRow>().AsNoTracking()
            .Where(r => r.Active && (r.UnbanDate > now || r.BanDate == r.UnbanDate))
            .OrderBy(r => r.AccountId).ThenBy(r => r.BanId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToRecord())];
    }

    public async Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default)
    {
        string p = prefix ?? string.Empty;
        long now = Now;
        List<IpBanRow> rows = await db.Set<IpBanRow>().AsNoTracking()
            .Where(r => r.Ip.StartsWith(p) && (r.UnbanDate > now || r.BanDate == r.UnbanDate))
            .OrderBy(r => r.Ip).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToRecord())];
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        long now = Now;
        int accounts = await db.Set<AccountBanRow>().Where(r => r.UnbanDate <= now && r.UnbanDate != r.BanDate && r.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Active, false), cancellationToken).ConfigureAwait(false);
        int ips = await db.Set<IpBanRow>().Where(r => r.UnbanDate <= now && r.UnbanDate != r.BanDate)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return accounts + ips;
    }

    private static string Normalize(string ip) => AccountBanEvaluator.NormalizeIp(ip) ?? ip.Trim();

    private static string Truncate(string? text, int max)
    {
        text ??= string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
