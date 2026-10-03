using System.Buffers.Binary;
using ArcaneCore.Game.Maps.Grid;

namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// One terrain tile (one grid's worth of ground): the parsed contents of a
/// <c>maps/MMMXXYY.map</c> file written by the vmangos / cmangos-classic map extractor, and the
/// height, area and liquid lookups vmangos <c>GridMap</c> performs on it (GridMap.cpp).
/// <para>
/// File format ("z1.4", identical in vmangos and cmangos-classic GridMapDefines.h and the
/// extractor's System.cpp), all little-endian:
/// </para>
/// <list type="bullet">
/// <item>Header — 10 × u32: <c>"MAPS"</c>, <c>"z1.4"</c>, then offset/size pairs of the area,
/// height, liquid and holes sections (an offset of 0 means the section is absent).</item>
/// <item>Area — u32 <c>"AREA"</c>, u16 flags (0x1 = no per-cell areas), u16 grid area, then
/// u16[16 × 16] area flags unless flag 0x1.</item>
/// <item>Height — u32 <c>"MHGT"</c>, u32 flags (0x1 no height, 0x2 stored as u16, 0x4 stored
/// as u8), f32 grid height, f32 grid max height, then V9[129 × 129] followed by V8[128 × 128]
/// as f32 / u16 / u8.</item>
/// <item>Liquid — u32 <c>"MLIQ"</c>, u8 flags (0x1 no type, 0x2 no height), u8 global liquid
/// flags, u16 global liquid entry, u8 offset X, u8 offset Y, u8 width, u8 height, f32 level;
/// then u16 entry[16 × 16] and u8 flags[16 × 16] unless no type, then f32[width × height]
/// levels unless no height.</item>
/// <item>Holes — u16[16][16].</item>
/// </list>
/// <para>Immutable after parsing, so it may be read from any thread.</para>
/// </summary>
public sealed class TerrainTile
{
    /// <summary><c>INVALID_HEIGHT_VALUE</c> (GridMap.h): the height reported where there is no data.</summary>
    public const float InvalidHeightValue = -200000.0f;

    /// <summary><c>INVALID_HEIGHT</c>: heights at or below this are "no data".</summary>
    public const float InvalidHeight = -100000.0f;

    private const uint MapMagic = 0x5350414D;        // "MAPS"
    private const uint MapVersionMagic = 0x342E317A; // "z1.4"
    private const uint AreaMagic = 0x41455241;       // "AREA"
    private const uint HeightMagic = 0x5447484D;     // "MHGT"
    private const uint LiquidMagic = 0x51494C4D;     // "MLIQ"

    private const ushort MapAreaNoArea = 0x0001;
    private const uint MapHeightNoHeight = 0x0001;
    private const uint MapHeightAsInt16 = 0x0002;
    private const uint MapHeightAsInt8 = 0x0004;
    private const byte MapLiquidNoType = 0x01;
    private const byte MapLiquidNoHeight = 0x02;

    private const int Resolution = GridDefines.MapResolution;
    private const float GridSize = GridDefines.SizeOfGrids;

    // vmangos GridMap.cpp holetab_h / holetab_v
    private static readonly ushort[] HoleTabH = [0x1111, 0x2222, 0x4444, 0x8888];
    private static readonly ushort[] HoleTabV = [0x000F, 0x00F0, 0x0F00, 0xF000];

    private readonly ushort[]? _areaMap;
    private readonly ushort _gridArea;

    private readonly HeightStorage _heightStorage;
    private readonly float _gridHeight = InvalidHeightValue;
    private readonly float _gridIntHeightMultiplier;
    private readonly float[]? _v9;
    private readonly float[]? _v8;
    private readonly int[]? _v9Int; // u16 or u8 samples, widened at load
    private readonly int[]? _v8Int;
    private readonly ushort[] _holes = new ushort[16 * 16];

    private readonly ushort _liquidGlobalEntry;
    private readonly byte _liquidGlobalFlags;
    private readonly int _liquidOffX;
    private readonly int _liquidOffY;
    private readonly int _liquidWidth;
    private readonly int _liquidHeight;
    private readonly float _liquidLevel = InvalidHeightValue;
    private readonly ushort[]? _liquidEntry;
    private readonly byte[]? _liquidFlags;
    private readonly float[]? _liquidMap;

