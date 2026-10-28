using System.Diagnostics;
using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Shapes the corners of a navigated bot route after the mesh and <see cref="PlayerbotRouteSmoothing"/> have produced it. The mesh is
/// built with a 0.2 yard agent radius and its string-pulled path touches every polygon vertex, so a corner around a post, a doodad hole or
/// a wall edge sits a fraction of a yard from it; the Bezier corner cut of <see cref="PlayerbotRouteSmoothing"/> cuts toward that obstacle
/// and gives up at a boundary vertex. Three behaviours, all per corner and all verified before they are used:
/// <list type="number">
/// <item><description><b>Clearance</b>: a corner is moved away from the obstacle on the inside of the turn until
/// <see cref="Settings.ClearanceYards"/> separate them (measured with a mesh raycast, so what counts is where the walkable mesh ends).</description></item>
/// <item><description><b>Centring</b>: where the corridor is bounded on both sides (a road between walls), the corner moves toward its middle; the
/// centring move is at most <see cref="Settings.CentringMaxYards"/>, and the push is the larger of that and the clearance still missing, so
/// the cap never reduces the clearance push.</description></item>
/// <item><description><b>Rounding</b>: a turn sharper than <see cref="MaxHeadingStepRadians"/> is replaced, where the legs and the clearance
/// leave room for it, by a circular arc that starts <see cref="Settings.TurnLeadYards"/> (at most 40% of the shorter leg) before the corner.
/// The arc has up to <see cref="MaxArcInteriorPoints"/> interior points spread evenly in angle, so no interior vertex of a rounded corner turns
/// more than <see cref="MaxHeadingStepRadians"/> (the two tangent points turn half as much). A corner not sharper than that whose push
/// would widen its turn past it is rounded too. A corner whose arc is not possible keeps the sharp turn it has, moved off the obstacle.</description></item>
/// </list>
/// <para>
/// The heading guarantee (G), where rounding is on (<see cref="Settings.TurnLeadYards"/> above 0), with A = <see cref="MaxHeadingStepRadians"/>:
/// every point the shaper creates (the tangent points, the interior arc points and the split point) turns at most A; a vertex it moves whose
/// turn was at most A stays at most A; an emitted vertex at most A is never raised above A by a later corner's push (the push is cut, or a
/// split point one yard along the incoming leg takes the turn); an emitted vertex already above A is raised by at most 0.3 radians. Exceptions:
/// (E1) a corner sharper than A whose arc is not possible (too little lead, no room, a rejected arc, the budget) keeps a sharp turn, widened by
/// its own push; (E2) a mesh corner kept unchanged whose turn the previous corner's push widened past A and that cannot be rounded;
/// (E3) the heading at the start point relative to the bot's facing is not bounded.
/// </para>
/// Nothing is squeezed: a corner whose far side leaves less than <see cref="EdgeMarginYards"/> after the move (a doorway, a bridge) keeps
/// its mesh position. A new leg must pass <see cref="SegmentSafe(Map, Vector3, Vector3)"/> or, only where the mesh leg it replaces
/// (<c>points[i-1]</c> to <c>points[i]</c> or <c>points[i]</c> to <c>points[i+1]</c> of the input, never a shaped neighbour) fails that too,
/// the bounded relative contract of <see cref="SegmentAcceptable(Map, Vector3, Vector3, Vector3, Vector3, ref OriginalLeg, ref ShapeStats)"/>;
/// otherwise the corner keeps its mesh position. The start and the destination are never moved.
/// <para>
/// Cost, per plan of a navigated route: nothing when disabled or when no corner turns sharply enough; otherwise a few mesh raycasts per
/// corner plus the leg checks, bounded by <see cref="MaxShapedCornersPerPath"/>, <see cref="PlayerbotRouteSmoothing.MaxSmoothHorizonYards"/>
/// and the query budget: every probe, ground snap, leg check and measurement of a mesh leg is charged to <see cref="ShapeStats.Queries"/>
/// before it is made and refused (the corner keeps its mesh position, <see cref="ShapeStats.FallbackBudget"/>) when it would take the path
/// past <see cref="MaxQueriesPerPath"/>. The count is an estimate of collision and mesh queries (<see cref="SegmentBaseQueries"/> for the fixed
/// part of a leg check, two or four height lookups per 3 yards of leg); the budget therefore holds for the estimate, not for a hardware counter,
/// and it is <see cref="MaxQueriesPerPath"/> plus at most one reverse chord check (about 40 queries or fewer), which is charged after the fact.
/// The only allocation of a path that changes is its point list. World thread; the process-wide totals use <see cref="Interlocked"/> and may be
/// read from any thread.
/// </para>
/// </summary>
internal static class PlayerbotPathShaping
{
    /// <summary>Smallest flat turn angle (radians, about 20 degrees) at a corner that is shaped at all.</summary>
    internal const float MinShapeTurnRadians = 0.35f;

    /// <summary>The largest heading change (radians, about 34 degrees) one vertex of a shaped route may make; a sharper turn is rounded with an arc.</summary>
    internal const float MaxHeadingStepRadians = 0.6f;

    /// <summary>The distance (yards) a moved corner keeps to the mesh boundary on the side it moves toward.</summary>
    internal const float EdgeMarginYards = 0.5f;

    /// <summary>The most corners of one path that are shaped; later corners keep their mesh position.</summary>
    internal const int MaxShapedCornersPerPath = 12;

    /// <summary>
    /// The query budget of one path (mesh raycasts count 1, a ground snap 3, a <see cref="SegmentSafe(Map, Vector3, Vector3)"/> its estimated cost,
    /// <see cref="SegmentBaseQueries"/> plus two height lookups per 3 yards, and the same again for the reverse chord check when it is needed).
    /// Each is charged before it is made and refused past this limit; the only charge made after the fact is the reverse chord check of a leg
    /// that failed its forward check, so the estimate of a path never exceeds the limit by more than one such check (at most 40 queries for
    /// legs of <see cref="MaxShapedLegYards"/> plus the largest push).
    /// </summary>
    internal const int MaxQueriesPerPath = 192;

    /// <summary>The most points a shaped route may have.</summary>
    internal const int MaxShapedPoints = 128;

    /// <summary>The shortest flat leg (yards) beside a corner that is shaped.</summary>
    internal const float MinLegYards = 1f;

    /// <summary>
    /// The longest flat leg (yards) beside a corner that is shaped. The floor check of a leg costs two lookups per 3 yards of it (more for
    /// the relative contract), so a corner next to a longer leg keeps its mesh position: that bounds the cost of any one leg check and
    /// lets the budget below pay for a whole corner.
    /// </summary>
    internal const float MaxShapedLegYards = 40f;

    /// <summary>The shortest arc tangent length (yards) worth walking.</summary>
    internal const float MinLeadYards = 1f;

    /// <summary>A push below this (yards) is no move.</summary>
    internal const float MinPushYards = 0.05f;

    /// <summary>
    /// The estimated queries of a <see cref="SegmentSafe(Map, Vector3, Vector3)"/> before its per-3-yard height lookups: the line of sight, the
    /// mesh raycast of the chord check, and the mesh walks of the on-mesh check (two when the first direction passes, up to eight when not).
    /// An estimate, so the count is documented as such; the budget only needs the right order of magnitude.
    /// </summary>
    internal const int SegmentBaseQueries = 6;

    /// <summary>
    /// The most interior points of a rounding arc. The arc divides the turn into one more step than it has interior points, so with 5 a turn
    /// of up to pi radians has steps of at most pi / 6 (about 0.52), below <see cref="MaxHeadingStepRadians"/>.
    /// </summary>
    internal const int MaxArcInteriorPoints = 5;

    /// <summary>The most points one corner yields: the arc (interior points and two tangent points) and the split point beside it.</summary>
    private const int MaxCornerPoints = MaxArcInteriorPoints + 2 + 1;

    /// <summary>Radians kept below <see cref="MaxHeadingStepRadians"/> by every cap, so float error never lands above it.</summary>
    private const float HeadingEpsilonRadians = 1e-3f;

    /// <summary>The most (radians) a push may raise the turn of an already emitted vertex that was above <see cref="MaxHeadingStepRadians"/> before.</summary>
    private const float SharpNeighbourSlackRadians = 0.3f;

