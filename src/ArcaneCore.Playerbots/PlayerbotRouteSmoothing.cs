using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Per-bot path variation and corner-cutting spline smoothing for navigation-mesh routes (<see cref="PlayerbotNavigation"/>):
/// <list type="number">
/// <item><description><b>Per-bot lateral corridor offset and organic weave</b>: Detour's <c>findStraightPath</c> string-pulls every walk
/// between two places onto the exact same 1D portal-vertex polyline, so multiple bots walking a road or zone corridor march in a tight
/// single-file line. On outdoor navigated routes at least <see cref="MinVariedRouteYards"/> long, each bot derives a deterministic
/// personal lane offset (<c>[-MaxLaneOffsetYards, +MaxLaneOffsetYards]</c>) and gentle sinusoidal weave from its GUID, tapered smoothly to
/// zero over the first and last <see cref="EndpointTaperYards"/> so the start and destination remain exact.</description></item>
/// <item><description><b>Quadratic Bezier corner-cutting spline</b>: Sharp interior polygon turns (<see cref="MinCornerRadians"/> to
/// <see cref="MaxCornerRadians"/>) on legs at least <see cref="MinCornerLegYards"/> long are replaced with a 4-point quadratic Bezier
/// arc across the trimmed corner, turning a sharp vertex snap into a gradual curve.</description></item>
/// <item><description><b>Collision, floor, and navmesh verification</b>: Every laterally offset point and every spline chord is verified
/// against the floor height (<see cref="MapCollision.GetHeight"/> within <see cref="MaxFloorDeltaYards"/>), doodad-aware chest-height
/// line of sight (<see cref="PlayerbotMoveCheck.LineClear"/>), and the walkable navigation mesh (<see cref="IMeshPointQuery.Raycast"/>
/// when present). Any point or chord that nears a tree, fence, wall, pillar, cliff, or indoor boundary falls back cleanly to the
/// un-offset or un-cut Detour vertex.</description></item>
/// </list>
/// </summary>
internal static class PlayerbotRouteSmoothing
{
    /// <summary>Shortest navigated route that receives per-bot lateral lane offset (flat yards).</summary>
    internal const float MinVariedRouteYards = 24f;

    /// <summary>Target spacing between subdivided points along long outdoor segments (flat yards).</summary>
    internal const float SubdivisionStepYards = 16f;

    /// <summary>Distance over which lateral offset ramps from zero after the start and back to zero before the destination (flat yards).</summary>
    internal const float EndpointTaperYards = 12f;

    /// <summary>Maximum per-bot constant lateral lane offset from the Detour centerline on outdoor corridors (yards).</summary>
    internal const float MaxLaneOffsetYards = 1.85f;

    /// <summary>Additional sinusoidal lateral weave amplitude (0: bots hold a steady personal lane without zig-zagging).</summary>
    internal const float MaxWeaveAmplitudeYards = 0f;

    /// <summary>Shortest adjacent leg length eligible for quadratic Bezier corner-cutting (flat yards).</summary>
    internal const float MinCornerLegYards = 6.0f;

    /// <summary>Maximum distance trimmed back from a sharp vertex along each adjacent leg when forming a corner spline (flat yards).</summary>
    internal const float MaxCornerTrimYards = 4.6f;

    /// <summary>Minimum turn angle (~23 degrees) that triggers corner spline smoothing.</summary>
    internal const float MinCornerRadians = 0.40f;

    /// <summary>Maximum turn angle (~150 degrees) eligible for corner spline smoothing (hairpins stay sharp).</summary>
    internal const float MaxCornerRadians = 2.62f;

    /// <summary>Maximum vertical difference allowed between an offset/smoothed point's interpolated Z and the actual floor height (yards).</summary>
    internal const float MaxFloorDeltaYards = 0.75f;

    /// <summary>Upper bound on output waypoints so a smoothed route remains compact.</summary>
    internal const int MaxSmoothedPoints = 96;

    /// <summary>
    /// Maximum flat distance (yards) from the start of a route over which subdivision, lateral variation, and corner smoothing
    /// run per plan. Long multi-hundred-yard routes re-plan as the bot advances, so smoothing only the upcoming horizon avoids
    /// hundreds of VMap/MMap height and raycast probes on faraway segments.
    /// </summary>
    internal const float MaxSmoothHorizonYards = 120f;

    private static readonly PathOptions WalkableMeshOptions = new()
    {
        Mover = PathMover.Player,
        ExcludeFlags = NavTerrain.SteepSlopes,
    };