    /// <summary>
    /// A tile without data (vmangos' freshly constructed <c>GridMap</c>, also used for a missing
    /// file): flat at <see cref="InvalidHeightValue"/>, area flag 0, no liquid.
    /// </summary>
    public static TerrainTile Empty { get; } = new();

    private TerrainTile()
    {
        _heightStorage = HeightStorage.Flat;
    }

    private TerrainTile(ReadOnlySpan<byte> file)
    {
        ReadHeader(file, out uint areaOffset, out uint heightOffset, out uint liquidOffset, out uint holesOffset);

        // Section order follows vmangos GridMap::loadData: area, holes, height, liquid.
        if (areaOffset != 0)
        {
            var area = new Reader(file, areaOffset);
            if (area.U32() != AreaMagic)
            {
                throw new InvalidDataException("bad area section magic");
            }

            ushort flags = area.U16();
            _gridArea = area.U16();
            if ((flags & MapAreaNoArea) == 0)
            {
                _areaMap = area.U16Array(16 * 16);
            }
        }

        if (holesOffset != 0)
        {
            new Reader(file, holesOffset).U16Array(16 * 16).CopyTo(_holes, 0);
        }

        _heightStorage = HeightStorage.Flat;
        if (heightOffset != 0)
        {
            var height = new Reader(file, heightOffset);
            if (height.U32() != HeightMagic)
            {
                throw new InvalidDataException("bad height section magic");
            }

            uint flags = height.U32();
            _gridHeight = height.F32();
            float gridMaxHeight = height.F32();
            if ((flags & MapHeightNoHeight) == 0)
            {
                if ((flags & MapHeightAsInt16) != 0)
                {
                    _v9Int = Widen(height.U16Array(129 * 129));
                    _v8Int = Widen(height.U16Array(128 * 128));
                    _gridIntHeightMultiplier = (gridMaxHeight - _gridHeight) / 65535;
                    _heightStorage = HeightStorage.Packed;
                }
                else if ((flags & MapHeightAsInt8) != 0)
                {
                    _v9Int = Widen(height.Bytes(129 * 129));
                    _v8Int = Widen(height.Bytes(128 * 128));
                    _gridIntHeightMultiplier = (gridMaxHeight - _gridHeight) / 255;
                    _heightStorage = HeightStorage.Packed;
                }
                else
                {
                    _v9 = height.F32Array(129 * 129);
                    _v8 = height.F32Array(128 * 128);
                    _heightStorage = HeightStorage.Float;
                }
            }
        }

        if (liquidOffset != 0)
        {
            var liquid = new Reader(file, liquidOffset);
            if (liquid.U32() != LiquidMagic)
            {
                throw new InvalidDataException("bad liquid section magic");
            }

            byte flags = liquid.U8();
            _liquidGlobalFlags = liquid.U8();
            _liquidGlobalEntry = liquid.U16();
            _liquidOffX = liquid.U8();
            _liquidOffY = liquid.U8();
            _liquidWidth = liquid.U8();
            _liquidHeight = liquid.U8();
            _liquidLevel = liquid.F32();

            if ((flags & MapLiquidNoType) == 0)
            {
                _liquidEntry = liquid.U16Array(16 * 16);
                _liquidFlags = liquid.Bytes(16 * 16);
            }

            if ((flags & MapLiquidNoHeight) == 0)
            {
                _liquidMap = liquid.F32Array(_liquidWidth * _liquidHeight);
            }
        }
    }

    private enum HeightStorage
    {
        Flat,
        Float,
        Packed,
    }

    /// <summary>Whether the tile came from a file (false for <see cref="Empty"/>).</summary>
    public bool HasData => !ReferenceEquals(this, Empty);

    /// <summary>
    /// Parse a <c>.map</c> file. Throws <see cref="InvalidDataException"/> for a wrong magic or
    /// version (vmangos: "non-compatible version") or a truncated section.
    /// </summary>
    public static TerrainTile Parse(ReadOnlySpan<byte> file)
    {
        try
        {
            return new TerrainTile(file);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("truncated .map file", ex);
        }
    }

