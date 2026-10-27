using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Server;
using ArcaneCore.Game.Maps.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Server;

/// <summary>
/// The server view over the real extracted 1.12.1 data (<c>ARCANECORE_TEST_TERRAIN_DIR</c>). Each assertion is a fact that only holds when the
/// files were read: counts cross-checked against the raw bytes, and floors the world database places spawns on.
/// </summary>
public sealed class ServerRealDataTests(ITestOutputHelper output) : IDisposable
{
    private const uint EasternKingdoms = 0;

    // Goldshire: tile (49, 31) of map 0 (TerrainTile.TileOf of the innkeeper position).
    private static readonly TileKey Goldshire = new(EasternKingdoms, 49, 31);

    // Farley, innkeeper inside the Lion's Pride Inn (world database guid 80346); the model floor there is 56.963 (docs/integration/maps-vmaps-mmaps.md:166).
    private static readonly Vector3 InsideInn = new(-9462.66f, 16.1915f, 57.0459f);
    private const double InnFloor = 56.963;

    // Northshire Abbey, inside and in front (tests/ArcaneCore.Game.Tests/Collision/RealTerrainDataTests.cs:51-54).
    private static readonly Vector3 InsideAbbey = new(-8918.36f, -208.411f, 82.3088f);
    private static readonly Vector3 InFrontOfAbbey = new(-8902.59f, -162.606f, 82.0223f);
    private static readonly Vector3 NorthshireStart = new(-8949.95f, -132.493f, 83.5312f);

    private readonly AtlasContext _context = AtlasModules.Compose(
        new AtlasPaths(AtlasServerRealTerrain.SkipReason() is null ? AtlasServerRealTerrain.Root : null, null),
        NullLoggerFactory.Instance,
        headless: true,
        [new ServerModule()]);

    public void Dispose() => _context.Dispose();

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string P(Vector3 v) => $"[{N(v.X)},{N(v.Y)},{N(v.Z)}]";

    private async Task<JsonObject> Ok(string command, string args)
    {
        CommandResult result = await _context.Commands.InvokeAsync(command, JsonNode.Parse(args)!.AsObject(), _context.Services, CommandOrigin.Cli, true, CancellationToken.None);
        Assert.True(result.Ok, $"{command} {args}: {result.ErrorCode} {result.ErrorMessage}");
        return result.Data!.AsObject();
    }

    private LayerBuild Build(string layer, TileKey tile)
    {
        Assert.True(_context.Layers.TryGet(layer, out ILayerSource source));
        return source.Build(layer, tile, CancellationToken.None);
    }

    private static string FilePath(string sub, string name) => Path.Combine(AtlasServerRealTerrain.Root, sub, name);

