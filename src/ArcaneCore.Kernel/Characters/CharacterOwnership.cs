namespace ArcaneCore.Kernel.Characters;

/// <summary>The durable authority carried by a character snapshot.</summary>
public sealed record CharacterOwnershipToken(string Scope, long Epoch, Guid Incarnation);

/// <summary>Characters-database ownership authority. It has no lease or automatic failover.</summary>
public interface ICharacterOwnershipStore
{
    Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active ownership of <paramref name="scope"/> as the database holds it now, or null when no one owns it. Read-only: unlike
    /// <see cref="TryAcquireAsync"/> it never claims an inactive row, so a caller that only verifies a token changes nothing.
    /// </summary>
    Task<CharacterOwnershipToken?> GetCurrentAsync(string scope, CancellationToken cancellationToken = default);

    Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, CancellationToken cancellationToken = default);

    Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, CancellationToken cancellationToken = default);

    Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default);
}

/// <summary>
/// A save carrying an ownership token was rejected by the durable fence, or the process fenced itself off the characters database
/// (a subclass, <c>CharacterDatabaseFencedException</c>). Every writer that treats this as terminal treats both alike.
/// </summary>
public class CharacterOwnershipRejectedException : InvalidOperationException
{
    public CharacterOwnershipRejectedException(string scope, long epoch)
        : this(scope, epoch, $"character ownership token for scope '{scope}' at epoch {epoch} is no longer active")
    {
    }

    protected CharacterOwnershipRejectedException(string scope, long epoch, string message)
        : base(message)
    {
        Scope = scope;
        Epoch = epoch;
    }

    public string Scope { get; }
    public long Epoch { get; }
}