    /// <summary>
    /// Apply per-bot outdoor corridor variation (when <paramref name="varyLateral"/> is true) and quadratic Bezier corner smoothing to a
    /// navigated <paramref name="route"/>, returning a new <see cref="PlayerbotRoute"/> when at least one point was varied or smoothed,
    /// or <paramref name="route"/> unchanged when no safe modification applied.
    /// </summary>
    internal static PlayerbotRoute VaryAndSmooth(Player player, Map map, PlayerbotRoute route, bool varyLateral = true)
    {
        if (!route.Navigated || route.Points.Count < 2)
            return route;

        IReadOnlyList<Vector3> result = VaryAndSmoothPoints(player.Guid.Value, map, route.Points, varyLateral);
        if (ReferenceEquals(result, route.Points))
            return route;

        float distance = 0f;
        for (int i = 1; i < result.Count; i++)
        {
            float leg = Vector3.Distance(result[i - 1], result[i]);
            if (!float.IsFinite(leg))
                return route;
            distance += leg;
        }

        return new PlayerbotRoute(result, distance, navigated: true)
        {
            CompletedQuestReturn = route.CompletedQuestReturn,
            CrossBesidePlaceMarks = route.CrossBesidePlaceMarks,
            Chase = route.Chase,
            Pathfinder = route.Pathfinder,
        };
    }

    /// <summary>
    /// Vary and smooth a raw waypoint list for the bot identified by <paramref name="botGuid"/> on <paramref name="map"/>.
    /// Start (<c>points[0]</c>) and destination (<c>points[^1]</c>) are always preserved exactly.
    /// </summary>
    internal static IReadOnlyList<Vector3> VaryAndSmoothPoints(ulong botGuid, Map map, IReadOnlyList<Vector3> points, bool varyLateral = true)
    {
        if (points.Count < 2 || points.Count > MaxSmoothedPoints)
            return points;

        float totalFlat = TotalFlatLength(points);
        if (!float.IsFinite(totalFlat) || totalFlat < 4f)
            return points;

        IReadOnlyList<Vector3> working = points;
        bool changed = false;

        if (varyLateral && totalFlat >= MinVariedRouteYards && botGuid != 0)
        {
            List<Vector3>? varied = TryApplyLateralVariation(botGuid, map, working, totalFlat);
            if (varied is not null)
            {
                working = varied;
                changed = true;
            }
        }

        if (working.Count >= 3 && working.Count + 3 <= MaxSmoothedPoints)
        {
            List<Vector3>? smoothed = TrySmoothCorners(map, working);
            if (smoothed is not null)
            {
                working = smoothed;
                changed = true;
            }
        }

        return changed ? working : points;
    }

    private static List<Vector3>? TryApplyLateralVariation(ulong botGuid, Map map, IReadOnlyList<Vector3> points, float totalFlat)
    {
        ComputeBotLaneProfile(botGuid, out float laneOffset, out float weaveAmplitude, out float weaveInvWavelength, out float weavePhase);
        float horizonFlat = MathF.Min(totalFlat, MaxSmoothHorizonYards);

        // Step 1: Build a subdivided spine along segments within the smoothing horizon so long straight roads have interior control points.
        var spine = new List<(Vector3 Point, Vector2 Normal, float AlongFlat)>(Math.Min(MaxSmoothedPoints, points.Count * 3));
        float accumulated = 0f;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 a = points[i];
            Vector3 b = points[i + 1];
            Vector2 delta = Flat(b) - Flat(a);
            float segLen = delta.Length();
            if (segLen <= 1e-3f)
            {
                if (i == 0)
                    spine.Add((a, Vector2.Zero, 0f));
                continue;
            }

            Vector2 dir = delta / segLen;
            Vector2 normal = new(-dir.Y, dir.X);

            if (i == 0)
            {
                spine.Add((a, normal, 0f));
            }
            else
            {
                // Smooth bisector normal at interior original vertex.
                Vector2 prevNormal = spine[^1].Normal;
                Vector2 bisector = prevNormal + normal;
                float biLen = bisector.Length();
                spine[^1] = (a, biLen > 1e-3f ? bisector / biLen : normal, accumulated);
            }

            int subdivisions = accumulated < MaxSmoothHorizonYards && segLen >= SubdivisionStepYards * 1.45f
                ? Math.Clamp((int)MathF.Round(MathF.Min(segLen, MaxSmoothHorizonYards - accumulated) / SubdivisionStepYards), 1, 8)
                : 1;
            if (spine.Count + subdivisions > MaxSmoothedPoints - 8)
                subdivisions = 1;

            for (int step = 1; step < subdivisions; step++)
            {
                float t = (float)step / subdivisions;
                Vector3 interp = Vector3.Lerp(a, b, t);
                // The chord's height is not the ground's (a hill, a hollow): the point is on the mesh surface under it, walked from the
                // leg's corner, so an un-offset point stays on the ground and an offset one is judged against the ground.
                if (map.Collision.TryWalkMeshSurface(a, interp, out float surface, WalkableMeshOptions))
                    interp = interp with { Z = surface };
                spine.Add((interp, normal, accumulated + (segLen * t)));
            }

            accumulated += segLen;
            spine.Add((b, normal, accumulated));
        }

