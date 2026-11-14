using System.Buffers.Binary;

namespace ArcaneCore.Atlas.Extract;

/// <summary>The area ids the navigation mesh generator marks triangles, spans and polygons with.</summary>
// ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:85-96 (NavMeshAreas)
internal static class MmapArea
{
    public const int None = 0;
    public const int Ground = 1;
    public const int GroundModel = 2;
    public const int SteepSlope = 3;
    public const int SteepSlopeModel = 4;
    public const int WaterTransition = 5;
    public const int Water = 6;
    public const int Magma = 7;
    public const int Slime = 8;
}

/// <summary>
/// The triangle soup one navigation tile is built from, in Recast space: a vertex is (world y, world z, world x).
/// Solid triangles are terrain first, then model triangles from <see cref="VmapFirstTriangle"/> on; liquid triangles each
/// carry an area id.
/// </summary>
// ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.h:70-91 (MeshData)
internal sealed class MmapMeshData
{
    public List<float> SolidVerts { get; } = [];

    public List<int> SolidTris { get; } = [];

    public List<float> LiquidVerts { get; } = [];

    public List<int> LiquidTris { get; } = [];

    public List<int> LiquidType { get; } = [];

    /// <summary>First solid triangle that came from a model.</summary>
    public int VmapFirstTriangle { get; set; }

    /// <summary>One past the last solid triangle that came from a model.</summary>
    public int VmapLastTriangle { get; set; }

    /// <summary>True for a terrain triangle (everything outside the model range).</summary>
    public bool IsTerrainTriangle(int triangle) => triangle < VmapFirstTriangle || triangle >= VmapLastTriangle;
}

/// <summary>
/// Turns the server's <c>.map</c> terrain files into the terrain and liquid triangles of one navigation tile: the whole
/// tile plus the one-square strip of each of its four neighbours, so the mesh has no seam at the tile border. It also keeps
/// the tile's height grid for the "is this point under the ground" test of the model pass.
/// </summary>
/// <remarks>
/// Tile indices here are the generator's own: its X counts along world Y and its Y along world X, so the file of generator
/// tile (x, y) is <c>maps/MMM&lt;y&gt;&lt;x&gt;.map</c> (the server's tile X = y, tile Y = x).
/// </remarks>
// ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:83-507 (loadMap)
internal sealed class MmapTerrain
{
    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.h:52-63
    public const float GridSize = 533.33333f;
    public const float GridPartSize = GridSize / V8Size;
    private const int V9Size = 129;
    private const int V9SizeSq = V9Size * V9Size;
    private const int V8Size = 128;
    private const int V8SizeSq = V8Size * V8Size;
    private const float InvalidLiquidHeight = -500f;
    private const float InvalidLiquidHeightMax = 5000f;

    // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:28-30, :40-41, :66-74
    private const uint VersionMagic = 0x342E317A;
    private const uint HeightNoHeight = 0x1;
    private const uint HeightAsInt16 = 0x2;
    private const uint HeightAsInt8 = 0x4;
    private const byte LiquidNoType = 0x1;
    private const byte LiquidNoHeight = 0x2;
    private const byte LiquidTypeMagma = 0x01;
    private const byte LiquidTypeOcean = 0x02;
    private const byte LiquidTypeSlime = 0x04;
    private const byte LiquidTypeWater = 0x08;
    private const byte LiquidTypeDeepWater = 0x10;

    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:587-588
    private static readonly ushort[] HoleTabH = [0x1111, 0x2222, 0x4444, 0x8888];
    private static readonly ushort[] HoleTabV = [0x000F, 0x00F0, 0x0F00, 0xF000];

    private readonly string _mapsDirectory;
    private readonly bool _skipLiquid;
    private float[]? _v9;
    private float[]? _v8;
    private int _heightTileX;
    private int _heightTileY;

    private enum Spot
    {
        Top = 1,
        Right = 2,
        Left = 3,
        Bottom = 4,
        Entire = 5,
    }

