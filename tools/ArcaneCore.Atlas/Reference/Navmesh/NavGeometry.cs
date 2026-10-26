using System.Numerics;
using ArcaneCore.Atlas.Core;
using DotRecast.Core.Numerics;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>Recast-space and world-space conversions for DotRecast data, and the chunk splitter the navmesh layers share.</summary>
internal static class NavGeometry
{
    // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs:46-50 (ToRecast(world) = (Y, Z, X), ToWorld(rc) = (Z, X, Y)); the
    // generator's Recast axes are x = world Y, y = height, z = world X (vmangos@0e3ff01 contrib/mmap/src/MapBuilder.cpp:503-506
    // takes tile bounds as bmax[0] = (32 - tileX) * GRID_SIZE for the Recast x axis and bmax[2] for z).

    /// <summary>World position of a Recast-space position.</summary>
    public static WorldPoint ToWorld(RcVec3f rc) => new(rc.Z, rc.X, rc.Y);

    /// <summary>World position of vertex <paramref name="index"/> of a DotRecast vertex array (x, y, z per vertex, Recast axes).</summary>
    public static WorldPoint ToWorld(float[] verts, int index)
    {
        int i = index * 3;
        return new WorldPoint(verts[i + 2], verts[i], verts[i + 1]);
    }

    /// <summary>Recast-space position of a world position (single precision, as the mesh stores it).</summary>
    public static RcVec3f ToRecast(Vector3 world) => new(world.Y, world.Z, world.X);

    /// <summary>The mean of a polygon's vertices (<c>DtNavMesh.GetPolyCenter</c>) as a world point.</summary>
    public static WorldPoint PolyCenter(float[] verts, int[] polyVerts, int vertCount)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < vertCount; i++)
        {
            WorldPoint p = ToWorld(verts, polyVerts[i]);
            x += p.X;
            y += p.Y;
            z += p.Z;
        }

        return new WorldPoint(x / vertCount, y / vertCount, z / vertCount);
    }
}

/// <summary>
/// A <see cref="GeometryBuilder"/> that starts a new part when the current one grows past a byte budget, so a tile never
/// exceeds <see cref="GeometryWire.MaxChunkBytes"/>. The caller resets any vertex cache when <see cref="RollIfFull"/> returns true.
/// </summary>
internal sealed class PartedBuilder(string layerId, TileKey key, WorldPoint origin, long maxBytes, Action<GeometryBuilder, int> decorate)
{
    private readonly List<GeometryChunk> _done = [];

    /// <summary>The builder receiving geometry.</summary>
    public GeometryBuilder Current { get; private set; } = new(layerId, key, origin, 0);

    /// <summary>Index of <see cref="Current"/>.</summary>
    public int Part { get; private set; }

    /// <summary>Closes the current part and opens the next when the current one is over budget. True when it did.</summary>
    public bool RollIfFull()
    {
        if (Current.EstimatedBytes <= maxBytes)
        {
            return false;
        }

        Finish();
        Part++;
        Current = new GeometryBuilder(layerId, key, origin, Part);
        return true;
    }

    /// <summary>Closes the last part and returns every non-empty chunk in part order.</summary>
    public IReadOnlyList<GeometryChunk> Complete()
    {
        Finish();
        return _done;
    }

    private void Finish()
    {
        decorate(Current, Part);
        if (!Current.IsEmpty)
        {
            _done.Add(Current.Build());
        }
    }
}
