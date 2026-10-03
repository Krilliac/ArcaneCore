namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// A character whose stored rows were durably deleted but whose runtime finalization (caches,
/// lists, directory) has not been confirmed complete (docs/integration/character-delete.md).
/// </summary>
public sealed record PendingCharacterDeletion(Guid OperationId, int CharacterId, int AccountId, string Name);

/// <summary>
/// Character deletion with a durable operation identity, so a deletion whose acknowledgement is
/// lost can be told apart from one that rolled back (the same pattern as the economy ledger,
/// <c>IEconomyStore.IsCommittedAsync</c>). The ledger row is written in the deletion
/// transaction, so it exists exactly when the character's rows are gone, and it stays until the
/// world reports that every runtime finalizer ran (<see cref="CompleteAsync"/>). A host without a
/// durable store (in-memory test hosts) registers no implementation and uses
/// <see cref="ICharacterStore.DeleteAsync"/>, which leaves no ledger.
/// </summary>
public interface ICharacterDeletionStore
{
    /// <summary>
    /// Delete a character owned by <paramref name="accountId"/> and record <paramref name="operationId"/>
    /// in the same transaction. False when the character is unknown, owned by another account or a
    /// module refused (a guild leader); nothing is recorded then. A caller-owned transaction decides
    /// the outcome: its rollback removes the ledger row with the deletion.
    /// </summary>
    Task<bool> DeleteAsync(Guid operationId, int id, int accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="operationId"/> committed and is still pending. False means "not
    /// observed as committed": a commit that was already sent to a database server can become
    /// visible shortly after a failed read, and is then found by <see cref="GetPendingAsync"/>.
    /// </summary>
    Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>The account's committed deletions whose finalization is not yet complete.</summary>
    Task<IReadOnlyList<PendingCharacterDeletion>> GetPendingAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>The finalizers ran: forget the operation. Idempotent.</summary>
    Task CompleteAsync(Guid operationId, CancellationToken cancellationToken = default);
}