    /// <summary>Margin on the planned arc excursion for the widening of the turn when the corner is pushed outward.</summary>
    private const float ArcWidenMargin = 1.25f;

    /// <summary>Margin on the excursion planned for the turn at the already moved corner (the turn is known then, so it is small).</summary>
    private const float ArcReplanMargin = 1.1f;

    /// <summary>Passes of the push against the turn it creates.</summary>
    private const int ReplanPasses = 3;

    /// <summary>Radians added to the turn when the push is planned for the arc's tangent points (the turn widens when the corner is pushed outward).</summary>
    private const float ArcPlanningAngleMarginRadians = 0.2f;

    /// <summary>
    /// How far inside the corner an arc with tangent length 1 reaches at its deepest chord, for a turn of <paramref name="theta"/>:
    /// the apex of the circle (tan(theta / 4)) plus the sagitta of the chord across the widest arc step.
    /// </summary>
    private static float ArcReach(float theta)
    {
        int interior = Math.Clamp((int)MathF.Ceiling(theta / MaxHeadingStepRadians) - 1, 1, MaxArcInteriorPoints);
        float step = theta / (interior + 1);
        return MathF.Tan(theta * 0.25f) + ((1f - MathF.Cos(step * 0.5f)) / MathF.Tan(theta * 0.5f));
    }

    private static long s_paths;
    private static long s_cornersShaped;
    private static long s_fallbacks;
    private static long s_queries;
    private static long s_ticks;

    /// <summary>The tunables of one shaping run (<see cref="PlayerbotOptions"/>), a value so that tests can call <see cref="ShapePoints"/> directly.</summary>
    /// <param name="Enabled">Whether to shape at all.</param>
    /// <param name="ClearanceYards">Clearance kept from the inside of a turn.</param>
    /// <param name="CentringMaxYards">Farthest a corner moves toward the middle of a bounded corridor; 0 = no centring.</param>
    /// <param name="TurnLeadYards">Tangent length of the rounding arc; 0 = no rounding.</param>
    internal readonly record struct Settings(bool Enabled, float ClearanceYards, float CentringMaxYards, float TurnLeadYards)
    {
        /// <summary>The settings the options ask for.</summary>
        internal static Settings From(PlayerbotOptions options)
            => new(options.PathShaping, options.CornerClearanceYards, options.CornerCentringMaxYards, options.CornerTurnLeadYards);
    }

    /// <summary>What one shaping run did and cost. Counters add up over runs (<see cref="Add"/>).</summary>
    internal struct ShapeStats
    {
        /// <summary>Corners that reached the probes (not skipped for being flat, short, beyond the horizon or over a bound).</summary>
        public int CornersConsidered;

        /// <summary>Corners that were moved or rounded.</summary>
        public int CornersShaped;

        /// <summary>Corners replaced by an arc (a subset of <see cref="CornersShaped"/>).</summary>
        public int CornersRounded;

        /// <summary>Corners left at their mesh position because shaping them was unsafe (no room, a failed leg or height check).</summary>
        public int Fallbacks;

        /// <summary>Mesh raycasts and mesh surface walks issued directly by the shaper.</summary>
        public int Probes;

        /// <summary><see cref="PlayerbotPathShaping.SegmentSafe(Map, Vector3, Vector3, ref ShapeStats)"/> calls.</summary>
        public int Segments;

        /// <summary>Estimated collision and mesh queries: <see cref="Probes"/> plus the height lookups of snapping plus the estimated cost of every segment.</summary>
        public int Queries;

        /// <summary>Corners kept because the move needed more room than the far side leaves (<see cref="Fallbacks"/> is the total of the room, ground, segment, budget and arc reasons).</summary>
        public int FallbackRoom;

        /// <summary>Corners kept because the moved corner had no acceptable mesh surface or floor height.</summary>
        public int FallbackGround;

        /// <summary>Corners kept because a new leg around the moved corner failed <see cref="PlayerbotPathShaping.SegmentSafe(Map, Vector3, Vector3, ref ShapeStats)"/>.</summary>
        public int FallbackSegment;

        /// <summary>Corners kept because the query budget (<see cref="PlayerbotPathShaping.MaxQueriesPerPath"/>) could not pay for the next probe, snap or leg check.</summary>
        public int FallbackBudget;

        /// <summary>Corners left unchanged because the arc they were planned to get was rejected and there was no push to keep (a rounding-only corner).</summary>
        public int FallbackArc;

        /// <summary>Charges refused by the query budget (a corner can be refused several).</summary>
        public int BudgetStops;

        /// <summary>Segments refused by the chest-height line of sight (doodads included).</summary>
        public int SegLineClear;

        /// <summary>Segments refused by the rest of the chord check (height step, mesh raycast, floor samples, non-finite points).</summary>
        public int SegChord;

        /// <summary>Segments refused by the motion's on-mesh check.</summary>
        public int SegOnMesh;

        /// <summary>Corners moved by less than the clearance asks (the full push failed the ground or segment checks; a smaller rung of the push ladder passed).</summary>
        public int PartialPushes;

        /// <summary>New legs accepted by the relative contract (<see cref="SegmentAcceptable(Map, Vector3, Vector3, Vector3, Vector3, ref OriginalLeg, ref ShapeStats)"/>): they fail the strict check, and so does the mesh leg they replace, and they are no worse than it.</summary>
        public int RelativeSegments;

        /// <summary>Segments that passed the chord check only from their far end (the mesh raycast from the start vertex found no start polygon on the leg).</summary>
        public int ReversedChords;

        /// <summary>Corners whose arc was dropped (tangent too short for the clearance, or an arc point or chord was not usable); the corner keeps its pushed position.</summary>
        public int ArcRejected;

        /// <summary>Corners whose push was cut so that the turn at the previously emitted vertex stays within the heading bound.</summary>
        public int NeighbourCaps;

        /// <summary>Corners that got a split point on the incoming leg so that the full push (and its arc) could be kept.</summary>
        public int NeighbourSplits;

        /// <summary>Unrounded corners whose push was cut so that their own turn stays within the heading bound.</summary>
        public int SelfCaps;

        /// <summary>Gentle corners (not sharper than the heading bound) that were rounded because their push would have widened the turn past it.</summary>
        public int AutoRounded;

        /// <summary>Corners kept because the heading bound left no push worth making and there was nothing to round.</summary>
        public int FallbackHeading;

        /// <summary>Stopwatch ticks (<see cref="Stopwatch.Frequency"/> per second) spent shaping.</summary>
        public long ElapsedTicks;

        /// <summary>Add another run's counters to this one.</summary>
        public void Add(in ShapeStats other)
        {
            CornersConsidered += other.CornersConsidered;
            CornersShaped += other.CornersShaped;
            CornersRounded += other.CornersRounded;
            Fallbacks += other.Fallbacks;
            Probes += other.Probes;
            Segments += other.Segments;
            Queries += other.Queries;
            ElapsedTicks += other.ElapsedTicks;
            FallbackRoom += other.FallbackRoom;
            FallbackBudget += other.FallbackBudget;
            FallbackArc += other.FallbackArc;
            BudgetStops += other.BudgetStops;
            FallbackGround += other.FallbackGround;
            FallbackSegment += other.FallbackSegment;
            SegLineClear += other.SegLineClear;
            SegChord += other.SegChord;
            SegOnMesh += other.SegOnMesh;
            ArcRejected += other.ArcRejected;
            PartialPushes += other.PartialPushes;
            ReversedChords += other.ReversedChords;
            RelativeSegments += other.RelativeSegments;
            NeighbourCaps += other.NeighbourCaps;
            NeighbourSplits += other.NeighbourSplits;
            SelfCaps += other.SelfCaps;
            AutoRounded += other.AutoRounded;
            FallbackHeading += other.FallbackHeading;
        }
    }

    /// <summary>
    /// The process-wide totals of every shaping run since start (or <see cref="ResetTotals"/>): paths shaped (runs that reached the corner loop),
    /// corners shaped, fallbacks, estimated queries and Stopwatch ticks. Divide by <c>Paths</c> for the average cost of a path.
    /// </summary>
    internal static (long Paths, long CornersShaped, long Fallbacks, long Queries, long Ticks) ReadTotals()
        => (Interlocked.Read(ref s_paths), Interlocked.Read(ref s_cornersShaped), Interlocked.Read(ref s_fallbacks),
            Interlocked.Read(ref s_queries), Interlocked.Read(ref s_ticks));

