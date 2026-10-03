using System.Numerics;
using System.Text;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Writes tiny repository-authored vmap data sets (vmtree, vmtile, vmo) into a temporary
/// directory: boxes placed at world positions. No client data is involved.
/// </summary>
internal sealed class VMapFixture : IDisposable
{
    private readonly List<(ModelSpawn Spawn, WorldModel Model)> _spawns = [];

    public VMapFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "arcanecore-vmap-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public IReadOnlyList<(ModelSpawn Spawn, WorldModel Model)> Spawns => _spawns;

    /// <summary>A closed box mesh from <paramref name="low"/> to <paramref name="high"/> (model space), one group.</summary>
    public static WorldModel Box(Vector3 low, Vector3 high, uint mogpFlags = 0, uint rootId = 7, uint groupId = 3)
    {
        Vector3[] v =
        [
            new(low.X, low.Y, low.Z), new(high.X, low.Y, low.Z), new(high.X, high.Y, low.Z), new(low.X, high.Y, low.Z),
            new(low.X, low.Y, high.Z), new(high.X, low.Y, high.Z), new(high.X, high.Y, high.Z), new(low.X, high.Y, high.Z),
        ];
        MeshTriangle[] t =
        [
            new(0, 2, 1), new(0, 3, 2), // bottom
            new(4, 5, 6), new(4, 6, 7), // top
            new(0, 1, 5), new(0, 5, 4), // -y
            new(2, 3, 7), new(2, 7, 6), // +y
            new(1, 2, 6), new(1, 6, 5), // +x
            new(3, 0, 4), new(3, 4, 7), // -x
        ];
        return new WorldModel(rootId, [new GroupModel(low, high, mogpFlags, groupId, v, t)]);
    }

    /// <summary>Place a model with its origin at a world position (rotation in degrees, format order).</summary>
    public ModelSpawn Place(string name, WorldModel model, Vector3 world, Vector3 rotation = default, float scale = 1, uint flags = 0, uint id = 0)
    {
        Vector3 position = VMapFormat.ToInternal(world);
        var probe = new ModelInstance(new ModelSpawn(flags, 1, id, position, rotation, scale, default, default, name), model);
        Vector3 low = new(float.MaxValue);
        Vector3 high = new(float.MinValue);
        foreach (GroupModel group in model.Groups)
        {
            foreach (Vector3 vertex in group.Vertices)
            {
                Vector3 p = probe.FromModel(vertex);
                low = Vector3.Min(low, p);
                high = Vector3.Max(high, p);
            }
        }

        var spawn = new ModelSpawn(flags | VMapFormat.ModHasBound, 1, id == 0 ? (uint)(_spawns.Count + 1) : id, position, rotation, scale, low, high, name);
        _spawns.Add((spawn, model));
        File.WriteAllBytes(Path.Combine(Directory, VMapFormat.ModelFileName(name)), model.ToBytes());
        return spawn;
    }

    /// <summary>
    /// Write <c>NNN.vmtree</c> over every placed spawn and, for a tiled map, one <c>.vmtile</c> per
    /// terrain tile listing the spawns whose origin lies in it (or <paramref name="global"/> for a
    /// WMO-only map: spawns written into the tree).
    /// </summary>
    public void Write(uint mapId, bool tiled = true)
    {
        BihTree tree = BihTree.Build([.. _spawns.Select(s => (s.Spawn.BoundLow, s.Spawn.BoundHigh))], leafSize: 1);
        using (var stream = File.Create(Path.Combine(Directory, VMapFormat.TreeFileName(mapId))))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII))
        {
            writer.Write(Encoding.ASCII.GetBytes(VMapFormat.Magic));
            writer.Write((byte)(tiled ? 1 : 0));
            writer.Write("NODE"u8);
            tree.Write(writer);
            writer.Write("GOBJ"u8);
            if (!tiled)
            {
                for (int i = 0; i < _spawns.Count; i++)
                {
                    _spawns[i].Spawn.Write(writer);
                    writer.Write((uint)i);
                }
            }
        }

        if (!tiled)
        {
            return;
        }

        foreach (var group in _spawns.Select((s, i) => (s.Spawn, Slot: i)).GroupBy(s => TerrainTile.TileOf(VMapFormat.ToWorld(s.Spawn.Position).X, VMapFormat.ToWorld(s.Spawn.Position).Y)!.Value))
        {
            WriteTile(mapId, group.Key.X, group.Key.Y, [.. group.Select(g => (g.Spawn, (uint)g.Slot))]);
        }
    }

    public void WriteTile(uint mapId, int tileX, int tileY, IReadOnlyList<(ModelSpawn Spawn, uint Slot)> spawns)
    {
        using var stream = File.Create(Path.Combine(Directory, VMapFormat.TileFileName(mapId, tileX, tileY)));
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes(VMapFormat.Magic));
        writer.Write((uint)spawns.Count);
        foreach ((ModelSpawn spawn, uint slot) in spawns)
        {
            spawn.Write(writer);
            writer.Write(slot);
        }
    }

    public string PathOf(string fileName) => Path.Combine(Directory, fileName);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