    /// <summary>
    /// The extractor's file name for a tile (vmangos TerrainInfo::LoadMapAndVMap and
    /// cmangos-classic agree): <c>maps/%03u%02u%02u.map</c> of map id, tile X, tile Y.
    /// </summary>
    public static string FileName(uint mapId, int tileX, int tileY) => $"{mapId:D3}{tileX:D2}{tileY:D2}.map";

    /// <summary>
    /// The tile holding a world position (vmangos <c>TerrainInfo::GetGrid</c>: tile X from
    /// world X = <c>(int)(32 - x / SIZE_OF_GRIDS)</c>, likewise Y), or null when outside the map.
    /// </summary>
    public static (int X, int Y)? TileOf(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return null;
        }

        int tx = (int)(32 - (x / GridSize));
        int ty = (int)(32 - (y / GridSize));
        return tx is >= 0 and < GridDefines.MaxNumberOfGrids && ty is >= 0 and < GridDefines.MaxNumberOfGrids ? (tx, ty) : null;
    }

    /// <summary>The terrain tile matching a grid (vmangos <c>Map::UnloadGrid</c>: tile = 63 − grid, per axis).</summary>
    public static (int X, int Y) TileOf(GridCoord grid)
        => (GridDefines.MaxNumberOfGrids - 1 - grid.X, GridDefines.MaxNumberOfGrids - 1 - grid.Y);

    /// <summary>The AreaTable explore flag at a position (vmangos <c>GridMap::getArea</c>).</summary>
    public ushort GetAreaFlag(float x, float y)
    {
        if (_areaMap is null)
        {
            return _gridArea;
        }

        x = 16 * (32 - (x / GridSize));
        y = 16 * (32 - (y / GridSize));
        int lx = (int)x & 15;
        int ly = (int)y & 15;
        return _areaMap[(lx * 16) + ly];
    }

    /// <summary>The ground height at a position (vmangos <c>GridMap::getHeight</c>).</summary>
    public float GetHeight(float x, float y) => _heightStorage switch
    {
        HeightStorage.Float => GetHeightFromFloat(x, y),
        HeightStorage.Packed => GetHeightFromPacked(x, y),
        _ => _gridHeight,
    };

    /// <summary>The liquid surface height at a position (vmangos <c>GridMap::getLiquidLevel</c>).</summary>
    public float GetLiquidLevel(float x, float y)
    {
        if (_liquidMap is null)
        {
            return _liquidLevel;
        }

        x = Resolution * (32 - (x / GridSize));
        y = Resolution * (32 - (y / GridSize));
        int cx = ((int)x & (Resolution - 1)) - _liquidOffY;
        int cy = ((int)y & (Resolution - 1)) - _liquidOffX;
        if (cx < 0 || cx >= _liquidHeight || cy < 0 || cy >= _liquidWidth)
        {
            return InvalidHeightValue;
        }

        return _liquidMap[(cx * _liquidWidth) + cy];
    }

    /// <summary>The liquid kind at a position (vmangos <c>GridMap::getTerrainType</c>).</summary>
    public LiquidTypeFlags GetTerrainType(float x, float y)
    {
        if (_liquidFlags is null)
        {
            return (LiquidTypeFlags)_liquidGlobalFlags;
        }

        x = 16 * (32 - (x / GridSize));
        y = 16 * (32 - (y / GridSize));
        int lx = (int)x & 15;
        int ly = (int)y & 15;
        return (LiquidTypeFlags)_liquidFlags[(lx * 16) + ly];
    }

    /// <summary>
    /// Where a position is relative to the liquid (vmangos <c>GridMap::getLiquidStatus</c>).
    /// The liquid kind is the file's flags: vmangos remaps it through LiquidType.dbc when that
    /// store is loaded, which ArcaneCore does not have (docs/areas/grid-terrain.md).
    /// </summary>
    /// <param name="requiredType">Only these liquid kinds count (None = any).</param>
    public LiquidStatus GetLiquidStatus(float x, float y, float z, LiquidTypeFlags requiredType, out LiquidData data)
    {
        data = default;
        if (_liquidFlags is null && _liquidGlobalFlags == 0)
        {
            return LiquidStatus.NoWater;
        }

        float cx = Resolution * (32 - (x / GridSize));
        float cy = Resolution * (32 - (y / GridSize));
        int xInt = (int)cx & (Resolution - 1);
        int yInt = (int)cy & (Resolution - 1);

        int idx = ((xInt >> 3) * 16) + (yInt >> 3);
        byte type = _liquidFlags is not null ? _liquidFlags[idx] : _liquidGlobalFlags;
        uint entry = _liquidEntry is not null ? _liquidEntry[idx] : _liquidGlobalEntry;
        if (type == 0)
        {
            return LiquidStatus.NoWater;
        }

        if (requiredType != LiquidTypeFlags.None && ((byte)requiredType & type) == 0)
        {
            return LiquidStatus.NoWater;
        }

        int lx = xInt - _liquidOffY;
        if (lx < 0 || lx >= _liquidHeight)
        {
            return LiquidStatus.NoWater;
        }

        int ly = yInt - _liquidOffX;
        if (ly < 0 || ly >= _liquidWidth)
        {
            return LiquidStatus.NoWater;
        }

        float liquidLevel = _liquidMap is not null ? _liquidMap[(lx * _liquidWidth) + ly] : _liquidLevel;
        float groundLevel = GetHeight(x, y);
        if (liquidLevel < groundLevel || z < groundLevel - 2)
        {
            return LiquidStatus.NoWater;
        }

        data = new LiquidData(entry, (LiquidTypeFlags)type, liquidLevel, groundLevel);

        // "For speed check as int values" — C++ int() truncates toward zero, as does (int).
        int delta = (int)((liquidLevel - z) * 10);
        if (delta > 20)
        {
            return LiquidStatus.UnderWater;
        }

        if (delta > 0)
        {
            return LiquidStatus.InWater;
        }

        return delta > -1 ? LiquidStatus.WaterWalk : LiquidStatus.AboveWater;
    }

    private static void ReadHeader(ReadOnlySpan<byte> file, out uint area, out uint height, out uint liquid, out uint holes)
    {
        if (file.Length < 40)
        {
            throw new InvalidDataException(".map file shorter than its header");
        }

        uint mapMagic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        if (mapMagic != MapMagic || version != MapVersionMagic)
        {
            throw new InvalidDataException("not a z1.4 .map file (vmangos: non-compatible version)");
        }

        area = BinaryPrimitives.ReadUInt32LittleEndian(file[8..]);
        height = BinaryPrimitives.ReadUInt32LittleEndian(file[16..]);
        liquid = BinaryPrimitives.ReadUInt32LittleEndian(file[24..]);
        holes = BinaryPrimitives.ReadUInt32LittleEndian(file[32..]);
    }

    /// <summary>vmangos <c>GridMap::isHole</c>: holes only matter for float height maps.</summary>
    private bool IsHole(int row, int col)
    {
        int cellRow = row / 8;
        int cellCol = col / 8;
        int holeRow = row % 8 / 2;
        int holeCol = (col - (cellCol * 8)) / 2;
        ushort hole = _holes[(cellRow * 16) + cellCol];
        return (hole & HoleTabH[holeCol] & HoleTabV[holeRow]) != 0;
    }

    /// <summary>vmangos <c>GridMap::getHeightFromFloat</c>: two triangles per square, around the V8 centre.</summary>
    private float GetHeightFromFloat(float x, float y)
    {
        float[] v9 = _v9!;
        float[] v8 = _v8!;
        x = Resolution * (32 - (x / GridSize));
        y = Resolution * (32 - (y / GridSize));

        int xInt = (int)x;
        int yInt = (int)y;
        x -= xInt;
        y -= yInt;
        xInt &= Resolution - 1;
        yInt &= Resolution - 1;

        if (IsHole(xInt, yInt))
        {
            return InvalidHeightValue;
        }

        float a, b, c;
        float h5 = 2 * v8[(xInt * 128) + yInt];
        if (x + y < 1)
        {
            if (x > y)
            {
                // 1 triangle (h1, h2, h5)
                float h1 = v9[(xInt * 129) + yInt];
                float h2 = v9[((xInt + 1) * 129) + yInt];
                a = h2 - h1;
                b = h5 - h1 - h2;
                c = h1;
            }
            else
            {
                // 2 triangle (h1, h3, h5)
                float h1 = v9[(xInt * 129) + yInt];
                float h3 = v9[(xInt * 129) + yInt + 1];
                a = h5 - h1 - h3;
                b = h3 - h1;
                c = h1;
            }
        }
        else if (x > y)
        {
            // 3 triangle (h2, h4, h5)
            float h2 = v9[((xInt + 1) * 129) + yInt];
            float h4 = v9[((xInt + 1) * 129) + yInt + 1];
            a = h2 + h4 - h5;
            b = h4 - h2;
            c = h5 - h4;
        }
        else
        {
            // 4 triangle (h3, h4, h5)
            float h3 = v9[(xInt * 129) + yInt + 1];
            float h4 = v9[((xInt + 1) * 129) + yInt + 1];
            a = h4 - h3;
            b = h3 + h4 - h5;
            c = h5 - h4;
        }

        return (a * x) + (b * y) + c;
    }

    /// <summary>
    /// vmangos <c>GridMap::getHeightFromUint16</c> / <c>getHeightFromUint8</c>: the same triangles
    /// on integer samples (V9 index <c>x*128 + x + y</c>, neighbours at +1, +129, +130), scaled by
    /// the multiplier and offset by the grid height. Holes are not checked (as in vmangos).
    /// </summary>
    private float GetHeightFromPacked(float x, float y)
    {
        int[] v9 = _v9Int!;
        int[] v8 = _v8Int!;
        x = Resolution * (32 - (x / GridSize));
        y = Resolution * (32 - (y / GridSize));

        int xInt = (int)x;
        int yInt = (int)y;
        x -= xInt;
        y -= yInt;
        xInt &= Resolution - 1;
        yInt &= Resolution - 1;

        int p = (xInt * 128) + xInt + yInt;
        int h5 = 2 * v8[(xInt * 128) + yInt];
        int a, b, c;
        if (x + y < 1)
        {
            if (x > y)
            {
                int h1 = v9[p];
                int h2 = v9[p + 129];
                a = h2 - h1;
                b = h5 - h1 - h2;
                c = h1;
            }
            else
            {
                int h1 = v9[p];
                int h3 = v9[p + 1];
                a = h5 - h1 - h3;
                b = h3 - h1;
                c = h1;
            }
        }
        else if (x > y)
        {
            int h2 = v9[p + 129];
            int h4 = v9[p + 130];
            a = h2 + h4 - h5;
            b = h4 - h2;
            c = h5 - h4;
        }
        else
        {
            int h3 = v9[p + 1];
            int h4 = v9[p + 130];
            a = h4 - h3;
            b = h3 + h4 - h5;
            c = h5 - h4;
        }

        // C++: (float)((a * x) + (b * y) + c) — int * float promotes to float.
        return (((a * x) + (b * y) + c) * _gridIntHeightMultiplier) + _gridHeight;
    }

    private static int[] Widen(ushort[] values) => Array.ConvertAll(values, v => (int)v);

    private static int[] Widen(byte[] values) => Array.ConvertAll(values, v => (int)v);

    /// <summary>Bounds-checked little-endian reader over the file (throws ArgumentOutOfRange when truncated).</summary>
    private ref struct Reader(ReadOnlySpan<byte> file, uint offset)
    {
        private readonly ReadOnlySpan<byte> _file = file;
        private int _position = checked((int)offset);

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public byte U8() => Take(1)[0];

        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public byte[] Bytes(int count) => Take(count).ToArray();

        public ushort[] U16Array(int count)
        {
            ReadOnlySpan<byte> raw = Take(count * 2);
            var values = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw[(i * 2)..]);
            }

            return values;
        }

        public float[] F32Array(int count)
        {
            ReadOnlySpan<byte> raw = Take(count * 4);
            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = BinaryPrimitives.ReadSingleLittleEndian(raw[(i * 4)..]);
            }

            return values;
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_position < 0 || count < 0 || _position > _file.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "section runs past the end of the file");
            }

            ReadOnlySpan<byte> slice = _file.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}
