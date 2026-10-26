using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.Atlas.Server;

/// <summary>Why the server engine refused a query.</summary>
public enum ServerErrorKind
{
    /// <summary>The query names something the request cannot hold (a range, a size).</summary>
    BadRequest,

    /// <summary>The data the query needs is not there (no data directory, no files for the map, no navmesh).</summary>
    Unavailable,
}

/// <summary>A query the server engine refused; the message says exactly what was missing or out of range.</summary>
public sealed class ServerEngineException(ServerErrorKind kind, string message) : Exception(message)
{
    /// <summary>The category of the refusal.</summary>
    public ServerErrorKind Kind { get; } = kind;
}

/// <summary>JSON helpers shared by the report records.</summary>
internal static class ReportJson
{
    public static JsonNode? Number(float? value) => value is { } v && float.IsFinite(v) ? (double)v : null;

    public static JsonNode Number(float value) => float.IsFinite(value) ? (double)value : throw new ArgumentException($"{value} is not finite.", nameof(value));

    public static JsonNode? Point(Vector3? value) => value is { } v ? ServerOverlay.Triple(v) : null;

    /// <summary>A height as the server reports it: null where it answers the invalid-height marker.</summary>
    public static float? Height(float z) => float.IsFinite(z) && z > TerrainTile.InvalidHeight ? z : null;
}

/// <summary>What the terrain file answers at a position.</summary>
/// <param name="OutsideMap">The position is outside the 64 x 64 tile grid.</param>
/// <param name="TileX">Terrain tile X (from world X).</param>
/// <param name="TileY">Terrain tile Y (from world Y).</param>
/// <param name="FileLoaded">The tile came from a file (false: the server's empty tile).</param>
/// <param name="Storage">float, packed or none (see TerrainLayerSource).</param>
/// <param name="Row">Height square row (0..127), from world X.</param>
/// <param name="Col">Height square column (0..127), from world Y.</param>
/// <param name="Z">The server's ground height, or null where it answers the invalid-height marker.</param>
/// <param name="Hole">The server answers no height because the square is a hole.</param>
/// <param name="HoleMaskCovers">The hole mask covers the square, whether or not the storage consults it.</param>
/// <param name="NoHeight">The tile stores no heights, so the answer is its single grid height (or none).</param>
/// <param name="AreaFlag">The area flag of the 16 x 16 cell.</param>
public sealed record TerrainSample(
    bool OutsideMap,
    int TileX,
    int TileY,
    bool FileLoaded,
    string Storage,
    int Row,
    int Col,
    float? Z,
    bool Hole,
    bool HoleMaskCovers,
    bool NoHeight,
    ushort AreaFlag)
{
    /// <summary>The <c>terrain</c> member of the height report.</summary>
    public JsonObject ToJson() => new()
    {
        ["outsideMap"] = OutsideMap,
        ["tile"] = new JsonArray(TileX, TileY),
        ["fileLoaded"] = FileLoaded,
        ["storage"] = Storage,
        ["z"] = ReportJson.Number(Z),
        ["hole"] = Hole,
        ["holeMaskCovers"] = HoleMaskCovers,
        ["noHeight"] = NoHeight,
        ["square"] = new JsonArray(Row, Col),
    };
}

/// <summary>The liquid at a position.</summary>
/// <param name="Status">The server's answer for any of water, magma, ocean and slime (<c>TerrainInfo.GetLiquidStatus</c>, which also refuses a surface at or below the ground).</param>
/// <param name="AnyTypeStatus">The tile's own answer for any liquid type, deep and WMO water included.</param>
/// <param name="Type">The liquid type byte of the cell.</param>
/// <param name="TypeName">The names of its flags.</param>
/// <param name="Entry">The liquid type entry stored with the cell.</param>
/// <param name="Level">The liquid surface height, or null without liquid.</param>
/// <param name="GroundZ">The ground height under the liquid, or null without liquid.</param>
public sealed record LiquidSample(string Status, string AnyTypeStatus, byte Type, string TypeName, uint Entry, float? Level, float? GroundZ)
{
    /// <summary>The liquid column above the ground, or null without liquid.</summary>
    public float? Depth => Level is { } l && GroundZ is { } g ? l - g : null;

