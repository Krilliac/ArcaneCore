using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The outcome checks of <see cref="PlayerbotMotion"/> (area <c>motion</c>, subsystem <c>bot-motion</c>): did the walk the bot was
/// given put it where a walking bot may stand, or did it end somewhere the code never meant it to be? Each check reports through
/// <see cref="PlayerbotAnomalies"/> once per bot and code per <see cref="EpisodeQuietMs"/> of world time (one episode, not one
/// report per heartbeat).
/// <list type="table">
/// <listheader><term>code</term><description>trigger, severity, cost</description></listheader>
/// <item><term>airwalk</term><description>a sampled heartbeat position more than <see cref="AirwalkYards"/> above the floor under it
/// (not flying, swimming, levitating, on a transport, falling or on a spline). Warning. One height query per bot per
/// <see cref="SampleIntervalMs"/>.</description></item>
/// <item><term>under-map</term><description>the terrain surface lies more than <see cref="UnderMapYards"/> above the bot and no
/// model explains it (inside a WMO, a model floor under it or a model surface just above it: caves, cellars and cities; unloaded model tiles never convict); Error beyond <see cref="FarUnderMapYards"/>. Same sample.</description></item>
/// <item><term>wall-crossed</term><description>a leg of a navigation-mesh route the bot walked since the last heartbeat is blocked by a
/// static model (line of sight at chest height) and leaves the walkable mesh (a leg that stays on it is not convicted by its line).
/// Warning. One to <see cref="MaxCrossingSegments"/> segment queries per sample (and a mesh walk per blocked one), only for mesh legs
/// (a stepped leg is refused by the motion itself).</description></item>
/// <item><term>door-crossed</term><description>the same leg is clear of walls but blocked by an M2 doodad (a door, a gate, a fence).
/// Info.</description></item>
/// <item><term>position-jump</term><description>the bot's stored position lies more than <see cref="JumpYards"/> from the last
/// position the motion reported while the route is still being walked: something else moved it, and the next heartbeat pulls it
/// back. Warning. Two float compares per tick.</description></item>
/// <item><term>non-finite-position</term><description>the bot's position, or the position the route walk computed, is NaN or infinite.
/// Error. Compares only.</description></item>
/// <item><term>lease-expired-midroute</term><description>the route's lease ran out with more than <see cref="MidRouteYards"/> left: no
/// think renewed it. Info. Failure path only.</description></item>
/// <item><term>stuck</term><description>heartbeats went on for <see cref="StuckWindowMs"/> and the bot ended less than
/// <see cref="StuckYards"/> from where the window began. Warning. A compare per heartbeat.</description></item>
/// <item><term>move-rejected</term><description>the server did not take a movement packet the bot sent (or put the bot elsewhere than
/// the packet said). Warning. Failure path only.</description></item>
/// <item><term>start-blocked</term><description>a route could not be started: its first stretch is not walkable. Warning.</description></item>
/// <item><term>route-blocked</term><description>a route being walked is no longer walkable ahead; the bot stopped. Warning.</description></item>
/// </list>
/// Nothing is checked while <see cref="WorldSession.IsInChaosProbe"/>. Thread affinity: world thread; a <see cref="MotionWatch"/> belongs to one bot.
/// </summary>
internal static class PlayerbotMotionAnomalies
{
    /// <summary>The area of every key: <c>bot-motion/&lt;code&gt;</c>.</summary>
    internal const string Area = "motion";

    /// <summary>The ground and crossing checks run at most this often per bot (world ms).</summary>
    internal const uint SampleIntervalMs = 1000;

    /// <summary>The same code is not reported again for the same bot sooner than this (world ms).</summary>
    internal const uint EpisodeQuietMs = 30_000;

    /// <summary>Height above the floor under a walking bot that is "no ground": mesh legs may float up to 4 yd (<see cref="PlayerbotMotion.MeshFloorToleranceYards"/>).</summary>
    internal const float AirwalkYards = 6f;

    /// <summary>
    /// Terrain surface above a bot that no model explains (<see cref="MapCollision.ClassifyUnderMap"/>) by more than this: under the map.
    /// A stateless margin, not "two samples in a row": the navmesh-versus-terrain drift that made 3 yd fire is a property of the spot, so a
    /// bot standing there would repeat it on every sample. 8 yd is a judgement call from the reported 3-10 yd drift range, not a value measured against a drift survey.
    /// </summary>
    internal const float UnderMapYards = 8f;

    /// <summary>Under the map by this much is an Error: the unit is far below the world.</summary>
    internal const float FarUnderMapYards = 40f;