    /// <summary>Zero the process-wide totals (tests).</summary>
    internal static void ResetTotals()
    {
        Interlocked.Exchange(ref s_paths, 0);
        Interlocked.Exchange(ref s_cornersShaped, 0);
        Interlocked.Exchange(ref s_fallbacks, 0);
        Interlocked.Exchange(ref s_queries, 0);
        Interlocked.Exchange(ref s_ticks, 0);
    }

    /// <summary>
    /// Shape the corners of a navigated <paramref name="route"/>: <paramref name="route"/> itself (the same reference) when shaping is off,
    /// the route is not navigated or has no mesh to judge by, or nothing changed; otherwise a new navigated route with the shaped points
    /// and the flags of <paramref name="route"/>.
    /// </summary>
    internal static PlayerbotRoute Shape(Map map, PlayerbotRoute route, PlayerbotOptions options)
    {
        if (!options.PathShaping || !route.Navigated || route.Points.Count < 3 || map.Collision.MeshPoints is null)
            return route;

        ShapeStats stats = default;
        IReadOnlyList<Vector3> shaped = ShapePoints(map, route.Points, Settings.From(options), ref stats);
        if (ReferenceEquals(shaped, route.Points))
            return route;

        float distance = 0f;
        for (int i = 1; i < shaped.Count; i++)
        {
            float leg = Vector3.Distance(shaped[i - 1], shaped[i]);
            if (!float.IsFinite(leg))
                return route;
            distance += leg;
        }

        return new PlayerbotRoute(shaped, distance, navigated: true)
        {
            CompletedQuestReturn = route.CompletedQuestReturn,
            CrossBesidePlaceMarks = route.CrossBesidePlaceMarks,
            Chase = route.Chase,
            Pathfinder = route.Pathfinder,
            MeshPoints = route.MeshPoints,
            MeshEndedShort = route.MeshEndedShort,
        };
    }

    /// <summary>
    /// Shape a polyline: <paramref name="points"/> itself when nothing changes, else a new list. <c>points[0]</c> and the last point are
    /// never moved. <paramref name="stats"/> receives this run's counters; the process-wide totals are updated too.
    /// </summary>
    internal static IReadOnlyList<Vector3> ShapePoints(Map map, IReadOnlyList<Vector3> points, in Settings s, ref ShapeStats stats)
    {
        if (!s.Enabled || points.Count < 3 || map.Collision.MeshPoints is not { } mesh)
            return points;

        long started = Stopwatch.GetTimestamp();
        ShapeStats run = default;
        IReadOnlyList<Vector3> result = ShapeCore(map, mesh, points, in s, ref run);
        run.ElapsedTicks = Stopwatch.GetTimestamp() - started;

        stats.Add(in run);
        Interlocked.Increment(ref s_paths);
        Interlocked.Add(ref s_cornersShaped, run.CornersShaped);
        Interlocked.Add(ref s_fallbacks, run.Fallbacks);
        Interlocked.Add(ref s_queries, run.Queries);
        Interlocked.Add(ref s_ticks, run.ElapsedTicks);
        return result;
    }

    /// <summary>
    /// Whether the straight leg from <paramref name="a"/> to <paramref name="b"/> is one the bot may walk: it passes the chord check of
    /// <see cref="PlayerbotRouteSmoothing"/> (chest-height line of sight including doodads, a mesh raycast, floor heights every 3 yards) and
    /// stays on the walkable mesh as the motion itself judges it (<see cref="PlayerbotMotionAnomalies.StaysOnMesh"/>).
    /// </summary>
    internal static bool SegmentSafe(Map map, Vector3 a, Vector3 b)
        => a.IsFinite() && b.IsFinite()
            && ChordSafeEitherWay(map, a, b, out _, out _)
            && PlayerbotMotionAnomalies.StaysOnMesh(map, a, b);

    /// <summary>
    /// The chord check (<see cref="PlayerbotRouteSmoothing.IsChordSafe"/>) from <paramref name="a"/> to <paramref name="b"/>, or else from
    /// <paramref name="b"/> to <paramref name="a"/>. Its mesh raycast starts at the leg's first point, and a ray that starts exactly at a mesh
    /// vertex (every corner of a string-pulled path is one) can pick a start polygon that does not contain the leg and report a hit at
    /// fraction 0 for a leg the mesh itself produced; the motion's own on-mesh check asks both ways for the same reason
    /// (<see cref="PlayerbotMotionAnomalies.StaysOnMesh"/>). The second ask costs queries only after a failure; a genuine obstacle fails both ways.
    /// </summary>
    private static bool ChordSafeEitherWay(Map map, Vector3 a, Vector3 b, out bool reversed, out bool askedReverse)
    {
        reversed = false;
        askedReverse = false;
        if (PlayerbotRouteSmoothing.IsChordSafe(map, a, b))
            return true;
        askedReverse = true;
        reversed = PlayerbotRouteSmoothing.IsChordSafe(map, b, a);
        return reversed;
    }

    /// <summary>Samples of the floor check along a leg (one every 3 yards, as <see cref="PlayerbotRouteSmoothing.IsChordSafe"/> takes them), 0 for a non-finite leg.</summary>
    private static int SampleCount(Vector3 a, Vector3 b)
    {
        float flat = Vector2.Distance(Flat(a), Flat(b));
        return float.IsFinite(flat) ? (int)MathF.Min(MathF.Ceiling(flat / 3f), 1000f) : 0;
    }

    /// <summary>The estimated queries of a strict leg check: <see cref="SegmentBaseQueries"/> plus two height lookups per interior sample.</summary>
    private static int StrictCost(Vector3 a, Vector3 b) => SegmentBaseQueries + (2 * Math.Max(0, SampleCount(a, b) - 1));

    /// <summary>The estimated queries of one chord check (line of sight, mesh ray and the attribution ask, two lookups per interior sample): the reverse ask of a strict check.</summary>
    private static int ChordCost(Vector3 a, Vector3 b) => 3 + (2 * Math.Max(0, SampleCount(a, b) - 1));

    /// <summary>The estimated queries of <see cref="MeasureLeg"/>: the floor, then the snap's terrain and floor lookups, per interior sample.</summary>
    private static int MeasureCost(Vector3 a, Vector3 b) => 4 * Math.Max(0, SampleCount(a, b) - 1);

    /// <summary>
    /// Charge <paramref name="cost"/> estimated queries to the path before they are made: false (and <see cref="ShapeStats.BudgetStops"/>
    /// counted) when that, plus <paramref name="reserve"/> that a check may still add, would take the path past <see cref="MaxQueriesPerPath"/>.
    /// </summary>
    private static bool TryCharge(ref ShapeStats stats, int cost, int reserve = 0)
    {
        if (stats.Queries + cost + reserve > MaxQueriesPerPath)
        {
            stats.BudgetStops++;
            return false;
        }

        stats.Queries += cost;
        return true;
    }

    /// <summary><see cref="SegmentSafe(Map, Vector3, Vector3)"/> that also counts the call and its estimated queries in <paramref name="stats"/>; refused (false) when the query budget cannot pay for it.</summary>
    internal static bool SegmentSafe(Map map, Vector3 a, Vector3 b, ref ShapeStats stats)
    {
        // The reverse chord check is charged when it is made, after this charge: a leg can take the path past the budget by that one check.
        if (!TryCharge(ref stats, StrictCost(a, b)))
            return false;

        stats.Segments++;
        if (!a.IsFinite() || !b.IsFinite())
        {
            stats.SegChord++;
            return false;
        }

        bool chordSafe = ChordSafeEitherWay(map, a, b, out bool reversed, out bool askedReverse);
        if (askedReverse)
            stats.Queries += ChordCost(a, b);

        if (!chordSafe)
        {
            // The chord check includes the line of sight; asking it again on a failure only attributes the refusal (no change of semantics).
            if (!PlayerbotMoveCheck.LineClear(map.Collision, a, b))
                stats.SegLineClear++;
            else
                stats.SegChord++;
            return false;
        }

        if (!PlayerbotMotionAnomalies.StaysOnMesh(map, a, b))
        {
            stats.SegOnMesh++;
            return false;
        }

        if (reversed)
            stats.ReversedChords++;

        return true;
    }

