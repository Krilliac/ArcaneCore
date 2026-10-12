namespace ArcaneCore.Kernel.Characters;

/// <summary>The scope and target of a managed playerbot's temporary set-aside.</summary>
public enum PlayerbotSetAsideKind : byte { Entry, Quest, Training, SharedEntry, SharedQuest, UnreachableQuest }

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
