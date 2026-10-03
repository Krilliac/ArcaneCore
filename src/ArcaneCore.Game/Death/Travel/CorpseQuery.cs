using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;

namespace ArcaneCore.Game.Death.Travel;

/// <summary>
/// The MSG_CORPSE_QUERY reply (vmangos <c>WorldSession::HandleCorpseQueryOpcode</c>, QueryHandler.cpp:155-201): the place of the
/// player's corpse for the client's map arrow. A corpse on another map than the player's, when that map is a dungeon with a ghost
/// entrance, is shown at the entrance instead: the map and the x and y of the dungeon's <c>ghost_entrance</c> and the z of the ground
/// there, and the last field is the corpse's real map. Otherwise it is the corpse's own place. The ground height comes from the
/// terrain of the entrance map (<see cref="Map.GetHeight"/>); without terrain data that is the terrain's own "no height" value.
/// </summary>
public static class CorpseQuery
{
    /// <summary>The reply for <paramref name="player"/>: "not found" without a corpse.</summary>
    public static byte[] Build(Player player, WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(world);
        if (player.Combat.Corpse is not { } corpse)
        {
            return CombatPackets.CorpseQueryNotFound();
        }

        uint corpseMap = corpse.MapId;
        int mapId = (int)corpseMap;
        float x = corpse.X;
        float y = corpse.Y;
        float z = corpse.Z;
        if (corpseMap != player.MapId
            && WorldMaps.Of(world).Registry.Find(corpseMap) is { IsDungeon: true, GhostEntranceMap: >= 0 } dungeon)
        {
            mapId = dungeon.GhostEntranceMap;
            x = dungeon.GhostEntranceX;
            y = dungeon.GhostEntranceY;
            z = world.GetMap((uint)mapId).GetHeight(x, y, GridDefines.MaxHeight);
        }

        return CombatPackets.CorpseQueryFound(mapId, x, y, z, corpseMap);
    }
}