    [AtlasServerRealTerrainFact]
    public void Terrain_Goldshire_TriangleCountFollowsTheRawHoleMask_AndHeightsAreTheServers()
    {
        // Independent read of the raw file: the height flags and the hole mask, straight from the bytes.
        byte[] raw = File.ReadAllBytes(FilePath("maps", TerrainTile.FileName(Goldshire.MapId, Goldshire.X, Goldshire.Y)));

        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:458-468 header u32 offsets: height at 16, holes at 32 (vmangos GridMap.cpp:80-100 map_fileheader)
        uint heightOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16));
        uint holesOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(32));

        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:125-130 MHGT magic then u32 flags; 1 no height, 2 u16, 4 u8, 0 float
        uint heightFlags = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan((int)heightOffset + 4));
        bool isFloat = (heightFlags & 7) == 0;
        int holeBits = 0;
        for (int i = 0; i < 256; i++)
        {
            holeBits += BitOperations.PopCount(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan((int)holesOffset + (2 * i))));
        }

        // Each hole bit is 2 x 2 squares (TerrainTile.cs:472-480); only float tiles honour the mask (TerrainTile.cs:244).
        int holeSquares = isFloat ? holeBits * 4 : 0;
        LayerBuild build = Build(LayerIds.Terrain, Goldshire);
        output.WriteLine($"{build.Status.Detail}: float {isFloat}, hole bits {holeBits}, {build.Status.Primitives} primitives");

        Assert.Equal(TileState.Ok, build.Status.State);
        GeometryChunk chunk = Assert.Single(build.Chunks);
        Assert.True(holeBits > 0, "Goldshire's tile has holes (the inn cellar and the mine)");
        Assert.Equal(0, (int)chunk.Stats["invalidHeightSquares"]!);
        Assert.Equal((128 * 128 * 4) - (4 * holeSquares), chunk.TriangleCount);

        // Every triangle lies on the server's interpolation: the centroid z is TerrainTile.GetHeight there.
        TerrainTile terrain = TerrainTile.Parse(raw);
        double worst = 0;
        for (int t = 0; t < chunk.TriangleCount; t += 7)
        {
            WorldPoint a = chunk.Vertex(chunk.Triangles[t * 3]);
            WorldPoint b = chunk.Vertex(chunk.Triangles[(t * 3) + 1]);
            WorldPoint c = chunk.Vertex(chunk.Triangles[(t * 3) + 2]);
            float server = terrain.GetHeight((float)((a.X + b.X + c.X) / 3), (float)((a.Y + b.Y + c.Y) / 3));
            worst = Math.Max(worst, Math.Abs(server - ((a.Z + b.Z + c.Z) / 3)));
        }

        output.WriteLine($"largest centroid height difference {worst:G4} over {chunk.TriangleCount / 7} sampled triangles");
        Assert.True(worst < 5e-3, $"largest centroid height difference {worst}");

        LayerBuild holes = Build(LayerIds.TerrainHoles, Goldshire);
        Assert.Equal(holeSquares, (int)Assert.Single(holes.Chunks).Stats["holeSquares"]!);
    }

    [AtlasServerRealTerrainFact]
    public async Task Height_InsideTheInn_ReportsTheModelFloor()
    {
        JsonObject data = await Ok(ServerCommands.HeightName, $$"""{"map":0,"at":{{P(InsideInn)}}}""");
        output.WriteLine(data.ToJsonString());

        Assert.Equal(InnFloor, (double)data["models"]!["z"]!, 2);
        Assert.Equal(InnFloor, (double)data["collision"]!["z"]!, 2);
        Assert.Equal("Ok", (string)data["files"]!["vmtile"]!["state"]!);
        Assert.Equal("Ok", (string)data["files"]!["mmtile"]!["state"]!);
        Assert.False((bool)data["indoor"]!["outdoors"]!);
    }

    [AtlasServerRealTerrainFact]
    public void NavPoly_Goldshire_CountsEveryPolygonOfTheTileHeader()
    {
        byte[] raw = File.ReadAllBytes(FilePath("mmaps", $"{Goldshire.MapId:D3}{Goldshire.X:D2}{Goldshire.Y:D2}.mmtile"));

        // ref: vmangos@0e3ff01 src/game/Maps/MoveMapSharedDefines.h:28-35 MmapTileHeader is five u32 (20 bytes), then the dtMeshHeader whose
        // seventh int is polyCount (vmangos@0e3ff01 dep/recastnavigation/Detour/Include/DetourNavMesh.h:261-269); NavMeshTile.cs:224 reads the same.
        int headerPolys = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(20 + (6 * 4)));
        LayerBuild build = Build(LayerIds.NavPoly, Goldshire);

        Assert.Equal(TileState.Ok, build.Status.State);
        JsonObject stats = build.Chunks[0].Stats;
        output.WriteLine(stats.ToJsonString());
        Assert.Equal(headerPolys, (int)stats["headerPolyCount"]!);
        Assert.Equal(headerPolys, (int)stats["polygons"]! + (int)stats["offMeshPolygonsSkipped"]! + (int)stats["degeneratePolygonsSkipped"]!);
        Assert.Equal(0, (int)stats["degeneratePolygonsSkipped"]!);
        Assert.Equal(headerPolys - (int)stats["offMeshPolygonsSkipped"]!, (int)stats["polygons"]!);
        Assert.True(build.Chunks.All(c => c.LineAttributes.All(a => c.Attributes.ContainsKey(a))));

        LayerBuild portals = Build(LayerIds.NavPortals, Goldshire);
        output.WriteLine(portals.Chunks.Count > 0 ? portals.Chunks[0].Stats.ToJsonString() : portals.Status.Detail);
        Assert.Equal(TileState.Ok, portals.Status.State);
        Assert.True((int)portals.Chunks[0].Stats["portals"]! > 0);

        LayerBuild detail = Build(LayerIds.NavDetail, Goldshire);
        Assert.Equal(TileState.Ok, detail.Status.State);
        Assert.True(detail.Chunks.Sum(c => c.TriangleCount) > 0);
    }

    [AtlasServerRealTerrainFact]
    public void VMapWmo_Goldshire_HoldsTheInnFloorUnderTheInnkeeper()
    {
        LayerBuild build = Build(LayerIds.VMapWmo, Goldshire);
        Assert.Equal(TileState.Ok, build.Status.State);
        output.WriteLine(build.Chunks[0].Stats.ToJsonString());

        // A WMO triangle in world space directly under the innkeeper at the documented floor height: the instance transform is the server's.
        JsonObject? floor = null;
        double floorZ = double.NaN;
        foreach (GeometryChunk chunk in build.Chunks)
        {
            for (int t = 0; t < chunk.TriangleCount; t++)
            {
                WorldPoint a = chunk.Vertex(chunk.Triangles[t * 3]);
                WorldPoint b = chunk.Vertex(chunk.Triangles[(t * 3) + 1]);
                WorldPoint c = chunk.Vertex(chunk.Triangles[(t * 3) + 2]);
                if (ZAt(a, b, c, InsideInn.X, InsideInn.Y) is { } z && Math.Abs(z - InnFloor) < 0.05)
                {
                    floor = chunk.Attributes[chunk.TriangleAttributes[t]];
                    floorZ = z;
                }
            }
        }

        Assert.NotNull(floor);
        output.WriteLine($"inn floor triangle at z {floorZ:F3}: {floor.ToJsonString()}");
        Assert.Equal("wmo group", (string)floor["kind"]!);
        Assert.EndsWith("Goldshireinn.wmo", (string)floor["model"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(30340u, (uint)floor["spawnId"]!);
        Assert.False((bool)floor["outdoors"]!);

        LayerBuild bounds = Build(LayerIds.VMapBounds, Goldshire);
        Assert.Equal(TileState.Ok, bounds.Status.State);
        Assert.Contains(
            bounds.Chunks.SelectMany(c => c.Attributes.Values),
            o => o["spawnId"]!.ToJsonString() == floor["spawnId"]!.ToJsonString() && Contains(o, InsideInn));
    }

    [AtlasServerRealTerrainFact]
    public void VMapM2_Goldshire_AttributesEveryDoodad_AndMatchesModelTriangleSum()
    {
        LayerBuild m2 = Build(LayerIds.VMapM2, Goldshire);
        LayerBuild wmo = Build(LayerIds.VMapWmo, Goldshire);
        Assert.Equal(TileState.Ok, m2.Status.State);
        Assert.Equal(TileState.Ok, wmo.Status.State);

        var trianglesBySpawnId = new Dictionary<uint, int>();
        foreach (GeometryChunk chunk in m2.Chunks)
        {
            chunk.Validate();
            for (int t = 0; t < chunk.TriangleCount; t++)
            {
                JsonObject attr = chunk.Attributes[chunk.TriangleAttributes[t]];
                Assert.Equal("m2 doodad", (string)attr["kind"]!);
                trianglesBySpawnId[(uint)attr["spawnId"]!] = (int)attr["triangles"]!;
            }
        }

        JsonObject m2Stats = m2.Chunks[0].Stats;
        JsonObject wmoStats = wmo.Chunks[0].Stats;
        output.WriteLine(m2Stats.ToJsonString());

        int wmoCountOfTile = (int)wmoStats["spawnsListed"]! - (int)wmoStats["spawnsOfOtherLayers"]!;
        Assert.Equal(wmoCountOfTile, (int)m2Stats["spawnsOfOtherLayers"]!);
        Assert.Equal((int)wmoStats["spawnsDrawn"]! + (int)wmoStats["spawnsDrawnByAnotherTile"]!, (int)m2Stats["spawnsOfOtherLayers"]!);

        ServerData data = _context.Services.Require<ServerData>();
        VMapTileData tileData = data.VMapTileFile(Goldshire).Value!;
        int modelTriangleSum = tileData.Spawns
            .Where(s => s.Spawn.IsM2 && trianglesBySpawnId.ContainsKey(s.Spawn.Id))
            .Sum(s => data.ModelFile(s.Spawn.Name).Value!.Groups.Sum(g => g.Triangles.Count));
        int drawnTriangles = m2.Chunks.Sum(c => c.TriangleCount);

        Assert.True(drawnTriangles > 0);
        Assert.Equal((int)m2Stats["spawnsDrawn"]!, trianglesBySpawnId.Count);
        Assert.Equal(drawnTriangles, trianglesBySpawnId.Values.Sum());
        Assert.Equal(drawnTriangles, modelTriangleSum);
        Assert.Equal(drawnTriangles, (int)m2Stats["triangles"]!);
    }

    [AtlasServerRealTerrainFact]
    public void VMapOwnership_AroundGoldshire3x3_DrawsEachSpawnInExactlyOneTile_OwnedByItsBoundsCentre()
    {
        TileKey[] tiles = [.. WorldGrid.Around(EasternKingdoms, InsideInn.X, InsideInn.Y, 1)];
        Assert.Equal(9, tiles.Length);
        HashSet<TileKey> tileSet = [.. tiles];
        ServerData data = _context.Services.Require<ServerData>();
        VMapTreeData tree = data.VMapTreeFile(EasternKingdoms).Value!;

        foreach (string layer in new[] { LayerIds.VMapWmo, LayerIds.VMapM2 })
        {
            bool wantM2 = layer == LayerIds.VMapM2;
            var ownerBySpawnId = new Dictionary<uint, TileKey>();
            var listedInTiles = new Dictionary<uint, (ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn Spawn, uint Slot, List<TileKey> Tiles)>();
            int tilesWithBorderSpawns = 0;

            foreach (TileKey tile in tiles)
            {
                LayerBuild build = Build(layer, tile);
                Assert.Equal(TileState.Ok, build.Status.State);
                JsonObject stats = build.Chunks[0].Stats;

                uint[] drawnIds = [.. build.Chunks.SelectMany(c => c.Attributes.Values).Select(a => (uint)a["spawnId"]!).Distinct()];
                Assert.Equal((int)stats["spawnsDrawn"]!, drawnIds.Length);
                foreach (uint spawnId in drawnIds)
                {
                    Assert.True(ownerBySpawnId.TryAdd(spawnId, tile), $"{layer}: spawnId {spawnId} drawn in both {ownerBySpawnId.GetValueOrDefault(spawnId)} and {tile}");
                }

                VMapTileData rawTile = data.VMapTileFile(tile).Value!;
                int borderSpawnsOnTile = 0;
                foreach (VMapSpawn entry in rawTile.Spawns)
                {
                    if (entry.Spawn.IsM2 != wantM2 || entry.Slot >= (uint)tree.SlotCount)
                    {
                        continue;
                    }

                    if (!listedInTiles.TryGetValue(entry.Spawn.Id, out var record))
                    {
                        record = (entry.Spawn, entry.Slot, []);
                        listedInTiles[entry.Spawn.Id] = record;
                    }

                    record.Tiles.Add(tile);
                    (Vector3 low, Vector3 high) = entry.Spawn.HasBound
                        ? VMapLayerSource.WorldBounds(entry.Spawn)
                        : (ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToWorld(entry.Spawn.Position), ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToWorld(entry.Spawn.Position));
                    Vector3 centre = (low + high) * 0.5f;
                    if (WorldGrid.TileOf(EasternKingdoms, centre.X, centre.Y) is { } centreOwner
                        && centreOwner != tile
                        && data.VMapTileFile(centreOwner) is { IsOk: true } ownerFile
                        && ownerFile.Value.Slots.Contains(entry.Slot))
                    {
                        borderSpawnsOnTile++;
                    }
                }

                int drawnByAnother = (int)stats["spawnsDrawnByAnotherTile"]!;
                int fallbackOwned = (int)stats["spawnsOwnedByFallbackRule"]!;
                int orphanOwned = (int)stats["spawnsListedByNoCoveringTile"]!;
                Assert.True(fallbackOwned >= 0 && orphanOwned >= 0);
                if (borderSpawnsOnTile > 0)
                {
                    tilesWithBorderSpawns++;
                    Assert.True(drawnByAnother > 0, $"{layer} {tile}: {borderSpawnsOnTile} border-spanning spawns but spawnsDrawnByAnotherTile == 0");
                }

                Assert.True(drawnByAnother >= borderSpawnsOnTile);
            }

            Assert.True(tilesWithBorderSpawns > 0, $"{layer}: expected border-spanning spawns in 3x3 around Goldshire");
            int multiTileVerified = 0;
            foreach ((ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn spawn, uint slot, List<TileKey> listingTiles) in listedInTiles.Values)
            {
                if (listingTiles.Count < 2)
                {
                    continue;
                }

                (Vector3 low, Vector3 high) = spawn.HasBound
                    ? VMapLayerSource.WorldBounds(spawn)
                    : (ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToWorld(spawn.Position), ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToWorld(spawn.Position));
                Vector3 centre = (low + high) * 0.5f;
                TileKey expectedOwner = WorldGrid.TileOf(EasternKingdoms, centre.X, centre.Y)!.Value;
                if (tileSet.Contains(expectedOwner) && data.VMapTileFile(expectedOwner).Value!.Slots.Contains(slot))
                {
                    Assert.True(ownerBySpawnId.TryGetValue(spawn.Id, out TileKey actualOwner), $"{layer}: multi-tile spawn {spawn.Id} was not drawn by its centre owner {expectedOwner}");
                    Assert.Equal(expectedOwner, actualOwner);
                    multiTileVerified++;
                }
            }

            output.WriteLine($"{layer}: {ownerBySpawnId.Count} unique spawns drawn across 9 tiles, {tilesWithBorderSpawns} tiles with border spawns, {multiTileVerified} multi-tile spawns verified against WorldGrid.TileOf(centre)");
            Assert.True(multiTileVerified > 0, $"{layer}: expected at least one multi-tile spawn with centre in the 3x3");
        }
    }

    [AtlasServerRealTerrainFact]
    public void VMapLiquid_AroundGoldshire_MatchesWmoLiquidGetLiquidHeight()
    {
        ServerData data = _context.Services.Require<ServerData>();
        int verifiedQuads = 0;

        foreach (TileKey tile in WorldGrid.Around(EasternKingdoms, InsideInn.X, InsideInn.Y, 1))
        {
            LayerBuild build = Build(LayerIds.VMapLiquid, tile);
            if (build.Status.State != TileState.Ok || build.Chunks.Count == 0)
            {
                continue;
            }

            VMapTileData rawTile = data.VMapTileFile(tile).Value!;
            Dictionary<uint, ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn> spawnsById = rawTile.Spawns
                .Where(s => !s.Spawn.IsM2)
                .GroupBy(s => s.Spawn.Id)
                .ToDictionary(g => g.Key, g => g.First().Spawn);

            foreach (GeometryChunk chunk in build.Chunks)
            {
                chunk.Validate();
                for (int t = 0; t + 1 < chunk.TriangleCount; t += 2)
                {
                    uint attrId = chunk.TriangleAttributes[t];
                    JsonObject attr = chunk.Attributes[attrId];
                    Assert.Equal("wmo liquid", (string)attr["kind"]!);
                    uint spawnId = (uint)attr["spawnId"]!;
                    int groupIndex = (int)attr["group"]!;
                    ArcaneCore.Game.Maps.Collision.VMaps.ModelSpawn spawn = spawnsById[spawnId];
                    ArcaneCore.Game.Maps.Collision.VMaps.WorldModel model = data.ModelFile(spawn.Name).Value!;
                    var instance = new ArcaneCore.Game.Maps.Collision.VMaps.ModelInstance(spawn, model);
                    ArcaneCore.Game.Maps.Collision.VMaps.WmoLiquid liquid = model.Groups[groupIndex].Liquid!;

                    // Each drawn liquid square emits two triangles: (a, b, d) for dx > dy and (a, d, c) for dx < dy.
                    for (int triOffset = 0; triOffset < 2; triOffset++)
                    {
                        WorldPoint a = chunk.Vertex(chunk.Triangles[(t + triOffset) * 3]);
                        WorldPoint b = chunk.Vertex(chunk.Triangles[((t + triOffset) * 3) + 1]);
                        WorldPoint c = chunk.Vertex(chunk.Triangles[((t + triOffset) * 3) + 2]);
                        var centroidWorld = new Vector3((float)((a.X + b.X + c.X) / 3.0), (float)((a.Y + b.Y + c.Y) / 3.0), (float)((a.Z + b.Z + c.Z) / 3.0));
                        Vector3 centroidModel = instance.ToModel(ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToInternal(centroidWorld));

                        Assert.True(TryGetWmoLiquidHeight(liquid, centroidModel, out float modelLiqZ));
                        Vector3 expectedWorld = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.ToWorld(
                            instance.FromModel(new Vector3(centroidModel.X, centroidModel.Y, modelLiqZ)));
                        double? drawnZ = ZAt(a, b, c, centroidWorld.X, centroidWorld.Y);
                        Assert.NotNull(drawnZ);
                        Assert.Equal(expectedWorld.Z, drawnZ.Value, 2);
                    }

                    verifiedQuads++;
                    if (verifiedQuads >= 32)
                    {
                        break;
                    }
                }

                if (verifiedQuads >= 32)
                {
                    break;
                }
            }

            if (verifiedQuads >= 32)
            {
                break;
            }
        }

        output.WriteLine($"verified {verifiedQuads} real WMO liquid quads against WmoLiquid::GetLiquidHeight");
        Assert.True(verifiedQuads > 0, "expected real WMO liquid quads in the 3x3 tiles around Goldshire");
    }

    [AtlasServerRealTerrainFact]
    public void NavBv_Goldshire_IsEmpty_BecauseTheBvTreePassedVerification()
    {
        LayerBuild bv = Build(LayerIds.NavBvRejected, Goldshire);
        output.WriteLine($"{bv.Status.State}: {bv.Status.Detail}");

        Assert.Equal(TileState.Empty, bv.Status.State);
        Assert.Empty(bv.Chunks);
        Assert.Contains("passed the server's verification", bv.Status.Detail, StringComparison.Ordinal);
    }

    // ref: vmangos@0e3ff01 src/game/vmap/WorldModel.cpp:158-204 WmoLiquid::GetLiquidHeight
    internal static bool TryGetWmoLiquidHeight(ArcaneCore.Game.Maps.Collision.VMaps.WmoLiquid liquid, Vector3 pos, out float liqHeight)
    {
        liqHeight = 0;
        float txF = (pos.X - liquid.Corner.X) / VMapLayerSource.LiquidTileSize;
        if (txF < 0f)
        {
            return false;
        }

        uint tx = (uint)txF;
        if (tx >= liquid.TilesX)
        {
            return false;
        }

        float tyF = (pos.Y - liquid.Corner.Y) / VMapLayerSource.LiquidTileSize;
        if (tyF < 0f)
        {
            return false;
        }

        uint ty = (uint)tyF;
        if (ty >= liquid.TilesY)
        {
            return false;
        }

        if ((liquid.Flags[tx + (ty * liquid.TilesX)] & 0x0F) == 0x0F)
        {
            return false;
        }

        float dx = txF - tx;
        float dy = tyF - ty;
        uint rowOffset = liquid.TilesX + 1;
        if (dx > dy)
        {
            float sx = liquid.Heights[tx + 1 + (ty * rowOffset)] - liquid.Heights[tx + (ty * rowOffset)];
            float sy = liquid.Heights[tx + 1 + ((ty + 1) * rowOffset)] - liquid.Heights[tx + 1 + (ty * rowOffset)];
            liqHeight = liquid.Heights[tx + (ty * rowOffset)] + (dx * sx) + (dy * sy);
        }
        else
        {
            float sx = liquid.Heights[tx + 1 + ((ty + 1) * rowOffset)] - liquid.Heights[tx + ((ty + 1) * rowOffset)];
            float sy = liquid.Heights[tx + ((ty + 1) * rowOffset)] - liquid.Heights[tx + (ty * rowOffset)];
            liqHeight = liquid.Heights[tx + (ty * rowOffset)] + (dx * sx) + (dy * sy);
        }

        return true;
    }

    [AtlasServerRealTerrainFact]
    public async Task Los_AbbeyWalls_BlockSight_AndTheSkyIsClear()
    {
        Vector3 eye = new(0, 0, 2);
        Vector3 sky = new(0, 0, 120);

        JsonObject blocked = await Ok(ServerCommands.LosName, $$"""{"map":0,"a":{{P(InsideAbbey + eye)}},"b":{{P(InFrontOfAbbey + eye)}}}""");
        JsonObject clear = await Ok(ServerCommands.LosName, $$"""{"map":0,"a":{{P(InsideAbbey + sky)}},"b":{{P(InFrontOfAbbey + sky)}}}""");
        output.WriteLine(blocked.ToJsonString());

        Assert.False((bool)blocked["spell"]!["clear"]!);
        Assert.False((bool)blocked["movement"]!["clear"]!);
        Assert.NotNull(blocked["spell"]!["hit"]);
        Assert.True((bool)clear["spell"]!["clear"]!);
        Assert.True((bool)clear["movement"]!["clear"]!);
    }

    [AtlasServerRealTerrainFact]
    public async Task Path_NorthshireToTheInn_CrossesTiles()
    {
        JsonObject path = await Ok(ServerCommands.PathName, $$"""{"map":0,"a":{{P(NorthshireStart)}},"b":{{P(InsideInn)}}}""");
        output.WriteLine($"{path["status"]} {path["flags"]!.ToJsonString()} points {path["points"]!.AsArray().Count} length {path["length"]} polys {path["polys"]!.AsArray().Count} {path["corridorNote"]}");

        Assert.DoesNotContain(path["flags"]!.AsArray(), f => (string)f! == "NotUsingPath");
        Assert.True(path["points"]!.AsArray().Count > 2);
        Assert.True((double)path["length"]! > Vector3.Distance(NorthshireStart, InsideInn) * 0.9);
        Assert.NotEmpty(path["polys"]!.AsArray());
        Assert.NotEmpty(path["overlay"]!["polylines"]!.AsArray());
    }

    [AtlasServerRealTerrainFact]
    public async Task FloorStack_InTheInn_HasTheFloorAndTheTerrainBelow()
    {
        JsonObject stack = await Ok(ServerCommands.FloorStackName, $$"""{"map":0,"x":{{N(InsideInn.X)}},"y":{{N(InsideInn.Y)}},"zMin":0,"zMax":200}""");
        output.WriteLine(stack.ToJsonString());
        JsonArray surfaces = stack["surfaces"]!.AsArray();

        Assert.Contains(surfaces, s => (string)s!["kind"]! == "model" && Math.Abs((double)s["z"]! - InnFloor) < 0.05);
        Assert.Contains(surfaces, s => (string)s!["kind"]! == "mesh");
        double[] z = [.. surfaces.Select(s => (double)s!["z"]!)];
        Assert.Equal(z.Order(), z);
    }

    [AtlasServerRealTerrainFact]
    public void Tiles_Goldshire_HasAllThreeFiles_AndAvailabilityListsIt()
    {
        LayerBuild build = Build(LayerIds.Tiles, Goldshire);
        output.WriteLine(build.Status.Detail);

        Assert.Equal(TileState.Ok, build.Status.State);
        Assert.Equal(TilesLayerSource.TerrainBit | TilesLayerSource.ModelBit | TilesLayerSource.NavBit, (int)build.Chunks[0].Stats["bits"]!);
        Assert.True(_context.Layers.TryGet(LayerIds.Tiles, out ILayerSource source));
        Assert.Contains(Goldshire, source.AvailableTiles(LayerIds.Tiles, EasternKingdoms));
        Assert.Equal(TileState.Missing, Build(LayerIds.Terrain, new TileKey(EasternKingdoms, 0, 0)).Status.State);
    }

    // Height of triangle (a, b, c) above (x, y), or null when (x, y) is outside its projection.
    internal static double? ZAt(WorldPoint a, WorldPoint b, WorldPoint c, double x, double y)
    {
        double d = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (Math.Abs(d) < 1e-9)
        {
            return null;
        }

        double l1 = (((b.Y - c.Y) * (x - c.X)) + ((c.X - b.X) * (y - c.Y))) / d;
        double l2 = (((c.Y - a.Y) * (x - c.X)) + ((a.X - c.X) * (y - c.Y))) / d;
        double l3 = 1 - l1 - l2;
        return l1 < 0 || l2 < 0 || l3 < 0 ? null : (l1 * a.Z) + (l2 * b.Z) + (l3 * c.Z);
    }

    private static double D(JsonNode? node) => JsonScalars.TryGetDouble(node, out double d) ? d : double.NaN;

    private static bool Contains(JsonObject bound, Vector3 p)
    {
        if (bound["boundLow"] is not JsonArray low || bound["boundHigh"] is not JsonArray high)
        {
            return false;
        }

        return p.X >= D(low[0]) && p.X <= D(high[0]) && p.Y >= D(low[1]) && p.Y <= D(high[1]);
    }
}