    /// <summary>The <c>liquid</c> member of the height report.</summary>
    public JsonObject ToJson() => new()
    {
        ["status"] = Status,
        ["anyTypeStatus"] = AnyTypeStatus,
        ["type"] = Type,
        ["typeName"] = TypeName,
        ["entry"] = Entry,
        ["level"] = ReportJson.Number(Level),
        ["depth"] = ReportJson.Number(Depth),
        ["groundZ"] = ReportJson.Number(GroundZ),
    };
}

/// <summary>The model floor under a position.</summary>
/// <param name="Available">The vmap service is on for height queries.</param>
/// <param name="Coverage">The server's <see cref="ModelCoverage"/> of the position's tile.</param>
/// <param name="ProbeZ">Where the downward search starts (the query's z plus 2, as <c>MapCollision.GetHeight</c> does).</param>
/// <param name="Search">How far below the probe the search reaches.</param>
/// <param name="Z">The nearest model surface below the probe (M2 models count, as in <c>GetModelHeight</c>), or null.</param>
/// <param name="ZIgnoringM2">The same search with doodad (M2) models ignored, or null.</param>
public sealed record ModelSample(bool Available, string Coverage, float ProbeZ, float Search, float? Z, float? ZIgnoringM2)
{
    /// <summary>The <c>models</c> member of the height report.</summary>
    public JsonObject ToJson() => new()
    {
        ["available"] = Available,
        ["coverage"] = Coverage,
        ["probeZ"] = ReportJson.Number(ProbeZ),
        ["search"] = ReportJson.Number(Search),
        ["z"] = ReportJson.Number(Z),
        ["ignoreM2"] = new JsonObject { ["false"] = ReportJson.Number(Z), ["true"] = ReportJson.Number(ZIgnoringM2) },
    };
}

/// <summary>The WMO group enclosing a position, as <c>TryGetAreaInfo</c> finds it.</summary>
public sealed record IndoorSample(bool InsideWmo, uint MogpFlags, int AdtId, int RootId, int GroupId, float GroundZ, bool Outdoors)
{
    /// <summary>The <c>indoor</c> member of the height report.</summary>
    public JsonObject ToJson() => new()
    {
        ["insideWmo"] = InsideWmo,
        ["outdoors"] = Outdoors,
        ["mogpFlags"] = InsideWmo ? $"0x{MogpFlags:X}" : null,
        ["adtId"] = InsideWmo ? AdtId : null,
        ["rootId"] = InsideWmo ? RootId : null,
        ["groupId"] = InsideWmo ? GroupId : null,
        ["groundZ"] = InsideWmo ? ReportJson.Number((float?)GroundZ) : null,
    };
}

/// <summary>A navmesh polygon located by a query.</summary>
public sealed record NavPolyInfo(int DetourX, int DetourY, int Poly, ushort Flags, byte Area, float PointZ, float Distance)
{
    /// <summary>The polygon as the <c>polyRef</c> of the height report.</summary>
    public JsonObject ToJson() => new()
    {
        ["tile"] = new JsonArray(DetourX, DetourY),
        ["poly"] = Poly,
        ["flags"] = $"0x{Flags:X}",
        ["flagNames"] = NavLayerSource.NavFlagNames(Flags),
        ["area"] = Area,
        ["pointZ"] = ReportJson.Number(PointZ),
        ["distance"] = ReportJson.Number(Distance),
    };
}

/// <summary>The navmesh floor under a position.</summary>
/// <param name="Available">The map has navmesh parameters.</param>
/// <param name="TileLoaded">The Detour tile under the position is loaded.</param>
/// <param name="FloorZ">The server's mesh floor within the same window the model floor uses, or null.</param>
/// <param name="Nearest">The nearest polygon within the server's near extents, or null.</param>
public sealed record MeshSample(bool Available, bool TileLoaded, float? FloorZ, NavPolyInfo? Nearest)
{
    /// <summary>The <c>mesh</c> member of the height report.</summary>
    public JsonObject ToJson() => new()
    {
        ["available"] = Available,
        ["tileLoaded"] = TileLoaded,
        ["z"] = ReportJson.Number(FloorZ),
        ["polyRef"] = Nearest?.ToJson(),
    };
}