    /// <summary>Flat yards the stored position may differ from the last reported motion position.</summary>
    internal const float JumpYards = 3f;

    /// <summary>A route left unrenewed with more than this many flat yards to go is cut short.</summary>
    internal const float MidRouteYards = 6f;

    /// <summary>The window in which heartbeats must get the bot somewhere.</summary>
    internal const uint StuckWindowMs = 8000;

    /// <summary>Walking that ends this near where the window began is not progress.</summary>
    internal const float StuckYards = 1f;

    /// <summary>Segments shorter than this are not tested for a crossing.</summary>
    internal const float MinSegmentYards = 0.5f;

    /// <summary>The most segments one sample tests.</summary>
    internal const int MaxCrossingSegments = 6;

    internal enum Code
    {
        Airwalk,
        UnderMap,
        WallCrossed,
        DoorCrossed,
        PositionJump,
        NonFinitePosition,
        LeaseExpired,
        Stuck,
        MoveRejected,
        StartBlocked,
        RouteBlocked,
    }

    private static readonly string[] Names =
    [
        "airwalk", "under-map", "wall-crossed", "door-crossed", "position-jump", "non-finite-position", "lease-expired-midroute",
        "stuck", "move-rejected", "start-blocked", "route-blocked",
    ];

    /// <summary>The normalised ledger key of <paramref name="code"/> (tests).</summary>
    internal static string Key(Code code) => PlayerbotAnomalies.Key(Area, Names[(int)code]);

    internal enum GroundVerdict { Ok, Airwalk, UnderMap, FarUnderMap }

    /// <summary>One bot's rate-limit and sampling state (inside <see cref="PlayerbotMotion.MotionState"/>).</summary>
    internal sealed class MotionWatch
    {
        private readonly uint[] _last = new uint[Names.Length];
        private int _seen;
        private bool _sampled;
        private uint _nextSampleMs;

        internal PlayerbotRoute? StuckRoute;
        internal Vector3 StuckAt;
        internal uint StuckMs;

        /// <summary>Whether <paramref name="code"/> may be reported now; true marks it reported (the next one waits <see cref="EpisodeQuietMs"/>).</summary>
        internal bool Due(Code code, uint now)
        {
            int bit = 1 << (int)code;
            if ((_seen & bit) != 0 && AgeOf(now, _last[(int)code]) < EpisodeQuietMs) return false;
            _seen |= bit;
            _last[(int)code] = now;
            return true;
        }

        /// <summary>Whether the sampled checks run now; true schedules the next sample.</summary>
        internal bool SampleDue(uint now)
        {
            if (_sampled && unchecked((int)(now - _nextSampleMs)) < 0) return false;
            _sampled = true;
            _nextSampleMs = unchecked(now + SampleIntervalMs);
            return true;
        }
    }

    private static uint AgeOf(uint now, uint then)
    {
        uint age = unchecked(now - then);
        return age <= int.MaxValue ? age : 0;
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static string Describe(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"{value.X:F1},{value.Y:F1},{value.Z:F1}");

    private static void Emit(Player player, Code code, string detail, AnomalySeverity severity)
        => PlayerbotAnomalies.Report(player, Area, Names[(int)code], detail, severity);

    /// <summary>The bot's stored position is NaN or infinite (compares only on success).</summary>
    internal static void CheckStoredPosition(WorldSession session, Player player, uint now)
    {
        if (float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z)) return;
        ReportNonFinite(session, player, "the bot's stored position", now);
    }

    /// <summary>A position the route walk computed (<paramref name="what"/>) is NaN or infinite.</summary>
    internal static void ReportNonFinite(WorldSession session, Player player, string what, uint now)
    {
        if (session.IsInChaosProbe || !PlayerbotMotion.Of(player).Watch.Due(Code.NonFinitePosition, now)) return;
        Emit(player, Code.NonFinitePosition, what + " is not a finite coordinate", AnomalySeverity.Error);
    }

    /// <summary>
    /// The route is being walked, yet the bot's stored position is not where the motion last put it: something else relocated it
    /// (a teleport that finished between two pumps, a script) and the next heartbeat would pull it back.
    /// </summary>
    internal static void CheckJump(WorldSession session, Player player, PlayerbotMotion.MotionState state, uint now)
    {
        float dx = player.X - state.Anchor.X, dy = player.Y - state.Anchor.Y;
        if (!(dx * dx + dy * dy > JumpYards * JumpYards)) return;
        if ((player.Movement.Flags & MovementFlags.OnTransport) != 0 || session.IsInChaosProbe || !state.Watch.Due(Code.PositionJump, now)) return;
        Emit(player, Code.PositionJump,
            string.Create(CultureInfo.InvariantCulture,
                $"moved {MathF.Sqrt((dx * dx) + (dy * dy)):F1} yd from the position its walk last reported ({Describe(state.Anchor)}) without the walk"),
            AnomalySeverity.Warning);
    }