    /// <summary>Yards a new leg may be worse than the mesh leg it replaces in floor mismatch and in height step (<see cref="SegmentAcceptable(Map, Vector3, Vector3, Vector3, Vector3, ref OriginalLeg, ref ShapeStats)"/>).</summary>
    internal const float RelativeSlackYards = 0.25f;

    /// <summary>
    /// What the chord check's floor samples and height step see on a leg: the largest difference over the samples (every 3 yards, as
    /// <see cref="PlayerbotRouteSmoothing.IsChordSafe"/> takes them) between the floor and the straight line between the ends
    /// (infinite when a sample has no floor or sits under an overhead model), and the end-to-end height change.
    /// </summary>
    internal readonly record struct LegMetrics(float MaxFloorMismatchYards, float AbsDeltaZYards);

    /// <summary>The mesh leg beside a corner, measured lazily and at most once per corner: whether it passes <see cref="SegmentSafe(Map, Vector3, Vector3)"/> and its <see cref="LegMetrics"/>.</summary>
    internal struct OriginalLeg
    {
        /// <summary>Whether <see cref="Strict"/> and <see cref="Metrics"/> are filled in.</summary>
        public bool Measured;

        /// <summary>Whether the mesh leg passes the strict check; then a new leg must pass it too.</summary>
        public bool Strict;

        /// <summary>The metrics of the mesh leg (only meaningful when <see cref="Strict"/> is false).</summary>
        public LegMetrics Metrics;
    }

    /// <summary>The floor mismatch and height step of the leg from <paramref name="a"/> to <paramref name="b"/>; counts its queries in <paramref name="stats"/>.</summary>
    internal static LegMetrics MeasureLeg(Map map, Vector3 a, Vector3 b, ref ShapeStats stats)
    {
        float dz = MathF.Abs(b.Z - a.Z);
        float flat = Vector2.Distance(Flat(a), Flat(b));
        if (!float.IsFinite(flat) || !float.IsFinite(dz))
            return new LegMetrics(float.PositiveInfinity, float.PositiveInfinity);

        int samples = SampleCount(a, b);
        // Per sample: the floor, then the snap's terrain and floor lookups. A measurement the budget cannot pay for is an unknown leg.
        if (!TryCharge(ref stats, MeasureCost(a, b)))
            return new LegMetrics(float.PositiveInfinity, dz);
        float worst = 0f;
        for (int s = 1; s < samples; s++)
        {
            Vector3 probe = Vector3.Lerp(a, b, s / (float)samples);
            float floor = map.Collision.GetHeight(probe.X, probe.Y, probe.Z);
            if (!float.IsFinite(floor) || floor == TerrainTile.InvalidHeight || floor == TerrainTile.InvalidHeightValue)
                return new LegMetrics(float.PositiveInfinity, dz);

            float mismatch = MathF.Abs(floor - probe.Z);
            // A snap refused for a reason other than the mismatch is an overhead model over the terrain: no leg is excused for that.
            if (mismatch <= PlayerbotRouteSmoothing.MaxFloorDeltaYards && !PlayerbotRouteSmoothing.TrySnapHeight(map, probe, out _))
                return new LegMetrics(float.PositiveInfinity, dz);
            worst = MathF.Max(worst, mismatch);
        }

        return new LegMetrics(worst, dz);
    }

    /// <summary>
    /// The contract for a leg of the shaped route that replaces the mesh leg <paramref name="origA"/> to <paramref name="origB"/>: it passes
    /// <see cref="SegmentSafe(Map, Vector3, Vector3, ref ShapeStats)"/>; or the mesh leg itself fails that (the floor samples of the chord
    /// check demand the floor to follow the straight line within 0.75 yd, which a mesh leg over steps or a ramp does not) and the new leg is
    /// no worse than it: its largest floor mismatch and its height step exceed the mesh leg's by at most <see cref="RelativeSlackYards"/>,
    /// the chest-height line of sight is clear, the mesh ray back from its end stays on the mesh, and the motion's on-mesh check passes.
    /// <paramref name="origA"/> to <paramref name="origB"/> is always the mesh's own leg of the input route, never a leg of an already shaped
    /// neighbour, so the allowance does not add up from corner to corner. When that leg passes the strict check, a new leg must pass it too.
    /// The mesh leg is measured through <paramref name="original"/> once per corner; every query is charged to <paramref name="stats"/>
    /// before it is made, and a check the query budget (<see cref="MaxQueriesPerPath"/>) cannot pay for refuses the leg
    /// (<see cref="ShapeStats.BudgetStops"/>). A leg is never accepted for what the mesh leg does not already do, or where it leaves the
    /// mesh or is blocked.
    /// </summary>
    internal static bool SegmentAcceptable(Map map, Vector3 a, Vector3 b, Vector3 origA, Vector3 origB, ref OriginalLeg original, ref ShapeStats stats)
    {
        if (SegmentSafe(map, a, b, ref stats))
            return true;
        if (!a.IsFinite() || !b.IsFinite() || !origA.IsFinite() || !origB.IsFinite())
            return false;

        if (!original.Measured)
        {
            // A measurement the budget refused says nothing about the mesh leg: it is not recorded and the new leg is refused.
            int stops = stats.BudgetStops;
            bool strict = SegmentSafe(map, origA, origB, ref stats);
            LegMetrics measured = strict ? default : MeasureLeg(map, origA, origB, ref stats);
            if (stats.BudgetStops != stops)
                return false;

            original.Strict = strict;
            original.Metrics = measured;
            original.Measured = true;
        }

        if (original.Strict || float.IsInfinity(original.Metrics.MaxFloorMismatchYards))
            return false;

        if (!TryCharge(ref stats, SegmentBaseQueries + 1)) // the line of sight, the on-mesh walks and the back ray
            return false;

        stats.Segments++;
        LegMetrics metrics = MeasureLeg(map, a, b, ref stats);
        if (!(metrics.MaxFloorMismatchYards <= original.Metrics.MaxFloorMismatchYards + RelativeSlackYards)
            || !(metrics.AbsDeltaZYards <= original.Metrics.AbsDeltaZYards + RelativeSlackYards)
            || !PlayerbotMoveCheck.LineClear(map.Collision, a, b)
            || !PlayerbotMotionAnomalies.StaysOnMesh(map, a, b)
            || map.Collision.MeshPoints is not { } mesh)
        {
            return false;
        }

        MeshRaycastResult back = mesh.Raycast(map.MapId, b, a, PlayerbotMoveCheck.MeshOptions);
        if (!back.Available || back.Hit || MathF.Abs(back.Point.Z - a.Z) > PlayerbotRouteSmoothing.MaxFloorDeltaYards * 2f)
            return false;

        stats.RelativeSegments++;
        return true;
    }

    /// <summary><see cref="SegmentAcceptable(Map, Vector3, Vector3, Vector3, Vector3, ref OriginalLeg, ref ShapeStats)"/> with a fresh measurement of the mesh leg (tests).</summary>
    internal static bool SegmentAcceptable(Map map, Vector3 a, Vector3 b, Vector3 origA, Vector3 origB, ref ShapeStats stats)
    {
        OriginalLeg original = default;
        return SegmentAcceptable(map, a, b, origA, origB, ref original, ref stats);
    }

