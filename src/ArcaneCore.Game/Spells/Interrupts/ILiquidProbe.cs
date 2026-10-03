using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Spells.Interrupts;

/// <summary>Answers vmangos ENVIRONMENT_FLAG_HIGH_LIQUID for a player (world thread). A seam so tests need no terrain.</summary>
public interface ILiquidProbe
{
    bool IsHighLiquid(Map map, Player player);
}

/// <summary>
/// Terrain-backed probe: vmangos Player::UpdateTerainEnvironmentFlags (D:\refs\vmangos\src\game\Objects\Player.cpp:
/// 20353-20419) queries the liquid at z + 0.01 for any liquid type and sets HIGH_LIQUID when the status is
/// in-water or under-water and the surface is above z + GetMinSwimDepth(), where the minimum swim depth is
/// 0.75 * collision height (Unit.h:543) and the collision height defaults to 2.0 (Unit.cpp:117).
/// Limits: terrain liquid only (no WMO liquid), the default collision height instead of model data, and the
/// object scale (1.0 for every player today) is not applied.
/// </summary>
public sealed class TerrainLiquidProbe : ILiquidProbe
{
    public const float DefaultCollisionHeight = 2.0f;
    public const float MinSwimDepth = DefaultCollisionHeight * 0.75f;

    public bool IsHighLiquid(Map map, Player player)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(player);
        LiquidStatus status = map.Terrain.GetLiquidStatus(player.X, player.Y, player.Z + 0.01f, LiquidTypeFlags.AllLiquids, out LiquidData liquid);
        return IsHighLiquid(status, liquid.Level, player.Z);
    }

    /// <summary>The flag rule: status is in-water or under-water and level is above z + minimum swim depth.</summary>
    public static bool IsHighLiquid(LiquidStatus status, float liquidLevel, float z) =>
        (status & (LiquidStatus.UnderWater | LiquidStatus.InWater)) != 0 && liquidLevel > z + MinSwimDepth;
}
