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
/// model explains it (inside a WMO, a model floor under it or a model surface just above it: caves, cellars and cities; unloaded model tiles never convict); Error beyond <see cref="FarUnderMapYards"/>. Same sample. Airwalk and under-map are one episode per place: a bot that stays within <see cref="SamePlaceYards"/> of its last report is not reported again for <see cref="SamePlaceQuietMs"/> (<see cref="MotionWatch.DueAt"/>).</description></item>
/// <item><term>wall-crossed</term><description>a leg of a navigation-mesh route the bot walked since the last heartbeat is blocked by a
/// static model (line of sight at chest height), unless the bot's walkable mesh walks it on the leg's own storey: the mesh walk never
/// leaves the mesh and starts and ends within <see cref="MeshStoreyYards"/> of the leg's ends (<see cref="StaysOnMesh"/>). Warning. One
/// to <see cref="MaxCrossingSegments"/> segment queries per sample (and two mesh walks per blocked one), only for mesh legs (a stepped
/// leg is refused by the motion itself).</description></item>
/// <item><term>door-crossed</term><description>the same leg is clear of walls but blocked by an M2 doodad (a door, a gate, a fence),
/// with the same exemption for a leg the mesh walks on its storey (the mesh is built from the same models). Info.</description></item>
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
/// <item><term>overspeed</term><description>a heartbeat step of at least <see cref="OverspeedMinAgeMs"/> covered more than
/// <see cref="OverspeedFactor"/> times the flat distance the speed the bot is entitled to allows (<see cref="PlayerbotMotion.Speed"/> for
/// the bot now, in the swim state the step was walked in). Warning. A compare per heartbeat.</description></item>
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

    /// <summary>
    /// A ground verdict (airwalk, under-map) is one episode while the bot stays within this many yards of where it was reported: a bot
    /// that stands in a void is reported once, not once per <see cref="EpisodeQuietMs"/>.
    /// </summary>
    internal const float SamePlaceYards = 4f;

    /// <summary>A ground verdict at the same place (<see cref="SamePlaceYards"/>) is reported again after this long (world ms): the episode is re-armed.</summary>
    internal const uint SamePlaceQuietMs = 600_000;

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

    /// <summary>A step shorter than this is not judged for its speed (a few ms of rounding is a large ratio).</summary>
    internal const uint OverspeedMinAgeMs = 200;

    /// <summary>A step covering more than this times the entitled speed is an overspeed.</summary>
    internal const float OverspeedFactor = 1.05f;

    /// <summary>Segments shorter than this are not tested for a crossing.</summary>
    internal const float MinSegmentYards = 0.5f;

    /// <summary>The most segments one sample tests.</summary>
    internal const int MaxCrossingSegments = 6;

    /// <summary>
    /// How far (up or down) the mesh surface a leg's walk starts and ends on may lie from the leg's own ends for the walk to count as
    /// the leg's (<see cref="StaysOnMesh"/>): a step or a slab, not another storey (stacked storeys stand a bot's height and a floor apart).
    /// </summary>
    internal const float MeshStoreyYards = 2f;

    private static readonly PathOptions SteepMeshOptions = new()
    {
        Mover = PathMover.Player,
        ExcludeFlags = NavTerrain.Empty,
        IncludeFlags = PathMover.Player.IncludeFlags | NavTerrain.SteepSlopes,
    };

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
        Overspeed,
    }

    private static readonly string[] Names =
    [
        "airwalk", "under-map", "wall-crossed", "door-crossed", "position-jump", "non-finite-position", "lease-expired-midroute",
        "stuck", "move-rejected", "start-blocked", "route-blocked", "overspeed",
    ];

    /// <summary>The normalised ledger key of <paramref name="code"/> (tests).</summary>
    internal static string Key(Code code) => PlayerbotAnomalies.Key(Area, Names[(int)code]);

    internal enum GroundVerdict { Ok, Airwalk, UnderMap, FarUnderMap }

    /// <summary>One bot's rate-limit and sampling state (inside <see cref="PlayerbotMotion.MotionState"/>).</summary>
    internal sealed class MotionWatch
    {
        private readonly uint[] _last = new uint[Names.Length];
        private readonly Vector3[] _at = new Vector3[Names.Length];
        private int _seen;
        private int _placed;
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

        /// <summary>
        /// <see cref="Due"/> for a report tied to a place: false (and nothing marked) while the last report of <paramref name="code"/> was
        /// within <see cref="SamePlaceYards"/> of <paramref name="at"/> less than <see cref="SamePlaceQuietMs"/> ago. A report at another
        /// place, or after that interval, goes through the ordinary <see cref="EpisodeQuietMs"/> gate and records the place.
        /// </summary>
        internal bool DueAt(Code code, uint now, Vector3 at)
        {
            int bit = 1 << (int)code;
            if ((_seen & bit) != 0 && (_placed & bit) != 0 && AgeOf(now, _last[(int)code]) < SamePlaceQuietMs && Vector3.DistanceSquared(_at[(int)code], at) <= SamePlaceYards * SamePlaceYards)
            {
                return false;
            }

            if (!Due(code, now)) return false;
            _at[(int)code] = at;
            _placed |= bit;
            return true;
        }

        /// <summary>
        /// The bot stood on ground (an Ok sample): the next <paramref name="code"/> report is a new episode even at the same place (a bot
        /// that falls back into the same void is reported again, still behind the <see cref="EpisodeQuietMs"/> cooldown).
        /// </summary>
        internal void EndPlace(Code code) => _placed &= ~(1 << (int)code);

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
        string lastThink = state.Thought
            ? string.Create(CultureInfo.InvariantCulture, $"last think {AgeOf(now, state.LastThinkMs)} ms ago, driver {state.LastThinkDriver}")
            : "no think seen";
        Emit(player, Code.LeaseExpired,
            string.Create(CultureInfo.InvariantCulture,
                $"no think renewed the route for {AgeOf(now, state.RenewedMs)} ms (lease {state.LeaseMs} ms, think gap {state.GapMs} ms, {lastThink}); {yards:F0} yd were left, unfollowed {AgeOf(now, state.FollowedMs)} ms of a {PlayerbotMotion.SettleBoundMs(state)} ms settle bound, route released {route.Released}"),
            AnomalySeverity.Info);
    }

    /// <summary>The server did not take a movement packet the motion sent (<paramref name="what"/> names it).</summary>
    internal static void ReportMoveRejected(WorldSession session, Player player, string what, Vector3 intended, uint now,
        int consecutiveRejections = 1, string? reason = null, string? moveMode = null)
    {
        if (session.IsInChaosProbe || !PlayerbotMotion.Of(player).Watch.Due(Code.MoveRejected, now)) return;
        string context = FormatRejectionContext(consecutiveRejections, reason, moveMode);
        Emit(player, Code.MoveRejected,
            $"{what} to {Describe(intended)} was not taken (the bot stands at {Describe(new Vector3(player.X, player.Y, player.Z))}){context}",
            AnomalySeverity.Warning);
    }

    /// <summary>A route could not be started: its first stretch from <paramref name="from"/> is not walkable.</summary>
    internal static void ReportStartBlocked(WorldSession session, Player player, PlayerbotRoute route, Vector3 from, uint now,
        int consecutiveRejections = 1, string? reason = null, string? moveMode = null)
    {
        if (session.IsInChaosProbe || !PlayerbotMotion.Of(player).Watch.Due(Code.StartBlocked, now)) return;
        string context = FormatRejectionContext(consecutiveRejections, reason, moveMode);
        Emit(player, Code.StartBlocked,
            $"the first stretch of a {route.Points.Count}-point route from {Describe(from)} towards {Describe(route.Points[Math.Clamp(route.NextPoint, 0, route.Points.Count - 1)])} is not walkable{context}",
            AnomalySeverity.Warning);
    }

    /// <summary>The route being walked is not walkable ahead of <paramref name="at"/>: the bot stopped.</summary>
    internal static void ReportRouteBlocked(WorldSession session, Player player, PlayerbotRoute route, Vector3 at, uint now,
        int consecutiveRejections = 1, string? reason = null, string? moveMode = null)
    {
        if (session.IsInChaosProbe || !PlayerbotMotion.Of(player).Watch.Due(Code.RouteBlocked, now)) return;
        string context = FormatRejectionContext(consecutiveRejections, reason, moveMode);
        Emit(player, Code.RouteBlocked,
            $"the route ({route.Points.Count} points, next {route.NextPoint}) is not walkable ahead of {Describe(at)}; the bot stopped{context}",
            AnomalySeverity.Warning);
    }

    private static string FormatRejectionContext(int consecutiveRejections, string? reason, string? moveMode)
    {
        if (reason is null && moveMode is null && consecutiveRejections <= 1) return string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $" [reason={reason ?? "NoGroundSupport"} moveMode={moveMode ?? "DirectStep"} consec={Math.Max(1, consecutiveRejections)}]");
    }

    /// <summary>
    /// After a heartbeat at <paramref name="to"/> (the walk came from <paramref name="from"/>, heading for point <paramref name="fromNext"/>,
    /// now for <paramref name="nextPoint"/>): the stuck check every heartbeat, the ground and crossing checks once per sample.
    /// </summary>
    internal static void AfterHeartbeat(WorldSession session, Player player, Map map, PlayerbotMotion.MotionState state,
        PlayerbotRoute route, Vector3 from, int fromNext, int nextPoint, Vector3 to, uint now, uint ageMs = 0, bool swam = false)
    {
        MotionWatch watch = state.Watch;
        CheckStuck(session, player, watch, route, to, now);
        CheckOverspeed(session, player, state, from, to, ageMs, swam, now);
        if (!watch.SampleDue(now) || session.IsInChaosProbe) return;
        CheckGround(player, map, watch, to, now);
        CheckCrossing(player, map, watch, route, fromNext, nextPoint, from, to, now);
    }

    /// <summary>
    /// The step from <paramref name="from"/> to <paramref name="to"/> took <paramref name="ageMs"/> and was walked in the swim state
    /// <paramref name="swam"/>: more than <see cref="OverspeedFactor"/> times the speed <see cref="PlayerbotMotion.Speed"/> gives the
    /// bot now is reported. The motion state's own speed is not the yardstick (a fault in it is what this finds).
    /// </summary>
    internal static void CheckOverspeed(WorldSession session, Player player, PlayerbotMotion.MotionState state, Vector3 from, Vector3 to,
        uint ageMs, bool swam, uint now)
    {
        if (ageMs < OverspeedMinAgeMs || session.IsInChaosProbe) return;
        if (!PlayerbotMotion.Speed(player, state.MoveSpeedCap, swam, out float expected, out bool walk)) return;
        float travelled = Vector2.Distance(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y));
        float actual = travelled / (ageMs / 1000f);
        if (!(actual > OverspeedFactor * expected) || !state.Watch.Due(Code.Overspeed, now)) return;
        Emit(player, Code.Overspeed,
            string.Create(CultureInfo.InvariantCulture,
                $"moved {actual:F2} yd/s, expected {expected:F2} yd/s (motion speed {state.Speed:F2}, run speed {UnitSpeed.Get(player, MoveType.Run):F2}, mounted {UnitSpeed.IsMounted(player)}, swimming {swam}, walk {walk}), {travelled:F2} yd in {ageMs} ms"),
            AnomalySeverity.Warning);
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

    internal const MovementFlags NotWalking = MovementFlags.Flying | MovementFlags.Levitating | MovementFlags.Swimming
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
        if (!ValidHeight(floor) || at.Z - floor > AirwalkYards)
        {
            float highFloor = collision.GetHeight(at.X, at.Y, at.Z + PlayerbotMotion.MeshFloorToleranceYards);
            if (ValidHeight(highFloor) && highFloor <= at.Z + 1.0f && (!ValidHeight(floor) || highFloor > floor))
            {
                floor = highFloor;
            }
        }

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
        if ((player.Movement.Flags & NotWalking) != 0 || player.Locomotion.ActiveSpline is not null || !at.IsFinite()) return;
        float floor = FloorUnder(map.Collision, at);
        GroundVerdict verdict = JudgeGround(at, floor, map.Collision.LineOfSight.Enabled, map.Collision, out UnderMapVerdict underMap);
        if (verdict == GroundVerdict.Ok)
        {
            watch.EndPlace(Code.UnderMap);
            watch.EndPlace(Code.Airwalk);
            return;
        }

        ReportGround(player, map, watch, verdict, at, at.Z + underMap.DepthYards, floor, now);
    }

    /// <summary>Report a ground verdict other than Ok (a bot in water is not convicted: the floor under a swimmer is the bottom).</summary>
    internal static void ReportGround(Player player, Map map, MotionWatch watch, GroundVerdict verdict, Vector3 at, float terrain,
        float floor, uint now)
    {
        if (verdict == GroundVerdict.Ok || PlayerbotWater.Submerged(map, at, player.Locomotion.CollisionHeight)) return;
        Code code = verdict == GroundVerdict.Airwalk ? Code.Airwalk : Code.UnderMap;
        if (!watch.DueAt(code, now, at)) return;
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
        if (!los.Enabled || !from.IsFinite() || !to.IsFinite() || !route.LegsNavigated(fromNext, nextPoint)) return;
        int first = Math.Max(1, fromNext);
        int last = Math.Min(nextPoint, route.Points.Count);
        Vector3 previous = from;
        int tested = 0;
        for (int index = first; index <= last && tested < MaxCrossingSegments; index++)
        {
            Vector3 end = index < last ? ResolveIntermediateCorner(map, from, to, route.Points[index]) : to;
            if (!end.IsFinite()) return;
            if (Vector2.Distance(new Vector2(previous.X, previous.Y), new Vector2(end.X, end.Y)) >= MinSegmentYards)
            {
                tested++;
                Vector3 a = previous with { Z = previous.Z + PlayerbotMoveCheck.ChestYards };
                Vector3 b = end with { Z = end.Z + PlayerbotMoveCheck.ChestYards };
                // ignoreM2 false is the stricter query: a clear answer clears both, so a clean leg costs one query. A blocked line over a
                // leg that stays on the walkable mesh is the line's fault, not the walk's: the chest-height line clips a step's edge, a
                // stair's slab, a lintel or a ramp the mesh (built from the same models) walks over or under.
                if (!los.IsInLineOfSight(map.MapId, a, b, ignoreM2: false)
                    && !StaysOnMesh(map, previous, end)
                    && !(route.MeshPoints is not null && index >= 1
                        && StaysOnMeshFromCorner(map, route.Points[index - 1], route.Points[Math.Min(index, route.Points.Count - 1)], previous, end)))
                {
                    bool wall = !los.IsInLineOfSight(map.MapId, a, b, ignoreM2: true);
                    ReportCrossing(player, watch, wall, previous, end, now);
                    return;
                }
            }

            previous = end;
        }
    }

    internal static bool CrossesStaticWall(Map map, PlayerbotRoute route, int fromNext, int nextPoint, Vector3 from, Vector3 to)
    {
        ILineOfSight los = map.Collision.LineOfSight;
        if (!los.Enabled || map.Collision.MeshPoints is null || !from.IsFinite() || !to.IsFinite() || !route.LegsNavigated(fromNext, nextPoint)) return false;
        int first = Math.Max(1, fromNext);
        int last = Math.Min(nextPoint, route.Points.Count);
        Vector3 previous = from;
        int tested = 0;
        for (int index = first; index <= last && tested < MaxCrossingSegments; index++)
        {
            Vector3 end = index < last ? ResolveIntermediateCorner(map, from, to, route.Points[index]) : to;
            if (!end.IsFinite()) return false;
            if (Vector2.Distance(new Vector2(previous.X, previous.Y), new Vector2(end.X, end.Y)) >= MinSegmentYards)
            {
                tested++;
                Vector3 a = previous with { Z = previous.Z + PlayerbotMoveCheck.ChestYards };
                Vector3 b = end with { Z = end.Z + PlayerbotMoveCheck.ChestYards };
                if (!los.IsInLineOfSight(map.MapId, a, b, ignoreM2: true)
                    && !StaysOnMesh(map, previous, end)
                    && !(route.MeshPoints is not null && index >= 1
                        && StaysOnMeshFromCorner(map, route.Points[index - 1], route.Points[Math.Min(index, route.Points.Count - 1)], previous, end)))
                {
                    return true;
                }
            }

            previous = end;
        }

        return false;
    }

    internal static Vector3 ResolveIntermediateCorner(Map map, Vector3 from, Vector3 to, Vector3 end)
    {
        float low = MathF.Min(from.Z, to.Z);
        float high = MathF.Max(from.Z, to.Z);
        if (end.Z >= low - 0.5f && end.Z <= high + 0.5f)
        {
            return end;
        }

        float minZ = low - PlayerbotMotion.MeshFloorToleranceYards;
        float maxZ = high + PlayerbotMotion.MeshFloorToleranceYards;
        float floor = PlayerbotMotion.ResolveWalkFloor(map, end.X, end.Y, high, 0f, high + 0.5f);
        if (!ValidHeight(floor) || floor < low - 0.5f || floor > high + 0.5f)
        {
            float probeZ = Math.Clamp(end.Z, minZ, maxZ);
            floor = PlayerbotMotion.ResolveWalkFloor(map, end.X, end.Y, probeZ, 0f, high + 0.5f);
        }

        if (ValidHeight(floor) && floor >= low - 0.5f && floor <= high + 0.5f)
        {
            end.Z = floor + 0.05f;
        }
        else if (end.Z < low || end.Z > high)
        {
            end.Z = Math.Clamp(end.Z, low, high);
        }

        return end;
    }

    private static bool StaysOnMeshFromCorner(Map map, Vector3 legStart, Vector3 legEnd, Vector3 previous, Vector3 end)
    {
        bool startNearStorey = MathF.Abs(previous.Z - legStart.Z) <= PlayerbotMotion.MeshFloorToleranceYards;
        bool endNearStorey = MathF.Abs(end.Z - legEnd.Z) <= PlayerbotMotion.MeshFloorToleranceYards;
        if (startNearStorey && StaysOnMesh(map, legStart, end)) return true;
        if (endNearStorey && StaysOnMesh(map, previous, legEnd)) return true;
        return startNearStorey && endNearStorey && StaysOnMesh(map, legStart, legEnd);
    }

    /// <summary>
    /// Whether the segment from <paramref name="from"/> to <paramref name="to"/> is a walk over the bot's walkable mesh
    /// (<see cref="PlayerbotMoveCheck.MeshOptions"/>: steep slopes excluded) on the leg's own storey: the walk
    /// (<see cref="IMeshPointQuery.Raycast"/>) never leaves the polygons the filter accepts, the polygon it starts from has its surface
    /// within <see cref="MeshStoreyYards"/> of <paramref name="from"/>, and the surface it ends on lies within <see cref="MeshStoreyYards"/>
    /// of <paramref name="to"/>. The walk is horizontal (its start polygon is the nearest within 5 yd up or down, its end the surface at
    /// the end's x/y on whatever storey it reached), so without the two height checks a leg from one storey to another (through a floor,
    /// a ramp or a wall into a stacked level: Ironforge, Undercity) would pass for one that stays on the mesh. False without a mesh, or
    /// with the start off it: then the line of sight alone judges the leg. Two mesh walks (a zero-length one for the start polygon).
    /// </summary>
    internal static bool StaysOnMesh(Map map, Vector3 from, Vector3 to)
    {
        if (map.Collision.MeshPoints is not { } mesh) return false;
        return StaysOnMeshWithOptions(mesh, map.MapId, from, to, PlayerbotMoveCheck.MeshOptions)
            || StaysOnMeshWithOptions(mesh, map.MapId, from, to, SteepMeshOptions);
    }

    private static bool StaysOnMeshWithOptions(IMeshPointQuery mesh, uint mapId, Vector3 from, Vector3 to, PathOptions options)
        => RayReachesOnStorey(mesh, mapId, from, to, options)
            || RayReachesOnStorey(mesh, mapId, to, from, options);

    private static bool RayReachesOnStorey(IMeshPointQuery mesh, uint mapId, Vector3 from, Vector3 to, PathOptions options)
    {
        MeshRaycastResult walk = mesh.Raycast(mapId, from, to, options);
        if (!walk.Available) return false;
        if (walk.Hit && Vector2.Distance(new(walk.Point.X, walk.Point.Y), new(to.X, to.Y)) > 0.35f) return false;
        if (!(MathF.Abs(walk.Point.Z - to.Z) < PlayerbotMotion.MeshFloorToleranceYards)) return false;
        // A zero-length walk answers the start polygon's surface under the start.
        MeshRaycastResult start = mesh.Raycast(mapId, from, from, options);
        return start.Available && MathF.Abs(start.Point.Z - from.Z) < PlayerbotMotion.MeshFloorToleranceYards;
    }

    private static void ReportCrossing(Player player, MotionWatch watch, bool wall, Vector3 from, Vector3 to, uint now)
    {
        Code code = wall ? Code.WallCrossed : Code.DoorCrossed;
        if (!watch.Due(code, now)) return;
        Emit(player, code,
            $"walked from {Describe(from)} to {Describe(to)} through {(wall ? "a wall (static model)" : "a door, gate or fence (M2 doodad)")}",
            wall ? AnomalySeverity.Warning : AnomalySeverity.Info);
    }
}