    /// <summary>The route's lease ran out before the route did (called just before the motion stops).</summary>
    internal static void CheckLeaseExpired(WorldSession session, Player player, PlayerbotRoute route, PlayerbotMotion.MotionState state, uint now)
    {
        if (AgeOf(now, state.RenewedMs) <= state.LeaseMs || session.IsInChaosProbe) return;
        if (!PlayerbotMotion.TryYardsToEnd(player, route, out float yards) || yards <= MidRouteYards) return;
        if (!state.Watch.Due(Code.LeaseExpired, now)) return;
        Emit(player, Code.LeaseExpired,
            string.Create(CultureInfo.InvariantCulture,
                $"no think renewed the route for {AgeOf(now, state.RenewedMs)} ms (lease {state.LeaseMs} ms, think gap {state.GapMs} ms); {yards:F0} yd were left"),
            AnomalySeverity.Info);
    }

    /// <summary>The server did not take a movement packet the motion sent (<paramref name="what"/> names it).</summary>
    internal static void ReportMoveRejected(WorldSession session, Player player, string what, Vector3 intended, uint now)
    {
        if (session.IsInChaosProbe || !PlayerbotMotion.Of(player).Watch.Due(Code.MoveRejected, now)) return;
        Emit(player, Code.MoveRejected,
            $"{what} to {Describe(intended)} was not taken (the bot stands at {Describe(new Vector3(player.X, player.Y, player.Z))})",
            AnomalySeverity.Warning);
    }

    /// <summary>A route could not be started: its first stretch from <paramref name="from"/> is not walkable.</summary>
    internal static void ReportStartBlocked(WorldSession session, Player player, PlayerbotRoute route, Vector3 from, uint now)
    {
        if (session.IsInChaosProbe || !PlayerbotMotion.Of(player).Watch.Due(Code.StartBlocked, now)) return;
        Emit(player, Code.StartBlocked,
            $"the first stretch of a {route.Points.Count}-point route from {Describe(from)} towards {Describe(route.Points[Math.Clamp(route.NextPoint, 0, route.Points.Count - 1)])} is not walkable",
            AnomalySeverity.Warning);
    }

    /// <summary>The route being walked is not walkable ahead of <paramref name="at"/>: the bot stopped there.</summary>
    internal static void ReportRouteBlocked(WorldSession session, Player player, PlayerbotRoute route, Vector3 at, uint now)
    {
        if (session.IsInChaosProbe || !PlayerbotMotion.Of(player).Watch.Due(Code.RouteBlocked, now)) return;
        Emit(player, Code.RouteBlocked,
            $"the route ({route.Points.Count} points, next {route.NextPoint}) is not walkable ahead of {Describe(at)}; the bot stopped",
            AnomalySeverity.Warning);
    }

    /// <summary>
    /// After a heartbeat at <paramref name="to"/> (the walk came from <paramref name="from"/>, heading for point <paramref name="fromNext"/>,
    /// now for <paramref name="nextPoint"/>): the stuck check every heartbeat, the ground and crossing checks once per sample.
    /// </summary>
    internal static void AfterHeartbeat(WorldSession session, Player player, Map map, PlayerbotMotion.MotionState state,
        PlayerbotRoute route, Vector3 from, int fromNext, int nextPoint, Vector3 to, uint now)
    {
        MotionWatch watch = state.Watch;
        CheckStuck(session, player, watch, route, to, now);
        if (!watch.SampleDue(now) || session.IsInChaosProbe) return;
        CheckGround(player, map, watch, to, now);
        CheckCrossing(player, map, watch, route, fromNext, nextPoint, from, to, now);
    }

