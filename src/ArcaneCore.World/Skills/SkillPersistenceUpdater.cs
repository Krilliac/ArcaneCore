using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.World.Skills;

/// <summary>
/// Per-map autosave of changed skill tables: every <paramref name="intervalMs"/> the snapshots of this map's
/// players that changed since their last one are handed to the <see cref="SkillSaveCoordinator"/> (vmangos writes
/// the skills with every Player::SaveToDB, Player.cpp:16858). Logout, shutdown and deletion flush on their own.
/// </summary>
internal sealed class SkillPersistenceUpdater(SkillsFeature feature, uint intervalMs) : IMapUpdater
{
    private uint _elapsedMs;

    public void Update(Map map, uint diffMs)
    {
        _elapsedMs += diffMs;
        if (_elapsedMs < intervalMs)
        {
            return;
        }

        _elapsedMs = 0;
        foreach (Player player in map.Players)
        {
            try
            {
                feature.EnqueueSnapshot(player);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("skill.persistence-updater", ex);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }
}
