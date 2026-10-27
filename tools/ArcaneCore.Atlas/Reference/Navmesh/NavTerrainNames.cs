using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// Names and colour keys for navmesh polygon flags. The flag values are the generator's <see cref="NavTerrain"/>.
/// </summary>
public static class NavTerrainNames
{
    // ref: vmangos@0e3ff01 src/game/Maps/MoveMapSharedDefines.h:40-52 (NAV_GROUND 0x01, NAV_MAGMA 0x02, NAV_SLIME 0x04,
    // NAV_WATER 0x08, NAV_STEEP_SLOPES 0x10, the rest unused) and this repo's src/ArcaneCore.Game/Maps/Collision/CollisionContracts.cs:204.
    private const ushort KnownMask = (ushort)(NavTerrain.Ground | NavTerrain.Magma | NavTerrain.Slime | NavTerrain.Water | NavTerrain.SteepSlopes);

    /// <summary>
    /// The <see cref="LayerInfo.AttributeColors"/> key for a polygon: magma, then slime, then water, then steep slopes, then ground.
    /// A polygon with several liquid bits shows the most hazardous one; a ground polygon on a steep slope is "steep"; a polygon
    /// with no known flag is "empty" (it passes no filter, so no query can reach it).
    /// </summary>
    public static string ColorKey(ushort flags)
    {
        if ((flags & (ushort)NavTerrain.Magma) != 0)
        {
            return ColorKeys.Magma;
        }

        if ((flags & (ushort)NavTerrain.Slime) != 0)
        {
            return ColorKeys.Slime;
        }

        if ((flags & (ushort)NavTerrain.Water) != 0)
        {
            return ColorKeys.Water;
        }

        if ((flags & (ushort)NavTerrain.SteepSlopes) != 0)
        {
            return ColorKeys.Steep;
        }

        return (flags & (ushort)NavTerrain.Ground) != 0 ? ColorKeys.Ground : ColorKeys.Empty;
    }

    /// <summary>The flag names joined by '|' ("None" for 0); bits the generator does not define print as one hex value.</summary>
    public static string Describe(ushort flags)
    {
        if (flags == 0)
        {
            return "None";
        }

        var names = new List<string>(6);
        AddIf(names, flags, NavTerrain.Ground, "Ground");
        AddIf(names, flags, NavTerrain.Magma, "Magma");
        AddIf(names, flags, NavTerrain.Slime, "Slime");
        AddIf(names, flags, NavTerrain.Water, "Water");
        AddIf(names, flags, NavTerrain.SteepSlopes, "SteepSlopes");
        int unknown = flags & ~KnownMask;
        if (unknown != 0)
        {
            names.Add($"0x{unknown:x}");
        }

        return string.Join('|', names);
    }

    /// <summary>The flag names as a list (for JSON), without the hex remainder.</summary>
    public static IReadOnlyList<string> Names(ushort flags)
    {
        var names = new List<string>(5);
        AddIf(names, flags, NavTerrain.Ground, "ground");
        AddIf(names, flags, NavTerrain.Magma, "magma");
        AddIf(names, flags, NavTerrain.Slime, "slime");
        AddIf(names, flags, NavTerrain.Water, "water");
        AddIf(names, flags, NavTerrain.SteepSlopes, "steep");
        return names;
    }

    /// <summary>
    /// Parses a terrain name (ground, water, magma, slime, steep) to its flag; null for an unknown name. Case-insensitive.
    /// </summary>
    public static NavTerrain? Parse(string name)
        => name.Trim().ToLowerInvariant() switch
        {
            "ground" => NavTerrain.Ground,
            "magma" => NavTerrain.Magma,
            "slime" => NavTerrain.Slime,
            "water" => NavTerrain.Water,
            "steep" or "steepslopes" => NavTerrain.SteepSlopes,
            _ => null,
        };

    private static void AddIf(List<string> names, ushort flags, NavTerrain flag, string name)
    {
        if ((flags & (ushort)flag) != 0)
        {
            names.Add(name);
        }
    }
}
