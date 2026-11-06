using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Login;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using MySqlConnector;

namespace ArcaneCore.Data.Stores;

public sealed class EfSessionTokenStore(AuthDbContext db) : ISessionTokenStore
{
    public async Task InsertLoginAsync(SessionTokenRecord token, CancellationToken cancellationToken = default)
    {
        ValidateLogin(token);
        SessionTokenRecord stored = Clone(token);
        stored.Ip = NormalizeIp(stored.Ip);
        db.Set<SessionTokenRecord>().Add(stored);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<SessionTokenRecord?> TryConsumeAndRotateAsync(SessionTokenConsumeRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return await ConsumeCoreAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSerializationConflict(exception) && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                return null;
            }
        }

        throw new InvalidOperationException("The session-token transaction did not complete.");
    }

    private async Task<SessionTokenRecord?> ConsumeCoreAsync(SessionTokenConsumeRequest request, CancellationToken cancellationToken)
    {
        DateTime now = RequireUtc(request.UtcNow, nameof(request.UtcNow));
        string requestIp = NormalizeIp(request.Ip);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        IQueryable<SessionTokenRecord> query = db.Set<SessionTokenRecord>().AsNoTracking();
        SessionTokenRecord? token = request.Kind == SessionTokenKind.Login
            ? await query.SingleOrDefaultAsync(x => x.Kind == SessionTokenKind.Login && x.TokenId == request.TokenId!, cancellationToken).ConfigureAwait(false)
            : await query.SingleOrDefaultAsync(x => x.Kind == SessionTokenKind.Resume && x.TokenSha256 == request.TokenSha256, cancellationToken).ConfigureAwait(false);
        if (token is null || token.ConsumedUtc is not null || token.ExpiresUtc <= now || token.ClientBuild != request.ClientBuild || !string.Equals(token.Ip, requestIp, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(token.TokenSha256, request.TokenSha256) || request.Kind == SessionTokenKind.Resume && token.IssuedRealmId != request.RealmId)
        { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return null; }
        bool hashCollision = await db.Set<SessionTokenRecord>().AnyAsync(x => x.TokenSha256 == request.ResumeSha256, cancellationToken).ConfigureAwait(false);
        bool idCollision = await db.Set<SessionTokenRecord>().AnyAsync(x => x.TokenId == request.ResumeTokenId, cancellationToken).ConfigureAwait(false);
        if (hashCollision || idCollision)
        { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return null; }
        int claimed = await db.Set<SessionTokenRecord>().Where(x => x.TokenId == token.TokenId && x.ConsumedUtc == null && x.ExpiresUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedUtc, now).SetProperty(x => x.ConsumedRealmId, request.RealmId), cancellationToken).ConfigureAwait(false);
        if (claimed != 1)
        { await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); return null; }
        await db.Set<SessionTokenRecord>().Where(x => x.AccountId == token.AccountId && x.Kind == SessionTokenKind.Resume && x.ConsumedUtc == null).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var resume = new SessionTokenRecord { TokenId = request.ResumeTokenId.ToArray(), Kind = SessionTokenKind.Resume, AccountId = token.AccountId,
            TokenSha256 = request.ResumeSha256.ToArray(), OriginTokenId = token.OriginTokenId.ToArray(), IntegritySeed = token.IntegritySeed.ToArray(), ClientBuild = token.ClientBuild,
            Ip = requestIp, CreatedUtc = now, ExpiresUtc = now + SessionTokenService.ResumeLifetime, IssuedRealmId = request.RealmId };
        db.Set<SessionTokenRecord>().Add(resume);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Clone(resume);
        }
        catch
        {
            db.Entry(resume).State = EntityState.Detached;
            throw;
        }
    }
    public Task<int> RevokeUnconsumedAsync(int accountId, CancellationToken cancellationToken = default)
    { if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId)); return db.Set<SessionTokenRecord>().Where(x => x.AccountId == accountId && x.ConsumedUtc == null).ExecuteDeleteAsync(cancellationToken); }
    public Task<int> PurgeExpiredAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = RequireUtc(cutoffUtc, nameof(cutoffUtc));
        return db.Set<SessionTokenRecord>().Where(x => x.ExpiresUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    private static void ValidateLogin(SessionTokenRecord token)
    {
        if (token is null || token.Kind != SessionTokenKind.Login || token.AccountId <= 0 || token.ClientBuild == 0
            || token.TokenId is null || token.TokenSha256 is null || token.OriginTokenId is null
            || token.IntegritySeed is null || token.Ip is null || token.TokenId.Length != 16
            || token.TokenSha256.Length != 32 || token.OriginTokenId.Length != 16
            || !token.TokenId.AsSpan().SequenceEqual(token.OriginTokenId) || token.IntegritySeed.Length != 32
            || token.IssuedRealmId is not null || token.ConsumedRealmId is not null || token.ConsumedUtc is not null
            || token.ExpiresUtc <= token.CreatedUtc || token.ExpiresUtc > token.CreatedUtc + SessionTokenService.LoginLifetime
            || token.CreatedUtc.Kind != DateTimeKind.Utc || token.ExpiresUtc.Kind != DateTimeKind.Utc
            || NormalizeIp(token.Ip) is null)
        {
            throw new ArgumentException("Invalid login token shape.", nameof(token));
        }
    }
    private static void ValidateRequest(SessionTokenConsumeRequest request)
    {
        if (request is null || request.RealmId <= 0 || request.ClientBuild == 0
            || request.TokenSha256 is null || request.ResumeTokenId is null || request.ResumeSha256 is null
            || request.Ip is null || request.UtcNow.Kind != DateTimeKind.Utc
            || request.TokenSha256.Length != 32 || request.ResumeTokenId.Length != 16
            || request.ResumeSha256.Length != 32 || request.Kind is not (SessionTokenKind.Login or SessionTokenKind.Resume)
            || request.Kind == SessionTokenKind.Login && request.TokenId?.Length != 16
            || NormalizeIp(request.Ip) is null)
        {
            throw new ArgumentException("Invalid token consume request.", nameof(request));
        }
    }
    private static string NormalizeIp(string ip) => AccountBanEvaluator.NormalizeIp(ip) ?? throw new ArgumentException("Invalid IP address.", nameof(ip));
    private static DateTime RequireUtc(DateTime value, string name)
    {
        if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamp must be UTC.", name);
        return value;
    }
    private static bool IsSerializationConflict(Exception exception)
        => DatabaseTransience.IsBusy(exception) || DatabaseTransience.HasSqlState(exception, "40001", "40P01");
    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 }) return true;
            if (current is DbException { SqlState: "23505" }) return true;
            if (current is MySqlException { Number: 1062 }) return true;
        }
        return false;
    }
    private static SessionTokenRecord Clone(SessionTokenRecord x) => new() { TokenId = x.TokenId.ToArray(), Kind = x.Kind, AccountId = x.AccountId, TokenSha256 = x.TokenSha256.ToArray(), OriginTokenId = x.OriginTokenId.ToArray(), IntegritySeed = x.IntegritySeed.ToArray(), ClientBuild = x.ClientBuild, Ip = x.Ip, CreatedUtc = x.CreatedUtc, ExpiresUtc = x.ExpiresUtc, ConsumedUtc = x.ConsumedUtc, IssuedRealmId = x.IssuedRealmId, ConsumedRealmId = x.ConsumedRealmId };
}