    /// <summary>Heartbeats went on for <see cref="StuckWindowMs"/> and the bot is where the window began.</summary>
    internal static void CheckStuck(WorldSession session, Player player, MotionWatch watch, PlayerbotRoute route, Vector3 at, uint now)
    {
        if (!ReferenceEquals(watch.StuckRoute, route) || AgeOf(now, watch.StuckMs) > StuckWindowMs * 3)
        {
            watch.StuckRoute = route;
            watch.StuckAt = at;
            watch.StuckMs = now;
            return;
        }

        uint age = AgeOf(now, watch.StuckMs);
        if (age < StuckWindowMs) return;
        Vector3 was = watch.StuckAt;
        watch.StuckAt = at;
        watch.StuckMs = now;
        float moved = Vector2.Distance(new Vector2(was.X, was.Y), new Vector2(at.X, at.Y));
        if (moved >= StuckYards || session.IsInChaosProbe || !watch.Due(Code.Stuck, now)) return;
        Emit(player, Code.Stuck,
            string.Create(CultureInfo.InvariantCulture,
                $"walked {age} ms along a {route.Points.Count}-point route and ended {moved:F1} yd from where it began ({Describe(was)})"),
            AnomalySeverity.Warning);
    }

    /// <summary>
    /// The verdict on a bot at height <paramref name="z"/> given the terrain surface and the floor (terrain or model) under it;
    /// heights that are not valid (no data) never convict. Pure.
    /// </summary>
    internal static GroundVerdict Classify(float z, float floor, UnderMapVerdict underMap)
    {
        if (!float.IsFinite(z)) return GroundVerdict.Ok;
        if (ValidHeight(floor)) return z - floor > AirwalkYards ? GroundVerdict.Airwalk : GroundVerdict.Ok;
        // No floor under the bot. Only the shared model-aware query convicts (caves, cellars and cities are Ok, unloaded data is Unknown).
        if (underMap.Kind != UnderMapKind.UnderMap) return GroundVerdict.Ok; // the query already applied UnderMapYards as its margin
        return underMap.DepthYards > FarUnderMapYards ? GroundVerdict.FarUnderMap : GroundVerdict.UnderMap;
    }

    private static bool ValidHeight(float height)
        => float.IsFinite(height) && height > TerrainTile.InvalidHeight;

    private const MovementFlags NotWalking = MovementFlags.Flying | MovementFlags.Levitating | MovementFlags.Swimming
        | MovementFlags.OnTransport | MovementFlags.Hover | MovementFlags.WaterWalking | MovementFlags.FallingFar
        | MovementFlags.Jumping | MovementFlags.SplineEnabled;

    /// <summary>
    /// The ground verdict at <paramref name="at"/> given the floor under it and whether model data backs the map. The model-aware
    /// under-map query is asked only when there is no floor, and with <see cref="UnderMapYards"/> as its margin.
    /// </summary>
    internal static GroundVerdict JudgeGround(Vector3 at, float floor, bool modelsEnabled, IUnderMapClassifier classifier, out UnderMapVerdict underMap)
    {
        // Without any model data a cave or a cellar looks like open ground under the terrain, so nothing is convicted.
        underMap = ValidHeight(floor) || !modelsEnabled
            ? UnderMapVerdict.Unknown
            : classifier.ClassifyUnderMap(at.X, at.Y, at.Z, UnderMapYards);
        return Classify(at.Z, floor, underMap);
    }

    /// <summary>
    /// The floor a walking bot at <paramref name="at"/> is judged against: <see cref="MapCollision.GetHeight"/>, unless that lies more
    /// than <see cref="AirwalkYards"/> below and the navigation mesh has a floor within that distance under the bot
    /// (<see cref="MapCollision.TryGetMeshFloorUnder"/>, the rule the under-map query shares: searched from 2 yards above, never
    /// higher). Inside a WMO the model query can miss the storey a bot walks on and answer the one below it (Ironforge: 21 yd "above
    /// the floor" at -4599.4,-902.4,523.9).
    /// </summary>
    internal static float FloorUnder(MapCollision collision, Vector3 at)
    {
        float floor = collision.GetHeight(at.X, at.Y, at.Z);
        if (ValidHeight(floor) && at.Z - floor > AirwalkYards
            && collision.TryGetMeshFloorUnder(at.X, at.Y, at.Z, AirwalkYards, out float mesh) && mesh > floor)
        {
            return mesh;
        }

        return floor;
    }

    /// <summary>One sampled ground check at <paramref name="at"/>, where a heartbeat just put the bot.</summary>
    internal static void CheckGround(Player player, Map map, MotionWatch watch, Vector3 at, uint now)
    {
        if ((player.Movement.Flags & NotWalking) != 0 || player.Locomotion.ActiveSpline is not null || !Finite(at)) return;
        float floor = FloorUnder(map.Collision, at);
        GroundVerdict verdict = JudgeGround(at, floor, map.Collision.LineOfSight.Enabled, map.Collision, out UnderMapVerdict underMap);
        if (verdict == GroundVerdict.Ok) return;
        ReportGround(player, map, watch, verdict, at, at.Z + underMap.DepthYards, floor, now);
    }

