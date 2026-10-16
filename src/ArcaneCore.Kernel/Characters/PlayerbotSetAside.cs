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
    /// <summary>
    /// A quest whose ender the bot found unreachable. Expires like the others (60 s, then 10 min, 1 h, 6 h and 24 h with each repeat), and
    /// RepeatCount carries the strikes. Rows written before that have ExpiresUnixMs = long.MaxValue: the loader restores them with
    /// a finite, staggered expiry and the next save rewrites them. Append-only: a kind byte is never renumbered or reused. A rollback
    /// to f67b41a1 drops these rows on its next save.
    /// </summary>
    UnreachableQuest = 5,
}

/// <summary>
/// Absolute expiry prevents a restart from extending a world-time suspension. RepeatCount is the strike count of an
/// <see cref="PlayerbotSetAsideKind.Entry"/> or <see cref="PlayerbotSetAsideKind.UnreachableQuest"/> (so a restart continues
/// its escalation), and the repeat count of a shared set-aside; 0 elsewhere.
/// </summary>
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
