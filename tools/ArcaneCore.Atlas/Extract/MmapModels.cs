using System.Collections.Concurrent;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision.VMaps;

namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// The models of one navigation tile, read from the server's own vmap files: adds their triangles and liquid to the
/// tile's mesh, removes the terrain that lies inside them, and answers the "is this point under a model" test the
/// generator uses to drop model triangles nobody can stand on. One instance serves one tile build; the model files are
/// shared between builds through <see cref="MmapModelCache"/>.
/// </summary>
// ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:619-945 (loadVMap)
internal sealed class MmapModels
{
    private const float MapCentre = 32 * MmapTerrain.GridSize;

    private readonly MmapModelCache _cache;
    private readonly bool _quick;
    private readonly List<Placed> _placed = [];
    private VMapTree? _tree;
    private BihTree _pointTree = BihTree.Empty;

    /// <param name="cache">The run's vmap files.</param>
    /// <param name="quick">Skip removing the terrain inside models.</param>
    public MmapModels(MmapModelCache cache, bool quick)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _quick = quick;
    }

    /// <summary>Model files a spawn named that are not in the vmaps folder (the reference skips such a spawn silently).</summary>
    public List<string> MissingModels { get; } = [];

    /// <summary>
    /// Loads the models of generator tile (<paramref name="tileX"/>, <paramref name="tileY"/>) into <paramref name="mesh"/>.
    /// False when the map has no vmap tree; the mesh is then untouched.
    /// </summary>
    public bool Load(uint mapId, int tileX, int tileY, MmapMeshData mesh)
    {
        if (_cache.Tree(mapId) is not { } bytes)
        {
            return false;
        }

        (VMapTree tree, List<(ModelSpawn Spawn, uint Slot)> spawns) = VMapTree.Parse(mapId, bytes);
        if (tree.IsTiled)
        {
            // The generator's tile X is the server's tile Y.
            spawns = _cache.Tile(mapId, serverTileX: tileY, serverTileY: tileX) ?? [];
        }

        var instances = new List<(ModelInstance Instance, uint Slot)>();
        var seen = new HashSet<uint>();
        foreach ((ModelSpawn spawn, uint slot) in spawns.OrderBy(s => s.Slot))
        {
            if (!seen.Add(slot) || !tree.IsValidSlot(slot))
            {
                continue;
            }

            if (_cache.Model(spawn.Name) is not { } model)
            {
                MissingModels.Add(spawn.Name);
                continue;
            }

            instances.Add((new ModelInstance(spawn, model), slot));
            _placed.Add(new Placed(spawn, model));
        }

        tree.AddTile((tileX, tileY), instances);
        _tree = tree;
        _pointTree = BihTree.Build([.. _placed.Select(p => (p.Spawn.BoundLow, p.Spawn.BoundHigh))]);

        int mapVertsCount = mesh.SolidVerts.Count;
        int mapTrisCount = mesh.SolidTris.Count;
        mesh.VmapFirstTriangle = mesh.SolidTris.Count / 3;
        foreach (Placed placed in _placed)
        {
            AddGeometry(placed, mesh);
        }

        mesh.VmapLastTriangle = mesh.SolidTris.Count / 3;
        if (!_quick)
        {
            RemoveTerrainInsideModels(mesh, mapVertsCount / 3, mapTrisCount / 3);
        }

        return true;
    }

    /// <summary>Whether a world position has a model's underside above it before any upside (it is inside or under a model).</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/VMapManager2.cpp:237-247, MapTree.cpp:137-171 and ModelInstance.cpp:100-122 (isUnderModel)
    public bool IsUnderModel(float x, float y, float z)
    {
        var point = new Vector3(MapCentre - x, MapCentre - y, z);
        float outDist = -1;
        float inDist = -1;
        _pointTree.IntersectPoint(point, primitive =>
        {
            Placed placed = _placed[primitive];
            float newOut = -1;
            float newIn = -1;
            if (placed.Spawn.IsM2 || Contains(placed.Spawn.BoundLow, placed.Spawn.BoundHigh, point))
            {
                ModelUnder(placed, point, ref newOut, ref newIn);
            }

            if (outDist < 0 || (newOut >= 0 && newOut < outDist))
            {
                outDist = newOut;
            }

            if (inDist < 0 || (newIn >= 0 && newIn < inDist))
            {
                inDist = newIn;
            }
        });
        return IsUnder(outDist, inDist);
    }

    private static bool IsUnder(float outDist, float inDist) => (outDist < 0 && inDist >= 0) || (inDist >= 0 && inDist < outDist);

    private static bool Contains(Vector3 low, Vector3 high, Vector3 p)
        => p.X >= low.X && p.X <= high.X && p.Y >= low.Y && p.Y <= high.Y && p.Z >= low.Z && p.Z <= high.Z;

    // ref: vmangos@0e3ff01 src/game/vmap/WorldModel.cpp:518-556 (WorldModel::IsUnderObject: the groups whose tree holds the point; the nearer of each distance wins)
    private static void ModelUnder(Placed placed, Vector3 point, ref float outDist, ref float inDist)
    {
        Vector3 modelPoint = placed.ToModel(point);
        Vector3 up = placed.Up;
        bool m2 = placed.Spawn.IsM2;
        float bestOut = -1;
        float bestIn = -1;
        placed.Model.GroupTree.IntersectPoint(modelPoint, primitive =>
        {
            GroupUnder(placed.Model.Groups[primitive], modelPoint, up, m2, out float currentOut, out float currentIn);
            if (bestOut < 0 || (currentOut >= 0 && currentOut <= bestOut))
            {
                bestOut = currentOut;
            }

            if (bestIn < 0 || (currentIn >= 0 && currentIn <= bestIn))
            {
                bestIn = currentIn;
            }
        });
        outDist = bestOut;
        inDist = bestIn;
    }

    /// <summary>
    /// Shoots a ray up from a model-space point through one group and reports the nearest triangle it leaves the model by
    /// (<paramref name="outDist"/>) and the nearest it enters by (<paramref name="inDist"/>), -1 for none. Under the group
    /// means: it enters before it leaves, or only enters.
    /// </summary>
    // ref: vmangos@0e3ff01 src/game/vmap/WorldModel.cpp:629-694 (IsGoingOut, GModelRayOrientedCallback, GroupModel::IsUnderObject)
    private static bool GroupUnder(GroupModel group, Vector3 point, Vector3 up, bool m2, out float outDist, out float inDist)
    {
        outDist = -1;
        inDist = -1;
        if (group.Triangles.Count == 0)
        {
            return false;
        }

        var callback = new OrientedCallback(group, point, up, m2);
        float distance = float.PositiveInfinity;
        group.MeshTree.IntersectRay(point, up, ref distance, ref callback, stopAtFirstHit: false);
        outDist = callback.MinOut;
        inDist = callback.MinIn;
        return IsUnder(outDist, inDist);
    }

    private struct OrientedCallback(GroupModel group, Vector3 origin, Vector3 direction, bool m2) : IRayPrimitiveCallback
    {
        public float MinOut = -1;
        public float MinIn = -1;

        public bool Invoke(int primitive, ref float maxDistance)
        {
            // The search distance is left alone: every triangle on the ray counts.
            MeshTriangle triangle = group.Triangles[primitive];
            float distance = maxDistance;
            if (group.IntersectTriangle(triangle, origin, direction, ref distance))
            {
                Vector3 p0 = group.Vertices[(int)(m2 ? triangle.Index2 : triangle.Index0)];
                Vector3 p1 = group.Vertices[(int)triangle.Index1];
                Vector3 p2 = group.Vertices[(int)(m2 ? triangle.Index0 : triangle.Index2)];
                bool goingOut = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), direction) < 0;
                if (goingOut)
                {
                    if (MinOut > distance || MinOut < 0)
                    {
                        MinOut = distance;
                    }
                }
                else if (MinIn > distance || MinIn < 0)
                {
                    MinIn = distance;
                }
            }

            return true;
        }
    }

    /// <summary>Appends an instance's triangles (and its WMO liquid) to the mesh in Recast space.</summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:638-757
    private static void AddGeometry(Placed placed, MmapMeshData mesh)
    {
        foreach (GroupModel group in placed.Model.Groups)
        {
            int offset = mesh.SolidVerts.Count / 3;
            foreach (Vector3 vertex in group.Vertices)
            {
                Vector3 world = placed.ToWorld(vertex);
                placed.Grow(world);
                mesh.SolidVerts.Add(world.Y);
                mesh.SolidVerts.Add(world.Z);
                mesh.SolidVerts.Add(world.X);
            }

            // An M2's triangles are stored facing the other way round.
            foreach (MeshTriangle triangle in group.Triangles)
            {
                mesh.SolidTris.Add((int)(placed.IsM2Name ? triangle.Index2 : triangle.Index0) + offset);
                mesh.SolidTris.Add((int)triangle.Index1 + offset);
                mesh.SolidTris.Add((int)(placed.IsM2Name ? triangle.Index0 : triangle.Index2) + offset);
            }

            if (group.Liquid is not { } liquid)
            {
                continue;
            }

            // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:693-704 (the type is read as "& 3": 0 and 1 water, 2 magma, 3 slime)
            int type = (liquid.Type & 3) switch
            {
                2 => MmapArea.Magma,
                3 => MmapArea.Slime,
                _ => MmapArea.Water,
            };
            int tilesX = (int)liquid.TilesX;
            int tilesY = (int)liquid.TilesY;
            int vertsX = tilesX + 1;
            int vertsY = tilesY + 1;
            int liquidOffset = mesh.LiquidVerts.Count / 3;
            for (int x = 0; x < vertsX; x++)
            {
                for (int y = 0; y < vertsY; y++)
                {
                    Vector3 world = placed.ToWorld(new Vector3(
                        liquid.Corner.X + (x * MmapTerrain.GridPartSize),
                        liquid.Corner.Y + (y * MmapTerrain.GridPartSize),
                        liquid.Heights[(y * vertsX) + x]));
                    mesh.LiquidVerts.Add(world.Y);
                    mesh.LiquidVerts.Add(world.Z);
                    mesh.LiquidVerts.Add(world.X);
                }
            }

            for (int x = 0; x < tilesX; x++)
            {
                for (int y = 0; y < tilesY; y++)
                {
                    if ((liquid.Flags[x + (y * tilesX)] & 0x0f) == 0x0f)
                    {
                        continue;
                    }

                    int square = (x * tilesY) + y;
                    int idx1 = square + x;
                    int idx2 = square + 1 + x;
                    int idx3 = square + tilesY + 1 + 1 + x;
                    int idx4 = square + tilesY + 1 + x;
                    mesh.LiquidTris.AddRange([idx2 + liquidOffset, idx1 + liquidOffset, idx3 + liquidOffset]);
                    mesh.LiquidType.Add(type);
                    mesh.LiquidTris.AddRange([idx3 + liquidOffset, idx1 + liquidOffset, idx4 + liquidOffset]);
                    mesh.LiquidType.Add(type);
                }
            }
        }
    }

    /// <summary>
    /// Finds the terrain vertices that have a model's floor closely above them (they are inside the model), cuts the
    /// terrain triangles that straddle the model's edge back to where the model begins, and blanks the ones wholly inside.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:759-945
    private void RemoveTerrainInsideModels(MmapMeshData mesh, int terrainVertsCount, int terrainTrisCount)
    {
        var inside = new float[terrainVertsCount];
        Array.Fill(inside, -1.0f);
        List<float> verts = mesh.SolidVerts;
        foreach (Placed placed in _placed)
        {
            for (int t = 0; t < terrainVertsCount; t++)
            {
                // A ray going up from the vertex can only meet the instance inside its footprint and below its top.
                var world = new Vector3(verts[(3 * t) + 2], verts[3 * t], verts[(3 * t) + 1] + 0.2f);
                if (world.X < placed.WorldLow.X || world.X > placed.WorldHigh.X || world.Y < placed.WorldLow.Y || world.Y > placed.WorldHigh.Y || world.Z > placed.WorldHigh.Z)
                {
                    continue;
                }

                Vector3 modelPoint = placed.ToModel(new Vector3(MapCentre - world.X, MapCentre - world.Y, world.Z));
                foreach (GroupModel group in placed.Model.Groups)
                {
                    // Less than 5 yards between the terrain and the model above it: the terrain there is not walkable.
                    if (GroupUnder(group, modelPoint, placed.Up, placed.IsM2Name, out _, out float inDist) && inDist < 5.0f && (inside[t] < 0.0f || inDist < inside[t]))
                    {
                        inside[t] = inDist;
                    }
                }
            }
        }

        float InsideDist(int vertex) => vertex < terrainVertsCount ? inside[vertex] : -1.0f;

        Vector3 Corner(int vertex) => new(verts[(3 * vertex) + 2], verts[3 * vertex], verts[(3 * vertex) + 1]);

        void AddVertex(Vector3 world)
        {
            verts.Add(world.Y);
            verts.Add(world.Z);
            verts.Add(world.X);
        }

        List<int> tris = mesh.SolidTris;
        Span<int> vertIdx = stackalloc int[3];
        Span<bool> insideModel = stackalloc bool[3];
        var tri = new Vector3[3];
        for (int i = 0; i < terrainTrisCount; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                vertIdx[j] = tris[(i * 3) + j];
                insideModel[j] = InsideDist(vertIdx[j]) >= 0.1f;
                tri[j] = Corner(vertIdx[j]);
            }

            if (InsideDist(vertIdx[0]) < 1.0f && InsideDist(vertIdx[1]) < 1.0f && InsideDist(vertIdx[2]) < 1.0f)
            {
                continue;
            }

            if (insideModel[0] == insideModel[1] && insideModel[1] == insideModel[2])
            {
                continue;
            }

            int outsideIdx = 0;
            while (insideModel[outsideIdx])
            {
                outsideIdx++;
            }

            int next = (outsideIdx + 1) % 3;
            int last = (outsideIdx + 2) % 3;
            if (insideModel[next] && insideModel[last])
            {
                // Two corners inside: pull both back to where the edges from the outside corner meet a model.
                Vector3 to1 = ObjectHit(tri[outsideIdx], tri[next], 0.0f) ?? (((tri[next] * 4) + tri[outsideIdx]) / 5);
                Vector3 to2 = ObjectHit(tri[outsideIdx], tri[last], 0.0f) ?? (((tri[last] * 4) + tri[outsideIdx]) / 5);
                int offset = verts.Count / 3;
                AddVertex(to1);
                AddVertex(to2);
                vertIdx[next] = offset;
                vertIdx[last] = offset + 1;
            }

            if (insideModel[next] != insideModel[last])
            {
                // One corner inside: cut it off, which leaves a quad (this triangle and one new one).
                int insideIdx = insideModel[next] ? next : last;
                int outsideIdx2 = insideIdx == next ? last : next;
                Vector3? from1 = ObjectHit(tri[outsideIdx], tri[insideIdx], -0.01f);
                Vector3? from2 = ObjectHit(tri[outsideIdx2], tri[insideIdx], -0.01f);
                Vector3 hit1 = from1 ?? tri[insideIdx];
                Vector3 hit2 = from2 ?? tri[insideIdx];
                if (from1 is null && from2 is null)
                {
                    hit1 = (tri[outsideIdx] + (tri[insideIdx] * 3)) / 4;
                    hit2 = (tri[outsideIdx2] + (tri[insideIdx] * 3)) / 4;
                }

                int offset = verts.Count / 3;
                AddVertex(hit1);
                AddVertex(hit2);
                tris.AddRange([vertIdx[outsideIdx2], offset, offset + 1, offset + 1, offset, vertIdx[outsideIdx2]]);
                vertIdx[insideIdx] = offset;
            }

            for (int j = 0; j < 3; j++)
            {
                tris[(i * 3) + j] = vertIdx[j];
            }
        }

        for (int i = 0; i < terrainTrisCount; i++)
        {
            bool allInside = true;
            bool allInBorder = true;
            for (int j = 0; j < 3; j++)
            {
                float distance = InsideDist(tris[(i * 3) + j]);
                allInside &= distance >= 1.0f;
                allInBorder &= distance < 1.5f;
            }

            if (allInside && !allInBorder)
            {
                tris[i * 3] = 0;
                tris[(i * 3) + 1] = 0;
                tris[(i * 3) + 2] = 0;
            }
        }
    }

    /// <summary>The first model hit on the world segment, moved <paramref name="modify"/> along it, or null when nothing is hit.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/VMapManager2.cpp (getObjectHitPos) through the server's VMapTree.TryGetObjectHit
    private Vector3? ObjectHit(Vector3 from, Vector3 to, float modify)
    {
        if (_tree is null || !_tree.TryGetObjectHit(ToInternal(from), ToInternal(to), modify, out Vector3 hit))
        {
            return null;
        }

        return new Vector3(MapCentre - hit.X, MapCentre - hit.Y, hit.Z);
    }

    private static Vector3 ToInternal(Vector3 world) => new(MapCentre - world.X, MapCentre - world.Y, world.Z);

    /// <summary>One loaded spawn with its transform and, once its geometry is added, the world box around it.</summary>
    private sealed class Placed
    {
        private readonly Matrix3 _rotation;
        private readonly Matrix3 _inverseRotation;
        private readonly float _inverseScale;

        public Placed(ModelSpawn spawn, WorldModel model)
        {
            Spawn = spawn;
            Model = model;

            // The same transform the server's ModelInstance uses for this spawn.
            const float toRadians = MathF.PI / 180.0f;
            _rotation = Matrix3.FromEulerAnglesZyx(spawn.Rotation.Y * toRadians, spawn.Rotation.X * toRadians, spawn.Rotation.Z * toRadians);
            _inverseRotation = _rotation.Transpose();
            _inverseScale = 1.0f / spawn.Scale;
            Up = _inverseRotation.Transform(Vector3.UnitZ) * _inverseScale;

            // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:652 (an instance is an M2 when its name holds ".m2" or ".M2")
            IsM2Name = spawn.Name.Contains(".m2", StringComparison.Ordinal) || spawn.Name.Contains(".M2", StringComparison.Ordinal);
        }

        public ModelSpawn Spawn { get; }

        public WorldModel Model { get; }

        public bool IsM2Name { get; }

        /// <summary>World up as a model-space vector whose length makes ray distances come out in world yards.</summary>
        public Vector3 Up { get; }

        public Vector3 WorldLow { get; private set; } = new(float.PositiveInfinity);

        public Vector3 WorldHigh { get; private set; } = new(float.NegativeInfinity);

        public Vector3 ToModel(Vector3 internalPoint) => _inverseRotation.Transform(internalPoint - Spawn.Position) * _inverseScale;

        public Vector3 ToWorld(Vector3 modelPoint)
        {
            Vector3 p = _rotation.Transform(modelPoint * Spawn.Scale) + Spawn.Position;
            return new Vector3(MapCentre - p.X, MapCentre - p.Y, p.Z);
        }

        public void Grow(Vector3 world)
        {
            WorldLow = Vector3.Min(WorldLow, world);
            WorldHigh = Vector3.Max(WorldHigh, world);
        }
    }
}

