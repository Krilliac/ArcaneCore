using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// The terrain layers of the server view, built from the parsed <c>.map</c> files: <c>terrain</c> (the triangle fan
/// <see cref="TerrainTile.GetHeight"/> interpolates), <c>terrain.holes</c>, <c>terrain.noheight</c>, <c>area</c> and <c>liquid</c>.
/// <para>
/// Format facts, each checked against two sources (vmangos 0e3ff01 at vmangos@0e3ff01 and cmangos-classic at mangos-classic) and the real
/// data: tile (tx, ty) holds 128 x 128 height squares; V9 is the 129 x 129 grid of corner heights and V8 the 128 x 128 square centres
/// (src/game/Maps/GridMap.cpp:286-352, <c>getHeightFromFloat</c>: row from world X, column from world Y, <c>x = MAP_RESOLUTION * (32 - x / SIZE_OF_GRIDS)</c>
/// at lines 291-292). A square is four triangles around its centre: (V9[r][c], V9[r+1][c], centre) when x + y &lt; 1 and x &gt; y,
/// (V9[r][c], V9[r][c+1], centre) when x + y &lt; 1 and x &lt;= y, (V9[r+1][c], V9[r+1][c+1], centre) when x + y &gt;= 1 and x &gt; y, and
/// (V9[r][c+1], V9[r+1][c+1], centre) otherwise (lines 306-345). A hole bit covers a 2 x 2 block of squares through
/// <c>holetab_h</c> and <c>holetab_v</c> (lines 41-42 and 274-284), and only float-stored tiles consult it (lines 296-298 against the integer variants).
/// </para>
/// </summary>
public sealed class TerrainLayerSource : ServerLayerSource
{
    private const int Resolution = GridDefines.MapResolution;
    private const int CellsPerSide = GridDefines.MaxNumberOfCells;
    private const int SquaresPerCell = Resolution / CellsPerSide;

    /// <summary>Creates the source over <paramref name="data"/>.</summary>
    public TerrainLayerSource(ServerData data)
        : base(data, [LayerIds.Terrain, LayerIds.TerrainHoles, LayerIds.TerrainNoHeight, LayerIds.Area, LayerIds.Liquid])
    {
    }

    /// <summary>The colour key of a liquid type byte (precedence magma, slime, deep water, ocean, water), or null for an unknown bit pattern.</summary>
    /// <remarks>Bits are the server's <see cref="LiquidTypeFlags"/> (vmangos GridMapDefines.h:66-75 <c>MAP_LIQUID_TYPE_*</c>).</remarks>
    // ref: src/ArcaneCore.Game/Maps/Terrain/LiquidStatus.cs:31-41 LiquidTypeFlags magma 0x01, ocean 0x02, slime 0x04, water 0x08, deep 0x10
    // ref: vmangos@0e3ff01 src/game/Maps/GridMapDefines.h:67-74 MAP_LIQUID_TYPE_MAGMA/OCEAN/SLIME/WATER/DEEP_WATER
    public static string? LiquidColorKey(byte type)
    {
        var flags = (LiquidTypeFlags)type;
        if ((flags & LiquidTypeFlags.Magma) != 0)
        {
            return ColorKeys.Magma;
        }

        if ((flags & LiquidTypeFlags.Slime) != 0)
        {
            return ColorKeys.Slime;
        }

        if ((flags & LiquidTypeFlags.DeepWater) != 0)
        {
            return ColorKeys.DeepWater;
        }

        if ((flags & LiquidTypeFlags.Ocean) != 0)
        {
            return ColorKeys.Ocean;
        }

        return (flags & (LiquidTypeFlags.Water | LiquidTypeFlags.WmoWater)) != 0 ? ColorKeys.Water : null;
    }

