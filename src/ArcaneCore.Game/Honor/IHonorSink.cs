using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Honor;

namespace ArcaneCore.Game.Honor;

/// <summary>Where honor changes are queued for persistence (the daemon implements it; world thread callers).</summary>
public interface IHonorSink
{
    /// <summary>A contribution point row was added.</summary>
    void CpAdded(Player player, HonorCpRecord row);

    /// <summary>The state changed (rank points, highest rank).</summary>
    void StateChanged(Player player, CharacterHonorState state);

    /// <summary>The player's honor was reset: delete the rows and clear the state.</summary>
    void Reset(Player player);
}

/// <summary>Read access to a player's honor ranks for the other areas (vendors, channels, conditions).</summary>
public interface IPlayerHonor
{
    /// <summary>The internal rank 0..18 (vmangos HonorMgr::GetRank().rank); 0 for a player with no honor state.</summary>
    byte CurrentRank(Player player);

    /// <summary>The highest internal rank reached (vmangos GetHighestRank().rank).</summary>
    byte HighestRank(Player player);

    /// <summary>The visual rank -4..14 shown on the honor tab.</summary>
    sbyte VisualRank(Player player);
}

/// <summary>
/// Give a player honor; the seam for lanes that award it outside open-world kills (battlegrounds bonus honor,
/// quests, the honor spell effect).
/// </summary>
public interface IHonorAwards
{
    /// <summary>HonorMgr::Add: false when nothing was awarded (zero points or no honor state).</summary>
    bool Add(Player player, float cp, HonorKind kind, Unit? source);
}
