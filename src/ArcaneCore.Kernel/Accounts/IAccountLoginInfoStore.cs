namespace ArcaneCore.Kernel.Accounts;

/// <summary>Records and reads the client details of an account's last successful logon.</summary>
public interface IAccountLoginInfoStore
{
    /// <summary>Overwrite the account's last-login record. An unknown account is ignored.</summary>
    Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default);

    /// <summary>The last-login record, or null if the account does not exist.</summary>
    Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default);
}
