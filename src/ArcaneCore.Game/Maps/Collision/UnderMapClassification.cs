namespace ArcaneCore.Game.Maps.Collision;

/// <summary>The answer of <see cref="IUnderMapClassifier.ClassifyUnderMap"/>.</summary>
public enum UnderMapKind
{
    /// <summary>The point is not under the map (above the terrain, or on/under/in a model).</summary>
    Ok,

    /// <summary>The point is below the terrain and no static model explains it.</summary>
    UnderMap,

    /// <summary>The data to decide is not loaded (no terrain tile, or a vmap tree whose tile for this point is not loaded). Never a report.</summary>
    Unknown,
}

/// <summary>An under-map verdict; <see cref="DepthYards"/> is the raw terrain height minus z and is meaningful for <see cref="UnderMapKind.UnderMap"/> only.</summary>
public readonly record struct UnderMapVerdict(UnderMapKind Kind, float DepthYards)
{
    public static UnderMapVerdict Ok { get; } = new(UnderMapKind.Ok, 0f);

    public static UnderMapVerdict Unknown { get; } = new(UnderMapKind.Unknown, 0f);

    public static UnderMapVerdict Under(float depthYards) => new(UnderMapKind.UnderMap, depthYards);
}

/// <summary>
/// Decides whether a point lies under the map, consulting the static models as well as the terrain: a cave, a city or a cellar is below the
/// terrain surface on purpose. A seam so detectors can be tested without extracted data. Thread affinity: world thread.
/// </summary>
public interface IUnderMapClassifier
{
    /// <summary>
    /// <see cref="UnderMapKind.UnderMap"/> only when the terrain is more than <paramref name="minDepthYards"/> above <paramref name="z"/>
    /// and nothing explains it. <see cref="UnderMapKind.Unknown"/> never convicts.
    /// </summary>
    UnderMapVerdict ClassifyUnderMap(float x, float y, float z, float minDepthYards = 0f);
}

/// <summary>How much of the model world is loaded around a point.</summary>
public enum ModelCoverage
{
    /// <summary>The map has no model tree: terrain is all there is.</summary>
    None,

    /// <summary>Model height queries are switched off while the service is on: models cannot explain a position, so the answer is unknown.</summary>
    Unavailable,

    /// <summary>The map has a tiled model tree but the tile of this point was never loaded: models may exist and be unseen. A tile with no vmtile file counts as loaded (and empty) once its load was attempted.</summary>
    TileNotLoaded,

    /// <summary>Models around this point are loaded (or the tree is not tiled).</summary>
    Loaded,
}

/// <summary>Optional on an <see cref="ILineOfSight"/>: whether the model tile of a terrain tile is loaded.</summary>
public interface IModelCoverage
{
    /// <summary>May read the map's model tree from disk on first use.</summary>
    ModelCoverage CoverageAt(uint mapId, int tileX, int tileY);
}
