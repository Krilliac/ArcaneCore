namespace ArcaneCore.Kernel.Accounts;

/// <summary>Administrative writes for the account's classic logon PIN/TOTP policy.</summary>
public interface IAccountLoginSecurityStore
{
    /// <summary>Set one factor or clear it. The caller must enforce Administrator access.</summary>
    Task<bool> SetAsync(string username, AccountLockFlags flags, string securityInfo,
        CancellationToken cancellationToken = default);
}
