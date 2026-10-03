using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.World.Npc;

/// <summary>Each map checks its resident online journals once; transfers retain the same state.</summary>
public sealed class QuestNpcMapUpdater(QuestNpcServices services) : IMapUpdater
{
    public void Update(Map map, uint diffMs)
    {
        foreach (Player player in map.Players)
        {
            if (ReferenceEquals(player.Map, map) && player.IsInWorld)
            {
                services.CheckTimers(player);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        // Removal also occurs during far teleports. The global logout event owns Untrack.
    }
}
