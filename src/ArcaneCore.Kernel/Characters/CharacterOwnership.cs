namespace ArcaneCore.Kernel.Characters;

/// <summary>The durable authority carried by a character snapshot.</summary>
public sealed record CharacterOwnershipToken(string Scope, long Epoch, Guid Incarnation);

/// <summary>Characters-database ownership authority. It has no lease or automatic failover.</summary>
public interface ICharacterOwnershipStore
{
    Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default);

    Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, CancellationToken cancellationToken = default);

    Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, CancellationToken cancellationToken = default);

    Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default);
}

/// <summary>A save carrying an ownership token was rejected by the durable fence.</summary>
public sealed class CharacterOwnershipRejectedException(string scope, long epoch)
    : InvalidOperationException($"character ownership token for scope '{scope}' at epoch {epoch} is no longer active")
{
    public string Scope { get; } = scope;
    public long Epoch { get; } = epoch;
}
