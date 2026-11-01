namespace ArcaneCore.Data.Stores;

/// <summary>
/// A characters-database write was refused because the worker does not hold the per-character claim it needs (design _wf/writer-lock-DESIGN.md section 1b):
/// the character was claimed by another world, its claim was released, or it never had one. Deliberately NOT a
/// <see cref="ArcaneCore.Kernel.Characters.CharacterOwnershipRejectedException"/>: that exception is terminal for a whole save queue and halts the host,
/// while losing one character to another worker is routine and concerns that character only. The write is refused; nothing was changed.
/// </summary>
public sealed class CharacterClaimRejectedException(int characterId, string scope)
    : InvalidOperationException($"character {characterId} is not claimed by scope '{scope}'; the write was refused")
{
    public int CharacterId { get; } = characterId;

    public string Scope { get; } = scope;
}

/// <summary>What a claim check requires of the character rows a write transaction touches.</summary>
public enum CharacterClaimRequirement
{
    /// <summary>The character's claim is held by this worker's own scope and incarnation: the rule for a character's own saves.</summary>
    HeldByThisWorker = 0,

    /// <summary>
    /// The character's claim is held by some live worker (its owner's ownership row is active under that incarnation) and no transfer is in flight.
    /// For economy commits that the Economy holder runs for a character another worker has claimed (design Q12): the holder cannot hold that claim
    /// itself, but a character nobody holds, or one whose owner died, or one mid-transfer, is refused.
    /// </summary>
    HeldByAnyLiveWorker = 1,
}

/// <summary>
/// The characters a write transaction is about to touch, so the write fence can check their per-character claims (<c>cluster_transfer_owner</c>) in the
/// same transaction as the worker-token check. Set by the write queues and the economy store around the store call; read by
/// <see cref="CharacterWriteFenceInterceptor"/>, which checks only when the verifying token is a worker-shard token (<c>worker:&lt;shard&gt;</c>). Realm scope, an
/// unfenced process and a transaction begun with no scope in force issue no claim SQL at all.
/// <para>
/// Flows with the logical call (<see cref="AsyncLocal{T}"/>), like <see cref="CharacterWriteFence"/>; a scope restores the previous frame when disposed, so
/// scopes nest. Disposing a scope twice, or out of order with another scope on the same flow, restores the frame it captured, which is the safe direction.
/// </para>
/// </summary>
public static class CharacterWriteClaim
{
    private static readonly AsyncLocal<Frame?> Active = new();

    /// <summary>The frame in force on this logical call, or null.</summary>
    internal static Frame? Current => Active.Value;

    /// <summary>Check the claim of <paramref name="characterId"/> (held by this worker) for transactions begun inside the returned scope. One small array per call.</summary>
    public static Scope Use(int characterId) => Push(new Frame([characterId], CharacterClaimRequirement.HeldByThisWorker));

    /// <summary>Check the claim of every character in <paramref name="characterIds"/> against <paramref name="requirement"/> for transactions begun inside the returned scope.</summary>
    public static Scope Use(IReadOnlyCollection<int> characterIds, CharacterClaimRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(characterIds);
        return characterIds.Count == 0 ? Push(null) : Push(new Frame([.. characterIds], requirement));
    }

    private static Scope Push(Frame? frame)
    {
        Frame? previous = Active.Value;
        if (frame is not null)
            Active.Value = frame;
        return new Scope(previous);
    }

    internal sealed record Frame(int[] CharacterIds, CharacterClaimRequirement Requirement);

    /// <summary>Restores the previous frame. A struct, so <c>using var</c> does not box it.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly Frame? _previous;

        internal Scope(Frame? previous) => _previous = previous;

        public void Dispose() => Active.Value = _previous;
    }
}
