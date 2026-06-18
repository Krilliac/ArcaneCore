namespace ArcaneCore.Kernel.Realms;

/// <summary>
/// Persistence seam for the realm list. Abstracted for the same clustering reason as
/// <see cref="Accounts.IAccountStore"/> (Charter §5).
/// </summary>
public interface IRealmStore
{
    /// <summary>All realms to advertise on the realm-list screen.</summary>
    Task<IReadOnlyList<RealmEntry>> GetRealmsAsync(CancellationToken cancellationToken = default);
}