/// <summary>The result of <c>query.height</c>.</summary>
public sealed record HeightReport(
    uint MapId,
    Vector3 At,
    TerrainSample Terrain,
    LiquidSample Liquid,
    ModelSample Models,
    float? CollisionZ,
    IndoorSample Indoor,
    MeshSample Mesh,
    UnderMapVerdict UnderMap)
{
    /// <summary>The report as JSON without files and overlay (the command adds them).</summary>
    public JsonObject ToJson() => new()
    {
        ["map"] = MapId,
        ["at"] = ServerOverlay.Triple(At),
        ["terrain"] = Terrain.ToJson(),
        ["liquid"] = Liquid.ToJson(),
        ["models"] = Models.ToJson(),
        ["collision"] = new JsonObject { ["z"] = ReportJson.Number(CollisionZ), ["note"] = "MapCollision.GetHeight: terrain and model floors combined as the server does" },
        ["area"] = new JsonObject { ["flags"] = Terrain.AreaFlag },
        ["indoor"] = Indoor.ToJson(),
        ["mesh"] = Mesh.ToJson(),
        ["underMap"] = new JsonObject
        {
            ["kind"] = UnderMap.Kind.ToString(),
            ["depth"] = UnderMap.Kind == UnderMapKind.UnderMap ? ReportJson.Number(UnderMap.DepthYards) : null,
        },
    };
}

/// <summary>One side of a line-of-sight answer.</summary>
/// <param name="Clear">No model blocks the segment.</param>
/// <param name="Hit">The nearest blocking point, when blocked.</param>
public sealed record LosSide(bool Clear, Vector3? Hit)
{
    /// <summary>The side as JSON.</summary>
    public JsonObject ToJson() => new() { ["clear"] = Clear, ["hit"] = ReportJson.Point(Hit) };
}

/// <summary>Terrain along a segment, sampled against the server's ground height.</summary>
/// <param name="Clear">No sample lies below the ground.</param>
/// <param name="Hit">The first point found under the ground.</param>
/// <param name="Samples">Positions tested.</param>
/// <param name="Unknown">Samples where the tile has no height (not counted as blocking).</param>
public sealed record TerrainLos(bool Clear, Vector3? Hit, int Samples, int Unknown)
{
    /// <summary>The terrain side as JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["clear"] = Clear,
        ["hit"] = ReportJson.Point(Hit),
        ["samples"] = Samples,
        ["samplesWithoutHeight"] = Unknown,
        ["note"] = "sampled once per yard against TerrainInfo.GetHeight; the server's own line of sight tests models only",
    };
}

/// <summary>The result of <c>query.los</c>.</summary>
public sealed record LosReport(
    uint MapId,
    Vector3 From,
    Vector3 To,
    float Length,
    LosSide Spell,
    LosSide Movement,
    TerrainLos Terrain,
    bool ModelsAvailable,
    int ModelTiles)
{
    /// <summary>The report as JSON without the overlay.</summary>
    public JsonObject ToJson() => new()
    {
        ["map"] = MapId,
        ["a"] = ServerOverlay.Triple(From),
        ["b"] = ServerOverlay.Triple(To),
        ["length"] = ReportJson.Number(Length),
        ["modelsAvailable"] = ModelsAvailable,
        ["modelTilesLoaded"] = ModelTiles,
        ["spell"] = Spell.ToJson(),
        ["movement"] = Movement.ToJson(),
        ["terrain"] = Terrain.ToJson(),
    };
}

/// <summary>A polygon on a path's corridor.</summary>
public sealed record PolyStep(int DetourX, int DetourY, int Poly, ushort Flags, byte Area)
{
    /// <summary>The step as JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["tile"] = new JsonArray(DetourX, DetourY),
        ["poly"] = Poly,
        ["flags"] = $"0x{Flags:X}",
        ["area"] = Area,
    };
}

