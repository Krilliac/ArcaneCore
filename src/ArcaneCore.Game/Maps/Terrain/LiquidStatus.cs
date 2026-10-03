namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// Where a position is relative to the liquid surface (vmangos/cmangos-classic
/// <c>GridMapLiquidStatus</c>, GridMapDefines.h). The values are bit flags in both cores.
/// </summary>
[Flags]
public enum LiquidStatus : uint
{
    /// <summary><c>LIQUID_MAP_NO_WATER</c>.</summary>
    NoWater = 0x00000000,

    /// <summary><c>LIQUID_MAP_ABOVE_WATER</c>: more than 0.1 yard above the surface.</summary>
    AboveWater = 0x00000001,

    /// <summary><c>LIQUID_MAP_WATER_WALK</c>: within 0.1 yard of the surface.</summary>
    WaterWalk = 0x00000002,

    /// <summary><c>LIQUID_MAP_IN_WATER</c>: up to 2 yards below the surface.</summary>
    InWater = 0x00000004,

    /// <summary><c>LIQUID_MAP_UNDER_WATER</c>: more than 2 yards below the surface.</summary>
    UnderWater = 0x00000008,
}

/// <summary>
/// Liquid kinds as stored in <c>.map</c> files (vmangos/cmangos-classic <c>MAP_LIQUID_TYPE_*</c>,
/// GridMapDefines.h). A cell may combine <see cref="DeepWater"/> with one of the others.
/// </summary>
[Flags]
public enum LiquidTypeFlags : byte
{
    None = 0x00,

    /// <summary><c>MAP_LIQUID_TYPE_MAGMA</c>.</summary>
    Magma = 0x01,

    /// <summary><c>MAP_LIQUID_TYPE_OCEAN</c>.</summary>
    Ocean = 0x02,

    /// <summary><c>MAP_LIQUID_TYPE_SLIME</c>.</summary>
    Slime = 0x04,

    /// <summary><c>MAP_LIQUID_TYPE_WATER</c>.</summary>
    Water = 0x08,

    /// <summary><c>MAP_LIQUID_TYPE_DEEP_WATER</c> (fatigue).</summary>
    DeepWater = 0x10,

    /// <summary><c>MAP_LIQUID_TYPE_WMO_WATER</c>.</summary>
    WmoWater = 0x20,

    /// <summary><c>MAP_ALL_LIQUIDS</c>: any liquid (for a required-type mask).</summary>
    AllLiquids = Water | Magma | Ocean | Slime,
}

/// <summary>The liquid at a position (vmangos <c>GridMapLiquidData</c>).</summary>
/// <param name="Entry">LiquidType entry from the file (0 when the file stores none).</param>
/// <param name="TypeFlags">The liquid kind.</param>
/// <param name="Level">Height of the liquid surface.</param>
/// <param name="DepthLevel">Ground height under the liquid.</param>
public readonly record struct LiquidData(uint Entry, LiquidTypeFlags TypeFlags, float Level, float DepthLevel);
