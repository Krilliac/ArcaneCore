using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// Writes tiny repository-authored navmeshes (<c>.mmap</c> + <c>.mmtile</c>) into a temporary
/// directory: a grid of square cells in world space, one quad polygon per walkable cell, laid out
/// as a Detour tile. No client data is involved.
/// </summary>
internal sealed class NavMeshFixture : IDisposable
{
    public NavMeshFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "arcanecore-mmap-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string PathOf(string fileName) => Path.Combine(Directory, fileName);

    public void WriteParams(uint mapId)
    {
        using var writer = new BinaryWriter(File.Create(PathOf(NavMeshFormat.ParamsFileName(mapId))));
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(533.3333f);
        writer.Write(533.3333f);
        writer.Write(4096);
        writer.Write(1 << 16);
    }

    public void WriteTile(uint mapId, int tileX, int tileY, CellTile tile, uint mmapVersion = NavMeshFormat.MmapVersion)
    {
        byte[] data = tile.Build();
        using var writer = new BinaryWriter(File.Create(PathOf(NavMeshFormat.TileFileName(mapId, tileX, tileY))));
        writer.Write(NavMeshFormat.MmapMagic);
        writer.Write(NavMeshFormat.DetourVersion);
        writer.Write(mmapVersion);
        writer.Write((uint)data.Length);
        writer.Write(0u);
        writer.Write(data);
    }

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

/// <summary>
/// A Detour tile made of square cells: cell (i, j) covers world X [X0 + i·Size, +Size) and
/// Y [Y0 + j·Size, +Size). Border edges on <see cref="LinkedSides"/> are written as external links.
/// </summary>
internal sealed record CellTile(int DetourX, int DetourY, float X0, float Y0, int CellsX, int CellsY, float Size = 2)
{
    public Func<int, int, bool> Walkable { get; init; } = (_, _) => true;

    public Func<float, float, float> Height { get; init; } = (_, _) => 0;

    public Func<int, int, NavTerrain> Flags { get; init; } = (_, _) => NavTerrain.Ground;

    public int[] LinkedSides { get; init; } = [];

    public bool DetailMesh { get; init; } = true;

    public int LinkSize { get; init; } = 16;

    public int? CorruptVertexIndex { get; init; }

    public byte[] Build()
    {
        int vx = CellsX + 1;
        var vertices = new List<Vector3>();
        for (int j = 0; j <= CellsY; j++)
        {
            for (int i = 0; i <= CellsX; i++)
            {
                float x = X0 + (i * Size);
                float y = Y0 + (j * Size);
                vertices.Add(NavMeshFormat.ToRecast(new Vector3(x, y, Height(x, y))));
            }
        }

        var polyIndex = new Dictionary<(int, int), int>();
        for (int j = 0; j < CellsY; j++)
        {
            for (int i = 0; i < CellsX; i++)
            {
                if (Walkable(i, j))
                {
                    polyIndex[(i, j)] = polyIndex.Count;
                }
            }
        }

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        Vector3 low = vertices.Aggregate(Vector3.Min) - new Vector3(0, 1, 0);
        Vector3 high = vertices.Aggregate(Vector3.Max) + new Vector3(0, 1, 0);
        int polyCount = polyIndex.Count;
        int maxLinks = polyCount * 4;
        int detailCount = DetailMesh ? polyCount : 0;
        foreach (int value in new[] { NavMeshFormat.DetourMagic, (int)NavMeshFormat.DetourVersion, DetourX, DetourY, 0, 0, polyCount, vertices.Count, maxLinks, detailCount, 0, detailCount * 2, 0, 0, polyCount })
        {
            w.Write(value);
        }

        w.Write(2f);
        w.Write(0.5f);
        w.Write(1f);
        Write(w, low);
        Write(w, high);
        w.Write(1f);

        foreach (Vector3 v in vertices)
        {
            Write(w, v);
        }

        // Quad corners (world): (X0,Y0) (X1,Y0) (X1,Y1) (X0,Y1). Edge e runs from corner e to e + 1:
        // e0 faces −Y (Recast −x, side 4), e1 +X (Recast +z, side 2), e2 +Y (side 0), e3 −X (side 6).
        (int Di, int Dj, int Side)[] edges = [(0, -1, 4), (1, 0, 2), (0, 1, 0), (-1, 0, 6)];
        foreach (((int i, int j), int _) in polyIndex.OrderBy(p => p.Value))
        {
            w.Write(0u);
            ushort[] corners = [(ushort)((j * vx) + i), (ushort)((j * vx) + i + 1), (ushort)(((j + 1) * vx) + i + 1), (ushort)(((j + 1) * vx) + i)];
            if (CorruptVertexIndex is { } bad)
            {
                corners[0] = (ushort)bad;
            }

            for (int k = 0; k < 6; k++)
            {
                w.Write(k < 4 ? corners[k] : (ushort)0);
            }

            for (int k = 0; k < 6; k++)
            {
                ushort nei = 0;
                if (k < 4)
                {
                    (int di, int dj, int side) = edges[k];
                    int ni = i + di;
                    int nj = j + dj;
                    if (ni < 0 || nj < 0 || ni >= CellsX || nj >= CellsY)
                    {
                        nei = LinkedSides.Contains(side) ? (ushort)(NavMeshFormat.ExternalLink | side) : (ushort)0;
                    }
                    else if (polyIndex.TryGetValue((ni, nj), out int other))
                    {
                        nei = (ushort)(other + 1);
                    }
                }

                w.Write(nei);
            }

            w.Write((ushort)Flags(i, j));
            w.Write((byte)4);
            w.Write((byte)0);
        }

        w.Write(new byte[maxLinks * LinkSize]);
        for (int p = 0; p < detailCount; p++)
        {
            w.Write(0u);
            w.Write((uint)(p * 2));
            w.Write((byte)0);
            w.Write((byte)2);
            w.Write((ushort)0);
        }

        for (int p = 0; p < detailCount; p++)
        {
            w.Write([0, 1, 2, 0, 0, 2, 3, 0]);
        }

        w.Flush();
        return stream.ToArray();
    }

    private static void Write(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }
}
