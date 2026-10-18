using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// The reference model queries behind <c>ref.model.info</c> and <c>ref.query.los</c>: they read the client's own WMO and
/// M2 files through <see cref="AtlasServices"/> and never touch the server's vmap data. Thread-safe.
/// </summary>
public sealed class ReferenceModelQueries(AtlasServices services, ClientModelCache cache)
{
    /// <summary>Most tiles one line-of-sight segment may cross (it loads every model those tiles place).</summary>
    public const int MaxTiles = 64;

    /// <summary>The nearest crossing of a segment with a triangle.</summary>
    internal sealed record RayHit(double T, WorldPoint Point, string Kind, string Model, uint UniqueId, int Group, string? GroupName, int Triangle, int? MopyFlags, string? Class, string Source, int DoodadIndex);

    /// <summary>What <c>ref.model.info</c> reports for a client model path (a WMO root or an M2/MDX name).</summary>
    public JsonObject ModelInfo(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        IClientFileSource files = services.Get<IClientFileSource>()
            ?? throw new CommandException(CommandException.Unavailable, "ref.model.info needs client files, which are not configured.");
        string clientPath = ClientModelNames.ClientFilePath(path);
        if (ClientModelNames.IsWmo(clientPath))
        {
            ModelLoad<WmoModel> load = cache.GetWmo(files, clientPath);
            return load.Model is { } wmo ? WmoInfo(files, wmo) : throw ProblemException(load.Problem!);
        }

        if (clientPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
        {
            ModelLoad<M2ModelFile> load = cache.GetM2(files, clientPath);
            return load.Model is { } m2 ? M2Info(files, clientPath, m2) : throw ProblemException(load.Problem!);
        }

        throw new CommandException(CommandException.BadArguments, "ref.model.info: path must end in .wmo, .m2, .mdx or .mdl.");
    }

    /// <summary>
    /// Casts the segment <paramref name="a"/> to <paramref name="b"/> against the client's WMO and M2 collision triangles of every
    /// tile it crosses. The spell variant ignores M2s; the movement variant includes them. Triangles are the ones the
    /// vmap extractor keeps (all triangles of its kept WMO groups, every M2 bounding triangle) unless
    /// <paramref name="includeExtractorSkipped"/> is set.
    /// </summary>
    public JsonObject LineOfSight(uint mapId, WorldPoint a, WorldPoint b, bool includeExtractorSkipped, CancellationToken cancellationToken)
    {
        IClientFileSource files = services.Get<IClientFileSource>()
            ?? throw new CommandException(CommandException.Unavailable, "ref.query.los needs client files, which are not configured.");
        IClientPlacementSource placements = services.Get<IClientPlacementSource>()
            ?? throw new CommandException(CommandException.Unavailable, "ref.query.los needs the client placement source, which is not registered.");
        if (!a.IsFinite || !b.IsFinite)
        {
            throw new CommandException(CommandException.BadArguments, "ref.query.los: both points must be finite.");
        }

        if (WorldGrid.TileOf(mapId, a.X, a.Y) is null || WorldGrid.TileOf(mapId, b.X, b.Y) is null)
        {
            throw new CommandException(CommandException.BadArguments, "ref.query.los: both points must lie inside the map.");
        }

        IReadOnlyList<TileKey> tiles = TilesCrossed(mapId, a, b);
        if (tiles.Count > MaxTiles)
        {
            throw new CommandException(CommandException.BadArguments, $"ref.query.los: the segment crosses {tiles.Count} tiles (limit {MaxTiles}).");
        }

        var all = new List<ClientPlacement>();
        var tileReport = new JsonArray();
        foreach (TileKey tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string state;
            int count = 0;
            try
            {
                IReadOnlyList<ClientPlacement>? list = placements.Placements(tile);
                if (list is null)
                {
                    state = "Missing";
                }
                else
                {
                    state = "Ok";
                    count = list.Count;
                    all.AddRange(list);
                }
            }
            catch (InvalidDataException e)
            {
                state = "Rejected: " + e.Message;
            }

            tileReport.Add(new JsonObject { ["tile"] = new JsonArray(tile.X, tile.Y), ["state"] = state, ["placements"] = count });
        }

        TilePlan plan = ModelPlanner.Plan(mapId, null, all, PlanScope.All, files, cache, cancellationToken);
        RayHit? spell = null;
        RayHit? movement = null;
        foreach (PlannedWmo wmo in plan.Wmos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (wmo.SkipReason is not null && !includeExtractorSkipped)
            {
                continue;
            }

            if (TraceWmo(wmo, a, b, includeExtractorSkipped) is { } hit)
            {
                spell = Nearer(spell, hit);
                movement = Nearer(movement, hit);
            }
        }

        foreach (PlannedM2 m2 in plan.M2s)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!m2.Model.HasCollisionHull || (m2.SkipReason is not null && !includeExtractorSkipped))
            {
                continue;
            }

            if (TraceM2(m2, a, b) is { } hit)
            {
                movement = Nearer(movement, hit);
            }
        }