/// <summary>The result of <c>query.path</c>.</summary>
public sealed record PathReport(
    uint MapId,
    Vector3 Start,
    Vector3 End,
    PathType Type,
    IReadOnlyList<Vector3> Points,
    float Length,
    IReadOnlyList<PolyStep> Polys,
    bool CorridorComplete,
    string CorridorNote,
    PathMover Mover,
    PathOptions Options)
{
    /// <summary>A one-word summary of <see cref="Type"/>.</summary>
    public string Status
    {
        get
        {
            if ((Type & PathType.TilesPending) != 0)
            {
                return "pending";
            }

            if ((Type & PathType.NoPath) != 0)
            {
                return "none";
            }

            if ((Type & (PathType.NotUsingPath | PathType.Shortcut)) != 0)
            {
                return "straight-line";
            }

            return (Type & PathType.Incomplete) != 0 ? "incomplete" : "complete";
        }
    }

    /// <summary>The names of the set <see cref="PathType"/> flags.</summary>
    public IReadOnlyList<string> FlagNames
    {
        get
        {
            var names = new List<string>();
            foreach (PathType flag in Enum.GetValues<PathType>())
            {
                if (flag != PathType.Blank && (Type & flag) == flag)
                {
                    names.Add(flag.ToString());
                }
            }

            return names;
        }
    }

    /// <summary>The report as JSON without the overlay.</summary>
    public JsonObject ToJson() => new()
    {
        ["map"] = MapId,
        ["a"] = ServerOverlay.Triple(Start),
        ["b"] = ServerOverlay.Triple(End),
        ["status"] = Status,
        ["flags"] = new JsonArray([.. FlagNames.Select(n => (JsonNode)n)]),
        ["flagBits"] = (int)Type,
        ["points"] = new JsonArray([.. Points.Select(p => (JsonNode)ServerOverlay.Triple(p))]),
        ["length"] = ReportJson.Number(Length),
        ["polys"] = new JsonArray([.. Polys.Select(p => (JsonNode)p.ToJson())]),
        ["corridorComplete"] = CorridorComplete,
        ["corridorNote"] = CorridorNote,
        ["mover"] = new JsonObject { ["canWalk"] = Mover.CanWalk, ["canSwim"] = Mover.CanSwim, ["canFly"] = Mover.CanFly, ["isPlayer"] = Mover.IsPlayer },
        ["options"] = new JsonObject
        {
            ["loadTiles"] = Options.LoadTiles,
            ["allowPartial"] = Options.AllowPartial,
            ["maxPoints"] = Options.MaxPoints,
            ["maxSearchNodes"] = Options.MaxSearchNodes,
            ["endTolerance"] = ReportJson.Number(Options.EndTolerance),
            ["wideEndSearch"] = Options.WideEndSearch,
            ["includeFlags"] = (int)Options.EffectiveIncludeFlags,
            ["excludeFlags"] = (int)Options.ExcludeFlags,
        },
    };
}

/// <summary>One surface of a vertical column.</summary>
/// <param name="Kind">terrain, model, liquid or mesh.</param>
/// <param name="Z">The surface height.</param>
/// <param name="Detail">What is known about it.</param>
public sealed record FloorSurface(string Kind, float Z, JsonObject Detail)
{
    /// <summary>The surface as JSON.</summary>
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind, ["z"] = ReportJson.Number(Z) };
        foreach ((string key, JsonNode? value) in Detail)
        {
            o[key] = value?.DeepClone();
        }

        return o;
    }
}

/// <summary>The result of <c>query.floorstack</c>.</summary>
public sealed record FloorStackReport(uint MapId, float X, float Y, float ZMin, float ZMax, IReadOnlyList<FloorSurface> Surfaces, bool Truncated)
{
    /// <summary>The report as JSON without files and overlay.</summary>
    public JsonObject ToJson() => new()
    {
        ["map"] = MapId,
        ["x"] = ReportJson.Number(X),
        ["y"] = ReportJson.Number(Y),
        ["zMin"] = ReportJson.Number(ZMin),
        ["zMax"] = ReportJson.Number(ZMax),
        ["truncated"] = Truncated,
        ["surfaces"] = new JsonArray([.. Surfaces.Select(s => (JsonNode)s.ToJson())]),
    };
}