    private static IReadOnlyList<Vector3> ShapeCore(Map map, IMeshPointQuery mesh, IReadOnlyList<Vector3> points, in Settings s, ref ShapeStats stats)
    {
        int n = points.Count;
        List<Vector3>? output = null;
        Vector3 prev = points[0];
        Vector3 prevPrev = prev; // the point before prev in the output
        bool hasPrevPrev = false;
        float accumulated = 0f;
        Span<Vector3> buffer = stackalloc Vector3[MaxCornerPoints];

        int i = 1;
        for (; i < n - 1; i++)
        {
            Vector3 v = points[i];
            Vector3 next = points[i + 1];
            accumulated += Vector2.Distance(Flat(points[i - 1]), Flat(v));

            // Past any of these nothing further is shaped, so the rest of the path is copied as it is.
            if (!float.IsFinite(accumulated) || accumulated > PlayerbotRouteSmoothing.MaxSmoothHorizonYards
                || stats.CornersShaped >= MaxShapedCornersPerPath || stats.Queries >= MaxQueriesPerPath)
            {
                break;
            }

            bool shapedHere = false;
            if (v.IsFinite() && next.IsFinite() && prev.IsFinite()
                && (output?.Count ?? i) + (n - i) + (MaxCornerPoints - 1) <= MaxShapedPoints)
            {
                Vector2 inVec = Flat(v) - Flat(prev);
                Vector2 outVec = Flat(next) - Flat(v);
                float lenIn = inVec.Length();
                float lenOut = outVec.Length();
                if (lenIn >= MinLegYards && lenOut >= MinLegYards && float.IsFinite(lenIn) && float.IsFinite(lenOut)
                    && lenIn <= MaxShapedLegYards && lenOut <= MaxShapedLegYards
                    && Vector2.Distance(Flat(points[i - 1]), Flat(v)) <= MaxShapedLegYards)
                {
                    Vector2 dirIn = inVec / lenIn;
                    Vector2 dirOut = outVec / lenOut;
                    float theta = MathF.Acos(Math.Clamp(Vector2.Dot(dirIn, dirOut), -1f, 1f));
                    if (theta >= MinShapeTurnRadians)
                    {
                        stats.CornersConsidered++;
                        int count = ShapeCorner(map, mesh, prevPrev, hasPrevPrev, prev, points[i - 1], v, next, in s, theta, lenIn, lenOut, dirIn, dirOut, ref stats, buffer, out bool rounded);
                        if (count > 0 && AllFinite(buffer[..count]))
                        {
                            if (output is null)
                            {
                                output = new List<Vector3>(n + MaxCornerPoints);
                                for (int j = 0; j < i; j++)
                                    output.Add(points[j]);
                            }

                            for (int j = 0; j < count; j++)
                                output.Add(buffer[j]);
                            prevPrev = count >= 2 ? buffer[count - 2] : prev;
                            prev = buffer[count - 1];
                            hasPrevPrev = true;
                            stats.CornersShaped++;
                            if (rounded)
                                stats.CornersRounded++;
                            shapedHere = true;
                        }
                    }
                }
            }

            if (!shapedHere)
            {
                output?.Add(v);
                prevPrev = prev;
                prev = v;
                hasPrevPrev = true;
            }
        }

        if (output is null)
            return points;

        for (; i < n; i++)
            output.Add(points[i]);
        return output;
    }

