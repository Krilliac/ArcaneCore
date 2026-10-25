namespace ArcaneCore.Atlas.Core;

/// <summary>Where a layer's data comes from.</summary>
public enum LayerView
{
    /// <summary>The ArcaneCore.Game readers over the extracted data: what the server sees.</summary>
    Server,

    /// <summary>The 1.12.1 client files read straight from the MPQs, independent of the server code.</summary>
    Client,

    /// <summary>DotRecast (vendored Detour port) re-reading the same navmesh tiles.</summary>
    DotRecast,

    /// <summary>A comparison of a server layer against a reference layer.</summary>
    Diff,

    /// <summary>Entities and paths from a running world daemon's map feed (drawn by the UI from live frames).</summary>
    Live,
}

/// <summary>The layer panel's sections.</summary>
public enum LayerGroup
{
    World,
    Terrain,
    Liquid,
    Collision,
    Navmesh,
    Reference,
    Diff,
    Live,
}

/// <summary>
/// One layer's identity and defaults. <paramref name="AttributeColors"/> maps an attribute key (the
/// <c>colorKey</c> field of a chunk's attribute table entry) to a colour; entries without a key use
/// <paramref name="DefaultColor"/>.
/// </summary>
public sealed record LayerInfo(
    string Id,
    LayerGroup Group,
    LayerView View,
    string Title,
    string Description,
    string DefaultColor,
    bool DefaultVisible,
    IReadOnlyDictionary<string, string>? AttributeColors = null,
    bool Tiled = true);

/// <summary>Layer ids. They are API: commands, saved views and the UI refer to layers by these strings.</summary>
public static class LayerIds
{
    public const string Tiles = "tiles";
    public const string Terrain = "terrain";
    public const string TerrainHoles = "terrain.holes";
    public const string TerrainNoHeight = "terrain.noheight";
    public const string Liquid = "liquid";
    public const string Area = "area";
    public const string VMapWmo = "vmap.wmo";
    public const string VMapM2 = "vmap.m2";
    public const string VMapBounds = "vmap.bounds";
    public const string VMapLiquid = "vmap.liquid";
    public const string NavPoly = "nav.poly";
    public const string NavDetail = "nav.detail";
    public const string NavPortals = "nav.portals";
    public const string NavBvRejected = "nav.bv";
    public const string RefTerrain = "ref.terrain";
    public const string RefLiquid = "ref.liquid";
    public const string RefWmo = "ref.wmo";
    public const string RefM2Collision = "ref.m2.collision";
    public const string RefM2Render = "ref.m2.render";
    public const string RefTerrainTex = "ref.terrain.tex";
    public const string RefWmoTex = "ref.wmo.tex";
    public const string RefM2Tex = "ref.m2.tex";
    public const string RefNavPoly = "ref.nav.poly";
    public const string RefNavLinks = "ref.nav.links";
    public const string DiffTerrain = "diff.terrain";
    public const string DiffModels = "diff.models";
    public const string DiffNav = "diff.nav";
    public const string LivePlayers = "live.players";
    public const string LiveBots = "live.bots";
    public const string LiveCreatures = "live.creatures";
    public const string LiveGameObjects = "live.gameobjects";
    public const string LiveSplines = "live.splines";
    public const string LiveRoutes = "live.routes";
    public const string LiveTrails = "live.trails";
    public const string LiveGrids = "live.grids";
}

/// <summary>Attribute colour keys shared by the server and reference layers so both views colour alike.</summary>
public static class ColorKeys
{
    public const string Water = "water";
    public const string Ocean = "ocean";
    public const string Magma = "magma";
    public const string Slime = "slime";
    public const string DeepWater = "deep";
    public const string Ground = "ground";
    public const string Steep = "steep";
    public const string Indoor = "indoor";
    public const string Outdoor = "outdoor";
    public const string Collidable = "collide";
    public const string RenderOnly = "render";
    public const string OnlyReference = "only-ref";
    public const string OnlyServer = "only-server";
    public const string Expected = "expected";
    public const string Ok = "ok";
    public const string Empty = "empty";
    public const string Missing = "missing";
    public const string Rejected = "rejected";
    public const string Idle = "idle";
    public const string Moving = "moving";
    public const string Chase = "chase";
    public const string Partial = "partial";
}

/// <summary>The fixed set of layers, in panel order.</summary>
public static class LayerCatalog
{
    private static readonly Dictionary<string, string> LiquidColors = new()
    {
        [ColorKeys.Water] = "#4FA3E0",
        [ColorKeys.Ocean] = "#2E6FD1",
        [ColorKeys.Magma] = "#FF6B3D",
        [ColorKeys.Slime] = "#8BD450",
        [ColorKeys.DeepWater] = "#2A4F9E",
    };