/// <summary>A snapshot of the server engine for <c>server.status</c>.</summary>
public sealed record ServerStatus(
    bool EngineStarted,
    string? DataDirectory,
    bool TerrainAvailable,
    bool ModelsAvailable,
    bool MeshAvailable,
    IReadOnlyList<uint> Maps,
    int ResidentTerrainTiles,
    int ResidentModelTiles,
    int ResidentNavTiles,
    int ModelFilesLoaded,
    int ModelCacheResets,
    long QueriesRun,
    long QueriesRefused,
    long QueriesFailed,
    double LastQueryMs,
    int PendingJobs,
    long CompletedJobs,
    IReadOnlyDictionary<string, ParseCacheStats> Caches)
{
    /// <summary>The status as JSON.</summary>
    public JsonObject ToJson()
    {
        var caches = new JsonObject();
        foreach ((string name, ParseCacheStats s) in Caches)
        {
            caches[name] = new JsonObject { ["entries"] = s.Entries, ["bytes"] = s.Cost, ["hits"] = s.Hits, ["loads"] = s.Loads };
        }

        var paths = new ArcaneCore.Atlas.Core.AtlasPaths(DataDirectory, null);
        return new JsonObject
        {
            ["engineStarted"] = EngineStarted,
            ["dataDirectory"] = DataDirectory,
            ["directories"] = new JsonObject
            {
                ["maps"] = paths.MapsDirectory,
                ["vmaps"] = paths.VMapsDirectory,
                ["mmaps"] = paths.MMapsDirectory,
            },
            ["readers"] = Readers(),
            ["terrainAvailable"] = TerrainAvailable,
            ["modelsAvailable"] = ModelsAvailable,
            ["meshAvailable"] = MeshAvailable,
            ["maps"] = new JsonArray([.. Maps.Select(m => (JsonNode)m)]),
            ["resident"] = new JsonObject
            {
                ["terrainTiles"] = ResidentTerrainTiles,
                ["modelTiles"] = ResidentModelTiles,
                ["navTiles"] = ResidentNavTiles,
                ["modelFilesLoaded"] = ModelFilesLoaded,
                ["modelCacheResets"] = ModelCacheResets,
            },
            ["queries"] = new JsonObject
            {
                ["run"] = QueriesRun,
                ["refused"] = QueriesRefused,
                ["failed"] = QueriesFailed,
                ["lastMs"] = Math.Round(LastQueryMs, 3),
            },
            ["ownerThread"] = new JsonObject { ["pending"] = PendingJobs, ["completed"] = CompletedJobs },
            ["parsedFileCaches"] = caches,
        };
    }

    /// <summary>The file versions the server's readers accept: what a data directory must hold for the engine to read it.</summary>
    public static JsonObject Readers()
    {
        // ref: src/ArcaneCore.Game/Maps/Terrain/TerrainTile.cs:38-39 "MAPS" + "z1.4" (vmangos vmangos@0e3ff01 src/game/Maps/GridMap.cpp:35-36)
        // ref: src/ArcaneCore.Game/Maps/Collision/VMaps/VMapFormat.cs:13 "VMAP_7.0"
        // ref: src/ArcaneCore.Game/Maps/Collision/MMaps/NavMeshFormat.cs:17-26 MMAP magic, Detour version 7, mmap version 6, DNAV
        return new JsonObject
        {
            ["map"] = new JsonObject { ["reader"] = nameof(TerrainTile), ["magic"] = Ascii(TerrainTile.MapMagic), ["version"] = Ascii(TerrainTile.MapVersionMagic) },
            ["vmap"] = new JsonObject { ["reader"] = "VMapManager", ["magic"] = ArcaneCore.Game.Maps.Collision.VMaps.VMapFormat.Magic },
            ["mmap"] = new JsonObject
            {
                ["reader"] = "NavMeshTile",
                ["magic"] = $"0x{ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.MmapMagic:X8}",
                ["mmapVersion"] = ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.MmapVersion,
                ["detourVersion"] = ArcaneCore.Game.Maps.Collision.MMaps.NavMeshFormat.DetourVersion,
            },
        };
    }

    // The magics are four ASCII bytes read as a little-endian u32.
    private static string Ascii(uint value)
        => string.Create(4, value, static (span, v) =>
        {
            for (int i = 0; i < 4; i++)
            {
                span[i] = (char)((v >> (8 * i)) & 0xFF);
            }
        });
}

