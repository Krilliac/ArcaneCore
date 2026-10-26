using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// The model layers of the server view, built from the <c>.vmtile</c> / <c>.vmtree</c> spawn lists and the <c>.vmo</c> model files with the
/// server's own readers and its own instance transform (<see cref="ModelInstance.FromModel"/> followed by <see cref="VMapFormat.ToWorld"/>):
/// <c>vmap.wmo</c> and <c>vmap.m2</c> (world-space triangles), <c>vmap.bounds</c> (spawn boxes) and <c>vmap.liquid</c> (WMO group liquid).
/// <para>
/// Format facts, each with two sources or a ground-truth test. A tile file lists the spawns that touch the tile, so a model spanning
/// several tiles is listed by each of them; the server loads it once per tree slot (vmangos src/game/vmap/StaticMapTree.cpp
/// <c>LoadMapTile</c>, referenced slot counts) and this source draws it in one tile only: the tile that contains the centre of its bounds,
/// else the lowest tile index among those covering the bounds that list the slot. Positions, rotations and bounds in the files are
/// vmap internal coordinates (a half turn about Z around the map centre, <see cref="VMapFormat.MapMid"/>; vmangos VMapManager2.h
/// <c>convertPositionToInternalRep</c>, src/game/vmap/VMapManager2.cpp:46-52 and 2 independent checks in the real-data tests). A
/// spawn whose slot is not below the tree's slot count is never loaded by the server and is not drawn
/// (<c>VMapTree.IsValidSlot</c>). WMO liquid is a grid of <c>TilesX x TilesY</c> squares of 533.333 / 128 yards from <c>Corner</c> in
/// model space; heights are stored at the <c>(TilesX + 1) x (TilesY + 1)</c> grid points (index <c>x + y * (TilesX + 1)</c>) and a square
/// holds no liquid when its flag's low nibble is 0xF (vmangos src/game/vmap/WorldModel.cpp:158-202 and
/// src/game/vmap/VMapDefinitions.h:22; mangos-classic src/game/vmap/WorldModel.cpp:158-190).
/// </para>
/// </summary>
public sealed class VMapLayerSource : ServerLayerSource
{
    /// <summary>Edge of one WMO liquid square in yards (<c>LIQUID_TILE_SIZE</c>; ref: vmangos@0e3ff01 src/game/vmap/VMapDefinitions.h:22).</summary>
    public const float LiquidTileSize = 533.333f / 128.0f;

    private const int MaxListedNames = 16;

    /// <summary>Creates the source over <paramref name="data"/>.</summary>
    public VMapLayerSource(ServerData data)
        : base(data, [LayerIds.VMapWmo, LayerIds.VMapM2, LayerIds.VMapBounds, LayerIds.VMapLiquid])
    {
    }

    /// <summary>The names of the set flags of a spawn, joined with <c>|</c>.</summary>
    public static string SpawnFlagNames(uint flags)
    {
        var names = new List<string>();
        if ((flags & VMapFormat.ModM2) != 0)
        {
            names.Add("M2");
        }

        if ((flags & VMapFormat.ModWorldSpawn) != 0)
        {
            names.Add("WORLDSPAWN");
        }

        if ((flags & VMapFormat.ModHasBound) != 0)
        {
            names.Add("HAS_BOUND");
        }

        uint rest = flags & ~(VMapFormat.ModM2 | VMapFormat.ModWorldSpawn | VMapFormat.ModHasBound);
        if (rest != 0)
        {
            names.Add(Hex(rest));
        }

        return names.Count == 0 ? "none" : string.Join('|', names);
    }