    /// <param name="mapsDirectory">The folder holding the <c>.map</c> files.</param>
    /// <param name="skipLiquid">Leave liquid out of the mesh.</param>
    public MmapTerrain(string mapsDirectory, bool skipLiquid)
    {
        _mapsDirectory = mapsDirectory ?? throw new ArgumentNullException(nameof(mapsDirectory));
        _skipLiquid = skipLiquid;
    }

    /// <summary>Loads generator tile (<paramref name="tileX"/>, <paramref name="tileY"/>) and the facing strips of its neighbours into <paramref name="mesh"/>.</summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:83-96
    public void Load(uint mapId, int tileX, int tileY, MmapMeshData mesh)
    {
        _v9 = null;
        _v8 = null;
        if (Load(mapId, tileX, tileY, mesh, Spot.Entire))
        {
            Load(mapId, tileX + 1, tileY, mesh, Spot.Left);
            Load(mapId, tileX - 1, tileY, mesh, Spot.Right);
            Load(mapId, tileX, tileY + 1, mesh, Spot.Top);
            Load(mapId, tileX, tileY - 1, mesh, Spot.Bottom);
        }
    }

    /// <summary>Drops the vertices no triangle uses and renumbers the triangles, keeping the vertices' order.</summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:1008-1049 (cleanVertices)
    public static void CleanVertices(List<float> verts, List<int> tris)
    {
        var used = new SortedDictionary<int, int>();
        foreach (int index in tris)
        {
            used[index] = 0;
        }

        var clean = new List<float>(used.Count * 3);
        int count = 0;
        foreach (int index in used.Keys.ToArray())
        {
            used[index] = count++;
            clean.Add(verts[index * 3]);
            clean.Add(verts[(index * 3) + 1]);
            clean.Add(verts[(index * 3) + 2]);
        }

        verts.Clear();
        verts.AddRange(clean);
        for (int i = 0; i < tris.Count; i++)
        {
            tris[i] = used[tris[i]];
        }
    }

    /// <summary>
    /// The terrain height at a world position inside the loaded tile, or -50000 when the tile has no height samples or the
    /// position is outside it.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:1106-1190 (getHeight)
    public float GetHeight(float x, float y)
    {
        if (_v8 is null || _v9 is null)
        {
            return -50000;
        }

        x = (V8Size * (32 - (x / GridSize))) - ((float)_heightTileY * V8Size);
        y = (V8Size * (32 - (y / GridSize))) - ((float)_heightTileX * V8Size);
        if (x < 0.0f || y < 0.0f || x >= V8Size || y >= V8Size)
        {
            return -50000;
        }

        int xInt = (int)x;
        int yInt = (int)y;
        x -= xInt;
        y -= yInt;

        // Each square is four triangles around its centre sample; solve h = a * x + b * y + c on the one the point is in.
        float a;
        float b;
        float c;
        if (x + y < 1)
        {
            if (x > y)
            {
                float h1 = _v9[(xInt * V9Size) + yInt];
                float h2 = _v9[((xInt + 1) * V9Size) + yInt];
                float h5 = 2 * _v8[(xInt * V8Size) + yInt];
                a = h2 - h1;
                b = h5 - h1 - h2;
                c = h1;
            }
            else
            {
                float h1 = _v9[(xInt * V9Size) + yInt];
                float h3 = _v9[(xInt * V9Size) + yInt + 1];
                float h5 = 2 * _v8[(xInt * V8Size) + yInt];
                a = h5 - h1 - h3;
                b = h3 - h1;
                c = h1;
            }
        }
        else if (x > y)
        {
            float h2 = _v9[((xInt + 1) * V9Size) + yInt];
            float h4 = _v9[((xInt + 1) * V9Size) + yInt + 1];
            float h5 = 2 * _v8[(xInt * V8Size) + yInt];
            a = h2 + h4 - h5;
            b = h4 - h2;
            c = h5 - h4;
        }
        else
        {
            float h3 = _v9[(xInt * V9Size) + yInt + 1];
            float h4 = _v9[((xInt + 1) * V9Size) + yInt + 1];
            float h5 = 2 * _v8[(xInt * V8Size) + yInt];
            a = h4 - h3;
            b = h3 + h4 - h5;
            c = h5 - h4;
        }

        return (a * x) + (b * y) + c;
    }

    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:98-507
    private bool Load(uint mapId, int tileX, int tileY, MmapMeshData mesh, Spot portion)
    {
        if (tileX is < 0 or > 63 || tileY is < 0 or > 63)
        {
            return false;
        }

        string path = Path.Combine(_mapsDirectory, $"{mapId:D3}{tileY:D2}{tileX:D2}.map");
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 40 || U32(file, 4) != VersionMagic)
        {
            throw new InvalidDataException($"'{path}' is not a z1.4 map file.");
        }

