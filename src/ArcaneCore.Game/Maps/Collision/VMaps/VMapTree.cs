using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>
/// The static models of one map (vmangos <c>StaticMapTree</c>): the map-wide spawn BIH from
/// <c>NNN.vmtree</c> and the model instances currently loaded into its slots, filled tile by tile
/// from <c>.vmtile</c> files (or all at once from the tree's global section for a map without
/// terrain tiles). A spawn listed by several tiles stays loaded while any of them is.
/// Queries are in vmap internal coordinates.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class VMapTree
{
    private readonly ModelInstance?[] _instances;
    private readonly int[] _refs;
    private readonly Dictionary<(int X, int Y), int[]> _loadedTiles = [];

    private VMapTree(uint mapId, bool isTiled, BihTree tree)
    {
        MapId = mapId;
        IsTiled = isTiled;
        Tree = tree;
        _instances = new ModelInstance?[tree.PrimitiveCount];
        _refs = new int[tree.PrimitiveCount];
    }

    public uint MapId { get; }

    /// <summary>Whether models come from per-tile files (false: the tree's global spawns, WMO-only maps).</summary>
    public bool IsTiled { get; }

    public BihTree Tree { get; }

    /// <summary>Number of spawn slots holding a model.</summary>
    public int LoadedInstanceCount => _instances.Count(i => i is not null);

    public IReadOnlyCollection<(int X, int Y)> LoadedTiles => _loadedTiles.Keys;

    /// <summary>
    /// <c>NNN.vmtree</c>: "VMAP_7.0", u8 tiled, "NODE" BIH, "GOBJ", then (untiled maps) spawn
    /// records each followed by its u32 tree slot until the end of the file.
    /// </summary>
    internal static (VMapTree Tree, List<(ModelSpawn Spawn, uint Slot)> GlobalSpawns) Parse(uint mapId, ReadOnlySpan<byte> data)
    {
        var reader = new CollisionDataReader(data);
        reader.Expect(VMapFormat.Magic);
        bool tiled = reader.ReadByte() != 0;
        reader.Expect("NODE");
        BihTree bih = BihTree.Read(ref reader);
        reader.Expect("GOBJ");
        var spawns = new List<(ModelSpawn, uint)>();
        while (!reader.AtEnd)
        {
            ModelSpawn spawn = ModelSpawn.Read(ref reader);
            spawns.Add((spawn, reader.ReadUInt32()));
        }

        return (new VMapTree(mapId, tiled, bih), spawns);
    }

    /// <summary><c>.vmtile</c>: "VMAP_7.0", u32 spawn count, then spawn records each followed by its u32 tree slot.</summary>
    internal static List<(ModelSpawn Spawn, uint Slot)> ParseTile(ReadOnlySpan<byte> data)
    {
        var reader = new CollisionDataReader(data);
        reader.Expect(VMapFormat.Magic);
        int count = reader.ReadCount(1);
        var spawns = new List<(ModelSpawn, uint)>(count);
        for (int i = 0; i < count; i++)
        {
            ModelSpawn spawn = ModelSpawn.Read(ref reader);
            spawns.Add((spawn, reader.ReadUInt32()));
        }

        return spawns;
    }

    public bool IsTileLoaded(int tileX, int tileY) => _loadedTiles.ContainsKey((tileX, tileY));

    /// <summary>Fill slots for a tile (or the global spawns); returns the slots referenced.</summary>
    internal void AddTile((int X, int Y) tile, IReadOnlyList<(ModelInstance Instance, uint Slot)> instances)
    {
        int[] slots = new int[instances.Count];
        for (int i = 0; i < instances.Count; i++)
        {
            (ModelInstance instance, uint slot) = instances[i];
            slots[i] = (int)slot;
            if (_refs[slot]++ == 0)
            {
                _instances[slot] = instance;
            }
        }

        _loadedTiles[tile] = slots;
    }

    /// <summary>Release a tile's slots; a model leaves when no loaded tile lists it.</summary>
    internal bool RemoveTile((int X, int Y) tile)
    {
        if (!_loadedTiles.Remove(tile, out int[]? slots))
        {
            return false;
        }

        foreach (int slot in slots)
        {
            if (_refs[slot] > 0 && --_refs[slot] == 0)
            {
                _instances[slot] = null;
            }
        }

        return true;
    }

    internal bool IsValidSlot(uint slot) => slot < _instances.Length;

    /// <summary>
    /// vmangos <c>StaticMapTree::getIntersectionTime</c>: the nearest (or, with
    /// <paramref name="stopAtFirstHit"/>, any) model hit within <paramref name="maxDistance"/>.
    /// </summary>
    public bool GetIntersectionTime(Vector3 origin, Vector3 direction, ref float maxDistance, bool stopAtFirstHit, bool ignoreM2)
    {
        float distance = maxDistance;
        bool hit = false;
        Tree.IntersectRay(origin, direction, ref distance, (int primitive, ref float max) =>
        {
            if ((uint)primitive >= (uint)_instances.Length || _instances[primitive] is not { } instance
                || !instance.IntersectRay(origin, direction, ref max, stopAtFirstHit, ignoreM2))
            {
                return false;
            }

            hit = true;
            return true;
        }, stopAtFirstHit);
        if (hit)
        {
            maxDistance = distance;
        }

        return hit;
    }

    /// <summary>vmangos <c>StaticMapTree::isInLineOfSight</c>.</summary>
    public bool IsInLineOfSight(Vector3 from, Vector3 to, bool ignoreM2)
    {
        float length = Vector3.Distance(from, to);
        if (!(length >= 1e-10f) || !float.IsFinite(length))
        {
            return true;
        }

        Vector3 direction = (to - from) / length;
        return !GetIntersectionTime(from, direction, ref length, stopAtFirstHit: true, ignoreM2);
    }

    /// <summary>vmangos <c>StaticMapTree::getObjectHitPos</c> (M2 models count here).</summary>
    public bool TryGetObjectHit(Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = to;
        float length = Vector3.Distance(from, to);
        if (!(length >= 1e-10f) || !float.IsFinite(length))
        {
            return false;
        }

        Vector3 direction = (to - from) / length;
        float distance = length;
        if (!GetIntersectionTime(from, direction, ref distance, stopAtFirstHit: false, ignoreM2: false))
        {
            return false;
        }

        hit = from + (direction * distance);
        if (modifyDistance < 0 && distance <= -modifyDistance)
        {
            hit = from;
        }
        else
        {
            hit += direction * modifyDistance;
        }

        return true;
    }

    /// <summary>
    /// vmangos <c>StaticMapTree::getHeight</c>: the nearest model surface straight down
    /// (<paramref name="maxSearchDistance"/> ≥ 0) or up (&lt; 0) within the distance.
    /// </summary>
    public float? GetHeight(Vector3 point, float maxSearchDistance)
    {
        if (!float.IsFinite(maxSearchDistance))
        {
            return null;
        }

        Vector3 direction = maxSearchDistance >= 0 ? -Vector3.UnitZ : Vector3.UnitZ;
        float distance = MathF.Abs(maxSearchDistance);
        if (!GetIntersectionTime(point, direction, ref distance, stopAtFirstHit: false, ignoreM2: false))
        {
            return null;
        }

        return maxSearchDistance >= 0 ? point.Z - distance : point.Z + distance;
    }

    /// <summary>
    /// vmangos <c>StaticMapTree::getAreaInfo</c>: over every loaded WMO whose bound holds the point,
    /// the group with the highest floor below it.
    /// </summary>
    public bool TryGetAreaInfo(Vector3 point, out ModelAreaInfo info)
    {
        ModelAreaInfo best = default;
        bool found = false;
        Tree.IntersectPoint(point, primitive =>
        {
            if ((uint)primitive < (uint)_instances.Length && _instances[primitive] is { } instance
                && instance.TryGetGroupFloor(point, out GroupModel? group, out float floor)
                && (!found || floor > best.GroundZ))
            {
                best = new ModelAreaInfo(group!.MogpFlags, instance.Spawn.AdtId, (int)instance.Model.RootWmoId, (int)group.GroupWmoId, floor);
                found = true;
            }
        });
        info = best;
        return found;
    }
}
