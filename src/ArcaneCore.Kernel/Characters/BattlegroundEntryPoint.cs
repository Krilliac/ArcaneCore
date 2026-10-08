namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// A character's battleground binding as vmangos saves it (<c>character_battleground_data</c>, Player.cpp:20950-20982): the match instance,
/// the team it fights for and where it joined from. The row exists while the character is in a match; a login on a battleground map returns
/// the character to <see cref="JoinMapId"/> (Player.cpp:14775-14788).
/// </summary>
public sealed record BattlegroundEntryPointRecord(
    int CharacterId,
    uint InstanceId,
    uint Team,
    uint JoinMapId,
    float JoinX,
    float JoinY,
    float JoinZ,
    float JoinOrientation);

/// <summary>The <c>character_battleground_data</c> rows (session tasks and the battleground feature's write queue; never the world thread).</summary>
public interface IBattlegroundEntryPointStore
{
    Task<BattlegroundEntryPointRecord?> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the character's row.</summary>
    Task SaveAsync(BattlegroundEntryPointRecord record, CancellationToken cancellationToken = default);

    Task DeleteAsync(int characterId, CancellationToken cancellationToken = default);
}
