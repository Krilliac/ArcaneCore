namespace ArcaneCore.Kernel.Characters;

public enum ManagedPlayerbotState { Stopped, Starting, Running, Stopping, Faulted }
/// <summary>A bot's current goal. Persisted as its number (managed_playerbot.Goal): append only, never reorder.</summary>
public enum PlayerbotGoalKind { Explore, Quest, Grind, Combat, Loot, Rest, Vendor, Train, Recover, Follow, Assist, Retreat, Group, Home, Battleground, Escape, Mount, Gather }

/// <summary>Durable server ownership, separate from ordinary character gameplay state.</summary>
public sealed record ManagedPlayerbot(
    Guid BotId, int AccountId, int CharacterId, string AccountName, bool DesiredEnabled,
    ManagedPlayerbotState State, PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId,
    long Revision, long CreatedUnix, long UpdatedUnix, string? ErrorCode = null);

/// <summary>One row of <see cref="IManagedPlayerbotStore.UpdateManyAsync"/>: the new record and the revision the stored row must still have.</summary>
public readonly record struct ManagedPlayerbotUpdate(ManagedPlayerbot Bot, long ExpectedRevision)
{
    /// <summary>The most rows one transaction of <see cref="IManagedPlayerbotStore.UpdateManyAsync"/> writes.</summary>
    public const int MaxBatch = 128;
}

public interface IManagedPlayerbotStore
{
    Task<IReadOnlyList<ManagedPlayerbot>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task<ManagedPlayerbot?> FindAsync(Guid botId, CancellationToken cancellationToken = default);
    Task CreateAsync(ManagedPlayerbot bot, CancellationToken cancellationToken = default);
    /// <summary>Optimistic revision update; identity fields never change. False means a stale operation.</summary>
    Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default);
    /// <summary>
    /// <see cref="UpdateAsync"/> for many bots: the rows are written in chunks of at most <see cref="ManagedPlayerbotUpdate.MaxBatch"/>,
    /// one transaction per chunk, each row keeping its own optimistic check. The result has one entry per update, in order: false is
    /// a stale or mismatched row (the others of its chunk still commit). An exception rolls the current chunk back; the chunks before it stay committed.
    /// </summary>
    Task<IReadOnlyList<bool>> UpdateManyAsync(IReadOnlyList<ManagedPlayerbotUpdate> updates, CancellationToken cancellationToken = default);
    /// <summary>
    /// Remove the registry row of a bot whose character is already deleted (a character deletion also clears the row in the
    /// same transaction in production; this removes what is left). Identity-checked: the row is removed only while it still has
    /// this account and character. True when a row was removed.
    /// </summary>
    Task<bool> DeleteAsync(Guid botId, int accountId, int characterId, CancellationToken cancellationToken = default);
}
