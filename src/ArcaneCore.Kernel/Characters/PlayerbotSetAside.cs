namespace ArcaneCore.Kernel.Characters;

/// <summary>The scope and target of a managed playerbot's temporary set-aside.</summary>
public enum PlayerbotSetAsideKind : byte
{
    // Persisted as the row's kind byte: never renumber.
    Entry = 0,
    Quest = 1,
    Training = 2,
    SharedEntry = 3,
    SharedQuest = 4,
    /// <summary>Never expires (ExpiresUnixMs = long.MaxValue). A rollback to f67b41a1 drops these rows on its next save.</summary>
    UnreachableQuest = 5,
}

/// <summary>Absolute expiry prevents a restart from extending a world-time suspension.</summary>
public sealed record PlayerbotSetAside(Guid BotId, int CharacterId, PlayerbotSetAsideKind Kind,
    uint TargetId, long ExpiresUnixMs, int RepeatCount = 0)
{
    public const int MaxRepeatCount = 256;
}

public interface IManagedPlayerbotSetAsideStore
{
    Task<IReadOnlyList<PlayerbotSetAside>> LoadAllAsync(CancellationToken cancellationToken = default);
    /// <summary>Atomically replace one bot's rows, or the shared rows when botId is empty.</summary>
    Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotSetAside> rows, CancellationToken cancellationToken = default);
}
