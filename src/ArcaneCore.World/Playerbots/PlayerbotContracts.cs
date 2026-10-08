using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots;

public sealed record PlayerbotOperationResult(bool Success, string Code, Guid? BotId = null, string? Name = null);
public sealed record PlayerbotStatus(Guid BotId, string Name, ManagedPlayerbotState State, bool DesiredEnabled,
    PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId, uint MapId, uint Health, string? ErrorCode, string? Risk = null);

public interface IPlayerbotService
{
    Task<PlayerbotOperationResult> CreateAsync(string name, byte race, byte characterClass, CancellationToken cancellationToken = default);
    Task<PlayerbotOperationResult> StartAsync(string idOrName, CancellationToken cancellationToken = default);
    Task<PlayerbotOperationResult> StopAsync(string idOrName, CancellationToken cancellationToken = default);
    IReadOnlyList<PlayerbotStatus> Snapshot();
}