    /// <summary>
    /// Shape one corner <paramref name="v"/>: the points that replace it in <paramref name="result"/> (their count), or 0 to keep it. The
    /// plan (see the type summary): probe the inside and the outside of the turn on the mesh, choose how far to push the corner outward
    /// (clearance, centring), never into less than <see cref="EdgeMarginYards"/> of room, validate the two new legs, then round the turn.
    /// </summary>
    private static int ShapeCorner(
        Map map,
        IMeshPointQuery mesh,
        Vector3 prevPrev,
        bool hasPrevPrev,
        Vector3 prev,
        Vector3 meshPrev,
        Vector3 v,
        Vector3 next,
        in Settings s,
        float theta,
        float lenIn,
        float lenOut,
        Vector2 dirIn,
        Vector2 dirOut,
        ref ShapeStats stats,
        Span<Vector3> result,
        out bool rounded)
    {
        rounded = false;
        int stopsBefore = stats.BudgetStops;
        Vector2 diff = dirOut - dirIn;
        float diffLength = diff.Length();
        if (!(diffLength > 1e-3f))
            return 0;

        // The inside of the turn is where the obstacle is (a boundary vertex of the mesh); the corner moves away from it.
        Vector2 inward = diff / diffLength;
        Vector2 outward = -inward;

        float clearance = s.ClearanceYards;
        float lead = MathF.Min(s.TurnLeadYards, 0.4f * MathF.Min(lenIn, lenOut));
        bool rounding = s.TurnLeadYards > 0f && theta > MaxHeadingStepRadians && lead >= MinLeadYards;
        bool bound = s.TurnLeadYards > 0f; // the heading bound (guarantee G) is kept only where rounding is on
        bool autoRounded = false; // a gentle corner rounded because its push would widen the turn past the bound

        // A fillet with tangent length L across a turn of theta has its apex L * tan(theta / 4) inside the corner, and its chords dip a
        // further sagitta toward it (ArcReach). Moving the corner outward widens the turn, so the planned excursion carries a margin and
        // the arc keeps its whole lead instead of being shrunk to nothing (TryBuildArc shrinks it when the margin is not enough).
        float excursion = rounding ? lead * ArcReach(theta) * ArcWidenMargin : 0f;

        uint mapId = map.MapId;
        if (!Probe(mesh, mapId, v, inward, clearance + excursion + 0.5f, ref stats, out bool inwardHit, out float distIn))
            return KeepForBudget(ref stats, stopsBefore);

        float outLength = Math.Clamp((2f * MathF.Max(s.CentringMaxYards, clearance + excursion)) + 0.5f, 1f, 12f);
        if (!Probe(mesh, mapId, v, outward, outLength, ref stats, out bool outwardHit, out float distOut))
            return KeepForBudget(ref stats, stopsBefore);

        if (outwardHit && distOut < MinPushYards)
        {
            // The corner is exactly on the boundary on this side too: look from a little inside the mesh.
            Vector3 shifted = new(v.X + (outward.X * 0.1f), v.Y + (outward.Y * 0.1f), v.Z);
            if (Probe(mesh, mapId, shifted, outward, outLength, ref stats, out bool retryHit, out float retryDist))
            {
                outwardHit = retryHit;
                distOut = 0.1f + retryDist;
            }
        }

        float room = distOut - EdgeMarginYards;
        float push = ComputePush(inwardHit, distIn, outwardHit, distOut, clearance, excursion, rounding ? lead : 0f, theta, s.CentringMaxYards, out float needed);
        if (push >= MinPushYards && push > room && rounding)
        {
            rounding = false;
            excursion = 0f;
            push = ComputePush(inwardHit, distIn, outwardHit, distOut, clearance, excursion, 0f, theta, s.CentringMaxYards, out needed);
        }

        // Auto-round (heading bound): a corner not sharper than the bound whose push would widen the turn past it is rounded instead.
        if (bound && !rounding && theta <= MaxHeadingStepRadians && lead >= MinLeadYards && push >= MinPushYards && push <= room)
        {
            float pushedTurn = PushedTurn(prev, v, next, outward, push);
            if (pushedTurn > MaxHeadingStepRadians)
            {
                rounding = true;
                autoRounded = true;
                excursion = lead * ArcReach(pushedTurn) * ArcWidenMargin;
                push = ComputePush(inwardHit, distIn, outwardHit, distOut, clearance, excursion, lead, theta, s.CentringMaxYards, out needed);
                if (push > room)
                {
                    rounding = false;
                    autoRounded = false;
                    excursion = 0f;
                    push = ComputePush(inwardHit, distIn, outwardHit, distOut, clearance, 0f, 0f, theta, s.CentringMaxYards, out needed);
                }
            }
        }

        // Pushing the corner outward widens the turn (on short legs by a lot: 0.9 rad can become 1.6), which brings the arc's tangent points
        // nearer the obstacle. The arc is therefore planned for the turn at the moved corner: a few passes of the push against its own result.
        if (rounding && push >= MinPushYards && push <= room)
        {
            for (int pass = 0; pass < ReplanPasses; pass++)
            {
                Vector2 moved = Flat(v) + (outward * push);
                Vector2 moveIn = moved - Flat(prev), moveOut = Flat(next) - moved;
                float movedIn = moveIn.Length(), movedOut = moveOut.Length();
                if (!(movedIn > 1e-3f) || !(movedOut > 1e-3f))
                    break;

                float movedTheta = MathF.Acos(Math.Clamp(Vector2.Dot(moveIn / movedIn, moveOut / movedOut), -1f, 1f));
                float movedLead = MathF.Min(s.TurnLeadYards, 0.4f * MathF.Min(movedIn, movedOut));
                if (!(movedTheta > MaxHeadingStepRadians) || movedLead < MinLeadYards)
                {
                    if (autoRounded)
                    {
                        // The moved corner needs no arc after all (or has no lead for one): back to a plain push.
                        rounding = false;
                        autoRounded = false;
                        excursion = 0f;
                        push = ComputePush(inwardHit, distIn, outwardHit, distOut, clearance, 0f, 0f, theta, s.CentringMaxYards, out needed);
                    }

                    break;
                }

                float movedExcursion = movedLead * ArcReach(movedTheta) * ArcReplanMargin;
                float replanned = ComputePush(inwardHit, distIn, outwardHit, distOut, clearance, movedExcursion, movedLead, movedTheta, s.CentringMaxYards, out float replannedNeeded);
                if (replanned > room)
                {
                    rounding = false;
                    autoRounded = false;
                    excursion = 0f;
                    push = ComputePush(inwardHit, distIn, outwardHit, distOut, clearance, 0f, 0f, theta, s.CentringMaxYards, out needed);
                    break;
                }

                bool settled = MathF.Abs(replanned - push) < 0.02f;
                push = replanned;
                needed = replannedNeeded;
                excursion = movedExcursion;
                lead = movedLead;
                if (settled)
                    break;
            }
        }

        if (push >= MinPushYards && push > room)
        {
            // The doorway or bridge case: never squeeze.
            stats.Fallbacks++;
            stats.FallbackRoom++;
            return 0;
        }

        if (push < MinPushYards)
        {
            push = 0f;
            if (!rounding)
                return 0;
        }

        OriginalLeg legIn = default, legOut = default; // the mesh legs beside the corner, measured at most once each if a relative check needs them
        int offset = 0; // 1 when a split point precedes the corner's own points
        Vector2 obstacle = new(v.X + (inward.X * distIn), v.Y + (inward.Y * distIn));
        if (bound && push >= MinPushYards)
        {
            // The push turns the leg from the previous point; keep the turn at that point (and at this corner) within the heading bound.
            if (hasPrevPrev)
            {
                float pMax = MaxPushForNeighbour(prevPrev, prev, v, dirIn, outward, theta);
                if (push > pMax)
                {
                    bool split = false;
                    if (rounding && lenIn - MinLegYards >= DReq(push, theta)
                        && PlanArcTangent(Flat(prev), Flat(v) + (outward * push), Flat(next), lead, distIn, push, clearance, inwardHit, obstacle, out _) >= MinLeadYards
                        && !(PlanArcTangent(Flat(prev), Flat(v) + (outward * pMax), Flat(next), lead, distIn, pMax, clearance, inwardHit, obstacle, out _) >= MinLeadYards))
                    {
                        // The cap would kill the planned arc: a point one yard along the incoming leg takes the turn instead.
                        Vector3 rawSplit = new(prev.X + (dirIn.X * MinLegYards), prev.Y + (dirIn.Y * MinLegYards), prev.Z);
                        if (TryGround(map, prev, rawSplit, ref stats, out Vector3 splitPoint)
                            && SegmentAcceptable(map, prev, splitPoint, meshPrev, v, ref legIn, ref stats))
                        {
                            result[0] = splitPoint;
                            offset = 1;
                            prev = splitPoint;
                            lenIn -= MinLegYards;
                            split = true;
                        }
                    }

                    if (!split)
                    {
                        push = MathF.Min(push, pMax);
                        stats.NeighbourCaps++;
                    }
                }
            }

            if (!rounding && theta <= MaxHeadingStepRadians && push >= MinPushYards)
            {
                float capped = MaxPushForSelf(prev, v, next, outward, push);
                if (capped < push)
                {
                    push = capped;
                    stats.SelfCaps++;
                }
            }

            if (push < MinPushYards)
            {
                push = 0f;
                if (!rounding)
                {
                    stats.Fallbacks++;
                    stats.FallbackHeading++;
                    return 0;
                }
            }
        }

        // The push ladder: the room is there, but the moved corner or a leg to it may not be walkable (a leg clipping the mesh near the
        // previous point, a ground height that does not match). Smaller pushes are tried in turn, down to half the clearance.
        Span<float> rungs = stackalloc float[MaxPushRungs];
        int rungCount = BuildPushLadder(push, needed, clearance, rungs);
        PlaceResult placeResult = PlaceResult.Segment;
        Vector3 placed = v;
        bool accepted = false;
        for (int rung = 0; rung < rungCount; rung++)
        {
            if (rung > 0 && stats.Queries >= MaxQueriesPerPath)
                break;

            placeResult = TryPlace(map, prev, meshPrev, v, next, outward, rungs[rung], ref legIn, ref legOut, ref stats, out placed);
            if (placeResult == PlaceResult.Placed)
            {
                push = rungs[rung];
                accepted = true;
                break;
            }
        }

        if (!accepted)
        {
            stats.Fallbacks++;
            if (stats.BudgetStops != stopsBefore)
                stats.FallbackBudget++;
            else if (placeResult == PlaceResult.Ground)
                stats.FallbackGround++;
            else
                stats.FallbackSegment++;
            return 0;
        }

        if (push > 0f && distIn + push < clearance - 0.01f)
            stats.PartialPushes++;

        if (rounding)
        {
            int arc = TryBuildArc(map, prev, meshPrev, placed, next, lead, distIn, push, clearance, inwardHit,
                obstacle, v, ref legIn, ref legOut, ref stats, result[offset..]);
            if (arc > 0)
            {
                rounded = true;
                if (offset > 0)
                    stats.NeighbourSplits++;
                if (autoRounded)
                    stats.AutoRounded++;
                return arc + offset;
            }

            stats.ArcRejected++;
            if (theta <= MaxHeadingStepRadians && PushedTurn(prev, v, next, outward, push) > MaxHeadingStepRadians)
            {
                // The gentle corner has no arc and its plain push would widen the turn past the bound: push less, or keep it.
                float capped = MaxPushForSelf(prev, v, next, outward, push);
                if (capped >= MinPushYards
                    && TryPlace(map, prev, meshPrev, v, next, outward, capped, ref legIn, ref legOut, ref stats, out placed) == PlaceResult.Placed)
                {
                    stats.SelfCaps++;
                    if (offset > 0)
                        stats.NeighbourSplits++;
                    result[offset] = placed;
                    return offset + 1;
                }

                stats.Fallbacks++;
                if (stats.BudgetStops != stopsBefore)
                    stats.FallbackBudget++;
                else
                    stats.FallbackHeading++;
                return 0;
            }
        }

        if (push == 0f)
        {
            // Nothing was moved and the arc this corner was planned to get is not possible: it keeps its mesh position, which is a fallback.
            stats.Fallbacks++;
            if (stats.BudgetStops != stopsBefore)
                stats.FallbackBudget++;
            else
                stats.FallbackArc++;
            return 0;
        }

        if (offset > 0)
            stats.NeighbourSplits++;
        result[offset] = placed;
        return offset + 1;
    }

    /// <summary>A corner left alone after the query budget refused a probe: counted as a budget fallback when the budget was the reason.</summary>
    private static int KeepForBudget(ref ShapeStats stats, int stopsBefore)
    {
        if (stats.BudgetStops != stopsBefore)
        {
            stats.Fallbacks++;
            stats.FallbackBudget++;
        }

        return 0;
    }

    /// <summary>The most rungs of the push ladder.</summary>
    private const int MaxPushRungs = 5;

    /// <summary>
    /// The pushes to try, largest first: the planned push, the push the clearance needs, the clearance itself and 0.75 and 0.5 of it, each
    /// at most the planned push and at least half the clearance (and <see cref="MinPushYards"/>), without near duplicates. A planned push
    /// of 0 (a corner that only rounds) is the single rung 0.
    /// </summary>
    private static int BuildPushLadder(float push, float needed, float clearance, Span<float> rungs)
    {
        if (push < MinPushYards)
        {
            rungs[0] = 0f;
            return 1;
        }

        float floor = MathF.Max(MinPushYards, 0.5f * clearance);
        ReadOnlySpan<float> candidates = [push, needed, clearance, 0.75f * clearance, 0.5f * clearance];
        int count = 0;
        foreach (float candidate in candidates)
        {
            if (!(candidate <= push + 1e-4f) || !(candidate >= floor - 1e-4f))
                continue;

            bool duplicate = false;
            for (int i = 0; i < count; i++)
            {
                if (MathF.Abs(rungs[i] - candidate) < 0.05f)
                    duplicate = true;
            }

            if (!duplicate)
                rungs[count++] = MathF.Min(candidate, push);
        }

        if (count == 0)
            rungs[count++] = push;

        // Largest first, whatever order the candidates came in.
        for (int i = 1; i < count; i++)
        {
            float value = rungs[i];
            int j = i - 1;
            while (j >= 0 && rungs[j] < value)
            {
                rungs[j + 1] = rungs[j];
                j--;
            }

            rungs[j + 1] = value;
        }

        return count;
    }