    /// <summary>The flag names of a liquid type byte, joined with <c>|</c> (unnamed bits as hex).</summary>
    public static string LiquidTypeName(byte type)
    {
        var flags = (LiquidTypeFlags)type;
        var names = new List<string>();
        foreach (LiquidTypeFlags flag in new[] { LiquidTypeFlags.Magma, LiquidTypeFlags.Ocean, LiquidTypeFlags.Slime, LiquidTypeFlags.Water, LiquidTypeFlags.DeepWater, LiquidTypeFlags.WmoWater })
        {
            if ((flags & flag) != 0)
            {
                names.Add(flag.ToString());
            }
        }

        byte known = (byte)(LiquidTypeFlags.Magma | LiquidTypeFlags.Ocean | LiquidTypeFlags.Slime | LiquidTypeFlags.Water | LiquidTypeFlags.DeepWater | LiquidTypeFlags.WmoWater);
        if ((type & ~known) != 0)
        {
            names.Add(Hex((uint)(type & ~known)));
        }

        return names.Count == 0 ? "None" : string.Join('|', names);
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<TileKey> ListTiles(string layerId, uint mapId) => Data.TerrainTiles(mapId);

    /// <inheritdoc />
    protected override TileStatus Peek(string layerId, TileKey tile)
    {
        if (!tile.IsInGrid)
        {
            return new TileStatus(layerId, tile, TileState.NotApplicable, "terrain files are per grid tile");
        }

        bool cached = Data.TryPeekTerrain(tile, out ParsedFile<TerrainTile> parsed);
        return StatusOf(layerId, tile, Data.TerrainFileExists(tile), cached, parsed, ".map file");
    }

    /// <inheritdoc />
    protected override LayerBuild BuildTile(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        if (!tile.IsInGrid)
        {
            return NotApplicable(layerId, tile, "terrain files are per grid tile; the map's global tile has none");
        }

        ParsedFile<TerrainTile> file = Data.TerrainFile(tile);
        if (!file.IsOk)
        {
            return FromFailure(layerId, tile, file);
        }

        TerrainTile terrain = file.Value;
        string name = file.Detail ?? TerrainTile.FileName(tile.MapId, tile.X, tile.Y);
        return layerId switch
        {
            LayerIds.Terrain => BuildSurface(tile, terrain, name),
            LayerIds.TerrainHoles => BuildHoles(tile, terrain, name),
            LayerIds.TerrainNoHeight => BuildNoHeight(tile, terrain, name),
            LayerIds.Area => BuildAreas(tile, terrain, name, cancellationToken),
            _ => BuildLiquid(tile, terrain, name),
        };
    }

    internal static string StorageName(TerrainTile terrain) => terrain.Storage switch
    {
        TerrainTile.HeightStorage.Float => "float",
        TerrainTile.HeightStorage.Packed => "packed",
        _ => "none",
    };

    internal static bool IsUsable(float height) => float.IsFinite(height) && height > TerrainTile.InvalidHeight;

    /// <summary>A corner height for drawing outlines: the sample, else the grid height when valid, else 0.</summary>
    internal static double DrawHeight(TerrainTile terrain, int row, int col)
    {
        float h = terrain.CornerHeight(row, col);
        if (IsUsable(h))
        {
            return h;
        }

        return IsUsable(terrain.GridHeight) ? terrain.GridHeight : 0;
    }

    private static LayerBuild BuildSurface(TileKey tile, TerrainTile terrain, string file)
    {
        if (terrain.Storage == TerrainTile.HeightStorage.Flat)
        {
            return LayerBuild.Empty(LayerIds.Terrain, tile, $"{file} stores no heights; the server answers one grid height there (see {LayerIds.TerrainNoHeight})");
        }

        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.Terrain, tile, origin);
        var corner = new int[(Resolution + 1) * (Resolution + 1)];
        var centre = new int[Resolution * Resolution];
        Array.Fill(corner, -1);
        Array.Fill(centre, -1);

        uint Corner(int r, int c)
        {
            ref int slot = ref corner[(r * (Resolution + 1)) + c];
            if (slot < 0)
            {
                slot = (int)builder.AddVertex(origin.X - (r * WorldGrid.SquareSize), origin.Y - (c * WorldGrid.SquareSize), terrain.CornerHeight(r, c));
            }

            return (uint)slot;
        }

        uint Centre(int r, int c)
        {
            ref int slot = ref centre[(r * Resolution) + c];
            if (slot < 0)
            {
                slot = (int)builder.AddVertex(
                    origin.X - ((r + 0.5) * WorldGrid.SquareSize),
                    origin.Y - ((c + 0.5) * WorldGrid.SquareSize),
                    terrain.CentreHeight(r, c));
            }

            return (uint)slot;
        }

        int holes = 0;
        int invalid = 0;
        int maskedButDrawn = 0;
        for (int r = 0; r < Resolution; r++)
        {
            for (int c = 0; c < Resolution; c++)
            {
                // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:472-480 hole bit (row % 8 / 2) * 4 + (col % 8 / 2) of cell (row / 8, col / 8), checked by float tiles only (:244)
                // ref: vmangos@0e3ff01 src/game/Maps/GridMap.cpp:274-284 isHole with holetab_h / holetab_v, called from getHeightFromFloat only (:301)
                if (terrain.IsServerHole(r, c))
                {
                    holes++;
                    continue;
                }

                if (terrain.Storage == TerrainTile.HeightStorage.Packed && terrain.HoleMaskCovers(r, c))
                {
                    maskedButDrawn++;
                }

                if (!IsUsable(terrain.CornerHeight(r, c)) || !IsUsable(terrain.CornerHeight(r + 1, c)) || !IsUsable(terrain.CornerHeight(r, c + 1))
                    || !IsUsable(terrain.CornerHeight(r + 1, c + 1)) || !IsUsable(terrain.CentreHeight(r, c)))
                {
                    invalid++;
                    continue;
                }

                // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:483-503 GetHeightFromFloat picks one of four triangles around h5 = 2 * V8
                // ref: vmangos@0e3ff01 src/game/Maps/GridMap.cpp:286-345 getHeightFromFloat (same four cases; the plane through h1, h2 and h5 / 2)
                // Upward-facing winding (world X north, Y west, Z up): the server's four triangles, each as (corner, corner, centre).
                uint a = Corner(r, c);
                uint b = Corner(r + 1, c);
                uint cc = Corner(r, c + 1);
                uint d = Corner(r + 1, c + 1);
                uint m = Centre(r, c);
                builder.AddTriangle(a, b, m);
                builder.AddTriangle(a, m, cc);
                builder.AddTriangle(b, d, m);
                builder.AddTriangle(cc, m, d);
            }
        }

        builder.DescribeAttribute(0, () => new JsonObject
        {
            ["kind"] = "terrain",
            ["file"] = file,
            ["storage"] = StorageName(terrain),
            ["note"] = "four triangles per height square around the V8 centre sample, as TerrainTile.GetHeight interpolates",
        });
        builder.Stats["file"] = file;
        builder.Stats["storage"] = StorageName(terrain);
        builder.Stats["squares"] = Resolution * Resolution;
        builder.Stats["holeSquares"] = holes;
        builder.Stats["invalidHeightSquares"] = invalid;
        builder.Stats["holeMaskSquaresIgnoredByServer"] = maskedButDrawn;
        builder.Stats["triangles"] = builder.TriangleCount;
        builder.Stats["vertices"] = builder.VertexCount;
        return LayerBuild.Of(LayerIds.Terrain, tile, [builder.Build()], file);
    }

