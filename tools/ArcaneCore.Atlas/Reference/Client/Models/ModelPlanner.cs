using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>Which instances a plan should resolve (loading a model costs file reads, so layers ask only for what they draw).</summary>
[Flags]
internal enum PlanScope
{
    None = 0,

    /// <summary>WMO placements (MODF).</summary>
    Wmo = 1,

    /// <summary>M2 placements (MDDF).</summary>
    M2 = 2,

    /// <summary>The M2 doodads of each WMO placement's selected doodad set (needs the WMO).</summary>
    Doodads = 4,

    All = Wmo | M2 | Doodads,
}

/// <summary>One resolved WMO placement.</summary>
internal sealed class PlannedWmo
{
    public required ClientPlacement Placement { get; init; }

    public required WmoModel Model { get; init; }

    public required InstanceTransform Transform { get; init; }

    /// <summary>The placement position in world space (after the extractor's (0, 0) rule).</summary>
    public required WorldPoint Position { get; init; }

    /// <summary>True when the extractor's raw (0, 0) rule moved this WMO to world (0, 0).</summary>
    public bool OriginQuirk { get; init; }

    /// <summary>Why the extractor drops the whole instance, or null.</summary>
    public string? SkipReason { get; init; }
}

/// <summary>One resolved M2 instance: an MDDF placement or a doodad of a WMO placement.</summary>
internal sealed class PlannedM2
{
    public required bool IsWmoDoodad { get; init; }

    public required string ModelPath { get; init; }

    public required M2ModelFile Model { get; init; }

    public required InstanceTransform Transform { get; init; }

    public required WorldPoint Position { get; init; }

    /// <summary>The MDDF unique id, or the owning WMO's unique id for a doodad.</summary>
    public required uint UniqueId { get; init; }

    /// <summary>Index into the WMO's MODD list for a doodad, else -1.</summary>
    public int DoodadIndex { get; init; } = -1;

    /// <summary>The set the doodad belongs to (WMO placements only), else -1.</summary>
    public int DoodadSet { get; init; } = -1;

    /// <summary>Why the extractor does not write this instance to the vmap, or null when it does.</summary>
    public string? SkipReason { get; init; }

    /// <summary>
    /// For a doodad, the angle in degrees between the composed (client) rotation and the rotation the server derives from
    /// the Euler triple the extractor stores; null for MDDF placements.
    /// </summary>
    public double? ServerRotationDeviationDegrees { get; init; }

    public ClientPlacement Source { get; init; } = null!;
}

/// <summary>Models a plan could not load, counted by kind with a few messages kept for the tile status.</summary>
internal sealed class PlanProblems
{
    private const int MaxSamples = 6;

    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _samples = [];

    public int Missing { get; private set; }

    public int Rejected { get; private set; }

    public int Total => Missing + Rejected;

    public IReadOnlyList<string> Samples => _samples;

    public void Add(ModelProblem problem)
    {
        if (!_paths.Add(problem.Path))
        {
            return;
        }

        if (problem.Kind == ModelProblemKind.Missing)
        {
            Missing++;
        }
        else
        {
            Rejected++;
        }

        if (_samples.Count < MaxSamples)
        {
            _samples.Add($"{problem.Kind} {problem.Path}: {problem.Message}");
        }
    }

    public JsonObject ToJson() => new()
    {
        ["missing"] = Missing,
        ["rejected"] = Rejected,
        ["samples"] = new JsonArray([.. _samples.Select(s => (JsonNode?)s)]),
    };
}

/// <summary>The instances of one tile (or a set of tiles) with everything needed to draw or ray-test them.</summary>
internal sealed class TilePlan
{
    public List<PlannedWmo> Wmos { get; } = [];

    public List<PlannedM2> M2s { get; } = [];

    public PlanProblems Problems { get; } = new();

    /// <summary>Placements read from the source (before ownership and duplicate filtering).</summary>
    public int PlacementsRead { get; set; }

    /// <summary>Placements left to the tile that contains their position.</summary>
    public int OwnedByOtherTile { get; set; }

    public int Duplicates { get; set; }

    /// <summary>WMO placements whose selected doodad set does not exist in the model.</summary>
    public int DoodadSetOutOfRange { get; set; }

    public int Doodads => M2s.Count(m => m.IsWmoDoodad);

    public JsonObject ToJson() => new()
    {
        ["placementsRead"] = PlacementsRead,
        ["ownedByOtherTile"] = OwnedByOtherTile,
        ["duplicateUniqueIds"] = Duplicates,
        ["wmoInstances"] = Wmos.Count,
        ["m2Placements"] = M2s.Count - Doodads,
        ["wmoDoodads"] = Doodads,
        ["doodadSetOutOfRange"] = DoodadSetOutOfRange,
        ["modelProblems"] = Problems.ToJson(),
    };
}

