using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.WorldState.Zones;

/// <summary>Answers "which zone and area is this player in" (vmangos <c>Player::GetZoneAndAreaId</c>) and looks up area entries.</summary>
public interface IZoneLocator
{
    /// <summary>Whether zones can be derived from data: area data is loaded AND terrain files are available (vmangos always has both; a development world may lack either).</summary>
    bool CanDeriveZones { get; }

    /// <summary>The (zone, area) at the player's position; (0, 0) when unknown.</summary>
    (uint ZoneId, uint AreaId) Locate(Map map, Player player);

    /// <summary>The area or zone entry with this id (vmangos <c>AreaEntry::GetById</c>), or null.</summary>
    AreaTemplate? Find(uint areaId);
}

/// <summary>The default locator: the map's terrain area flags through the world's <see cref="WorldMaps.Areas"/>.</summary>
internal sealed class TerrainZoneLocator(WorldRuntime world) : IZoneLocator
{
    public bool CanDeriveZones => WorldMaps.Of(world).Areas.Count > 0 && WorldMaps.Of(world).Terrain.Enabled;

    public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => map.GetZoneAndAreaId(player.X, player.Y, player.Z);

    public AreaTemplate? Find(uint areaId) => WorldMaps.Of(world).Areas.GetById(areaId);
}
