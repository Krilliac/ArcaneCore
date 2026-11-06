using System.Security.Cryptography;
using System.Net;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Kernel.Login;

/// <summary>Issues and consumes world-entry secrets. Credential, ban and session-admission authorities remain outside this service.</summary>
public sealed class SessionTokenService
{
    public static readonly TimeSpan LoginLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ResumeLifetime = TimeSpan.FromMinutes(5);
    private readonly ISessionTokenStore _store;
    private readonly TimeProvider _time;
    public SessionTokenService(ISessionTokenStore store, TimeProvider? timeProvider = null) { _store = store ?? throw new ArgumentNullException(nameof(store)); _time = timeProvider ?? TimeProvider.System; }

    public async Task<SessionTokenTicket> IssueLoginAsync(int accountId, uint clientBuild, string ip, CancellationToken cancellationToken = default)
    {
        if (accountId <= 0) throw new ArgumentOutOfRangeException(nameof(accountId));
        if (clientBuild == 0) throw new ArgumentOutOfRangeException(nameof(clientBuild));
        string normalizedIp = NormalizeIp(ip);
        byte[] id = RandomBytes(16), secret = RandomBytes(32), seed = RandomBytes(32);
        DateTime now = Now();
        try
        {
            await _store.InsertLoginAsync(new SessionTokenRecord { TokenId = id.ToArray(), Kind = SessionTokenKind.Login, AccountId = accountId,
                TokenSha256 = SHA256.HashData(secret), OriginTokenId = id.ToArray(), IntegritySeed = seed, ClientBuild = clientBuild, Ip = normalizedIp,
                CreatedUtc = now, ExpiresUtc = now + LoginLifetime }, cancellationToken).ConfigureAwait(false);
            return new SessionTokenTicket(id, secret, seed, now + LoginLifetime);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(id);
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public Task<SessionTokenAuthentication?> ConsumeLoginAsync(byte[] tokenId, byte[] token, uint clientBuild, string ip, int realmId, CancellationToken cancellationToken = default)
        => ConsumeAsync(SessionTokenKind.Login, tokenId, token, clientBuild, ip, realmId, cancellationToken);
    public Task<SessionTokenAuthentication?> ConsumeResumeAsync(byte[] token, uint clientBuild, string ip, int realmId, CancellationToken cancellationToken = default)
        => ConsumeAsync(SessionTokenKind.Resume, null, token, clientBuild, ip, realmId, cancellationToken);

    private async Task<SessionTokenAuthentication?> ConsumeAsync(SessionTokenKind kind, byte[]? tokenId, byte[] token, uint build, string ip, int realmId, CancellationToken cancellationToken)
    {
        if (build == 0 || realmId <= 0 || token is null || token.Length != 32 || (kind == SessionTokenKind.Login && (tokenId is null || tokenId.Length != 16))) return null;
        string normalizedIp;
        try { normalizedIp = NormalizeIp(ip); } catch (ArgumentException) { return null; }
        byte[] hash = SHA256.HashData(token), resumeId = RandomBytes(16), resumeSecret = RandomBytes(32);
        try
        {
            SessionTokenRecord? record = await _store.TryConsumeAndRotateAsync(new SessionTokenConsumeRequest(kind, tokenId?.ToArray(), hash, build, normalizedIp, realmId, Now(), resumeId, SHA256.HashData(resumeSecret)), cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                CryptographicOperations.ZeroMemory(resumeSecret);
                return null;
            }
            SessionTokenAuthentication result = new(record, resumeSecret);
            CryptographicOperations.ZeroMemory(record.IntegritySeed);
            CryptographicOperations.ZeroMemory(resumeSecret);
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(resumeSecret); throw; }
        finally { CryptographicOperations.ZeroMemory(hash); CryptographicOperations.ZeroMemory(resumeId); }
    }
    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
    private static string NormalizeIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out _)) throw new ArgumentException("Invalid IP address.", nameof(ip));
        return AccountBanEvaluator.NormalizeIp(ip) ?? throw new ArgumentException("Invalid IP address.", nameof(ip));
    }
    private static byte[] RandomBytes(int length) { byte[] value = new byte[length]; RandomNumberGenerator.Fill(value); return value; }
}