/// <summary>
/// Turns a tile's client placements into resolved instances: loads each model, applies the placement transforms and the
/// vmap extractor's inclusion rules (kept as flags, never as silent omission). Stateless; thread-safe given a
/// thread-safe <see cref="ClientModelCache"/>.
/// </summary>
internal static class ModelPlanner
{
    /// <summary>
    /// Resolves <paramref name="placements"/>. With <paramref name="onlyOwnedBy"/> set, a placement is kept only when its
    /// world position lies inside that tile (an object listed by several ADTs is drawn once, where its position is);
    /// the global tile owns every placement. Duplicate (kind, unique id) pairs are kept once.
    /// </summary>
    public static TilePlan Plan(
        uint mapId,
        TileKey? onlyOwnedBy,
        IReadOnlyList<ClientPlacement> placements,
        PlanScope scope,
        IClientFileSource files,
        ClientModelCache cache,
        CancellationToken cancellationToken)
    {
        var plan = new TilePlan();
        var seen = new HashSet<(PlacementKind, uint)>();
        bool loadWmo = (scope & (PlanScope.Wmo | PlanScope.Doodads)) != 0;

        foreach (ClientPlacement placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.PlacementsRead++;
            if (placement.Kind == PlacementKind.Wmo ? !loadWmo : (scope & PlanScope.M2) == 0)
            {
                continue;
            }

            bool isWmo = placement.Kind == PlacementKind.Wmo;
            InstanceTransform transform;
            bool originQuirk = false;
            if (isWmo)
            {
                transform = PlacementTransform.ForWmo(placement, out originQuirk);
            }
            else
            {
                transform = PlacementTransform.ForM2(placement);
            }

            WorldPoint position = PlacementTransform.InternalToWorld(transform.Position);
            if (onlyOwnedBy is { } owner && !owner.IsGlobal && WorldGrid.TileOf(mapId, position.X, position.Y) != owner)
            {
                plan.OwnedByOtherTile++;
                continue;
            }

            if (!seen.Add((placement.Kind, placement.UniqueId)))
            {
                plan.Duplicates++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(placement.ModelPath))
            {
                plan.Problems.Add(new ModelProblem(ModelProblemKind.Rejected, $"(placement {placement.UniqueId})", "the placement names no model."));
                continue;
            }

            if (isWmo)
            {
                PlanWmo(plan, placement, transform, position, originQuirk, scope, files, cache, cancellationToken);
            }
            else
            {
                string clientPath = ClientModelNames.ClientFilePath(placement.ModelPath);
                ModelLoad<M2ModelFile> load = cache.GetM2(files, clientPath);
                if (load.Model is null)
                {
                    plan.Problems.Add(load.Problem!);
                    continue;
                }

                plan.M2s.Add(new PlannedM2
                {
                    IsWmoDoodad = false,
                    ModelPath = clientPath,
                    Model = load.Model,
                    Transform = transform,
                    Position = position,
                    UniqueId = placement.UniqueId,
                    Source = placement,
                    SkipReason = load.Model.HasCollisionHull ? null : "no-bounding-hull",
                });
            }
        }

        return plan;
    }

    private static void PlanWmo(
        TilePlan plan,
        ClientPlacement placement,
        InstanceTransform transform,
        WorldPoint position,
        bool originQuirk,
        PlanScope scope,
        IClientFileSource files,
        ClientModelCache cache,
        CancellationToken cancellationToken)
    {
        ModelLoad<WmoModel> load = cache.GetWmo(files, ClientModelNames.ClientFilePath(placement.ModelPath));
        if (load.Model is not { } model)
        {
            plan.Problems.Add(load.Problem!);
            return;
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:562 (a destructible WMO (MODF flag 1) is not written)
        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/wmo.cpp:583-584 (a WMO with no recorded triangles is not written)
        string? skip = (placement.Flags & 1) != 0
            ? "destructible"
            : model.ExtractorTriangleCount == 0 ? "no-collision-triangles" : null;
        plan.Wmos.Add(new PlannedWmo
        {
            Placement = placement,
            Model = model,
            Transform = transform,
            Position = position,
            OriginQuirk = originQuirk,
            SkipReason = skip,
        });

        if ((scope & PlanScope.Doodads) == 0)
        {
            return;
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:225-226 (a doodad set beyond the model's sets places nothing)
        WmoRootFile root = model.Root;
        if (placement.DoodadSet >= root.DoodadSets.Count)
        {
            plan.DoodadSetOutOfRange++;
            return;
        }

        // ref: vmangos@0e3ff01/contrib/vmap_extractor/vmapextract/model.cpp:232-237 (only MODD entries inside the selected set's [StartIndex, StartIndex + Count) run)
        WmoDoodadSet set = root.DoodadSets[placement.DoodadSet];
        IReadOnlySet<int> referenced = model.ReferencedDoodads();
        long first = set.FirstDoodad;
        long end = Math.Min(first + set.DoodadCount, root.Doodads.Count);
        for (long index = first; index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WmoDoodad doodad = root.Doodads[(int)index];
            if (string.IsNullOrWhiteSpace(doodad.ModelPath))
            {
                plan.Problems.Add(new ModelProblem(ModelProblemKind.Rejected, $"{model.Path} doodad {index}", "the doodad names no model."));
                continue;
            }

            string clientPath = ClientModelNames.ClientFilePath(doodad.ModelPath);
            ModelLoad<M2ModelFile> doodadLoad = cache.GetM2(files, clientPath);
            if (doodadLoad.Model is not { } m2)
            {
                plan.Problems.Add(doodadLoad.Problem!);
                continue;
            }

            InstanceTransform doodadTransform = PlacementTransform.ForWmoDoodad(transform, doodad);
            string? doodadSkip = skip is not null
                ? "wmo-" + skip
                : !referenced.Contains((int)index)
                    ? "no-kept-group-reference"
                    : m2.HasCollisionHull ? null : "no-bounding-hull";
            (float ex, float ey, float ez) = PlacementTransform.ExtractorDoodadRotation(doodad.Rotation, placement.Rotation);
            Mat3d stored = PlacementTransform.ServerRotation(ex, ey, ez);
            plan.M2s.Add(new PlannedM2
            {
                IsWmoDoodad = true,
                ModelPath = clientPath,
                Model = m2,
                Transform = doodadTransform,
                Position = PlacementTransform.InternalToWorld(doodadTransform.Position),
                UniqueId = placement.UniqueId,
                DoodadIndex = (int)index,
                DoodadSet = placement.DoodadSet,
                Source = placement,
                SkipReason = doodadSkip,
                ServerRotationDeviationDegrees = doodadTransform.Rotation.AngleToDegrees(stored),
            });
        }
    }
}
