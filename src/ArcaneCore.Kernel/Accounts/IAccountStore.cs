namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// Persistence seam for accounts. The realm daemon talks to this abstraction, never to
/// the database directly — this is the boundary where clustering / a remote account
/// service will later slot in without rewriting the auth logic (Charter §5).
/// </summary>
public interface IAccountStore
{
    /// <summary>Look up an account by (case-insensitive) username, or null if none exists.</summary>
    Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Persist a brand-new account. Returns the stored account (with its id).</summary>
    Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>Replace the salt + verifier for an existing account (password change).</summary>
    Task UpdateCredentialsAsync(
        string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default);

    /// <summary>Store the session key produced by a successful logon proof.</summary>
    Task UpdateSessionKeyAsync(
        string username, byte[] sessionKey, CancellationToken cancellationToken = default);
}