    /// <summary>The spawn's bounds in world space (the stored bounds are internal space, where the half turn swaps minimum and maximum).</summary>
    public static (Vector3 Low, Vector3 High) WorldBounds(ModelSpawn spawn)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        Vector3 a = VMapFormat.ToWorld(spawn.BoundLow);
        Vector3 b = VMapFormat.ToWorld(spawn.BoundHigh);
        return (Vector3.Min(a, b), Vector3.Max(a, b));
    }

    /// <inheritdoc />
    protected override IReadOnlyCollection<TileKey> ListTiles(string layerId, uint mapId) => Data.VMapTiles(mapId);

    /// <inheritdoc />
    protected override TileStatus Peek(string layerId, TileKey tile)
    {
        if (tile.IsGlobal)
        {
            return Data.VMapTreeFileExists(tile.MapId)
                ? new TileStatus(layerId, tile, TileState.Unknown, ".vmtree not read yet")
                : new TileStatus(layerId, tile, TileState.Missing, "no .vmtree file");
        }

        bool cached = Data.TryPeekVMapTile(tile, out ParsedFile<VMapTileData> parsed);
        return StatusOf(layerId, tile, Data.VMapTileFileExists(tile), cached, parsed, ".vmtile file");
    }

    /// <inheritdoc />
    protected override LayerBuild BuildTile(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        ParsedFile<VMapTreeData> treeFile = Data.VMapTreeFile(tile.MapId);
        if (!treeFile.IsOk)
        {
            // Without the tree the server's VMapManager loads no model of the map, whatever the tile files say.
            return treeFile.State == TileState.Rejected
                ? LayerBuild.Rejected(layerId, tile, treeFile.Detail ?? "the vmap tree was refused")
                : LayerBuild.Missing(layerId, tile, $"{treeFile.Detail}: the server loads no models of this map without it");
        }

        VMapTreeData tree = treeFile.Value;
        IReadOnlyList<VMapSpawn> spawns;
        string source;
        if (tile.IsGlobal)
        {
            if (tree.IsTiled)
            {
                return NotApplicable(layerId, tile, $"{treeFile.Detail} lists models per tile; the map has no global models");
            }

            spawns = tree.GlobalSpawns;
            source = treeFile.Detail ?? "vmtree";
        }
        else
        {
            if (!tree.IsTiled)
            {
                return NotApplicable(layerId, tile, $"{treeFile.Detail} holds all models of this map; the server reads no tile files for it");
            }

            ParsedFile<VMapTileData> file = Data.VMapTileFile(tile);
            if (!file.IsOk)
            {
                return FromFailure(layerId, tile, file);
            }

            spawns = file.Value.Spawns;
            source = file.Detail ?? "vmtile";
        }

        var parts = new ChunkParts(layerId, tile, WorldGrid.Origin(tile));
        var tally = new Tally();
        uint attribute = 0;
        foreach (VMapSpawn entry in spawns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelSpawn spawn = entry.Spawn;
            tally.Listed++;
            bool wanted = layerId switch
            {
                LayerIds.VMapWmo or LayerIds.VMapLiquid => !spawn.IsM2,
                LayerIds.VMapM2 => spawn.IsM2,
                _ => true,
            };
            if (!wanted)
            {
                tally.OtherKind++;
                continue;
            }

            if (entry.Slot >= (uint)tree.SlotCount)
            {
                tally.InvalidSlots++;
                continue;
            }

            if (!tile.IsGlobal && !OwnsHere(tile, entry, tally))
            {
                tally.OwnedElsewhere++;
                continue;
            }

            if (layerId == LayerIds.VMapBounds)
            {
                DrawBounds(parts, spawn, entry.Slot, ref attribute, tally);
                continue;
            }

            ParsedFile<WorldModel> model = Data.ModelFile(spawn.Name);
            if (!model.IsOk)
            {
                tally.NoteModel(model, spawn.Name);
                continue;
            }

            var instance = new ModelInstance(spawn, model.Value);
            if (layerId == LayerIds.VMapLiquid)
            {
                DrawLiquid(parts, instance, entry.Slot, ref attribute, tally);
            }
            else
            {
                DrawTriangles(parts, instance, entry.Slot, layerId == LayerIds.VMapWmo, ref attribute, tally);
            }
        }

        tally.WriteTo(parts.Stats, source, tree);
        IReadOnlyList<GeometryChunk> chunks = parts.Build();
        string detail = $"{source}: {tally.Listed} spawns listed, {tally.Drawn} drawn";
        return LayerBuild.Of(layerId, tile, chunks, detail);
    }

    private static string DisplayName(string name)
    {
        int nul = name.IndexOf('\0', StringComparison.Ordinal);
        return nul >= 0 ? name[..nul] : name;
    }

    private static JsonObject DescribeSpawn(string kind, VMapSpawn entry)
    {
        ModelSpawn s = entry.Spawn;
        Vector3 position = VMapFormat.ToWorld(s.Position);
        return new JsonObject
        {
            ["kind"] = kind,
            ["model"] = DisplayName(s.Name),
            ["spawnId"] = s.Id,
            ["slot"] = entry.Slot,
            ["adtId"] = s.AdtId,
            ["flags"] = Hex(s.Flags),
            ["flagNames"] = SpawnFlagNames(s.Flags),
            ["scale"] = s.Scale,
            ["position"] = Point(position),
            ["rotation"] = Point(s.Rotation),
        };
    }

    private static string GroupColorKey(GroupModel group)
        => (group.MogpFlags & ModelAreaInfo.MogpExterior) != 0 ? ColorKeys.Outdoor : ColorKeys.Indoor;

    private void DrawTriangles(ChunkParts parts, ModelInstance instance, uint slot, bool wmo, ref uint attribute, Tally tally)
    {
        WorldModel model = instance.Model;
        var spawn = new VMapSpawn(instance.Spawn, slot);
        uint doodadAttribute = 0;
        bool drewAny = false;
        for (int g = 0; g < model.Groups.Count; g++)
        {
            GroupModel group = model.Groups[g];
            if (group.Triangles.Count == 0)
            {
                continue;
            }

            parts.RollIfFull();
            GeometryBuilder builder = parts.Current;
            uint id;
            if (wmo)
            {
                id = ++attribute;
                int groupIndex = g;
                builder.DescribeAttribute(id, () =>
                {
                    JsonObject o = DescribeSpawn("wmo group", spawn);
                    o["rootWmoId"] = model.RootWmoId;
                    o["group"] = groupIndex;
                    o["groupWmoId"] = group.GroupWmoId;
                    o["mogpFlags"] = Hex(group.MogpFlags);
                    // ref: src/ArcaneCore.Game/Maps/Collision/CollisionContracts.cs:62 MogpExterior 0x8000; vmangos@0e3ff01 src/game/Maps/GridMap.cpp:875-878 IsOutdoorWMO
                    o["outdoors"] = (group.MogpFlags & ModelAreaInfo.MogpExterior) != 0;
                    o["colorKey"] = GroupColorKey(group);
                    o["triangles"] = group.Triangles.Count;
                    return o;
                });
            }
            else
            {
                if (doodadAttribute == 0)
                {
                    doodadAttribute = ++attribute;
                }

                id = doodadAttribute;
                builder.DescribeAttribute(id, () =>
                {
                    JsonObject o = DescribeSpawn("m2 doodad", spawn);
                    o["triangles"] = model.Groups.Sum(x => x.Triangles.Count);
                    o["note"] = "spell line of sight ignores M2 models; movement checks and the model floor query do not";
                    return o;
                });
            }

            var map = new uint[group.Vertices.Count];
            Array.Fill(map, uint.MaxValue);
            foreach (MeshTriangle t in group.Triangles)
            {
                builder.AddTriangle(WorldVertex(builder, instance, group, map, t.Index0), WorldVertex(builder, instance, group, map, t.Index1), WorldVertex(builder, instance, group, map, t.Index2), id);
                tally.Triangles++;
            }

            drewAny = true;
        }

        if (drewAny)
        {
            tally.Drawn++;
        }
        else
        {
            tally.ModelsWithoutTriangles++;
        }
    }

    private static uint WorldVertex(GeometryBuilder builder, ModelInstance instance, GroupModel group, uint[] map, uint index)
    {
        if (map[index] == uint.MaxValue)
        {
            Vector3 w = VMapFormat.ToWorld(instance.FromModel(group.Vertices[(int)index]));
            map[index] = builder.AddVertex(w.X, w.Y, w.Z);
        }

        return map[index];
    }

    private static void DrawBounds(ChunkParts parts, ModelSpawn spawn, uint slot, ref uint attribute, Tally tally)
    {
        if (!spawn.HasBound)
        {
            tally.NoBound++;
            return;
        }

        (Vector3 low, Vector3 high) = WorldBounds(spawn);
        parts.RollIfFull();
        GeometryBuilder builder = parts.Current;
        uint id = ++attribute;
        var entry = new VMapSpawn(spawn, slot);
        builder.DescribeAttribute(id, () =>
        {
            JsonObject o = DescribeSpawn(spawn.IsM2 ? "m2 bounds" : "wmo bounds", entry);
            o["boundLow"] = Point(low);
            o["boundHigh"] = Point(high);
            return o;
        });

        var corner = new uint[8];
        for (int i = 0; i < 8; i++)
        {
            corner[i] = builder.AddVertex((i & 1) == 0 ? low.X : high.X, (i & 2) == 0 ? low.Y : high.Y, (i & 4) == 0 ? low.Z : high.Z);
        }

        // Cube corner i has bit 0 = X, bit 1 = Y, bit 2 = Z: an edge joins two corners that differ in exactly one bit.
        for (int i = 0; i < 8; i++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                int j = i | bit;
                if (j != i)
                {
                    builder.AddLine(corner[i], corner[j], id);
                }
            }
        }

        tally.Drawn++;
    }

    private static void DrawLiquid(ChunkParts parts, ModelInstance instance, uint slot, ref uint attribute, Tally tally)
    {
        WorldModel model = instance.Model;
        var spawn = new VMapSpawn(instance.Spawn, slot);
        bool drewAny = false;
        for (int g = 0; g < model.Groups.Count; g++)
        {
            GroupModel group = model.Groups[g];
            if (group.Liquid is not { } liquid)
            {
                continue;
            }

            parts.RollIfFull();
            GeometryBuilder builder = parts.Current;
            uint id = ++attribute;
            int width = checked((int)liquid.TilesX);
            int depth = checked((int)liquid.TilesY);
            int stride = width + 1;
            var vertex = new Dictionary<int, uint>();
            int squares = 0;
            int emptySquares = 0;
            int badSquares = 0;

            uint Vertex(int tx, int ty)
            {
                int key = tx + (ty * stride);
                if (!vertex.TryGetValue(key, out uint index))
                {
                    var model3 = new Vector3(liquid.Corner.X + (tx * LiquidTileSize), liquid.Corner.Y + (ty * LiquidTileSize), liquid.Heights[key]);
                    Vector3 w = VMapFormat.ToWorld(instance.FromModel(model3));
                    vertex[key] = index = builder.AddVertex(w.X, w.Y, w.Z);
                }

                return index;
            }

            for (int ty = 0; ty < depth; ty++)
            {
                for (int tx = 0; tx < width; tx++)
                {
                    if ((liquid.Flags[tx + (ty * width)] & 0x0F) == 0x0F)
                    {
                        emptySquares++;
                        continue;
                    }

                    if (!float.IsFinite(liquid.Heights[tx + (ty * stride)]) || !float.IsFinite(liquid.Heights[tx + 1 + (ty * stride)])
                        || !float.IsFinite(liquid.Heights[tx + ((ty + 1) * stride)]) || !float.IsFinite(liquid.Heights[tx + 1 + ((ty + 1) * stride)]))
                    {
                        badSquares++;
                        continue;
                    }

                    uint a = Vertex(tx, ty);
                    uint b = Vertex(tx + 1, ty);
                    uint c = Vertex(tx, ty + 1);
                    uint d = Vertex(tx + 1, ty + 1);

                    // Both triangles share the diagonal from (tx, ty) to (tx + 1, ty + 1), as the server's WmoLiquid height lookup does.
                    builder.AddTriangle(a, b, d, id);
                    builder.AddTriangle(a, d, c, id);
                    squares++;
                }
            }

            if (squares == 0)
            {
                tally.LiquidGroupsWithoutSquares++;
                continue;
            }

            int groupIndex = g;
            builder.DescribeAttribute(id, () =>
            {
                JsonObject o = DescribeSpawn("wmo liquid", spawn);
                o["rootWmoId"] = model.RootWmoId;
                o["group"] = groupIndex;
                o["groupWmoId"] = group.GroupWmoId;
                o["liquidType"] = liquid.Type;
                o["tiles"] = new JsonArray(width, depth);
                o["squares"] = squares;
                o["squaresWithoutLiquid"] = emptySquares;
                o["squaresWithBadHeight"] = badSquares;
                o["note"] = "parsed by the server but read by no query";
                return o;
            });
            tally.LiquidSquares += squares;
            drewAny = true;
        }

        if (drewAny)
        {
            tally.Drawn++;
        }
    }

    private bool Lists(TileKey tile, uint slot)
        => Data.VMapTileFile(tile) is { IsOk: true } file && file.Value.Slots.Contains(slot);

    /// <summary>
    /// Whether this tile is the one that draws the spawn. The owner is the tile containing the centre of the bounds when that tile lists
    /// the slot; otherwise the lowest-indexed tile among those the bounds cover that lists it; a spawn no covering tile lists is drawn by
    /// every tile that lists it (counted).
    /// </summary>
    private bool OwnsHere(TileKey tile, VMapSpawn entry, Tally tally)
    {
        ModelSpawn spawn = entry.Spawn;
        Vector3 low;
        Vector3 high;
        if (spawn.HasBound)
        {
            (low, high) = WorldBounds(spawn);
        }
        else
        {
            low = high = VMapFormat.ToWorld(spawn.Position);
        }

        // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/VMapFormat.cs:28-34 internal = (MapMid - x, MapMid - y, z), its own inverse
        // ref: vmangos@0e3ff01 src/game/vmap/VMapManager2.cpp:51-56 convertPositionToInternalRep
        Vector3 centre = (low + high) * 0.5f;
        if (WorldGrid.TileOf(tile.MapId, centre.X, centre.Y) is { } owner)
        {
            if (owner == tile)
            {
                return true;
            }

            if (Lists(owner, entry.Slot))
            {
                return false;
            }
        }

        int minX = ClampTile(32 - (high.X / WorldGrid.TileSize));
        int maxX = ClampTile(32 - (low.X / WorldGrid.TileSize));
        int minY = ClampTile(32 - (high.Y / WorldGrid.TileSize));
        int maxY = ClampTile(32 - (low.Y / WorldGrid.TileSize));
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                var candidate = new TileKey(tile.MapId, x, y);
                if (Lists(candidate, entry.Slot))
                {
                    if (candidate == tile)
                    {
                        tally.FallbackOwned++;
                        return true;
                    }

                    return false;
                }
            }
        }

        tally.OrphanOwned++;
        return true;
    }

    private static int ClampTile(double index) => (int)Math.Clamp(Math.Floor(index), 0, WorldGrid.TilesPerSide - 1);

    /// <summary>Counters of one tile build, written into the chunk statistics.</summary>
    private sealed class Tally
    {
        private readonly List<string> _missing = [];
        private readonly List<string> _rejected = [];

        public int Listed;
        public int Drawn;
        public int OtherKind;
        public int InvalidSlots;
        public int OwnedElsewhere;
        public int FallbackOwned;
        public int OrphanOwned;
        public int NoBound;
        public int Triangles;
        public int ModelsWithoutTriangles;
        public int MissingModels;
        public int RejectedModels;
        public int LiquidSquares;
        public int LiquidGroupsWithoutSquares;

        public void NoteModel(ParsedFile<WorldModel> model, string name)
        {
            if (model.State == TileState.Rejected)
            {
                RejectedModels++;
                if (_rejected.Count < MaxListedNames)
                {
                    _rejected.Add(model.Detail ?? DisplayName(name));
                }
            }
            else
            {
                MissingModels++;
                string shown = DisplayName(name);
                if (_missing.Count < MaxListedNames && !_missing.Contains(shown))
                {
                    _missing.Add(shown);
                }
            }
        }

        public void WriteTo(JsonObject stats, string source, VMapTreeData tree)
        {
            stats["source"] = source;
            stats["treeSlots"] = tree.SlotCount;
            stats["spawnsListed"] = Listed;
            stats["spawnsDrawn"] = Drawn;
            stats["spawnsOfOtherLayers"] = OtherKind;
            stats["spawnsWithInvalidSlot"] = InvalidSlots;
            stats["spawnsDrawnByAnotherTile"] = OwnedElsewhere;
            stats["spawnsOwnedByFallbackRule"] = FallbackOwned;
            stats["spawnsListedByNoCoveringTile"] = OrphanOwned;
            stats["spawnsWithoutBound"] = NoBound;
            stats["triangles"] = Triangles;
            stats["modelsWithoutTriangles"] = ModelsWithoutTriangles;
            stats["modelFilesMissing"] = MissingModels;
            stats["modelFilesRejected"] = RejectedModels;
            if (_missing.Count > 0)
            {
                stats["missingModels"] = new JsonArray([.. _missing.Select(n => (JsonNode)n)]);
            }

            if (_rejected.Count > 0)
            {
                stats["rejectedModels"] = new JsonArray([.. _rejected.Select(n => (JsonNode)n)]);
            }

            stats["liquidSquares"] = LiquidSquares;
            stats["liquidGroupsWithoutSquares"] = LiquidGroupsWithoutSquares;
        }
    }
}