    private static readonly Dictionary<string, string> NavColors = new()
    {
        [ColorKeys.Ground] = "#3CCB9A",
        [ColorKeys.Steep] = "#C9A227",
        [ColorKeys.Water] = "#49B6FF",
        [ColorKeys.Magma] = "#FF6B3D",
        [ColorKeys.Slime] = "#8BD450",
    };

    private static readonly Dictionary<string, string> TileColors = new()
    {
        [ColorKeys.Ok] = "#3B4E5A",
        [ColorKeys.Empty] = "#5E6B73",
        [ColorKeys.Missing] = "#2A353C",
        [ColorKeys.Rejected] = "#E5484D",
    };

    private static readonly Dictionary<string, string> WmoColors = new()
    {
        [ColorKeys.Indoor] = "#D9C9A3",
        [ColorKeys.Outdoor] = "#E8E3D9",
    };

    private static readonly Dictionary<string, string> RefWmoColors = new()
    {
        [ColorKeys.Collidable] = "#C6B8FF",
        [ColorKeys.RenderOnly] = "#8E7FD6",
    };

    private static readonly Dictionary<string, string> DiffColors = new()
    {
        [ColorKeys.OnlyReference] = "#FF3EA5",
        [ColorKeys.OnlyServer] = "#39E5FF",
        [ColorKeys.Expected] = "#7C8A93",
    };

    private static readonly Dictionary<string, string> CreatureColors = new()
    {
        [ColorKeys.Idle] = "#C9D1D6",
        [ColorKeys.Moving] = "#FFD34D",
        [ColorKeys.Chase] = "#FF5A5A",
    };

    private static readonly Dictionary<string, string> RouteColors = new()
    {
        [ColorKeys.Ok] = "#7FD3FF",
        [ColorKeys.Partial] = "#F5A524",
    };