        uint heightOffset = U32(file, 16);
        uint liquidOffset = U32(file, 24);
        uint holesOffset = U32(file, 32);
        uint holesSize = U32(file, 36);
        uint heightFlags = U32(file, Checked(file, heightOffset + 4, 4, path));
        bool haveTerrain = (heightFlags & HeightNoHeight) == 0;
        bool haveLiquid = liquidOffset != 0 && !_skipLiquid;
        if (!haveTerrain && !haveLiquid)
        {
            return false;
        }

        var holes = new ushort[256];
        var liquidFlags = new byte[256];
        bool liquidTypeLoaded = false;
        var ltriangles = new List<int>();
        var ttriangles = new List<int>();
        (int loopStart, int loopEnd, int loopInc) = LoopVars(portion);
        float xoffset = ((float)tileX - 32) * GridSize;
        float yoffset = ((float)tileY - 32) * GridSize;

        if (haveTerrain)
        {
            float gridHeight = F32(file, (int)heightOffset + 8);
            float gridMaxHeight = F32(file, (int)heightOffset + 12);
            int at = (int)heightOffset + 16;
            var v9 = new float[V9SizeSq];
            var v8 = new float[V8SizeSq];
            if ((heightFlags & HeightAsInt8) != 0)
            {
                Checked(file, at, V9SizeSq + V8SizeSq, path);
                float multiplier = (gridMaxHeight - gridHeight) / 255;
                for (int i = 0; i < V9SizeSq; i++)
                {
                    v9[i] = ((float)file[at + i] * multiplier) + gridHeight;
                }

                for (int i = 0; i < V8SizeSq; i++)
                {
                    v8[i] = ((float)file[at + V9SizeSq + i] * multiplier) + gridHeight;
                }
            }
            else if ((heightFlags & HeightAsInt16) != 0)
            {
                Checked(file, at, (V9SizeSq + V8SizeSq) * 2, path);
                float multiplier = (gridMaxHeight - gridHeight) / 65535;
                for (int i = 0; i < V9SizeSq; i++)
                {
                    v9[i] = ((float)BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + (i * 2))) * multiplier) + gridHeight;
                }

