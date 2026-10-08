namespace ArcaneCore.Kernel.AntiCheat;

/// <summary>
/// One row of the anticheat violation log (<c>character_anticheat_log</c>, docs/areas/anticheat.md). Repeats of one type by
/// one character within the coalescing interval are folded into one row: <see cref="Count"/> findings, their summed
/// <see cref="Weight"/>, the <see cref="Score"/> after the last one. Times are unix seconds.
/// </summary>
public sealed record AntiCheatLogEntry(
    int CharacterId,
    int AccountId,
    byte Type,
    float Weight,
    float Score,
    int Count,
    uint MapId,
    float X,
    float Y,
    float Z,
    string Detail,
    long FirstAt,
    long LastAt);

/// <summary>Persistence seam of the violation log: written in batches by the anticheat feature, read by <c>.anticheat report</c>.</summary>
public interface IAntiCheatLogStore
{
    /// <summary>Insert a batch of rows in one round trip.</summary>
    Task AppendAsync(IReadOnlyList<AntiCheatLogEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>The newest rows of a character, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<AntiCheatLogEntry>> RecentAsync(int characterId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Delete every row of a character (<c>.anticheat delete</c>); returns how many there were.</summary>
    Task<int> DeleteAsync(int characterId, CancellationToken cancellationToken = default);
}
