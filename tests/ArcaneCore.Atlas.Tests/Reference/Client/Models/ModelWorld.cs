using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Game.Maps.Collision.VMaps;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>An in-memory client: files, placements and the services the model layers look up, all synthetic.</summary>
internal sealed class ModelWorld
{
    public const string WallPath = "World\\wmo\\Test\\Wall.wmo";
    public const string CubePath = "World\\Doodads\\Test\\Cube.m2";
    public const string CubeStoredPath = "World\\Doodads\\Test\\Cube.mdx";

    /// <summary>Server tile 0/49/31 (Azeroth_31_49.adt): world x about -9600 to -9067, y about 0 to 533.</summary>
    public static readonly TileKey Goldshire = new(0, 49, 31);

    /// <summary>The tile east of it (larger world x): 0/48/31.</summary>
    public static readonly TileKey Neighbour = new(0, 48, 31);

    public ModelWorld(bool withFiles = true, bool withPlacements = true, bool withMaps = true)
    {
        if (withFiles)
        {
            Services.Add<IClientFileSource>(Files);
        }

        if (withPlacements)
        {
            Services.Add<IClientPlacementSource>(Placements);
        }

        if (withMaps)
        {
            Services.Add<IMapDirectory>(new FakeMapDirectory(new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0)));
        }
    }

    public MemoryClientFiles Files { get; } = new();

    public FakePlacements Placements { get; } = new();

    public AtlasServices Services { get; } = new();

    public ClientModelCache Cache { get; } = new();

    public ClientModelLayerSource Source => new(Services, Cache, NullLogger.Instance);

    public ReferenceModelQueries Queries => new(Services, Cache);

    /// <summary>The raw ADT position (x, y, z in file order) of a point given in world coordinates.</summary>
    public static WorldPoint Raw(double worldX, double worldY, double worldZ)
        => new(VMapFormat.MapMid - worldY, worldZ, VMapFormat.MapMid - worldX);

    public static ClientPlacement Wmo(uint uniqueId, WorldPoint world, TileKey tile, string path = WallPath, double yaw = 0, ushort flags = 0, ushort doodadSet = 0)
        => new(PlacementKind.Wmo, path, uniqueId, Raw(world.X, world.Y, world.Z), new WorldPoint(0, yaw, 0), 1, flags, doodadSet, tile);

    public static ClientPlacement M2(uint uniqueId, WorldPoint world, TileKey tile, string path = CubeStoredPath, double scale = 1024)
        => new(PlacementKind.M2, path, uniqueId, Raw(world.X, world.Y, world.Z), new WorldPoint(0, 0, 0), scale, 0, 0, tile);

    /// <summary>Stores a WMO root and one file per group; the root's group count is set from <paramref name="groups"/>.</summary>
    public void AddWmo(string rootPath, SyntheticWmoRoot root, params SyntheticWmoGroup[] groups)
    {
        root.GroupCount = (uint)groups.Length;
        Files.Add(rootPath, ModelFixtures.WmoRoot(root));
        for (int i = 0; i < groups.Length; i++)
        {
            Files.Add(WmoRootFile.GroupFilePath(rootPath, i), ModelFixtures.WmoGroup(groups[i]));
        }
    }

    public void AddCube(string path = CubePath, float half = 1f, bool withHull = true)
        => Files.Add(path, ModelFixtures.CubeM2(half, withHull));

    public void AddAdt(TileKey tile)
        => Files.Add($"World\\Maps\\Azeroth\\Azeroth_{tile.Y}_{tile.X}.adt", [0]);

    public LayerBuild Build(string layerId, TileKey tile) => Source.Build(layerId, tile, CancellationToken.None);

    /// <summary>The vertices of a chunk in world coordinates.</summary>
    public static List<WorldPoint> WorldVertices(GeometryChunk chunk)
    {
        var points = new List<WorldPoint>(chunk.VertexCount);
        for (int i = 0; i < chunk.VertexCount; i++)
        {
            points.Add(new WorldPoint(
                chunk.Origin.X + chunk.Positions[3 * i],
                chunk.Origin.Y + chunk.Positions[(3 * i) + 1],
                chunk.Origin.Z + chunk.Positions[(3 * i) + 2]));
        }

        return points;
    }

    /// <summary>The attribute description of triangle <paramref name="triangle"/>.</summary>
    public static System.Text.Json.Nodes.JsonObject AttributeOf(GeometryChunk chunk, int triangle)
        => chunk.Attributes[chunk.TriangleAttributes[triangle]];

    /// <summary>A one-group WMO with a single wall in the plane x = <paramref name="x"/> (model space), 10 by 10 yards.</summary>
    public void AddWall(string path = WallPath, float x = 0f, byte flags = 0x20, uint groupFlags = 0)
        => AddWmo(path, new SyntheticWmoRoot(), ModelFixtures.WallGroup(x, flags, groupFlags));
}

/// <summary>Counts reads, to prove the model cache loads a file once.</summary>
internal sealed class CountingFiles(IClientFileSource inner) : IClientFileSource
{
    private int _reads;

    public int Reads => Volatile.Read(ref _reads);

    public string Description => inner.Description;

    public bool Exists(string path) => inner.Exists(path);

    public byte[]? Read(string path)
    {
        Interlocked.Increment(ref _reads);
        return inner.Read(path);
    }

    public IReadOnlyList<string> List(string prefix) => inner.List(prefix);

    public string? ArchiveOf(string path) => inner.ArchiveOf(path);
}