/// <summary>One sample along the segment of <c>query.profile</c>.</summary>
/// <param name="T">Interpolation fraction in <c>0..1</c> from start to end.</param>
/// <param name="Distance">Planar distance in yards from the start point.</param>
/// <param name="X">World X coordinate.</param>
/// <param name="Y">World Y coordinate.</param>
/// <param name="TerrainZ">Ground height from the terrain file, or null where absent/hole.</param>
/// <param name="FloorZ">Combined floor height (<c>CollisionZ</c>, else <c>TerrainZ</c>), or null.</param>
/// <param name="LiquidZ">Liquid surface height, or null without liquid.</param>
/// <param name="HasLiquid">True when liquid is present at the sample.</param>
/// <param name="DeltaFloorZ">Signed change in <paramref name="FloorZ"/> from the previous sample (0 on the first sample, or null when either sample has no floor).</param>
/// <param name="GradeDegrees">Slope angle in degrees from the previous sample, or null on the first sample or when either sample has no floor.</param>
public sealed record ProfileSample(
    float T,
    float Distance,
    float X,
    float Y,
    float? TerrainZ,
    float? FloorZ,
    float? LiquidZ,
    bool HasLiquid,
    float? DeltaFloorZ,
    float? GradeDegrees)
{
    /// <summary>The sample as JSON.</summary>
    public JsonObject ToJson() => new()
    {
        ["t"] = ReportJson.Number(T),
        ["distance"] = ReportJson.Number(Distance),
        ["x"] = ReportJson.Number(X),
        ["y"] = ReportJson.Number(Y),
        ["terrainZ"] = ReportJson.Number(TerrainZ),
        ["floorZ"] = ReportJson.Number(FloorZ),
        ["liquidZ"] = ReportJson.Number(LiquidZ),
        ["hasLiquid"] = HasLiquid,
        ["deltaFloorZ"] = ReportJson.Number(DeltaFloorZ),
        ["gradeDegrees"] = ReportJson.Number(GradeDegrees),
    };
}

/// <summary>The result of <c>query.profile</c>.</summary>
public sealed record ProfileReport(
    uint MapId,
    Vector3 From,
    Vector3 To,
    int Steps,
    float TotalHorizontalDistance,
    float? MinTerrainZ,
    float? MaxTerrainZ,
    float? MinFloorZ,
    float? MaxFloorZ,
    float? MaxGradeDegrees,
    IReadOnlyList<ProfileSample> Samples)
{
    /// <summary>The report as JSON without the overlay.</summary>
    public JsonObject ToJson() => new()
    {
        ["map"] = MapId,
        ["from"] = ServerOverlay.Triple(From),
        ["to"] = ServerOverlay.Triple(To),
        ["steps"] = Steps,
        ["totalHorizontalDistance"] = ReportJson.Number(TotalHorizontalDistance),
        ["minTerrainZ"] = ReportJson.Number(MinTerrainZ),
        ["maxTerrainZ"] = ReportJson.Number(MaxTerrainZ),
        ["minFloorZ"] = ReportJson.Number(MinFloorZ),
        ["maxFloorZ"] = ReportJson.Number(MaxFloorZ),
        ["maxGradeDegrees"] = ReportJson.Number(MaxGradeDegrees),
        ["samples"] = new JsonArray([.. Samples.Select(s => (JsonNode)s.ToJson())]),
    };
}
