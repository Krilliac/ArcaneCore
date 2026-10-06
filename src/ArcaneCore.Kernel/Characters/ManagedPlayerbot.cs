namespace ArcaneCore.Kernel.Characters;

public enum ManagedPlayerbotState { Stopped, Starting, Running, Stopping, Faulted }
public enum PlayerbotGoalKind { Explore, Quest, Grind, Combat, Loot, Rest, Vendor, Train, Recover }

/// <summary>Durable server ownership, separate from ordinary character gameplay state.</summary>
public sealed record ManagedPlayerbot(
    Guid BotId, int AccountId, int CharacterId, string AccountName, bool DesiredEnabled,
    ManagedPlayerbotState State, PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId,
    long Revision, long CreatedUnix, long UpdatedUnix, string? ErrorCode = null);

public interface IManagedPlayerbotStore
{
    Task<IReadOnlyList<ManagedPlayerbot>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task<ManagedPlayerbot?> FindAsync(Guid botId, CancellationToken cancellationToken = default);
    Task CreateAsync(ManagedPlayerbot bot, CancellationToken cancellationToken = default);
    /// <summary>Optimistic revision update; identity fields never change. False means a stale operation.</summary>
    Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default);
}