    private static LayerBuild BuildHoles(TileKey tile, TerrainTile terrain, string file)
    {
        var hole = new bool[Resolution, Resolution];
        int holes = 0;
        int masked = 0;
        for (int r = 0; r < Resolution; r++)
        {
            for (int c = 0; c < Resolution; c++)
            {
                if (terrain.HoleMaskCovers(r, c))
                {
                    masked++;
                }

                if (terrain.IsServerHole(r, c))
                {
                    hole[r, c] = true;
                    holes++;
                }
            }
        }

        if (holes == 0)
        {
            string why = masked == 0
                ? "the hole mask is empty"
                : $"the hole mask covers {masked} squares but the tile stores {StorageName(terrain)} heights, which the server never checks against the mask";
            return LayerBuild.Empty(LayerIds.TerrainHoles, tile, why);
        }

        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.TerrainHoles, tile, origin);
        var corner = new int[(Resolution + 1) * (Resolution + 1)];
        Array.Fill(corner, -1);

        uint Corner(int r, int c)
        {
            ref int slot = ref corner[(r * (Resolution + 1)) + c];
            if (slot < 0)
            {
                slot = (int)builder.AddVertex(origin.X - (r * WorldGrid.SquareSize), origin.Y - (c * WorldGrid.SquareSize), DrawHeight(terrain, r, c));
            }

            return (uint)slot;
        }

