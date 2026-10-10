namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>
/// vmangos VAR_STV_FISHING_ANNOUNCE_EVENT_BEGIN, VAR_STV_FISHING_ANNOUNCE_POOLS_DESPAN, VAR_STV_FISHING_HAS_WINNER and
/// VAR_STV_FISHING_PREV_WIN_TIME (sObjectMgr saved variables, written at once by npc_riggle_bassbait).
/// </summary>
public readonly record struct FishingExtravaganzaState(bool AnnounceBegin, bool AnnounceOver, bool HasWinner, long PreviousWinTime)
{
    /// <summary>Nothing saved yet: the start yell is owed.</summary>
    public static FishingExtravaganzaState Initial => new(true, false, false, 0);
}

/// <summary>The saved Stranglethorn Fishing Extravaganza variables (characters database).</summary>
public interface IFishingExtravaganzaStore
{
    Task<FishingExtravaganzaState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(FishingExtravaganzaState state, CancellationToken cancellationToken = default);
}