    /// <summary>How far the corner moves outward: the clearance still missing, or the way to the middle of a bounded corridor, whichever is more.</summary>
    private static float ComputePush(bool inwardHit, float distIn, bool outwardHit, float distOut, float clearance, float excursion, float tangent, float theta, float centringMax, out float needed)
    {
        // A rounding corner also needs its arc's tangent points clear of the obstacle: with D the distance from the obstacle to the
        // moved corner along the bisector, a tangent point at tangent length L is sqrt(L^2 + D^2 - 2 L D sin(theta / 2)) from it.
        float required = clearance + excursion;
        if (tangent > 0f)
        {
            // The turn at the moved corner is wider than at the mesh corner, which brings the tangent points nearer the obstacle: plan for a wider one.
            float half = MathF.Min(theta + ArcPlanningAngleMarginRadians, 3.1f) * 0.5f;
            float cosL = tangent * MathF.Cos(half);
            required = MathF.Max(required, (tangent * MathF.Sin(half)) + MathF.Sqrt(MathF.Max(0f, (clearance * clearance) - (cosL * cosL))));
        }

        needed = required - distIn;
        float centre = inwardHit && outwardHit && centringMax > 0f
            ? MathF.Min(((distIn + distOut) * 0.5f) - distIn, centringMax)
            : 0f;
        return MathF.Max(MathF.Max(needed, centre), 0f);
    }

    /// <summary>One mesh raycast from <paramref name="from"/> along <paramref name="direction"/> for <paramref name="length"/> yards; false when the mesh cannot answer.</summary>
    private static bool Probe(IMeshPointQuery mesh, uint mapId, Vector3 from, Vector2 direction, float length, ref ShapeStats stats, out bool hit, out float distance)
    {
        hit = false;
        distance = 0f;
        if (!TryCharge(ref stats, 1))
            return false;

        stats.Probes++;
        Vector3 to = new(from.X + (direction.X * length), from.Y + (direction.Y * length), from.Z);
        MeshRaycastResult ray = mesh.Raycast(mapId, from, to, PlayerbotMoveCheck.MeshOptions);
        hit = ray.Available && ray.Hit;
        distance = hit ? ray.Fraction * length : length;
        return ray.Available && float.IsFinite(distance);
    }

    /// <summary>The corner pushed <paramref name="push"/> yards along <paramref name="outward"/>, on the mesh surface and the floor, with both legs around it safe.</summary>
    private static PlaceResult TryPlace(Map map, Vector3 prev, Vector3 meshPrev, Vector3 v, Vector3 next, Vector2 outward, float push, ref OriginalLeg legIn, ref OriginalLeg legOut, ref ShapeStats stats, out Vector3 placed)
    {
        placed = v;
        if (push > 0f)
        {
            Vector3 raw = new(v.X + (outward.X * push), v.Y + (outward.Y * push), v.Z);
            if (!raw.IsFinite() || !TryGround(map, v, raw, ref stats, out placed))
                return PlaceResult.Ground;
        }

        return SegmentAcceptable(map, prev, placed, meshPrev, v, ref legIn, ref stats) && SegmentAcceptable(map, placed, next, v, next, ref legOut, ref stats)
            ? PlaceResult.Placed
            : PlaceResult.Segment;
    }

    private enum PlaceResult
    {
        Placed,
        Ground,
        Segment,
    }

    /// <summary>The point under <paramref name="raw"/>: the mesh surface walked from <paramref name="anchor"/> (kept at its own height when the walk fails), then the floor within the route smoothing tolerance.</summary>
    private static bool TryGround(Map map, Vector3 anchor, Vector3 raw, ref ShapeStats stats, out Vector3 grounded)
    {
        grounded = raw;
        if (!TryCharge(ref stats, 3)) // the mesh walk and the two height lookups of the snap
            return false;

        stats.Probes++;
        Vector3 candidate = map.Collision.TryWalkMeshSurface(anchor, raw, out float surface, PlayerbotMoveCheck.MeshOptions)
            ? raw with { Z = surface }
            : raw;
        return PlayerbotRouteSmoothing.TrySnapHeight(map, candidate, out grounded);
    }

    /// <summary>
    /// The fillet across the corner <paramref name="v"/> (already placed): the tangent points and arc points in <paramref name="result"/>
    /// (their count), or 0 when the arc is not usable (too short, too close to the obstacle, a chord that is not safe). When the inward
    /// probe found an obstacle (<paramref name="hasObstacle"/>, at <paramref name="obstacle"/>) every arc point keeps at least the clearance
    /// from it (or, for a partial push, as much as the moved corner itself has); the tangent length shrinks until it does.
    /// </summary>
    private static int TryBuildArc(
        Map map,
        Vector3 prev,
        Vector3 meshPrev,
        Vector3 v,
        Vector3 next,
        float lead,
        float distIn,
        float push,
        float clearance,
        bool hasObstacle,
        Vector2 obstacle,
        Vector3 corner,
        ref OriginalLeg legIn,
        ref OriginalLeg legOut,
        ref ShapeStats stats,
        Span<Vector3> result)
    {
        float tangent = PlanArcTangent(Flat(prev), Flat(v), Flat(next), lead, distIn, push, clearance, hasObstacle, obstacle, out ArcPlan plan);
        if (!(tangent >= MinLeadYards))
            return 0;

        Vector2 vf = Flat(v);
        int interior = plan.Interior;
        int count = interior + 2;
        Vector2 start = vf - (plan.DirIn * tangent);
        Vector2 end = vf + (plan.DirOut * tangent);
        Vector2 centre = start + (plan.NormalIn * (tangent / MathF.Tan(plan.Theta * 0.5f)));
        float turn = plan.Turn;
        float theta = plan.Theta;

        Vector3 last = default;
        for (int k = 0; k < count; k++)
        {
            Vector2 flat = ArcFlatPoint(k, count, interior, start, end, centre, turn, theta);
            if (!TryGround(map, v, new Vector3(flat.X, flat.Y, v.Z), ref stats, out Vector3 point))
                return 0;
            // The sub-leg from the previous point to the first tangent point is part of the mesh leg's replacement as well; its end has a snapped height.
            if (k == 0 && !SegmentAcceptable(map, prev, point, meshPrev, corner, ref legIn, ref stats))
                return 0;
            if (k > 0 && !(k <= count / 2
                    ? SegmentAcceptable(map, last, point, meshPrev, corner, ref legIn, ref stats)
                    : SegmentAcceptable(map, last, point, corner, next, ref legOut, ref stats)))
                return 0;

            result[k] = point;
            last = point;
        }

        // ...and so is the sub-leg from the last tangent point to the next point.
        if (!SegmentAcceptable(map, last, next, corner, next, ref legOut, ref stats))
            return 0;

        return count;
    }

    /// <summary>The geometry of a planned fillet (see <see cref="PlanArcTangent"/>).</summary>
    private readonly record struct ArcPlan(int Interior, float Turn, float Theta, Vector2 NormalIn, Vector2 DirIn, Vector2 DirOut);

