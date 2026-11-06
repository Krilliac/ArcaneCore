using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Login;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

public sealed class EfAccountCredentialStore(AuthDbContext db) : IAccountCredentialStore
{
    public async Task<AccountCredential?> FindByEmailNormalizedAsync(string emailNormalized, CancellationToken cancellationToken = default)
    {
        string email = Normalize(emailNormalized);
        AccountCredential? value = await db.Set<AccountCredential>().AsNoTracking().SingleOrDefaultAsync(x => x.EmailNormalized == email, cancellationToken).ConfigureAwait(false);
        return value is null ? null : Clone(value);
    }
    public Task<LoginAccountInfo?> FindAccountAsync(int accountId, CancellationToken cancellationToken = default)
        => db.Accounts.AsNoTracking().Where(x => x.Id == accountId).Select(x => new LoginAccountInfo(x.Id, x.Status, x.Security)).SingleOrDefaultAsync(cancellationToken);
    public async Task CreateAsync(AccountCredential credential, CancellationToken cancellationToken = default)
    {
        Validate(credential); db.Set<AccountCredential>().Add(Clone(credential)); await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<bool> TryRecordFailureAsync(int accountId, byte[] expectedSecurityStamp, DateTime nowUtc, int threshold, TimeSpan lockDuration, CancellationToken cancellationToken = default)
    {
        RequireUtc(nowUtc);
        ValidateStamp(expectedSecurityStamp);
        if (threshold is < 1 or > 100 || lockDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException();
        // Literal setter values avoid provider-dependent left-to-right UPDATE evaluation.
        // A failed CAS retries; each competing failure either advances the counter or locks it.
        for (int attempt = 0; attempt < threshold; attempt++)
        {
            AccountCredential? previous = await db.Set<AccountCredential>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.AccountId == accountId && x.SecurityStamp == expectedSecurityStamp
                    && (x.LockedUntilUtc == null || x.LockedUntilUtc <= nowUtc), cancellationToken).ConfigureAwait(false);
            if (previous is null) return false;
            int count = previous.LockedUntilUtc is not null ? 1 : checked(previous.FailedLoginCount + 1);
            DateTime? until = count >= threshold ? nowUtc + lockDuration : null;
            int rows = await db.Set<AccountCredential>()
                .Where(x => x.AccountId == accountId && x.SecurityStamp == expectedSecurityStamp
                    && x.FailedLoginCount == previous.FailedLoginCount && x.LockedUntilUtc == previous.LockedUntilUtc)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedLoginCount, count)
                    .SetProperty(x => x.LastFailedLoginUtc, nowUtc).SetProperty(x => x.LockedUntilUtc, until), cancellationToken).ConfigureAwait(false);
            if (rows == 1) return true;
        }
        return false;
    }
    public async Task<bool> TryCompleteLoginAsync(int accountId, byte[] expectedSecurityStamp, DateTime nowUtc, long? totpStep, CancellationToken cancellationToken = default)
    {
        RequireUtc(nowUtc); ValidateStamp(expectedSecurityStamp);
        IQueryable<AccountCredential> query = db.Set<AccountCredential>().Where(x => x.AccountId == accountId && x.SecurityStamp == expectedSecurityStamp && x.EmailVerifiedUtc != null && x.PasswordHash != null && (x.LockedUntilUtc == null || x.LockedUntilUtc <= nowUtc));
        if (totpStep is { } step)
        {
            query = query.Where(x => x.TotpEnabledUtc != null && x.TotpSecretEncrypted != null && step > x.TotpLastStep);
        }
        else
        {
            query = query.Where(x => x.TotpEnabledUtc == null && x.TotpSecretEncrypted == null);
        }
        int rows = totpStep is { } acceptedStep
            ? await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedLoginCount, 0).SetProperty(x => x.LastFailedLoginUtc, (DateTime?)null).SetProperty(x => x.LockedUntilUtc, (DateTime?)null).SetProperty(x => x.TotpLastStep, acceptedStep), cancellationToken).ConfigureAwait(false)
            : await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.FailedLoginCount, 0).SetProperty(x => x.LastFailedLoginUtc, (DateTime?)null).SetProperty(x => x.LockedUntilUtc, (DateTime?)null), cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }
    private static string Normalize(string value) { string n = IAccountCredentialStore.NormalizeEmail(value); if (n.Length is 0 or > 254) throw new ArgumentException("Invalid email.", nameof(value)); return n; }
    private static void RequireUtc(DateTime value) { if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("Timestamp must be UTC."); }
    private static void ValidateStamp(byte[] value) { if (value is null || value.Length != 16) throw new ArgumentException("Security stamp must be 16 bytes."); }
    private static void Validate(AccountCredential x)
    {
        if (x.AccountId <= 0 || x.SecurityStamp is null || x.SecurityStamp.Length != 16 || x.Email is null || x.Email.Length > 254
            || x.EmailNormalized is null || Normalize(x.EmailNormalized) != x.EmailNormalized || x.PasswordHash?.Length > 255
            || x.TotpSecretEncrypted is { Length: < 29 or > 96 } || x.TotpKeyId == 0 && x.TotpSecretEncrypted is not null
            || x.TotpSecretEncrypted is not null && x.TotpKeyId == 0 || x.EmailVerifiedUtc?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified
            || x.PasswordChangedUtc?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || x.TotpEnabledUtc?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified
            || x.LockedUntilUtc?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified || x.LastFailedLoginUtc?.Kind is DateTimeKind.Local or DateTimeKind.Unspecified)
        {
            throw new ArgumentException("Invalid credential.");
        }
    }
    private static AccountCredential Clone(AccountCredential x) => new() { AccountId=x.AccountId, Email=x.Email, EmailNormalized=x.EmailNormalized, EmailVerifiedUtc=x.EmailVerifiedUtc, PasswordHash=x.PasswordHash, PasswordChangedUtc=x.PasswordChangedUtc, SecurityStamp=x.SecurityStamp.ToArray(), TotpSecretEncrypted=x.TotpSecretEncrypted?.ToArray(), TotpKeyId=x.TotpKeyId, TotpEnabledUtc=x.TotpEnabledUtc, TotpLastStep=x.TotpLastStep, FailedLoginCount=x.FailedLoginCount, LockedUntilUtc=x.LockedUntilUtc, LastFailedLoginUtc=x.LastFailedLoginUtc };
}