    public static IReadOnlyList<LayerInfo> All { get; } =
    [
        new(LayerIds.Tiles, LayerGroup.World, LayerView.Server, "Tile grid",
            "Tile borders coloured by which data files exist and parse.", "#3B4E5A", true, TileColors),
        new(LayerIds.Terrain, LayerGroup.Terrain, LayerView.Server, "Terrain",
            "Height grid as the server interpolates it: four triangles per square around the centre sample.", "#B9A77A", true),
        new(LayerIds.TerrainHoles, LayerGroup.Terrain, LayerView.Server, "Terrain holes",
            "Squares the server treats as holes (no terrain height).", "#E5484D", true),
        new(LayerIds.TerrainNoHeight, LayerGroup.Terrain, LayerView.Server, "Flat tiles",
            "Tiles stored without heights; the server answers their single grid height everywhere.", "#F5A524", true),
        new(LayerIds.Area, LayerGroup.Terrain, LayerView.Server, "Area cells",
            "Area flag borders per 33.3 yd cell.", "#7C8A93", false),
        new(LayerIds.Liquid, LayerGroup.Liquid, LayerView.Server, "Terrain liquid",
            "Liquid surfaces per height square from the .map liquid section.", "#4FA3E0", true, LiquidColors),
        new(LayerIds.VMapLiquid, LayerGroup.Liquid, LayerView.Server, "Model liquid",
            "WMO liquid parsed from the vmaps (the server parses it but no query reads it).", "#7FC8F8", false),
        new(LayerIds.VMapWmo, LayerGroup.Collision, LayerView.Server, "WMO collision",
            "World-model triangles the server collides with, placed in world space.", "#E8E3D9", true, WmoColors),
        new(LayerIds.VMapM2, LayerGroup.Collision, LayerView.Server, "M2 collision",
            "Doodad triangles; spell line of sight ignores these, movement checks do not.", "#F2A541", true),
        new(LayerIds.VMapBounds, LayerGroup.Collision, LayerView.Server, "Model bounds",
            "Spawn bounding boxes from the vmap tiles.", "#6E7C85", false),
        new(LayerIds.NavPoly, LayerGroup.Navmesh, LayerView.Server, "Navmesh polygons",
            "Navmesh polygons coloured by the flags the server filters on.", "#3CCB9A", true, NavColors),
        new(LayerIds.NavDetail, LayerGroup.Navmesh, LayerView.Server, "Navmesh detail",
            "Detail triangles used for polygon heights.", "#2A8F6E", false),
        new(LayerIds.NavPortals, LayerGroup.Navmesh, LayerView.Server, "Tile portals",
            "Cross-tile connections the server derives from overlapping border edges.", "#FFFFFF", false),
        new(LayerIds.NavBvRejected, LayerGroup.Navmesh, LayerView.Server, "BV tree rejected",
            "Tiles whose BV tree failed verification (nearest-polygon lookups scan linearly).", "#E5484D", false),
        new(LayerIds.RefTerrain, LayerGroup.Reference, LayerView.Client, "Client terrain",
            "ADT heights straight from the client MPQs (9x9 + 8x8 samples per chunk).", "#8FA9B8", false),
        new(LayerIds.RefLiquid, LayerGroup.Reference, LayerView.Client, "Client liquid",
            "Every MCLQ liquid layer from the client, before the extractor merges them.", "#5DD6E8", false, LiquidColors),
        new(LayerIds.RefWmo, LayerGroup.Reference, LayerView.Client, "Client WMOs",
            "Every WMO group triangle from the client, split into collidable and render-only faces.", "#C6B8FF", false, RefWmoColors),
        new(LayerIds.RefM2Collision, LayerGroup.Reference, LayerView.Client, "Client M2 collision",
            "M2 bounding (collision) triangles from the client.", "#FFC97A", false),
        new(LayerIds.RefM2Render, LayerGroup.Reference, LayerView.Client, "Client M2 meshes",
            "M2 render meshes from the client, for seeing what has no collision.", "#B07AD8", false),
        new(LayerIds.RefTerrainTex, LayerGroup.Reference, LayerView.Client, "Client terrain (textured)",
            "Client terrain with its ground textures. Reserved: the source arrives with the terrain texture lane.", "#8FA9B8", false),
        new(LayerIds.RefWmoTex, LayerGroup.Reference, LayerView.Client, "Client WMOs (textured)",
            "Client WMO groups with their materials. Reserved: the source arrives with the model material lane.", "#C6B8FF", false),
        new(LayerIds.RefM2Tex, LayerGroup.Reference, LayerView.Client, "Client M2 models (textured)",
            "Client M2 doodads with their textures. Reserved: the source arrives with the model material lane.", "#B07AD8", false),
        new(LayerIds.RefNavPoly, LayerGroup.Reference, LayerView.DotRecast, "DotRecast polygons",
            "The same navmesh tiles read by DotRecast.", "#FFD34D", false),
        new(LayerIds.RefNavLinks, LayerGroup.Reference, LayerView.DotRecast, "DotRecast links",
            "Detour links between polygons, including off-mesh connections the server skips.", "#FF8AD8", false),
        new(LayerIds.DiffTerrain, LayerGroup.Diff, LayerView.Diff, "Terrain difference",
            "Server height minus client height at every sample; holes that disagree.", "#FF3EA5", false, DiffColors),
        new(LayerIds.DiffModels, LayerGroup.Diff, LayerView.Diff, "Model difference",
            "Collision triangles present in only one of the server vmaps and the client files.", "#FF3EA5", false, DiffColors),
        new(LayerIds.DiffNav, LayerGroup.Diff, LayerView.Diff, "Navmesh difference",
            "Polygons and links where the server reader and DotRecast disagree.", "#FF3EA5", false, DiffColors),
        new(LayerIds.LivePlayers, LayerGroup.Live, LayerView.Live, "Players",
            "Connected players from the world daemon's map feed.", "#FFFFFF", true, Tiled: false),
        new(LayerIds.LiveBots, LayerGroup.Live, LayerView.Live, "Bots",
            "Managed bots from the map feed.", "#7FD3FF", true, Tiled: false),
        new(LayerIds.LiveCreatures, LayerGroup.Live, LayerView.Live, "Creatures",
            "Creatures coloured by motion: idle, moving or chasing.", "#C9D1D6", true, CreatureColors, Tiled: false),
        new(LayerIds.LiveGameObjects, LayerGroup.Live, LayerView.Live, "Game objects",
            "Spawned game objects from the map feed.", "#A3B1BA", false, Tiled: false),
        new(LayerIds.LiveSplines, LayerGroup.Live, LayerView.Live, "Movement splines",
            "Creature movement splines with a head at the current progress.", "#3CCB9A", true, Tiled: false),
        new(LayerIds.LiveRoutes, LayerGroup.Live, LayerView.Live, "Bot routes",
            "Navmesh routes bots are following; partial routes are dashed.", "#7FD3FF", true, RouteColors, Tiled: false),
        new(LayerIds.LiveTrails, LayerGroup.Live, LayerView.Live, "Trails",
            "Recent positions, fading with age.", "#93A1AA", false, Tiled: false),
        new(LayerIds.LiveGrids, LayerGroup.Live, LayerView.Live, "Loaded grids",
            "Grids the world daemon has loaded on this map.", "#1F5FFF", false, Tiled: false),
    ];

    private static readonly Dictionary<string, LayerInfo> ById = All.ToDictionary(l => l.Id, StringComparer.Ordinal);

    public static bool TryGet(string id, out LayerInfo info) => ById.TryGetValue(id, out info!);

    public static LayerInfo Get(string id)
        => ById.TryGetValue(id, out LayerInfo? info) ? info : throw new KeyNotFoundException($"Unknown layer '{id}'.");
}
