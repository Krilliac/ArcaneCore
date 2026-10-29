using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// How far apart (yd, at most) a creature leg's ground is sampled. The terrain is a 4.17 yd lattice with centre vertices, rock and
    /// doodad tops are narrower: at 4 yd (<see cref="GroundHeight.DrapePath"/>'s default) the as-sent terrain offset had p95 0.17 and a
    /// worst leg 0.66 yd, at 2 yd p95 0.17 and 0.27, at 1 yd p95 0.15 and 0.17 (docs/areas/collision-pathing.md, "Ground snap").
    /// </summary>
    internal const float GroundSampleYards = 1f;

    /// <summary>
    /// How far (yd) a sample may lie off the chord of the points kept before it. The offset the client sees adds the stand offset
    /// (<see cref="GroundHeight.StandOffset"/>, 0.05) to this, and the gates are p95 under 0.15: 0.15 here measured p95 0.17.
    /// </summary>
    internal const float GroundToleranceYards = 0.11f;

    /// <summary>
    /// The same tolerance where the floor is a model or the mesh (<see cref="GroundHeight.DrapePath"/> <c>modelTolerance</c>): WMO ramps and
    /// stairs need 12 points a leg at 0.11 (Undercity, median of the raw mesh legs) and 7 at 0.40, with model offsets still p95 +0.30 and p05 -0.25 (gate: p95 0.5).
    /// </summary>
    internal const float GroundModelToleranceYards = 0.4f;

    /// <summary>The floor window (yd up and down) for a height that is already a floor: a spawn's database z when the creature stands at home.</summary>
    internal const float AtHomeSnapYards = 0.5f;

    /// <summary>
    /// A creature leg draped over the ground (<see cref="GroundHeight.DrapePath"/>): every point on the floor plus the stand offset, and
    /// split every 1 yd where the chord between points would pass over or under it. Walkers and swimmers; a flier keeps the mesh's heights
    /// and a creature that only swims (<c>InhabitType</c> 2) stays in its water. A creature that also swims is put on the liquid surface
    /// where the liquid covers the floor, a pure walker on the floor. Where no floor is in the window (another storey, no data, a terrain
    /// tile that is not in memory) a point keeps its height. On the calling thread, which must be allowed to read the model trees: the world
    /// thread, or a map thread inside the serial gate. An ungated wander leg tries the drape outside the gate first, from what is already loaded
    /// (<see cref="GroundHeight.TryDrapePathShared"/>), and enters the gate for it only when that gives up, like its other model reads.
    /// The time it takes is part of the update's path-time budget (<see cref="PathTicksThisTick"/>, <see cref="HasPathBudget"/>), and a
    /// finished path job's result is only applied while that budget has room (<see cref="HasDrapeBudget"/>).
    /// </summary>
    /// <param name="cornersOnMesh">The corners are navigation-mesh heights (a mesh or partial path): a floor is looked for from at most
    /// <see cref="GroundHeight.MeshCeilingYards"/> (1 yd) above one, so a roof or an upper storey higher than that is not taken for the walkable
    /// floor (<see cref="GroundHeight.DrapePath"/> <c>cornersOnMesh</c>).</param>
    /// <param name="window">A narrower floor window (yards up and down) for a height that is already a floor: a spawn's database z. Terrain and
    /// model floors only: with no floor inside it the height is kept (the mesh fallback would search up to 4 yd above).</param>
    internal List<Vector3> DrapeOnGround(Creature creature, Vector3 start, IReadOnlyList<Vector3> corners, bool cornersOnMesh = false, float? window = null)
    {
        uint inhabit = creature.Template.InhabitType;
        // The same exemptions as CreatureAnomalies' under-map check: fliers, water-only creatures, a creature on a transport (its heights are
        // the transport's) and one a script set flying or hovering.
        if (!GroundSnapEnabled || corners.Count == 0 || (inhabit & 4) != 0 || inhabit == 2
            || creature.Movement.TransportGuid != 0
            || (creature.Movement.Flags & (MovementFlags.Flying | MovementFlags.Hover)) != 0)
        {
            return corners as List<Vector3> ?? [.. corners];
        }

        GroundQuery query = GroundHeight.Walker with { Liquid = (inhabit & 2) != 0 ? LiquidMode.Surface : LiquidMode.Bottom };
        if (window is { } w)
        {
            // No mesh fallback: it searches up to MeshFallbackUpYards above, which would lift a spawn onto a deck 3 yd over it.
            query = query with { Up = w, Down = w, AllowMeshFallback = false };
        }

        long started = Stopwatch.GetTimestamp();
        DrapedPaths++;
        List<Vector3> draped;
        if (_ungatedMotion)
        {
            // First from what is loaded, outside the gate (GroundHeight.TryDrapePathShared: every sample equals the gated answer for the model and terrain snapshot and the navmesh state at that moment). It gives up, and the
            // drape is repeated inside the gate as before, when a tile it needs is not in memory or the navmesh is not one a shared query may use.
            List<Vector3>? shared = GroundHeight.TryDrapePathShared(Map.Collision, start, corners, out SharedDrapeAbort abort, step: GroundSampleYards,
                tolerance: GroundToleranceYards, query: query, modelTolerance: GroundModelToleranceYards, cornersOnMesh: cornersOnMesh);
            if (shared is not null)
            {
                SharedDrapes++;
                SharedDrapeStats.RecordHit();
                NoteDrapeTicks(Stopwatch.GetTimestamp() - started);
                return shared;
            }

            SharedDrapeStats.RecordFallback(abort);
            GatedDrapes++;
            NoteDrapeTicks(Stopwatch.GetTimestamp() - started); // the shared attempt that gave up is part of the update's path time
            using (Serial())
            {
                // Timed inside the gate: the wait for another map to release it is not this map's drape time.
                long gated = Stopwatch.GetTimestamp();
                draped = GroundHeight.DrapePath(Map.Collision, start, corners, step: GroundSampleYards, tolerance: GroundToleranceYards, query: query,
                    modelTolerance: GroundModelToleranceYards, cornersOnMesh: cornersOnMesh);
                NoteDrapeTicks(Stopwatch.GetTimestamp() - gated);
            }

            return draped;
        }

        draped = GroundHeight.DrapePath(Map.Collision, start, corners, step: GroundSampleYards, tolerance: GroundToleranceYards, query: query,
            modelTolerance: GroundModelToleranceYards, cornersOnMesh: cornersOnMesh);
        NoteDrapeTicks(Stopwatch.GetTimestamp() - started);
        return draped;
    }

    /// <summary>
    /// Whether this update's path-time budget still has room for the drape of one more finished path-job result. Searches and nodes are
    /// not asked: a job's search ran on a pool thread. Unlike <see cref="HasPathBudget"/> it is not spent by count, so only the time the
    /// searches and drapes of this update took limits how many results are applied; the rest wait with their creatures for the next update.
    /// </summary>
    internal bool HasDrapeBudget => _pathTicksThisTick < _pathTickLimit;

    /// <summary>Paths put on the ground since the system was created (diagnostics, tests).</summary>
    internal long DrapedPaths { get; private set; }

    /// <summary>Drapes of an ungated wander leg answered outside the serial gate (<see cref="GroundHeight.TryDrapePathShared"/>; diagnostics, tests).</summary>
    internal long SharedDrapes { get; private set; }

    /// <summary>Drapes of an ungated wander leg that took the serial gate: the shared drape gave up, and a gated drape ran (diagnostics, tests).</summary>
    internal long GatedDrapes { get; private set; }

    /// <summary>The <see cref="Stopwatch"/> ticks those drapes took (diagnostics, tests).</summary>
    internal long DrapeTicksTotal { get; private set; }

    private void NoteDrapeTicks(long ticks)
    {
        _pathTicksThisTick += ticks;
        DrapeTicksTotal += ticks;
    }

    /// <summary>Whether finished paths are put on the ground (<see cref="DrapeOnGround"/>); off only to measure what it costs.</summary>
    internal bool GroundSnapEnabled { get; set; } = true;
}