        if (spine.Count < 3)
            return null;

        int n = spine.Count;
        Span<float> desiredOffsets = n <= 96 ? stackalloc float[n] : new float[n];
        Span<float> safeScales = n <= 96 ? stackalloc float[n] : new float[n];
        Span<float> probedScales = n <= 96 ? stackalloc float[n] : new float[n];
        var probedPoints = new Vector3[n];
        desiredOffsets.Clear();
        safeScales.Clear();
        probedScales.Clear();

        for (int i = 1; i < n - 1; i++)
        {
            (Vector3 basePt, Vector2 normal, float s) = spine[i];
            if (s > horizonFlat || normal.LengthSquared() < 0.5f || !map.Collision.IsOutdoors(basePt.X, basePt.Y, basePt.Z + 1f))
                continue;

            float startRamp = SmoothRamp(s / EndpointTaperYards);
            float endRamp = SmoothRamp((horizonFlat - s) / EndpointTaperYards);
            float envelope = startRamp * endRamp;

            float spatialArg = (s * weaveInvWavelength) + ((basePt.X + basePt.Y) * 0.04f) + weavePhase;
            float desiredOffset = envelope * (laneOffset + (weaveAmplitude * MathF.Sin(spatialArg)));
            if (MathF.Abs(desiredOffset) < 0.08f)
                continue;

            desiredOffsets[i] = desiredOffset;
            float scale = ProbeMaxSafePointScale(map, basePt, normal, desiredOffset, out Vector3 snapped);
            safeScales[i] = scale;
            probedScales[i] = scale;
            probedPoints[i] = snapped;
        }

        // Smooth scale transitions across adjacent interior spine points so an obstacle near one point
        // tapers the lateral lane gradually instead of snapping back to the centerline and rebounding (chicane).
        for (int i = 2; i < n - 1; i++)
            safeScales[i] = MathF.Min(safeScales[i], safeScales[i - 1] + 0.5f);
        for (int i = n - 3; i >= 1; i--)
            safeScales[i] = MathF.Min(safeScales[i], safeScales[i + 1] + 0.5f);

        var result = new List<Vector3>(n) { spine[0].Point };
        bool anyOffset = false;

        for (int i = 1; i < n - 1; i++)
        {
            (Vector3 basePt, Vector2 normal, float s) = spine[i];
            Vector3 nextBasePt = spine[i + 1].Point;
            if (s <= horizonFlat)
            {
                float targetOffset = desiredOffsets[i] * safeScales[i];
                if (MathF.Abs(targetOffset) >= 0.08f)
                {
                    if (safeScales[i] == probedScales[i]
                        && IsChordSafe(map, result[^1], probedPoints[i])
                        && IsChordSafe(map, probedPoints[i], nextBasePt))
                    {
                        result.Add(probedPoints[i]);
                        anyOffset = true;
                        continue;
                    }

                    if (TrySelectSafeOffsetPoint(map, result[^1], basePt, nextBasePt, normal, targetOffset, out Vector3 shifted))
                    {
                        result.Add(shifted);
                        anyOffset = true;
                        continue;
                    }
                }
            }

            result.Add(basePt);
        }