    /// <summary>Report a ground verdict other than Ok (a bot in water is not convicted: the floor under a swimmer is the bottom).</summary>
    internal static void ReportGround(Player player, Map map, MotionWatch watch, GroundVerdict verdict, Vector3 at, float terrain,
        float floor, uint now)
    {
        if (verdict == GroundVerdict.Ok || PlayerbotWater.Submerged(map, at, player.Locomotion.CollisionHeight)) return;
        Code code = verdict == GroundVerdict.Airwalk ? Code.Airwalk : Code.UnderMap;
        if (!watch.Due(code, now)) return;
        string detail = verdict == GroundVerdict.Airwalk
            ? string.Create(CultureInfo.InvariantCulture, $"walking {at.Z - floor:F1} yd above the floor under it (floor {floor:F1}, at {Describe(at)})")
            : string.Create(CultureInfo.InvariantCulture, $"{terrain - at.Z:F1} yd under the terrain surface ({terrain:F1}) with no model floor under it (at {Describe(at)})");
        Emit(player, code, detail, verdict == GroundVerdict.FarUnderMap ? AnomalySeverity.Error : AnomalySeverity.Warning);
    }

    /// <summary>
    /// The mesh legs walked since the last heartbeat (from <paramref name="from"/> through the route points
    /// <paramref name="fromNext"/> up to <paramref name="nextPoint"/> - 1, to <paramref name="to"/>) must not cross a static model.
    /// The route's own legs are tested, not the chord between two heartbeats: the chord cuts across the pillar a leg goes around.
    /// </summary>
    internal static void CheckCrossing(Player player, Map map, MotionWatch watch, PlayerbotRoute route, int fromNext, int nextPoint,
        Vector3 from, Vector3 to, uint now)
    {
        ILineOfSight los = map.Collision.LineOfSight;
        if (!los.Enabled || !Finite(from) || !Finite(to) || !route.LegsNavigated(fromNext, nextPoint)) return;
        int first = Math.Max(1, fromNext);
        int last = Math.Min(nextPoint, route.Points.Count);
        Vector3 previous = from;
        int tested = 0;
        for (int index = first; index <= last && tested < MaxCrossingSegments; index++)
        {
            Vector3 end = index < last ? route.Points[index] : to;
            if (!Finite(end)) return;
            if (Vector2.Distance(new Vector2(previous.X, previous.Y), new Vector2(end.X, end.Y)) >= MinSegmentYards)
            {
                tested++;
                Vector3 a = previous with { Z = previous.Z + PlayerbotMoveCheck.ChestYards };
                Vector3 b = end with { Z = end.Z + PlayerbotMoveCheck.ChestYards };
                // ignoreM2 false is the stricter query: a clear answer clears both, so a clean leg costs one query. A blocked line over a
                // leg that stays on the walkable mesh is the line's fault, not the walk's: the chest-height line clips a step's edge, a
                // stair's slab, a lintel or a ramp the mesh (built from the same models) walks over or under.
                if (!los.IsInLineOfSight(map.MapId, a, b, ignoreM2: false) && !StaysOnMesh(map, previous, end))
                {
                    bool wall = !los.IsInLineOfSight(map.MapId, a, b, ignoreM2: true);
                    ReportCrossing(player, watch, wall, previous, end, now);
                    return;
                }
            }

            previous = end;
        }
    }

    /// <summary>
    /// Whether the segment from <paramref name="from"/> to <paramref name="to"/> runs on the map's navigation mesh from end to end
    /// (<see cref="IMeshPointQuery.Raycast"/>: <paramref name="from"/> on a polygon and the walk along the surface never leaving it).
    /// False without a mesh, or with the start off it: then the line of sight alone judges the leg.
    /// </summary>
    internal static bool StaysOnMesh(Map map, Vector3 from, Vector3 to)
        => map.Collision.MeshPoints is { } mesh && mesh.Raycast(map.MapId, from, to) is { Available: true, Hit: false };

    private static void ReportCrossing(Player player, MotionWatch watch, bool wall, Vector3 from, Vector3 to, uint now)
    {
        Code code = wall ? Code.WallCrossed : Code.DoorCrossed;
        if (!watch.Due(code, now)) return;
        Emit(player, code,
            $"walked from {Describe(from)} to {Describe(to)} through {(wall ? "a wall (static model)" : "a door, gate or fence (M2 doodad)")}",
            wall ? AnomalySeverity.Warning : AnomalySeverity.Info);
    }
}