        var result = new JsonObject
        {
            ["map"] = mapId,
            ["a"] = Point(a),
            ["b"] = Point(b),
            ["length"] = Math.Sqrt(Dist2(a, b)),
            ["extractorSkippedIncluded"] = includeExtractorSkipped,
            ["tiles"] = tileReport,
            ["spell"] = Outcome(spell),
            ["movement"] = Outcome(movement),
            ["plan"] = plan.ToJson(),
            ["overlay"] = Overlay(a, b, spell, movement),
        };
        return result;
    }

    /// <summary>The tiles a world-space segment passes through, in order from <paramref name="a"/>, inside the map grid.</summary>
    internal static IReadOnlyList<TileKey> TilesCrossed(uint mapId, WorldPoint a, WorldPoint b)
    {
        // Tile coordinates u = 32 - x / TileSize, v = 32 - y / TileSize; the server's tile is the integer part (WorldGrid.TileOf).
        // Amanatides and Woo grid traversal over those cells.
        double g = WorldGrid.TileSize;
        double u0 = 32 - (a.X / g);
        double v0 = 32 - (a.Y / g);
        double u1 = 32 - (b.X / g);
        double v1 = 32 - (b.Y / g);
        int cx = (int)Math.Floor(u0);
        int cy = (int)Math.Floor(v0);
        int ex = (int)Math.Floor(u1);
        int ey = (int)Math.Floor(v1);
        double du = u1 - u0;
        double dv = v1 - v0;
        int stepX = du > 0 ? 1 : -1;
        int stepY = dv > 0 ? 1 : -1;
        double tMaxX = du == 0 ? double.PositiveInfinity : ((du > 0 ? cx + 1 : cx) - u0) / du;
        double tMaxY = dv == 0 ? double.PositiveInfinity : ((dv > 0 ? cy + 1 : cy) - v0) / dv;
        double tDeltaX = du == 0 ? double.PositiveInfinity : Math.Abs(1 / du);
        double tDeltaY = dv == 0 ? double.PositiveInfinity : Math.Abs(1 / dv);
        var tiles = new List<TileKey>();
        for (int guard = 0; guard < 4096; guard++)
        {
            var tile = new TileKey(mapId, cx, cy);
            if (tile.IsInGrid)
            {
                tiles.Add(tile);
            }

            if (cx == ex && cy == ey)
            {
                break;
            }

            if (tMaxX < tMaxY)
            {
                cx += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                cy += stepY;
                tMaxY += tDeltaY;
            }
        }

        return tiles;
    }

    private static CommandException ProblemException(ModelProblem problem)
        => new(
            problem.Kind == ModelProblemKind.Missing ? CommandException.BadArguments : CommandException.Failed,
            $"ref.model.info: {problem.Path}: {problem.Message}");

    private static JsonObject WmoInfo(IClientFileSource files, WmoModel model)
    {
        WmoRootFile root = model.Root;
        var groups = new JsonArray();
        int keptGroups = 0;
        for (int i = 0; i < model.Groups.Count; i++)
        {
            WmoGroupFile group = model.Groups[i];
            string? skip = model.SkipReason(i);
            if (skip is null)
            {
                keptGroups++;
            }

            int collidable = group.TriangleFlags.Count(WmoMaterialFlags.IsCollidable);
            var entry = new JsonObject
            {
                ["index"] = i,
                ["file"] = WmoRootFile.GroupFilePath(model.Path, i),
                ["name"] = model.GroupName(i),
                ["flags"] = group.Flags,
                ["groupId"] = group.GroupId,
                ["triangles"] = group.TriangleCount,
                ["collidableTriangles"] = collidable,
                ["renderOnlyTriangles"] = group.TriangleCount - collidable,
                ["vertices"] = group.Vertices.Length,
                ["doodadReferences"] = group.DoodadReferences.Length,
                ["boundsMin"] = Point(group.BoundsMin),
                ["boundsMax"] = Point(group.BoundsMax),
                ["extractorSkipped"] = skip is not null,
                ["skipReason"] = skip,
            };
            if (group.Liquid is { } liquid)
            {
                entry["liquid"] = new JsonObject
                {
                    ["verticesX"] = liquid.VerticesX,
                    ["verticesY"] = liquid.VerticesY,
                    ["tilesX"] = liquid.TilesX,
                    ["tilesY"] = liquid.TilesY,
                    ["materialId"] = liquid.MaterialId,
                    ["corner"] = Point(liquid.Corner),
                };
            }

            groups.Add(entry);
        }

        IReadOnlySet<int> referenced = model.ReferencedDoodads();
        var sets = new JsonArray();
        for (int i = 0; i < root.DoodadSets.Count; i++)
        {
            WmoDoodadSet set = root.DoodadSets[i];
            long end = Math.Min((long)set.FirstDoodad + set.DoodadCount, root.Doodads.Count);
            int referencedInSet = 0;
            for (long d = set.FirstDoodad; d < end; d++)
            {
                if (referenced.Contains((int)d))
                {
                    referencedInSet++;
                }
            }

            sets.Add(new JsonObject
            {
                ["index"] = i,
                ["name"] = set.Name,
                ["first"] = set.FirstDoodad,
                ["count"] = set.DoodadCount,
                ["referencedByKeptGroups"] = referencedInSet,
            });
        }

        return new JsonObject
        {
            ["kind"] = "wmo",
            ["path"] = model.Path,
            ["archive"] = files.ArchiveOf(model.Path),
            ["version"] = root.Version,
            ["header"] = new JsonObject
            {
                ["textures"] = root.Header.TextureCount,
                ["groups"] = root.Header.GroupCount,
                ["portals"] = root.Header.PortalCount,
                ["lights"] = root.Header.LightCount,
                ["doodadNames"] = root.Header.DoodadNameCount,
                ["doodadDefinitions"] = root.Header.DoodadDefinitionCount,
                ["doodadSets"] = root.Header.DoodadSetCount,
                ["ambientColor"] = root.Header.AmbientColor,
                ["wmoId"] = root.Header.WmoId,
                ["boundsMin"] = Point(root.Header.BoundsMin),
                ["boundsMax"] = Point(root.Header.BoundsMax),
                ["flags"] = root.Header.Flags,
            },
            ["groups"] = groups,
            ["doodadSets"] = sets,
            ["doodads"] = root.Doodads.Count,
            ["extractor"] = new JsonObject
            {
                ["keptGroups"] = keptGroups,
                ["skippedGroups"] = model.Groups.Count - keptGroups,
                ["triangles"] = model.ExtractorTriangleCount,
                ["written"] = model.ExtractorTriangleCount > 0,
            },
            ["notes"] = new JsonArray(
                "Doodad set 0 (the default set) is not added to a selected set: whether the client always draws it is unverified.",
                "Group triangles are WMO-local; MODF/MDDF placement transforms are in ref.wmo / ref.m2.* attributes."),
        };
    }

    private static JsonObject M2Info(IClientFileSource files, string clientPath, M2ModelFile model)
    {
        (Vector3 renderMin, Vector3 renderMax) = model.RenderBox;
        (Vector3 hullMin, Vector3 hullMax) = model.BoundingBox;
        return new JsonObject
        {
            ["kind"] = "m2",
            ["path"] = clientPath,
            ["archive"] = files.ArchiveOf(clientPath),
            ["version"] = M2ModelFile.SupportedVersion,
            ["name"] = model.Name,
            ["vertices"] = model.Vertices.Length,
            ["views"] = model.ViewCount,
            ["renderTriangles"] = model.RenderTriangleCount,
            ["boundingTriangles"] = model.BoundingTriangleCount,
            ["boundingVertices"] = model.BoundingVertices.Length,
            ["hasCollisionHull"] = model.HasCollisionHull,
            ["renderBoundsMin"] = Point(renderMin),
            ["renderBoundsMax"] = Point(renderMax),
            ["hullBoundsMin"] = Point(hullMin),
            ["hullBoundsMax"] = Point(hullMax),
        };
    }

    private static RayHit? Nearer(RayHit? current, RayHit candidate)
        => current is null || candidate.T < current.T ? candidate : current;

    private static RayHit? TraceWmo(PlannedWmo wmo, WorldPoint a, WorldPoint b, bool includeSkipped)
    {
        // Model-space segment: the instance transform is a rotation, a uniform scale and a translation, so it maps the
        // segment onto a segment with the same parameter t.
        (Vec3d p0, Vec3d p1) = ToModel(wmo.Transform, a, b);
        RayHit? best = null;
        WmoModel model = wmo.Model;
        for (int g = 0; g < model.Groups.Count; g++)
        {
            if (!includeSkipped && model.SkipReason(g) is not null)
            {
                continue;
            }

            WmoGroupFile group = model.Groups[g];
            if (group.TriangleCount == 0 || !SegmentHitsBox(p0, p1, Vec3d.From(group.VertexBoundsMin), Vec3d.From(group.VertexBoundsMax)))
            {
                continue;
            }

            for (int t = 0; t < group.TriangleCount; t++)
            {
                if (SegmentTriangle(
                        p0,
                        p1,
                        Vec3d.From(group.Vertices[group.Indices[3 * t]]),
                        Vec3d.From(group.Vertices[group.Indices[(3 * t) + 1]]),
                        Vec3d.From(group.Vertices[group.Indices[(3 * t) + 2]])) is { } time
                    && (best is null || time < best.T))
                {
                    byte flags = group.TriangleFlags[t];
                    best = new RayHit(
                        time,
                        Lerp(a, b, time),
                        "wmo",
                        model.Path,
                        wmo.Placement.UniqueId,
                        g,
                        model.GroupName(g),
                        t,
                        flags,
                        WmoMaterialFlags.IsCollidable(flags) ? "collide" : "render-only",
                        "modf",
                        -1);
                }
            }
        }

        return best;
    }

    private static RayHit? TraceM2(PlannedM2 m2, WorldPoint a, WorldPoint b)
    {
        (Vec3d p0, Vec3d p1) = ToModel(m2.Transform, a, b);
        M2ModelFile model = m2.Model;
        (Vector3 min, Vector3 max) = model.BoundingBox;
        if (!SegmentHitsBox(p0, p1, Vec3d.From(min), Vec3d.From(max)))
        {
            return null;
        }

        RayHit? best = null;
        for (int t = 0; t < model.BoundingTriangleCount; t++)
        {
            if (SegmentTriangle(
                    p0,
                    p1,
                    Vec3d.From(model.BoundingVertices[model.BoundingIndices[3 * t]]),
                    Vec3d.From(model.BoundingVertices[model.BoundingIndices[(3 * t) + 1]]),
                    Vec3d.From(model.BoundingVertices[model.BoundingIndices[(3 * t) + 2]])) is { } time
                && (best is null || time < best.T))
            {
                best = new RayHit(
                    time,
                    Lerp(a, b, time),
                    "m2",
                    m2.ModelPath,
                    m2.UniqueId,
                    -1,
                    null,
                    t,
                    null,
                    null,
                    m2.IsWmoDoodad ? "wmo-doodad" : "mddf",
                    m2.DoodadIndex);
            }
        }

        return best;
    }

    private static (Vec3d P0, Vec3d P1) ToModel(InstanceTransform transform, WorldPoint a, WorldPoint b)
    {
        Mat3d inverse = transform.Rotation.Transpose();
        double inverseScale = 1.0 / transform.Scale;
        Vec3d Map(WorldPoint world) => inverse.Transform(PlacementTransform.WorldToInternal(world) - transform.Position) * inverseScale;
        return (Map(a), Map(b));
    }

    private static bool SegmentHitsBox(Vec3d p0, Vec3d p1, Vec3d min, Vec3d max)
    {
        double tNear = 0;
        double tFar = 1;
        return Slab(p0.X, p1.X - p0.X, min.X, max.X, ref tNear, ref tFar)
            && Slab(p0.Y, p1.Y - p0.Y, min.Y, max.Y, ref tNear, ref tFar)
            && Slab(p0.Z, p1.Z - p0.Z, min.Z, max.Z, ref tNear, ref tFar);
    }

    private static bool Slab(double origin, double direction, double min, double max, ref double tNear, ref double tFar)
    {
        const double pad = 1e-6;
        if (Math.Abs(direction) < 1e-12)
        {
            return origin >= min - pad && origin <= max + pad;
        }

        double t1 = (min - pad - origin) / direction;
        double t2 = (max + pad - origin) / direction;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        tNear = Math.Max(tNear, t1);
        tFar = Math.Min(tFar, t2);
        return tNear <= tFar;
    }

    /// <summary>Moller-Trumbore for a segment (two-sided): the parameter in [0, 1] where it crosses the triangle, else null.</summary>
    internal static double? SegmentTriangle(Vec3d p0, Vec3d p1, Vec3d v0, Vec3d v1, Vec3d v2)
    {
        Vec3d direction = p1 - p0;
        Vec3d edge1 = v1 - v0;
        Vec3d edge2 = v2 - v0;
        Vec3d p = Vec3d.Cross(direction, edge2);
        double determinant = Vec3d.Dot(edge1, p);
        if (Math.Abs(determinant) < 1e-12)
        {
            return null;
        }

        double inverse = 1.0 / determinant;
        Vec3d offset = p0 - v0;
        double u = Vec3d.Dot(offset, p) * inverse;
        if (u < 0 || u > 1)
        {
            return null;
        }

        Vec3d q = Vec3d.Cross(offset, edge1);
        double v = Vec3d.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1)
        {
            return null;
        }

        double t = Vec3d.Dot(edge2, q) * inverse;
        return t is >= 0 and <= 1 ? t : null;
    }

    private static WorldPoint Lerp(WorldPoint a, WorldPoint b, double t)
        => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));

    private static double Dist2(WorldPoint a, WorldPoint b)
        => ((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)) + ((b.Z - a.Z) * (b.Z - a.Z));

    private static JsonObject Outcome(RayHit? hit)
    {
        var outcome = new JsonObject { ["clear"] = hit is null };
        if (hit is null)
        {
            return outcome;
        }

        outcome["hit"] = new JsonObject
        {
            ["point"] = Point(hit.Point),
            ["t"] = hit.T,
            ["kind"] = hit.Kind,
            ["model"] = hit.Model,
            ["uniqueId"] = hit.UniqueId,
            ["group"] = hit.Group >= 0 ? hit.Group : null,
            ["groupName"] = hit.GroupName,
            ["triangle"] = hit.Triangle,
            ["mopyFlags"] = hit.MopyFlags,
            ["class"] = hit.Class,
            ["source"] = hit.Source,
            ["doodadIndex"] = hit.DoodadIndex >= 0 ? hit.DoodadIndex : null,
        };
        return outcome;
    }

    private static JsonObject Overlay(WorldPoint a, WorldPoint b, RayHit? spell, RayHit? movement)
    {
        string wmoColor = LayerCatalog.Get(LayerIds.RefWmo).AttributeColors![ColorKeys.Collidable];
        string m2Color = LayerCatalog.Get(LayerIds.RefM2Collision).DefaultColor;
        var points = new JsonArray
        {
            new JsonObject { ["at"] = Point(a), ["label"] = "ref los A" },
            new JsonObject { ["at"] = Point(b), ["label"] = "ref los B" },
        };
        var segments = new JsonArray();
        RayHit? first = movement;
        if (first is null)
        {
            segments.Add(new JsonObject { ["from"] = Point(a), ["to"] = Point(b), ["label"] = "ref los clear", ["color"] = wmoColor });
        }
        else
        {
            segments.Add(new JsonObject { ["from"] = Point(a), ["to"] = Point(first.Point), ["label"] = "ref los to first hit", ["color"] = wmoColor });
            segments.Add(new JsonObject { ["from"] = Point(first.Point), ["to"] = Point(b), ["label"] = "ref los blocked", ["color"] = wmoColor, ["dashed"] = true });
        }

        if (spell is not null)
        {
            points.Add(new JsonObject { ["at"] = Point(spell.Point), ["label"] = "ref spell hit " + spell.Model, ["color"] = wmoColor });
        }

        if (movement is not null && !ReferenceEquals(movement, spell))
        {
            points.Add(new JsonObject
            {
                ["at"] = Point(movement.Point),
                ["label"] = "ref movement hit " + movement.Model,
                ["color"] = movement.Kind == "m2" ? m2Color : wmoColor,
            });
        }

        return new JsonObject { ["points"] = points, ["segments"] = segments };
    }

    private static JsonArray Point(WorldPoint p) => new(p.X, p.Y, p.Z);

    private static JsonArray Point(Vector3 v) => new((double)v.X, (double)v.Y, (double)v.Z);
}