                for (int i = 0; i < V8SizeSq; i++)
                {
                    v8[i] = ((float)BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + ((V9SizeSq + i) * 2))) * multiplier) + gridHeight;
                }
            }
            else
            {
                Checked(file, at, (V9SizeSq + V8SizeSq) * 4, path);
                for (int i = 0; i < V9SizeSq; i++)
                {
                    v9[i] = F32(file, at + (i * 4));
                }

                for (int i = 0; i < V8SizeSq; i++)
                {
                    v8[i] = F32(file, at + ((V9SizeSq + i) * 4));
                }
            }

            int holeBytes = (int)Math.Min(holesSize, 512);
            Checked(file, holesOffset, holeBytes, path);
            for (int i = 0; i < holeBytes / 2; i++)
            {
                holes[i] = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)holesOffset + (i * 2)));
            }

            if (portion == Spot.Entire)
            {
                _v9 = v9;
                _v8 = v8;
                _heightTileX = tileX;
                _heightTileY = tileY;
            }

            // Every sample becomes a vertex (the corner grid, then the centre grid); unused ones are cleaned away later.
            int count = mesh.SolidVerts.Count / 3;
            for (int i = 0; i < V9SizeSq; i++)
            {
                mesh.SolidVerts.Add((xoffset + ((i % V9Size) * GridPartSize)) * -1f);
                mesh.SolidVerts.Add(v9[i]);
                mesh.SolidVerts.Add((yoffset + ((i / V9Size) * GridPartSize)) * -1f);
            }

            for (int i = 0; i < V8SizeSq; i++)
            {
                mesh.SolidVerts.Add((xoffset + ((i % V8Size) * GridPartSize) + (GridPartSize / 2f)) * -1f);
                mesh.SolidVerts.Add(v8[i]);
                mesh.SolidVerts.Add((yoffset + ((i / V8Size) * GridPartSize) + (GridPartSize / 2f)) * -1f);
            }

            Span<int> indices = stackalloc int[3];
            for (int i = loopStart; i < loopEnd; i += loopInc)
            {
                for (int j = (int)Spot.Top; j <= (int)Spot.Bottom; j++)
                {
                    HeightTriangle(i, (Spot)j, indices, liquid: false);
                    ttriangles.Add(indices[2] + count);
                    ttriangles.Add(indices[1] + count);
                    ttriangles.Add(indices[0] + count);
                }
            }
        }

        if (haveLiquid)
        {
            int at = Checked(file, liquidOffset, 16, path);
            byte flags = file[at + 4];
            byte headerLiquidFlags = file[at + 5];
            int offsetX = file[at + 8];
            int offsetY = file[at + 9];
            int width = file[at + 10];
            int height = file[at + 11];
            float liquidLevel = F32(file, at + 12);
            at += 16;
            if ((flags & LiquidNoType) == 0)
            {
                Checked(file, at, 512 + 256, path);
                file.AsSpan(at + 512, 256).CopyTo(liquidFlags);
                at += 512 + 256;
            }
            else
            {
                // One liquid over the whole tile: its type comes from the header.
                Array.Fill(liquidFlags, headerLiquidFlags);
            }

            liquidTypeLoaded = true;
            float[]? liquidMap = null;
            if ((flags & LiquidNoHeight) == 0)
            {
                Checked(file, at, width * height * 4, path);
                liquidMap = new float[width * height];
                for (int i = 0; i < liquidMap.Length; i++)
                {
                    liquidMap[i] = F32(file, at + (i * 4));
                }
            }

            int count = mesh.LiquidVerts.Count / 3;
            if (liquidMap is not null)
            {
                int j = 0;
                for (int i = 0; i < V9SizeSq; i++)
                {
                    int row = i / V9Size;
                    int col = i % V9Size;
                    mesh.LiquidVerts.Add((xoffset + (col * GridPartSize)) * -1);
                    if (row < offsetY || row >= offsetY + height || col < offsetX || col >= offsetX + width)
                    {
                        // A vertex outside the stored rectangle: marked with the invalid height.
                        mesh.LiquidVerts.Add(InvalidLiquidHeight);
                    }
                    else
                    {
                        mesh.LiquidVerts.Add(liquidMap[j++]);
                    }

                    mesh.LiquidVerts.Add((yoffset + (row * GridPartSize)) * -1);
                }
            }
            else
            {
                for (int i = 0; i < V9SizeSq; i++)
                {
                    mesh.LiquidVerts.Add((xoffset + ((i % V9Size) * GridPartSize)) * -1);
                    mesh.LiquidVerts.Add(liquidLevel);
                    mesh.LiquidVerts.Add((yoffset + ((i / V9Size) * GridPartSize)) * -1);
                }
            }

            Span<int> indices = stackalloc int[3];
            for (int i = loopStart; i < loopEnd; i += loopInc)
            {
                for (int j = (int)Spot.Top; j <= (int)Spot.Bottom; j += (int)Spot.Bottom - (int)Spot.Top)
                {
                    HeightTriangle(i, (Spot)j, indices, liquid: true);
                    ltriangles.Add(indices[2] + count);
                    ltriangles.Add(indices[1] + count);
                    ltriangles.Add(indices[0] + count);
                }
            }
        }

        if (ltriangles.Count + ttriangles.Count == 0)
        {
            return false;
        }

        // Per half square (two terrain triangles and one liquid triangle), decide what to keep: terrain unless it is a
        // hole or under deep water, liquid unless it lies wholly under the terrain.
        // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:344-505
        List<float> lverts = mesh.LiquidVerts;
        float[] lvertsCopy = [.. lverts];
        List<float> tverts = mesh.SolidVerts;
        int lt = 0;
        int tt = 0;
        for (int i = loopStart; i < loopEnd; i += loopInc)
        {
            for (int j = 0; j < 2; j++)
            {
                bool useTerrain = true;
                bool useLiquid = true;
                int liquidType = MmapArea.None;
                if (!liquidTypeLoaded || lverts.Count == 0 || ltriangles.Count == 0)
                {
                    useLiquid = false;
                }
                else
                {
                    byte type = portion == Spot.Entire ? liquidFlags[((i / V8Size / 8) * 16) + (i % V8Size / 8)] : LiquidTypeWater;
                    if ((type & LiquidTypeDeepWater) != 0)
                    {
                        // Players cannot be here, so creatures should not be either.
                        useTerrain = false;
                        useLiquid = false;
                    }
                    else if ((type & (LiquidTypeWater | LiquidTypeOcean)) != 0)
                    {
                        liquidType = MmapArea.Water;
                    }
                    else if ((type & LiquidTypeMagma) != 0)
                    {
                        liquidType = MmapArea.Magma;
                    }
                    else if ((type & LiquidTypeSlime) != 0)
                    {
                        liquidType = MmapArea.Slime;
                    }
                    else
                    {
                        useLiquid = false;
                    }
                }

                if (ttriangles.Count == 0)
                {
                    useTerrain = false;
                }

                // The extraction loses the levels of the closing row and column; fill them in from the triangle's valid corners.
                if (useLiquid)
                {
                    float quadHeight = 0;
                    int validCount = 0;
                    for (int idx = 0; idx < 3; idx++)
                    {
                        float h = lvertsCopy[(ltriangles[lt + idx] * 3) + 1];
                        if (h != InvalidLiquidHeight && h < InvalidLiquidHeightMax)
                        {
                            quadHeight += h;
                            validCount++;
                        }
                    }

                    if (validCount is > 0 and < 3)
                    {
                        quadHeight /= validCount;
                        for (int idx = 0; idx < 3; idx++)
                        {
                            int vertex = (ltriangles[lt + idx] * 3) + 1;
                            if (lverts[vertex] == InvalidLiquidHeight || lverts[vertex] > InvalidLiquidHeightMax)
                            {
                                lverts[vertex] = quadHeight;
                            }
                        }
                    }

                    if (validCount == 0)
                    {
                        useLiquid = false;
                    }
                }

                if (useTerrain)
                {
                    useTerrain = !IsHole(i, holes);
                }

                if (useTerrain && useLiquid)
                {
                    float maxLLevel = InvalidLiquidHeight;
                    for (int x = 0; x < 3; x++)
                    {
                        float h = lverts[(ltriangles[lt + x] * 3) + 1];
                        if (maxLLevel < h)
                        {
                            maxLLevel = h;
                        }
                    }

                    float minTLevel = InvalidLiquidHeightMax;
                    for (int x = 0; x < 6; x++)
                    {
                        float h = tverts[(ttriangles[tt + x] * 3) + 1];
                        if (minTLevel > h)
                        {
                            minTLevel = h;
                        }
                    }

                    // The terrain stays even under liquid (the mesh is built on it); liquid under the terrain goes.
                    if (minTLevel > maxLLevel)
                    {
                        useLiquid = false;
                    }
                }

                if (useLiquid)
                {
                    mesh.LiquidType.Add(liquidType);
                    for (int k = 0; k < 3; k++)
                    {
                        mesh.LiquidTris.Add(ltriangles[lt + k]);
                    }
                }

                if (useTerrain)
                {
                    for (int k = 0; k < 6; k++)
                    {
                        mesh.SolidTris.Add(ttriangles[tt + k]);
                    }
                }

                lt += 3;
                tt += 6;
            }
        }

        return mesh.SolidTris.Count != 0 || mesh.LiquidTris.Count != 0;
    }

    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:50-80 (getLoopVars: the whole tile, or the row or column of squares facing the centre tile)
    private static (int Start, int End, int Inc) LoopVars(Spot portion) => portion switch
    {
        Spot.Top => (0, V8Size, 1),
        Spot.Left => (0, V8SizeSq - V8Size + 1, V8Size),
        Spot.Right => (V8Size - 1, V8SizeSq, V8Size),
        Spot.Bottom => (V8SizeSq - V8Size, V8SizeSq, 1),
        _ => (0, V8SizeSq, 1),
    };

    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:530-575 (getHeightTriangle: four triangles around the centre sample; liquid: two triangles per square)
    private static void HeightTriangle(int square, Spot triangle, Span<int> indices, bool liquid)
    {
        int rowOffset = square / V8Size;
        if (!liquid)
        {
            switch (triangle)
            {
                case Spot.Top:
                    indices[0] = square + rowOffset;
                    indices[1] = square + 1 + rowOffset;
                    indices[2] = V9SizeSq + square;
                    break;
                case Spot.Left:
                    indices[0] = square + rowOffset;
                    indices[1] = V9SizeSq + square;
                    indices[2] = square + V9Size + rowOffset;
                    break;
                case Spot.Right:
                    indices[0] = square + 1 + rowOffset;
                    indices[1] = square + V9Size + 1 + rowOffset;
                    indices[2] = V9SizeSq + square;
                    break;
                default:
                    indices[0] = V9SizeSq + square;
                    indices[1] = square + V9Size + 1 + rowOffset;
                    indices[2] = square + V9Size + rowOffset;
                    break;
            }
        }
        else if (triangle == Spot.Top)
        {
            indices[0] = square + rowOffset;
            indices[1] = square + 1 + rowOffset;
            indices[2] = square + V9Size + 1 + rowOffset;
        }
        else
        {
            indices[0] = square + rowOffset;
            indices[1] = square + V9Size + 1 + rowOffset;
            indices[2] = square + V9Size + rowOffset;
        }
    }

    // ref: vmangos@0e3ff01 contrib/mmap/src/TerrainBuilder.cpp:591-602
    private static bool IsHole(int square, ushort[] holes)
    {
        int row = square / 128;
        int col = square % 128;
        int cellRow = row / 8;
        int cellCol = col / 8;
        int holeRow = row % 8 / 2;
        int holeCol = (square - ((row * 128) + (cellCol * 8))) / 2;
        return (holes[(cellRow * 16) + cellCol] & HoleTabH[holeCol] & HoleTabV[holeRow]) != 0;
    }

    private static uint U32(byte[] file, int at) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at));

    private static float F32(byte[] file, int at) => BinaryPrimitives.ReadSingleLittleEndian(file.AsSpan(at));

    private static int Checked(byte[] file, long at, long length, string path)
        => at >= 0 && length >= 0 && at + length <= file.Length ? (int)at : throw new InvalidDataException($"'{path}' is cut off.");
}