        result.Add(spine[^1].Point);
        return anyOffset && result.Count >= 3 ? result : null;
    }

    private static readonly float[] OffsetScales = [1.0f, 0.6f, 0.3f];

    private static float ProbeMaxSafePointScale(
        Map map,
        Vector3 basePt,
        Vector2 normal,
        float desiredOffset,
        out Vector3 snapped)
    {
        foreach (float scale in OffsetScales)
        {
            float off = desiredOffset * scale;
            if (MathF.Abs(off) < 0.08f)
                continue;

            Vector3 candidate = new(
                basePt.X + (normal.X * off),
                basePt.Y + (normal.Y * off),
                basePt.Z);

            if (!map.Collision.IsOutdoors(candidate.X, candidate.Y, candidate.Z + 1f))
                continue;
            if (!TrySnapHeight(map, candidate, out snapped))
                continue;

            return scale;
        }

        snapped = basePt;
        return 0f;
    }

    private static bool TrySelectSafeOffsetPoint(
        Map map,
        Vector3 prevAccepted,
        Vector3 basePt,
        Vector3 nextBasePt,
        Vector2 normal,
        float desiredOffset,
        out Vector3 chosen)
    {
        // Only try same-side offsets (never negative/opposite-side scales, which cause left-right slalom chicanes).
        foreach (float scale in OffsetScales)
        {
            float off = desiredOffset * scale;
            if (MathF.Abs(off) < 0.08f)
                continue;

            Vector3 candidate = new(
                basePt.X + (normal.X * off),
                basePt.Y + (normal.Y * off),
                basePt.Z);

            if (!map.Collision.IsOutdoors(candidate.X, candidate.Y, candidate.Z + 1f))
                continue;
            if (!TrySnapHeight(map, candidate, out Vector3 snapped))
                continue;
            if (!IsChordSafe(map, prevAccepted, snapped) || !IsChordSafe(map, snapped, nextBasePt))
                continue;

            chosen = snapped;
            return true;
        }

        chosen = basePt;
        return false;
    }

    private static List<Vector3>? TrySmoothCorners(Map map, IReadOnlyList<Vector3> points)
    {
        var output = new List<Vector3>(Math.Min(MaxSmoothedPoints, points.Count + 16)) { points[0] };
        bool anySmoothed = false;
        float accumulatedFlat = 0f;

        for (int i = 1; i < points.Count - 1; i++)
        {
            Vector3 prev = output[^1];
            Vector3 corner = points[i];
            Vector3 next = points[i + 1];
            accumulatedFlat += Vector2.Distance(Flat(points[i - 1]), Flat(corner));

            if (accumulatedFlat <= MaxSmoothHorizonYards
                && output.Count + 4 + (points.Count - i) <= MaxSmoothedPoints
                && TryBuildCornerArc(map, prev, corner, next, out Vector3 a, out Vector3 q1, out Vector3 q2, out Vector3 b))
            {
                output.Add(a);
                output.Add(q1);
                output.Add(q2);
                output.Add(b);
                anySmoothed = true;
            }
            else
            {
                output.Add(corner);
            }
        }

        output.Add(points[^1]);
        return anySmoothed ? output : null;
    }

    private static bool TryBuildCornerArc(
        Map map,
        Vector3 prev,
        Vector3 corner,
        Vector3 next,
        out Vector3 a,
        out Vector3 q1,
        out Vector3 q2,
        out Vector3 b)
    {
        a = q1 = q2 = b = corner;

        Vector2 inVec = Flat(corner) - Flat(prev);
        Vector2 outVec = Flat(next) - Flat(corner);
        float lenIn = inVec.Length();
        float lenOut = outVec.Length();
        if (lenIn < MinCornerLegYards || lenOut < MinCornerLegYards)
            return false;

        Vector2 dirIn = inVec / lenIn;
        Vector2 dirOut = outVec / lenOut;
        float cos = Math.Clamp(Vector2.Dot(dirIn, dirOut), -1f, 1f);
        float angle = MathF.Acos(cos);
        if (angle < MinCornerRadians || angle > MaxCornerRadians)
            return false;

        float trim = MathF.Min(MaxCornerTrimYards, 0.38f * MathF.Min(lenIn, lenOut));
        if (trim < 2.0f)
            return false;

        Vector3 rawA = Vector3.Lerp(corner, prev, trim / lenIn);
        Vector3 rawB = Vector3.Lerp(corner, next, trim / lenOut);
        Vector3 rawQ1 = QuadraticBezier(rawA, corner, rawB, 1f / 3f);
        Vector3 rawQ2 = QuadraticBezier(rawA, corner, rawB, 2f / 3f);

        if (!TrySnapHeight(map, rawA, out a)
            || !TrySnapHeight(map, rawQ1, out q1)
            || !TrySnapHeight(map, rawQ2, out q2)
            || !TrySnapHeight(map, rawB, out b))
        {
            return false;
        }

        return IsChordSafe(map, prev, a)
            && IsChordSafe(map, a, q1)
            && IsChordSafe(map, q1, q2)
            && IsChordSafe(map, q2, b)
            && IsChordSafe(map, b, next);
    }

    private static Vector3 QuadraticBezier(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        float u = 1f - t;
        return (u * u * p0) + (2f * u * t * p1) + (t * t * p2);
    }

    internal static bool TrySnapHeight(Map map, Vector3 candidate, out Vector3 snapped)
    {
        snapped = candidate;
        float terrain = map.Terrain.GetHeight(candidate.X, candidate.Y, candidate.Z);
        float floor = map.Collision.GetHeight(candidate.X, candidate.Y, candidate.Z);
        // When the base NavMesh point is on the terrain, refuse any offset/cut point where MapCollision.GetHeight hits an overhead
        // M2/WMO model (fence, bush, rock, tree branch, porch eave) above the terrain.
        if (ValidHeight(terrain) && ValidHeight(floor) && floor > terrain + 0.35f && MathF.Abs(terrain - candidate.Z) <= 0.6f)
            return false;

        if (!ValidHeight(floor) || MathF.Abs(floor - candidate.Z) > MaxFloorDeltaYards)
            return false;

        snapped = candidate with { Z = floor };
        return true;
    }

    internal static bool IsChordSafe(Map map, Vector3 from, Vector3 to)
    {
        if (MathF.Abs(to.Z - from.Z) > MaxFloorDeltaYards * 3f)
            return false;

        if (!PlayerbotMoveCheck.LineClear(map.Collision, from, to))
            return false;

        if (map.Collision.MeshPoints is { } mesh)
        {
            MeshRaycastResult ray = mesh.Raycast(map.MapId, from, to, WalkableMeshOptions);
            if (!ray.Available || ray.Hit || MathF.Abs(ray.Point.Z - to.Z) > MaxFloorDeltaYards * 2f)
                return false;
        }

        float flatDist = Vector2.Distance(Flat(from), Flat(to));
        int samples = (int)MathF.Ceiling(flatDist / 3.0f);
        for (int s = 1; s < samples; s++)
        {
            Vector3 probe = Vector3.Lerp(from, to, s / (float)samples);
            if (!TrySnapHeight(map, probe, out _))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Deterministic per-bot corridor profile derived from <paramref name="botGuid"/> via SplitMix64:
    /// assigns a constant lateral lane preference in <c>[-MaxLaneOffsetYards, +MaxLaneOffsetYards]</c> (avoiding the exact 0 centerline
    /// so every bot picks a left or right lane bias), a weave amplitude, wavelength (22..36 yards), and phase.
    /// </summary>
    internal static void ComputeBotLaneProfile(
        ulong botGuid,
        out float laneOffset,
        out float weaveAmplitude,
        out float weaveInvWavelength,
        out float weavePhase)
    {
        ulong h1 = Mix64(botGuid ^ 0x9E3779B97F4A7C15UL);
        ulong h2 = Mix64(h1 ^ 0xBF58476D1CE4E5B9UL);

        // Map u0 in [0, 1) to either [-MaxLaneOffsetYards, -0.35f] or [+0.35f, +MaxLaneOffsetYards] so bots never default to 0.0 centerline.
        float u0 = UnitFloat(h1);
        float sign = (h1 & 1UL) == 0 ? 1f : -1f;
        float magnitude = 0.35f + (u0 * (MaxLaneOffsetYards - 0.35f));
        laneOffset = sign * magnitude;

        float u1 = UnitFloat(h1 >> 24);
        weaveAmplitude = (0.30f + (0.70f * u1)) * MaxWeaveAmplitudeYards;

        float u2 = UnitFloat(h2);
        float wavelength = 22f + (14f * u2); // 22..36 yards per full wave
        weaveInvWavelength = MathF.Tau / wavelength;

        float u3 = UnitFloat(h2 >> 24);
        weavePhase = u3 * MathF.Tau;
    }

    private static ulong Mix64(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static float UnitFloat(ulong bits)
        => (bits & 0xFFFFFFUL) / (float)0x1000000UL;

    private static float SmoothRamp(float x)
    {
        float t = Math.Clamp(x, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static float TotalFlatLength(IReadOnlyList<Vector3> points)
    {
        float sum = 0f;
        for (int i = 1; i < points.Count; i++)
            sum += Vector2.Distance(Flat(points[i - 1]), Flat(points[i]));
        return sum;
    }

    private static bool ValidHeight(float height)
        => float.IsFinite(height) && height != TerrainTile.InvalidHeight && height != TerrainTile.InvalidHeightValue;
}
