using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Kernel.Login;

public sealed class AccountCredential
{
    public int AccountId { get; set; }
    public required string Email { get; set; }
    public required string EmailNormalized { get; set; }
    public DateTime? EmailVerifiedUtc { get; set; }
    public string? PasswordHash { get; set; }
    public DateTime? PasswordChangedUtc { get; set; }
    public required byte[] SecurityStamp { get; set; }
    public byte[]? TotpSecretEncrypted { get; set; }
    public byte TotpKeyId { get; set; }
    public DateTime? TotpEnabledUtc { get; set; }
    public long TotpLastStep { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastFailedLoginUtc { get; set; }
}

public sealed record LoginAccountInfo(int AccountId, AccountStatus Status, AccountSecurity Security);

public interface IAccountCredentialStore
{
    Task<AccountCredential?> FindByEmailNormalizedAsync(string emailNormalized, CancellationToken cancellationToken = default);
    Task<LoginAccountInfo?> FindAccountAsync(int accountId, CancellationToken cancellationToken = default);
    Task CreateAsync(AccountCredential credential, CancellationToken cancellationToken = default);
    Task<bool> TryRecordFailureAsync(int accountId, byte[] expectedSecurityStamp, DateTime nowUtc, int threshold, TimeSpan lockDuration, CancellationToken cancellationToken = default);
    Task<bool> TryCompleteLoginAsync(int accountId, byte[] expectedSecurityStamp, DateTime nowUtc, long? totpStep, CancellationToken cancellationToken = default);
    static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

public interface IPasswordHasher { string Hash(string password); bool Verify(string password, string encodedHash); }
public interface ITotpSecretProtector { byte[] Encrypt(byte keyId, byte[] secret); bool TryDecrypt(byte keyId, byte[] encrypted, out byte[] secret); }