    /// <summary>
    /// The longest tangent (yards) the fillet across the placed corner <paramref name="vf"/> can have, or 0 when there is no usable arc:
    /// the lead capped by the legs, shrunk so that the arc's apex and chords keep the clearance (or, for a partial push, what the placed
    /// corner itself has) from the obstacle, then bisected until every arc point does. Pure geometry: no mesh query is made.
    /// </summary>
    private static float PlanArcTangent(
        Vector2 pf,
        Vector2 vf,
        Vector2 nf,
        float lead,
        float distIn,
        float push,
        float clearance,
        bool hasObstacle,
        Vector2 obstacle,
        out ArcPlan plan)
    {
        plan = default;
        Vector2 inVec = vf - pf;
        Vector2 outVec = nf - vf;
        float lenIn = inVec.Length();
        float lenOut = outVec.Length();
        if (!(lenIn > 1e-3f) || !(lenOut > 1e-3f))
            return 0f;

        Vector2 dirIn = inVec / lenIn;
        Vector2 dirOut = outVec / lenOut;
        float theta = MathF.Acos(Math.Clamp(Vector2.Dot(dirIn, dirOut), -1f, 1f));
        if (!(theta > MinShapeTurnRadians))
            return 0f;

        float cross = (dirIn.X * dirOut.Y) - (dirIn.Y * dirOut.X);
        float turn = cross >= 0f ? 1f : -1f; // +1: a left (counter-clockwise) turn
        Vector2 normalIn = turn > 0f ? new Vector2(-dirIn.Y, dirIn.X) : new Vector2(dirIn.Y, -dirIn.X); // toward the inside of the turn
        int interior = Math.Clamp((int)MathF.Ceiling(theta / MaxHeadingStepRadians) - 1, 1, MaxArcInteriorPoints);

        float tangent = MathF.Min(lead, 0.4f * MathF.Min(lenIn, lenOut));

        // The apex of the arc (tangent * tan(theta / 4) inside the corner) plus the sagitta of the chord that replaces the arc at its
        // deepest point must still keep the clearance from the obstacle behind the corner. The turn at the placed corner is wider than
        // the one the lead was planned for, so the tangent shrinks to fit (both terms are proportional to it).
        float reach = ArcReach(theta);
        float placedDistance = distIn + push;
        float target = MathF.Min(clearance, placedDistance);
        if (reach > 1e-4f)
            tangent = MathF.Min(tangent, (placedDistance - target) / reach);

        // The tangent points lie on the legs beside the corner and are nearer the obstacle than the corner is, so the arc points are
        // measured too; pure geometry, so the tangent is bisected without any query.
        if (hasObstacle && tangent >= MinLeadYards && !ArcClears(tangent, vf, dirIn, dirOut, theta, turn, normalIn, interior, obstacle, target - 0.05f))
        {
            float low = 0f, high = tangent;
            for (int iteration = 0; iteration < ArcBisections; iteration++)
            {
                float middle = 0.5f * (low + high);
                if (ArcClears(middle, vf, dirIn, dirOut, theta, turn, normalIn, interior, obstacle, target - 0.05f))
                    low = middle;
                else
                    high = middle;
            }

            tangent = low;
        }

        plan = new ArcPlan(interior, turn, theta, normalIn, dirIn, dirOut);
        return tangent >= MinLeadYards ? tangent : 0f;
    }

    /// <summary>The flat turn angle (radians, 0 to pi) at <paramref name="b"/> on the way from <paramref name="a"/> to <paramref name="c"/>; 0 when a leg has no length.</summary>
    private static float Turn(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 u = b - a, w = c - b;
        float lu = u.Length(), lw = w.Length();
        if (!(lu > 1e-6f) || !(lw > 1e-6f))
            return 0f;

        return MathF.Acos(Math.Clamp(Vector2.Dot(u / lu, w / lw), -1f, 1f));
    }

    /// <summary>The signed flat turn at <paramref name="b"/> (positive: counter-clockwise); 0 when a leg has no length.</summary>
    private static float SignedTurn(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 u = b - a, w = c - b;
        float lu = u.Length(), lw = w.Length();
        if (!(lu > 1e-6f) || !(lw > 1e-6f))
            return 0f;

        u /= lu;
        w /= lw;
        return MathF.Atan2((u.X * w.Y) - (u.Y * w.X), Vector2.Dot(u, w));
    }

    /// <summary>The turn at <paramref name="v"/> after it is pushed <paramref name="p"/> yards along <paramref name="outward"/>.</summary>
    internal static float PushedTurn(Vector3 prev, Vector3 v, Vector3 next, Vector2 outward, float p)
        => Turn(Flat(prev), Flat(v) + (outward * p), Flat(next));

    /// <summary>
    /// The largest push of <paramref name="v"/> along <paramref name="outward"/> that keeps the turn at the already emitted
    /// <paramref name="prev"/> within the heading bound (a turn that was already above it may grow by at most <see cref="SharpNeighbourSlackRadians"/>);
    /// <see cref="float.PositiveInfinity"/> when no push can break it, 0 when none is allowed. Pushing rotates the leg from <paramref name="prev"/>
    /// by atan2(p cos(theta / 2), d + p sin(theta / 2)) toward <paramref name="outward"/>; this solves that for the allowed rotation.
    /// </summary>
    internal static float MaxPushForNeighbour(Vector3 prevPrev, Vector3 prev, Vector3 v, Vector2 dirIn, Vector2 outward, float theta)
    {
        float tauOld = SignedTurn(Flat(prevPrev), Flat(prev), Flat(v));
        float sigma = ((dirIn.X * outward.Y) - (dirIn.Y * outward.X)) >= 0f ? 1f : -1f;
        float absOld = MathF.Abs(tauOld);
        float allow = absOld <= MaxHeadingStepRadians ? MaxHeadingStepRadians - HeadingEpsilonRadians : absOld + SharpNeighbourSlackRadians - HeadingEpsilonRadians;
        float deltaMax = (sigma * tauOld) >= 0f ? allow - absOld : allow + absOld;
        if (deltaMax <= 0f)
            return 0f;
        if (deltaMax >= 1.5f)
            return float.PositiveInfinity;

        float t = MathF.Tan(deltaMax);
        float den = MathF.Cos(theta * 0.5f) - (t * MathF.Sin(theta * 0.5f));
        return den <= 0f ? float.PositiveInfinity : t * Vector2.Distance(Flat(v), Flat(prev)) / den;
    }

    /// <summary>The shortest incoming leg (yards) from which a push of <paramref name="push"/> turns that leg by no more than the heading bound.</summary>
    private static float DReq(float push, float theta)
    {
        float t = MathF.Tan(MaxHeadingStepRadians - HeadingEpsilonRadians);
        float den = MathF.Cos(theta * 0.5f) - (t * MathF.Sin(theta * 0.5f));
        return den <= 0f ? 0f : push * den / t;
    }

    /// <summary>The largest push up to <paramref name="push"/> that leaves the turn at <paramref name="v"/> within the heading bound (0 when none does).</summary>
    private static float MaxPushForSelf(Vector3 prev, Vector3 v, Vector3 next, Vector2 outward, float push)
    {
        float limit = MaxHeadingStepRadians - HeadingEpsilonRadians;
        if (PushedTurn(prev, v, next, outward, push) <= limit)
            return push;

        float low = 0f, high = push;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            float middle = 0.5f * (low + high);
            if (PushedTurn(prev, v, next, outward, middle) <= limit)
                low = middle;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>Bisection steps that find the longest arc tangent that keeps its clearance (1/256 of the lead).</summary>
    private const int ArcBisections = 8;

    /// <summary>Point <paramref name="k"/> of <paramref name="count"/> of the fillet: the tangent point on the incoming leg, the arc points, the tangent point on the outgoing leg.</summary>
    private static Vector2 ArcFlatPoint(int k, int count, int interior, Vector2 start, Vector2 end, Vector2 centre, float turn, float theta)
    {
        if (k == 0)
            return start;
        if (k == count - 1)
            return end;

        float angle = turn * theta * k / (interior + 1);
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        Vector2 radial = start - centre;
        return centre + new Vector2((radial.X * cos) - (radial.Y * sin), (radial.X * sin) + (radial.Y * cos));
    }

    /// <summary>Whether every point of the fillet with tangent length <paramref name="tangent"/> is at least <paramref name="minDistance"/> (flat) from <paramref name="obstacle"/>.</summary>
    private static bool ArcClears(float tangent, Vector2 corner, Vector2 dirIn, Vector2 dirOut, float theta, float turn, Vector2 normalIn, int interior, Vector2 obstacle, float minDistance)
    {
        Vector2 start = corner - (dirIn * tangent);
        Vector2 end = corner + (dirOut * tangent);
        Vector2 centre = start + (normalIn * (tangent / MathF.Tan(theta * 0.5f)));
        int count = interior + 2;
        for (int k = 0; k < count; k++)
        {
            if (Vector2.Distance(ArcFlatPoint(k, count, interior, start, end, centre, turn, theta), obstacle) < minDistance)
                return false;
        }

        return true;
    }

    private static bool AllFinite(ReadOnlySpan<Vector3> points)
    {
        foreach (Vector3 point in points)
        {
            if (!point.IsFinite())
                return false;
        }

        return true;
    }
}