        bool IsHoleAt(int r, int c) => r is >= 0 and < Resolution && c is >= 0 and < Resolution && hole[r, c];

        for (int r = 0; r < Resolution; r++)
        {
            for (int c = 0; c < Resolution; c++)
            {
                if (!hole[r, c])
                {
                    continue;
                }

                uint a = Corner(r, c);
                uint b = Corner(r + 1, c);
                uint cc = Corner(r, c + 1);
                uint d = Corner(r + 1, c + 1);
                builder.AddTriangle(a, b, d, 0, edges: false);
                builder.AddTriangle(a, d, cc, 0, edges: false);

                // Outline: an edge is drawn when the square across it is not a hole.
                if (!IsHoleAt(r - 1, c))
                {
                    builder.AddLine(a, cc);
                }

                if (!IsHoleAt(r + 1, c))
                {
                    builder.AddLine(b, d);
                }

                if (!IsHoleAt(r, c - 1))
                {
                    builder.AddLine(a, b);
                }

                if (!IsHoleAt(r, c + 1))
                {
                    builder.AddLine(cc, d);
                }
            }
        }

        builder.DescribeAttribute(0, () => new JsonObject
        {
            ["kind"] = "terrain hole",
            ["file"] = file,
            ["note"] = "squares TerrainTile.GetHeight answers InvalidHeightValue for (float tiles only; a hole bit covers 2 x 2 squares)",
        });
        builder.Stats["file"] = file;
        builder.Stats["holeSquares"] = holes;
        builder.Stats["holeMaskSquares"] = masked;
        return LayerBuild.Of(LayerIds.TerrainHoles, tile, [builder.Build()], file);
    }

    private static LayerBuild BuildNoHeight(TileKey tile, TerrainTile terrain, string file)
    {
        if (terrain.Storage != TerrainTile.HeightStorage.Flat)
        {
            return LayerBuild.Empty(LayerIds.TerrainNoHeight, tile, $"{file} stores {StorageName(terrain)} heights");
        }

        WorldPoint origin = WorldGrid.Origin(tile);
        bool valid = IsUsable(terrain.GridHeight);
        double z = valid ? terrain.GridHeight : 0;
        var builder = new GeometryBuilder(LayerIds.TerrainNoHeight, tile, origin);
        double far = WorldGrid.TileSize;
        uint a = builder.AddVertex(origin.X, origin.Y, z);
        uint b = builder.AddVertex(origin.X - far, origin.Y, z);
        uint c = builder.AddVertex(origin.X - far, origin.Y - far, z);
        uint d = builder.AddVertex(origin.X, origin.Y - far, z);
        builder.AddTriangle(a, b, c, 0, edges: false);
        builder.AddTriangle(a, c, d, 0, edges: false);
        builder.AddLine(a, b);
        builder.AddLine(b, c);
        builder.AddLine(c, d);
        builder.AddLine(d, a);
        builder.DescribeAttribute(0, () => new JsonObject
        {
            ["kind"] = "tile without heights",
            ["file"] = file,
            ["gridHeight"] = terrain.GridHeight,
            ["gridHeightUsable"] = valid,
            ["note"] = valid
                ? "the server answers this one height over the whole tile"
                : "the grid height is the invalid marker: the server answers no height here; the outline is drawn at z 0",
        });
        builder.Stats["file"] = file;
        builder.Stats["gridHeight"] = terrain.GridHeight;
        builder.Stats["gridHeightUsable"] = valid;
        return LayerBuild.Of(LayerIds.TerrainNoHeight, tile, [builder.Build()], file);
    }

    private LayerBuild BuildAreas(TileKey tile, TerrainTile terrain, string file, CancellationToken cancellationToken)
    {
        // Cell (i, j) is the 16 x 16 area map entry the server reads for a position (GetAreaFlag: i from world X, j from world Y), so its
        // flag is the server's own answer at the cell centre; a tile with one area for the whole tile answers the same flag everywhere.
        WorldPoint origin = WorldGrid.Origin(tile);
        double cell = WorldGrid.TileSize / CellsPerSide;
        var flags = new ushort[CellsPerSide, CellsPerSide];
        var distinct = new SortedSet<ushort>();
        for (int i = 0; i < CellsPerSide; i++)
        {
            for (int j = 0; j < CellsPerSide; j++)
            {
                // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:326 GetAreaFlag reads the 16 x 16 area map by cell; vmangos@0e3ff01 src/game/Maps/GridMap.cpp:257-272 getArea
                flags[i, j] = terrain.GetAreaFlag((float)(origin.X - ((i + 0.5) * cell)), (float)(origin.Y - ((j + 0.5) * cell)));
                distinct.Add(flags[i, j]);
            }
        }

        // Borders to the tiles beyond row 16 and column 16 are drawn here and the others by the neighbours, so a shared border is drawn once.
        ushort? Beyond(int dx, int dy, int i, int j, ref int unresolved)
        {
            var other = new TileKey(tile.MapId, tile.X + dx, tile.Y + dy);
            if (!other.IsInGrid)
            {
                return null;
            }

            ParsedFile<TerrainTile> parsed = Data.TerrainFile(other);
            if (!parsed.IsOk)
            {
                unresolved++;
                return null;
            }

            WorldPoint o = WorldGrid.Origin(other);
            return parsed.Value.GetAreaFlag((float)(o.X - ((i + 0.5) * cell)), (float)(o.Y - ((j + 0.5) * cell)));
        }

        var builder = new GeometryBuilder(LayerIds.Area, tile, origin);
        var neighbours = new Dictionary<ushort, SortedSet<ushort>>();
        int borders = 0;
        int unresolved = 0;

        void Border(ushort a, ushort b, int rowFrom, int colFrom, int rowTo, int colTo)
        {
            ushort owner = Math.Max(a, b);
            ushort other = Math.Min(a, b);
            if (!neighbours.TryGetValue(owner, out SortedSet<ushort>? set))
            {
                neighbours[owner] = set = [];
            }

            set.Add(other);
            uint previous = 0;
            int steps = Math.Max(Math.Abs(rowTo - rowFrom), Math.Abs(colTo - colFrom));
            for (int k = 0; k <= steps; k++)
            {
                int r = rowFrom + (steps == 0 ? 0 : (rowTo - rowFrom) * k / steps);
                int c = colFrom + (steps == 0 ? 0 : (colTo - colFrom) * k / steps);
                uint v = builder.AddVertex(origin.X - (r * WorldGrid.SquareSize), origin.Y - (c * WorldGrid.SquareSize), DrawHeight(terrain, r, c));
                if (k > 0)
                {
                    builder.AddLine(previous, v, owner);
                }

                previous = v;
            }

            borders++;
        }

        for (int i = 0; i < CellsPerSide; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < CellsPerSide; j++)
            {
                // Border between (i, j) and (i + 1, j): a line of constant world X at height-grid row (i + 1) * 8.
                ushort? below = i + 1 < CellsPerSide ? flags[i + 1, j] : Beyond(1, 0, 0, j, ref unresolved);
                if (below is { } b1 && b1 != flags[i, j])
                {
                    Border(flags[i, j], b1, (i + 1) * SquaresPerCell, j * SquaresPerCell, (i + 1) * SquaresPerCell, (j + 1) * SquaresPerCell);
                }

                // Border between (i, j) and (i, j + 1): a line of constant world Y at height-grid column (j + 1) * 8.
                ushort? right = j + 1 < CellsPerSide ? flags[i, j + 1] : Beyond(0, 1, i, 0, ref unresolved);
                if (right is { } b2 && b2 != flags[i, j])
                {
                    Border(flags[i, j], b2, i * SquaresPerCell, (j + 1) * SquaresPerCell, (i + 1) * SquaresPerCell, (j + 1) * SquaresPerCell);
                }
            }
        }

        builder.Stats["file"] = file;
        builder.Stats["distinctAreaFlags"] = distinct.Count;
        builder.Stats["borderSegments"] = borders;
        builder.Stats["unresolvedTileBorders"] = unresolved;
        if (builder.IsEmpty)
        {
            string detail = distinct.Count == 1
                ? $"the whole tile has area flag {distinct.Min}"
                : "no cell borders a different area flag on this tile's drawn sides";
            return LayerBuild.Empty(LayerIds.Area, tile, detail);
        }

        foreach ((ushort owner, SortedSet<ushort> others) in neighbours)
        {
            builder.DescribeAttribute(owner, () => new JsonObject
            {
                ["kind"] = "area border",
                ["areaFlag"] = owner,
                ["borders"] = new JsonArray([.. others.Select(o => (JsonNode)o)]),
                ["cells"] = Enumerable.Range(0, CellsPerSide * CellsPerSide).Count(n => flags[n / CellsPerSide, n % CellsPerSide] == owner),
                ["note"] = "the higher area flag of the two cells it separates owns the border; AreaTable.dbc names are not read by this source",
            });
        }

        return LayerBuild.Of(LayerIds.Area, tile, [builder.Build()], file);
    }

    private static LayerBuild BuildLiquid(TileKey tile, TerrainTile terrain, string file)
    {
        WorldPoint origin = WorldGrid.Origin(tile);
        var builder = new GeometryBuilder(LayerIds.Liquid, tile, origin);
        var shared = new Dictionary<(int Row, int Col, int Level), uint>();
        var described = new SortedDictionary<uint, (byte Type, ushort Entry, int Squares)>();
        int squares = 0;
        int belowGround = 0;
        int noLevel = 0;

        uint Vertex(int r, int c, float level)
        {
            (int, int, int) key = (r, c, BitConverter.SingleToInt32Bits(level));
            if (!shared.TryGetValue(key, out uint index))
            {
                shared[key] = index = builder.AddVertex(origin.X - (r * WorldGrid.SquareSize), origin.Y - (c * WorldGrid.SquareSize), level);
            }

            return index;
        }

        for (int r = 0; r < Resolution; r++)
        {
            for (int c = 0; c < Resolution; c++)
            {
                if (!terrain.TryGetLiquidSquare(r, c, out byte type, out ushort entry, out float level))
                {
                    continue;
                }

                if (!IsUsable(level))
                {
                    noLevel++;
                    continue;
                }

                // TerrainTile.GetLiquidStatus answers "no water" where the surface is below the ground at the query point. A square whose
                // every height sample is above the surface never has water at any point, so it is counted and not drawn.
                if (!terrain.IsServerHole(r, c) && terrain.Storage != TerrainTile.HeightStorage.Flat
                    && Math.Min(
                        Math.Min(terrain.CornerHeight(r, c), terrain.CornerHeight(r + 1, c)),
                        Math.Min(Math.Min(terrain.CornerHeight(r, c + 1), terrain.CornerHeight(r + 1, c + 1)), terrain.CentreHeight(r, c))) > level)
                {
                    belowGround++;
                    continue;
                }

                uint attribute = ((uint)entry << 8) | type;
                described[attribute] = described.TryGetValue(attribute, out (byte Type, ushort Entry, int Squares) known)
                    ? (type, entry, known.Squares + 1)
                    : (type, entry, 1);
                uint a = Vertex(r, c, level);
                uint b = Vertex(r + 1, c, level);
                uint cc = Vertex(r, c + 1, level);
                uint d = Vertex(r + 1, c + 1, level);
                builder.AddTriangle(a, b, d, attribute, edges: false);
                builder.AddTriangle(a, d, cc, attribute, edges: false);
                builder.AddEdge(a, b, attribute);
                builder.AddEdge(b, d, attribute);
                builder.AddEdge(d, cc, attribute);
                builder.AddEdge(cc, a, attribute);
                squares++;
            }
        }

        foreach ((uint attribute, (byte type, ushort entry, int count) ) in described)
        {
            builder.DescribeAttribute(attribute, () => new JsonObject
            {
                ["kind"] = "liquid",
                ["type"] = type,
                ["typeName"] = LiquidTypeName(type),
                ["liquidEntry"] = entry,
                ["colorKey"] = LiquidColorKey(type),
                ["squares"] = count,
            });
        }

        builder.Stats["file"] = file;
        builder.Stats["liquidSquares"] = squares;
        builder.Stats["squaresBelowGroundNotDrawn"] = belowGround;
        builder.Stats["squaresWithoutLevel"] = noLevel;
        return LayerBuild.Of(LayerIds.Liquid, tile, [builder.Build()], builder.IsEmpty ? $"{file} has no liquid the server would report" : file);
    }
}
