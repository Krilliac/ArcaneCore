using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>Records what was logged, so tests can prove refusals stay at Warning or below.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    /// <summary>A snapshot of the entries so far.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_gate)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// A small client in memory: maps 0 (Azeroth), 1 (Kalimdor) and 36 (a dungeon with a global WMO), the Azeroth WDT listing
/// three tiles of which two have an ADT file.
/// </summary>
internal sealed class TerrainFixture
{
    /// <summary>Listed in the WDT and present: a sloping plane with a hole, liquid, an area and placements.</summary>
    public static readonly TileKey Goldshire = new(0, 48, 32);

    /// <summary>Listed in the WDT and present: flat.</summary>
    public static readonly TileKey Flat = new(0, 48, 33);

    /// <summary>Listed in the WDT but with no file.</summary>
    public static readonly TileKey ListedButAbsent = new(0, 49, 32);

    /// <summary>Not listed in the WDT.</summary>
    public static readonly TileKey NotListed = new(0, 10, 10);

    /// <summary>The global-WMO map.</summary>
    public const uint DungeonMap = 36;

    /// <summary>Plane offset of <see cref="Goldshire"/>.</summary>
    public const float PlaneA = 120f;

    /// <summary>Plane X slope of <see cref="Goldshire"/>.</summary>
    public const float PlaneSlopeX = 0.02f;

    /// <summary>Plane Y slope of <see cref="Goldshire"/>.</summary>
    public const float PlaneSlopeY = -0.01f;

    /// <summary>Chunk index holding a hole in <see cref="Goldshire"/>.</summary>
    public const int HoleChunk = (3 * 16) + 4;

    /// <summary>Chunk index holding river and ocean liquid in <see cref="Goldshire"/>.</summary>
    public const int LiquidChunk = (6 * 16) + 7;

    /// <summary>Area id of every chunk of <see cref="Goldshire"/>.</summary>
    public const uint Area = 12;

    /// <summary>The files.</summary>
    public MemoryClientFiles Files { get; } = new();

    /// <summary>The map directory.</summary>
    public FakeMapDirectory Maps { get; } = SyntheticTerrain
        .Maps(new ClientMap(0, "Azeroth", "Eastern Kingdoms", 0), new ClientMap(1, "Kalimdor", "Kalimdor", 0), new ClientMap(DungeonMap, "DeadminesInstance", "The Deadmines", 1))
        .WithArea(Area, "Elwynn Forest");

    /// <summary>The logger every service made here writes to.</summary>
    public ListLogger Log { get; } = new();

    /// <summary>Creates the fixture's files.</summary>
    public TerrainFixture()
    {
        SyntheticTerrain.AddMap(
            Files,
            "Azeroth",
            SyntheticTerrain.Wdt([(Goldshire.X, Goldshire.Y), (Flat.X, Flat.Y), (ListedButAbsent.X, ListedButAbsent.Y)]),
            (Goldshire, GoldshireAdt().Bytes),
            (Flat, SyntheticTerrain.Adt(new AdtSpec { Tile = Flat }).Bytes));
        Files.Add(
            ClientMapPaths.WdtPath("DeadminesInstance"),
            SyntheticTerrain.Wdt([], WdtFile.GlobalWmoFlag, [new WmoSpec("World\\wmo\\Dungeon\\Deadmines.wmo", 77, new WorldPoint(1, 2, 3), new WorldPoint(0, 90, 0), new WorldPoint(-5, -6, -7), new WorldPoint(5, 6, 7), 0x1, 4, 2)]));
    }

    /// <summary>A service over the fixture with every dependency registered.</summary>
    public ClientTerrainService Service(int capacity = ClientTerrainService.DefaultCacheCapacity)
        => new(() => Files, () => Maps, Log, capacity);

    /// <summary>The ADT of <see cref="Goldshire"/>.</summary>
    public static BuiltAdt GoldshireAdt()
    {
        AdtSpec spec = SyntheticTerrain.Plane(Goldshire, PlaneA, PlaneSlopeX, PlaneSlopeY);
        for (int i = 0; i < 256; i++)
        {
            spec.Areas[i] = Area;
        }

        spec.Holes[HoleChunk] = 0x0001;
        spec.Liquid[LiquidChunk] =
        [
            new LiquidLayerSpec(AdtLiquidKind.River, 150, 152, (y, x) => 150f + y, (y, x) => y == 2 && x == 2 ? (byte)0x80 : (y < 4 ? (byte)0 : AdtLiquidLayer.HiddenSquare)),
            new LiquidLayerSpec(AdtLiquidKind.Ocean, 160, 160, (_, _) => 160f, (y, x) => y == 2 && x == 2 ? (byte)0 : AdtLiquidLayer.HiddenSquare),
        ];
        spec.Textures.Add("Tileset\\Elwynn\\ElwynnGrassBase.blp");
        spec.Doodads.Add(new DoodadSpec("World\\Azeroth\\Tree01.mdx", 7001, new WorldPoint(10, 20, 30), new WorldPoint(0, 90, 0), 2048, 0x1));
        spec.Doodads.Add(new DoodadSpec("World\\Azeroth\\Rock02.mdx", 7002, new WorldPoint(11, 21, 31), new WorldPoint(0, 0, 0), 1024, 0));
        spec.Wmos.Add(new WmoSpec("World\\wmo\\Inn.wmo", 9001, new WorldPoint(100, 200, 300), new WorldPoint(0, 180, 0), new WorldPoint(90, 190, 290), new WorldPoint(110, 210, 310), 0x1, 2, 3));
        return SyntheticTerrain.Adt(spec);
    }

    /// <summary>The world position of fraction (<paramref name="fx"/>, <paramref name="fy"/>) in tile-wide square (<paramref name="row"/>, <paramref name="column"/>) of <paramref name="tile"/>.</summary>
    public static (double X, double Y) At(TileKey tile, int row, int column, double fx = 0.5, double fy = 0.5)
    {
        WorldPoint origin = WorldGrid.Origin(tile);
        return (origin.X - ((row + fx) * WorldGrid.SquareSize), origin.Y - ((column + fy) * WorldGrid.SquareSize));
    }

    /// <summary>The plane's height at a world position.</summary>
    public static double PlaneAt(double x, double y) => PlaneA + (PlaneSlopeX * x) + (PlaneSlopeY * y);
}
