namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// Administrative account mutations that go beyond the logon path: change the status column (the operator
/// override that stays honoured next to the ban rows), revoke a stored session key, and look up which of a set
/// of accounts are not Active. A separate interface so <see cref="IAccountStore"/> and its fakes stay unchanged.
/// A real status change raises <see cref="AccountStatusEvents.StatusChanged"/> after the commit.
/// </summary>
public interface IAccountAdmin
{
    /// <summary>
    /// Set the status column. Returns false for an unknown account. The event is raised only when the
    /// stored value actually changed.
    /// </summary>
    Task<bool> SetStatusAsync(
        string username, AccountStatus status, int? actorAccountId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Null the stored session key, so a world reconnect with the old key is refused (hardening beyond retail,
    /// which keeps the key and relies on the ban check). Returns false for an unknown account.
    /// </summary>
    Task<bool> RevokeSessionKeyAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>Which of <paramref name="accountIds"/> have a non-Active status column (chunks of 500).</summary>
    Task<IReadOnlySet<int>> FindNonActiveAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default);
}
