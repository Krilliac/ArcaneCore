using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Fishing;

/// <summary>
/// The water a bobber lands in. The default (<see cref="MapFishingTerrain"/>) reads the map's ADT liquid layer; a test or a build with
/// other liquid data (WMO water) supplies its own. World thread.
/// </summary>
public interface IFishingTerrain
{
    /// <summary>
    /// vmangos <c>TerrainInfo::IsSwimmable</c> (GridMap.cpp:1054-1070): the liquid at (<paramref name="x"/>, <paramref name="y"/>) as seen
    /// from height <paramref name="z"/> lets a unit swim, i.e. the surface is reachable and deeper than <paramref name="radius"/>.
    /// <paramref name="data"/> is filled whenever there is liquid, whatever the answer.
    /// </summary>
    bool IsSwimmable(Map map, float x, float y, float z, float radius, out LiquidData data);
}

/// <summary>Port of <c>TerrainInfo::IsSwimmable</c> over <see cref="Map.GetLiquidStatus"/> (ADT liquid only: no WMO or vmap water).</summary>
public sealed class MapFishingTerrain : IFishingTerrain
{
    /// <summary>vmangos JUMP_HEIGHT (GridMap.cpp:1052): a unit this far above the water may still jump in.</summary>
    public const float JumpHeight = 0.6f;

    public static MapFishingTerrain Instance { get; } = new();

    public bool IsSwimmable(Map map, float x, float y, float z, float radius, out LiquidData data)
    {
        ArgumentNullException.ThrowIfNull(map);
        LiquidStatus status = map.GetLiquidStatus(x, y, z, LiquidTypeFlags.AllLiquids, out data);
        return Evaluate(status, data, z, radius);
    }

    /// <summary>The decision of IsSwimmable once the liquid at the point is known (GridMap.cpp:1060-1066).</summary>
    public static bool Evaluate(LiquidStatus status, LiquidData data, float z, float radius)
    {
        bool reachable = status is LiquidStatus.InWater or LiquidStatus.UnderWater or LiquidStatus.WaterWalk
            || (status == LiquidStatus.AboveWater && data.Level + JumpHeight >= z);
        return reachable && data.Level - data.DepthLevel > radius;
    }
}