using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class ClientPlacementSourceTests
{
    private static ClientPlacementSource Source(TerrainFixture fixture) => new(fixture.Service());

    [Fact]
    public void Placements_ListWmosThenDoodadsWithTheRawFields()
    {
        IReadOnlyList<ClientPlacement>? placements = Source(new TerrainFixture()).Placements(TerrainFixture.Goldshire);

        Assert.NotNull(placements);
        Assert.Equal(3, placements.Count);
        Assert.All(placements, p => Assert.Equal(TerrainFixture.Goldshire, p.Tile));

        ClientPlacement wmo = placements[0];
        Assert.Equal(PlacementKind.Wmo, wmo.Kind);
        Assert.Equal("World\\wmo\\Inn.wmo", wmo.ModelPath);
        Assert.Equal(9001u, wmo.UniqueId);
        Assert.Equal(new WorldPoint(100, 200, 300), wmo.Position);
        Assert.Equal(new WorldPoint(0, 180, 0), wmo.Rotation);
        Assert.Equal(1.0, wmo.Scale);
        Assert.Equal((ushort)1, wmo.Flags);
        Assert.Equal((ushort)2, wmo.DoodadSet);

        ClientPlacement tree = placements[1];
        Assert.Equal(PlacementKind.M2, tree.Kind);
        Assert.Equal("World\\Azeroth\\Tree01.mdx", tree.ModelPath);
        Assert.Equal(7001u, tree.UniqueId);
        Assert.Equal(new WorldPoint(10, 20, 30), tree.Position);
        Assert.Equal(new WorldPoint(0, 90, 0), tree.Rotation);
        Assert.Equal(2.0, tree.Scale);
        Assert.Equal((ushort)1, tree.Flags);
        Assert.Equal((ushort)0, tree.DoodadSet);

        Assert.Equal(1.0, placements[2].Scale);
        Assert.Equal("World\\Azeroth\\Rock02.mdx", placements[2].ModelPath);
    }

    [Fact]
    public void Placements_OfATileWithNoModelsAreEmptyNotNull()
    {
        IReadOnlyList<ClientPlacement>? placements = Source(new TerrainFixture()).Placements(TerrainFixture.Flat);

        Assert.NotNull(placements);
        Assert.Empty(placements);
    }

    [Fact]
    public void Placements_AreNullWhenTheFileIsNotThere()
    {
        var fixture = new TerrainFixture();
        ClientPlacementSource source = Source(fixture);

        Assert.Null(source.Placements(TerrainFixture.ListedButAbsent));
        Assert.Null(source.Placements(TerrainFixture.NotListed));
        Assert.Null(source.Placements(new TileKey(1, 5, 5)));
        Assert.Null(source.Placements(new TileKey(999, 5, 5)));
        Assert.Null(source.Placements(TileKey.Global(1)));
        Assert.Null(new ClientPlacementSource(new ClientTerrainService(() => null, () => fixture.Maps, NullLogger.Instance)).Placements(TerrainFixture.Goldshire));
    }

    [Fact]
    public void Placements_OfTheGlobalTileAreTheWdtsGlobalWmo()
    {
        ClientPlacementSource source = Source(new TerrainFixture());

        IReadOnlyList<ClientPlacement>? dungeon = source.Placements(TileKey.Global(TerrainFixture.DungeonMap));
        IReadOnlyList<ClientPlacement>? terrainMap = source.Placements(TileKey.Global(0));

        ClientPlacement placement = Assert.Single(dungeon!);
        Assert.Equal(PlacementKind.Wmo, placement.Kind);
        Assert.Equal("World\\wmo\\Dungeon\\Deadmines.wmo", placement.ModelPath);
        Assert.Equal(77u, placement.UniqueId);
        Assert.Equal(new WorldPoint(1, 2, 3), placement.Position);
        Assert.Equal(new WorldPoint(0, 90, 0), placement.Rotation);
        Assert.Equal((ushort)4, placement.DoodadSet);
        Assert.Equal(TileKey.Global(TerrainFixture.DungeonMap), placement.Tile);
        Assert.NotNull(terrainMap);
        Assert.Empty(terrainMap);
    }

    [Fact]
    public void Placements_ThrowInvalidDataForAFileTheReaderRefuses()
    {
        var fixture = new TerrainFixture();
        fixture.Files.Add(ClientMapPaths.AdtPath("Azeroth", TerrainFixture.Flat), new byte[64]);
        fixture.Files.Add(ClientMapPaths.WdtPath("DeadminesInstance"), [1, 2, 3]);
        ClientPlacementSource source = Source(fixture);

        InvalidDataException adt = Assert.Throws<InvalidDataException>(() => source.Placements(TerrainFixture.Flat));
        InvalidDataException wdt = Assert.Throws<InvalidDataException>(() => source.Placements(TileKey.Global(TerrainFixture.DungeonMap)));

        Assert.Contains("Azeroth_33_48.adt", adt.Message, StringComparison.Ordinal);
        Assert.Contains("DeadminesInstance.wdt", wdt.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Placements_RefuseATileOutsideTheGrid()
    {
        ClientPlacementSource source = Source(new TerrainFixture());

        Assert.Throws<ArgumentException>(() => source.Placements(new TileKey(0, 64, 0)));
        Assert.Throws<ArgumentException>(() => source.Placements(new TileKey(0, -1, 5)));
    }
}