/// <summary>
/// The vmap files of one data root, read once and shared by every tile build of a run: tree files, tile spawn lists and
/// model files (the last held weakly, so a long run does not keep every model of the world in memory). Thread-safe.
/// </summary>
internal sealed class MmapModelCache
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<uint, byte[]?> _trees = new();
    private readonly ConcurrentDictionary<string, WeakReference<WorldModel>?> _models = new(StringComparer.Ordinal);

    /// <param name="vmapsDirectory">The folder holding the vmap files.</param>
    public MmapModelCache(string vmapsDirectory)
    {
        _directory = vmapsDirectory ?? throw new ArgumentNullException(nameof(vmapsDirectory));
    }

    /// <summary>The bytes of a map's tree file, or null when it has none.</summary>
    public byte[]? Tree(uint mapId)
        => _trees.GetOrAdd(mapId, id =>
        {
            string path = Path.Combine(_directory, VMapFormat.TreeFileName(id));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        });

    /// <summary>The spawns of a tile, or null when the tile has no file.</summary>
    public List<(ModelSpawn Spawn, uint Slot)>? Tile(uint mapId, int serverTileX, int serverTileY)
    {
        string path = Path.Combine(_directory, VMapFormat.TileFileName(mapId, serverTileX, serverTileY));
        return File.Exists(path) ? VMapTree.ParseTile(File.ReadAllBytes(path)) : null;
    }

    /// <summary>A model file by spawn name, or null when it is not there.</summary>
    public WorldModel? Model(string name)
    {
        if (_models.TryGetValue(name, out WeakReference<WorldModel>? reference))
        {
            if (reference is null)
            {
                return null;
            }

            if (reference.TryGetTarget(out WorldModel? cached))
            {
                return cached;
            }
        }

        if (!VMapFormat.IsSafeModelName(name))
        {
            _models[name] = null;
            return null;
        }

        string path = Path.Combine(_directory, VMapFormat.ModelFileName(name));
        if (!File.Exists(path))
        {
            _models[name] = null;
            return null;
        }

        WorldModel model = WorldModel.Parse(File.ReadAllBytes(path));
        _models[name] = new WeakReference<WorldModel>(model);
        return model;
    }
}
