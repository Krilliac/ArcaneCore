using System.Security.Cryptography;

namespace ArcaneCore.Kernel.Login;

public enum SessionTokenKind : byte
{
    Login = 1,
    Resume = 2,
}

/// <summary>Auth-database state: the token secret is hashed; the integrity seed is retained for the world session.</summary>
public sealed class SessionTokenRecord
{
    public required byte[] TokenId { get; set; }
    public SessionTokenKind Kind { get; set; }
    public int AccountId { get; set; }
    public required byte[] TokenSha256 { get; set; }
    public required byte[] OriginTokenId { get; set; }
    public required byte[] IntegritySeed { get; set; }
    public uint ClientBuild { get; set; }
    public required string Ip { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? ConsumedUtc { get; set; }
    public int? IssuedRealmId { get; set; }
    public int? ConsumedRealmId { get; set; }
}

public sealed record SessionTokenConsumeRequest(
    SessionTokenKind Kind, byte[]? TokenId, byte[] TokenSha256, uint ClientBuild, string Ip, int RealmId,
    DateTime UtcNow, byte[] ResumeTokenId, byte[] ResumeSha256);

public interface ISessionTokenStore
{
    Task InsertLoginAsync(SessionTokenRecord token, CancellationToken cancellationToken = default);
    Task<SessionTokenRecord?> TryConsumeAndRotateAsync(SessionTokenConsumeRequest request, CancellationToken cancellationToken = default);
    Task<int> RevokeUnconsumedAsync(int accountId, CancellationToken cancellationToken = default);
    Task<int> PurgeExpiredAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

/// <summary>Secret-bearing login ticket. Getters return caller-owned copies; disposal zeros private buffers.</summary>
public sealed class SessionTokenTicket : IDisposable
{
    private byte[]? _tokenId;
    private byte[]? _token;
    private byte[]? _integritySeed;
    internal SessionTokenTicket(byte[] tokenId, byte[] token, byte[] integritySeed, DateTime expiresUtc)
    {
        _tokenId = tokenId.ToArray();
        _token = token.ToArray();
        _integritySeed = integritySeed.ToArray();
        ExpiresUtc = expiresUtc;
    }
    public DateTime ExpiresUtc { get; }
    public byte[] TokenId => Copy(_tokenId);
    public byte[] Token => Copy(_token);
    public byte[] IntegritySeed => Copy(_integritySeed);
    public void Dispose()
    {
        if (_tokenId is { } id) { CryptographicOperations.ZeroMemory(id); _tokenId = null; }
        if (_token is { } secret) { CryptographicOperations.ZeroMemory(secret); _token = null; }
        if (_integritySeed is { } seed) { CryptographicOperations.ZeroMemory(seed); _integritySeed = null; }
    }
    private static byte[] Copy(byte[]? value) { ObjectDisposedException.ThrowIf(value is null, typeof(SessionTokenTicket)); return value.ToArray(); }
}

public sealed class SessionTokenAuthentication : IDisposable
{
    private byte[]? _resumeToken;
    private SessionTokenRecord? _record;
    internal SessionTokenAuthentication(SessionTokenRecord record, byte[] resumeToken)
    {
        _record = CopyRecord(record);
        _resumeToken = resumeToken.ToArray();
    }
    /// <summary>A caller-owned snapshot, including a copy of the integrity seed.</summary>
    public SessionTokenRecord Record => CopyRecord(_record ?? throw new ObjectDisposedException(nameof(SessionTokenAuthentication)));
    public byte[] ResumeToken => _resumeToken?.ToArray() ?? throw new ObjectDisposedException(nameof(SessionTokenAuthentication));
    public void Dispose()
    {
        if (_resumeToken is { } value) CryptographicOperations.ZeroMemory(value);
        if (_record is { } record) CryptographicOperations.ZeroMemory(record.IntegritySeed);
        _resumeToken = null;
        _record = null;
    }

    private static SessionTokenRecord CopyRecord(SessionTokenRecord record) => new()
    {
        TokenId = record.TokenId.ToArray(), Kind = record.Kind, AccountId = record.AccountId,
        TokenSha256 = record.TokenSha256.ToArray(), OriginTokenId = record.OriginTokenId.ToArray(),
        IntegritySeed = record.IntegritySeed.ToArray(), ClientBuild = record.ClientBuild, Ip = record.Ip,
        CreatedUtc = record.CreatedUtc, ExpiresUtc = record.ExpiresUtc, ConsumedUtc = record.ConsumedUtc,
        IssuedRealmId = record.IssuedRealmId, ConsumedRealmId = record.ConsumedRealmId,
    };
}
