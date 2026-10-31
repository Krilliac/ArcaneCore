using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// Server-decided movement of a spell's caster or target (SPELL_EFFECT_CHARGE, LEAP and TELEPORT_UNITS_FACE_CASTER): where the unit ends
/// up, and the one spline a charge plays. The geometry is the mangoszero spell code re-expressed over the repo's collision services:
/// <c>ContactPointNear</c> (WorldObjectSummon.cpp:534-541, FindFreeSpotNear :395-519), <c>Spell::EffectLeapForward</c>
/// (SpellEffectObjectCombat.cpp:944-1112) and <c>Spell::EffectCharge</c> (:1245-1273).
/// <para>
/// State ownership: nothing here keeps state but the spline id counter; every call runs on the world thread against the unit's own
/// map. Allocation: per cast, never per tick (a charge allocates its point list and one packet, a blink one vector per probe).
/// </para>
/// <para>
/// "No data is open" (docs/integration/vmap-los.md): a map without terrain or model heights answers every height query with
/// <see cref="TerrainTile.InvalidHeightValue"/>. A unit that stands where no height is known keeps its z and the move is not refused for
/// want of ground; a unit that stands on known ground and would step onto unknown ground (a hole, the map edge) stops, as the reference does.
/// </para>
/// </summary>
public static class ForcedMovement
{
    /// <summary>The charge speed (yards per second) of <c>MonsterMoveWithSpeed(x, y, z, 24.f, ...)</c> (SpellEffectObjectCombat.cpp:1262).</summary>
    public const float ChargeSpeed = 24.0f;

    /// <summary>The gap a charge stops from its target's edge (<c>ContactPointNear(..., 3.666666f)</c>, :1255; "seem to give more accurate result" than 5).</summary>
    public const float ChargeContactGap = 3.666666f;

    /// <summary>Blink checks the way in steps of this length (<c>step = 2.0f</c>, :1000).</summary>
    public const float LeapStep = 2.0f;

    /// <summary>The steepest slope a blink climbs, in degrees ("50 seem best value for walkable slope", :1001).</summary>
    public const float LeapMaxSlopeDegrees = 50.0f;

    /// <summary>A blink ends this far below the water surface in water (<c>IN_OR_UNDER_LIQUID_RANGE</c>, :950).</summary>
    public const float LiquidRange = 0.8f;

    private const float FloorRange = 4.0f;          // GetHeightInRange default maxSearchDist
    private const float OriginFloorRange = 3.0f;    // the origin lookup of EffectLeapForward (:965)
    private const float FallingFloorRange = 10.0f;  // "fix z to ground if near of it" while falling (:984)
    private const float EyeHeight = 1.5f;           // the z lift of the blink's collision segment (:1096)

    // The order FindFreeSpotNear walks the angles around the bearing (the object position selector alternates sides).
    private static readonly float[] s_contactAngles =
    [
        0f, MathF.PI / 4, -MathF.PI / 4, MathF.PI / 2, -MathF.PI / 2, 3 * MathF.PI / 4, -3 * MathF.PI / 4, MathF.PI,
    ];

    private static int s_splineId;

    /// <summary>
    /// ContactPointNear: the point <paramref name="mover"/> stops at when it goes to <paramref name="anchor"/>, on the line from the anchor
    /// toward the mover at (gap + both bounding radii). The first free angle whose floor is within that reach of the anchor's z and
    /// that the anchor can see wins; when none is, the point straight toward the mover is used ("BAD BAD NEWS: all found pos have LOS problem").
    /// Other units standing there are not avoided (the reference's used-position selector is not ported).
    /// </summary>
    public static Vector3 ContactPoint(Unit anchor, Unit mover, float gap)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(mover);
        float anchorRadius = Radius(anchor);
        float moverRadius = Radius(mover);
        float distance = gap + anchorRadius + moverRadius;
        float limit = distance + moverRadius + anchorRadius;
        float bearing = BearingFromTo(anchor, mover);
        MapCollision? collision = anchor.Map?.Collision;

        Vector3 first = default;
        for (int i = 0; i < s_contactAngles.Length; i++)
        {
            Vector3 candidate = PointAt(anchor, distance, bearing + s_contactAngles[i], collision);
            if (i == 0)
            {
                first = candidate;
            }

            if (collision is null)
            {
                return first;
            }

            if (MathF.Abs(anchor.Z - candidate.Z) < limit && collision.IsWithinLineOfSight(anchor, candidate.X, candidate.Y, candidate.Z))
            {
                return candidate;
            }
        }

        return first;
    }

    /// <summary>
    /// ClosePointNear with an angle of 0 around <paramref name="anchor"/>'s facing: the point (distance + both radii) in front of the anchor,
    /// dropped to the ground (TELEPORT_UNITS_FACE_CASTER without a destination, <paramref name="distance"/> = the effect radius).
    /// </summary>
    public static Vector3 PointInFront(Unit anchor, Unit subject, float distance)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(subject);
        return PointAt(anchor, distance + Radius(anchor) + Radius(subject), anchor.Orientation, anchor.Map?.Collision);
    }

    /// <summary>
    /// Play a charge spline for <paramref name="unit"/>: SMSG_MONSTER_MOVE to the unit's own client (a player) and to everyone who sees it, at
    /// <paramref name="speed"/> yards per second without the run flag ("only send MOVEMENTFLAG_WALK_MODE, client has strange issues with other move
    /// flags"), then the unit is relocated to the last point at once and faces along the last leg. The server does not step the unit
    /// along the spline (the reference does): observers and the unit's client animate it, the server position is already the destination, so a
    /// hit that lands during the flight is resolved at the destination. A creature's own spline ends first. Returns false when the unit is not in a map.
    /// </summary>
    public static bool LaunchSpline(Unit unit, IReadOnlyList<Vector3> points, float speed)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(points);
        if (unit.Map is not { } map || points.Count == 0 || speed <= 0)
        {
            return false;
        }

        var start = new Vector3(unit.X, unit.Y, unit.Z);
        float length = 0;
        Vector3 previous = start;
        foreach (Vector3 point in points)
        {
            length += Vector3.Distance(previous, point);
            previous = point;
        }

        uint duration = (uint)Math.Max(1.0, Math.Ceiling(length / speed * 1000.0));
        if (unit is Creature creature)
        {
            map.FindUpdater<CreatureMapSystem>()?.StopMoving(creature);
        }

        uint id = unchecked((uint)Interlocked.Increment(ref s_splineId));
        byte[] packet = CreatureMovePackets.BuildPath(unit.Guid, start, id, SplineFacing.None, run: false, duration, points);
        if (unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgMonsterMove, packet);
        }

        map.BroadcastToObservers(unit, WorldOpcode.SmsgMonsterMove, packet);

        Vector3 end = points[^1];
        Vector3 lastLeg = points.Count > 1 ? points[^2] : start;
        float heading = Vector3.Distance(lastLeg, end) > 0.001f ? MathF.Atan2(end.Y - lastLeg.Y, end.X - lastLeg.X) : unit.Orientation;
        if (heading < 0)
        {
            heading += 2 * MathF.PI;
        }

        if (heading >= 2 * MathF.PI)
        {
            heading -= 2 * MathF.PI; // a leg along -x with a rounding error in y comes out as -epsilon + 2 pi, which rounds up to 2 pi
        }

        if (unit is Player mover)
        {
            // vmangos UpdateSplinePosition -> Player::SetPosition (Unit.cpp:10672): the spline moves the player, so a state emote ends. A creature keeps its state.
            mover.ClearEmoteState();
            mover.Locomotion.StartSpline(new CreatureSpline(id, start.X, start.Y, start.Z, end.X, end.Y, end.Z, Run: false, heading, 0L, duration)
            {
                Path = points.Count > 1 ? [.. points] : [],
            });
        }

        unit.Relocate(end.X, end.Y, end.Z, heading, unit.Movement.Time);
        if (unit is Player moved)
        {
            moved.NeedsVisibilityUpdate = true;
        }

        return true;
    }

    /// <summary>The path options of a charge: the unit's own locomotion (<see cref="PathMover.For"/>) and vmangos' 7 yd end tolerance.</summary>
    private static PathOptions ChargeOptions(Unit unit) => new() { Mover = PathMover.For(unit), EndTolerance = PathOptions.VmangosEndTolerance };

    /// <summary>
    /// vmangos Spell::CheckCast SPELL_EFFECT_CHARGE (Spell.cpp:5909-5934, tested only under strict): a caster on the ground (not in water,
    /// within 1.6 yd of the floor) needs a path that is neither <see cref="PathType.NoPath"/> nor <see cref="PathType.Incomplete"/> to the
    /// target's position; the cast fails with SPELL_FAILED_NOPATH otherwise. A charge the water shortcut covers
    /// (<see cref="ChargeSwims"/>) is let through, and <see cref="ChargePath"/> then answers the straight line for it: both read the same
    /// decision, so a cast that passes the check always launches a spline. The path length cap of the reference (:5931) is not modelled.
    /// </summary>
    public static bool ChargeHasNoPath(Unit caster, Unit target)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(target);
        if (caster.Map is not { } map || !ReferenceEquals(map, target.Map))
        {
            return false;
        }

        var start = new Vector3(caster.X, caster.Y, caster.Z);
        var destination = new Vector3(target.X, target.Y, target.Z);
        PathOptions options = ChargeOptions(caster);
        if (ChargeSwims(map, start, destination, options))
        {
            return false;
        }

        float ground = map.Collision.GetHeight(start.X, start.Y, start.Z);
        if (ground <= TerrainTile.InvalidHeight || MathF.Abs(ground - start.Z) >= 1.6f)
        {
            return false;
        }

        PathResult path = map.Collision.FindPath(start, destination, options);
        return (path.Type & (PathType.Incomplete | PathType.NoPath)) != 0;
    }

    /// <summary>
    /// The swimming shortcut of a charge (vmangos PathFinder.cpp:197-201): the caster is in water, or it can swim and the destination is in water.
    /// With no mesh path such a charge goes straight instead of having no path, in the cast check and in <see cref="ChargePath"/> alike.
    /// </summary>
    private static bool ChargeSwims(Map map, Vector3 start, Vector3 probe, PathOptions options)
        => IsInWater(map, start, out _) || (options.Mover is { CanSwim: true } && IsInWater(map, probe, out _));

    /// <summary>
    /// The way a charge goes: the navigation path from the unit to <paramref name="destination"/> when the map has one that reaches it, else
    /// the straight line (<c>generatePath = true</c>, <c>forceDestination = true</c>). Points after the start. A whole path that ends short of
    /// <paramref name="destination"/> (a target a few yards off the mesh) is walked to its end, and then straight to the destination only when
    /// that last segment has line of sight, else the charge stops at the path end (vmangos walks the path, then UpdateForMelee). EMPTY when the
    /// mesh answers <see cref="PathType.NoPath"/> and the swimming shortcut (<see cref="ChargeSwims"/>, probed at <paramref name="target"/> when
    /// given) does not cover the charge: no move, as vmangos <c>ChargeMovementGenerator::Initialize</c> returns on <c>PATHFIND_NOPATH</c>
    /// (PointMovementGenerator.cpp:237-238). A map or tile without mesh data still answers the straight line.
    /// </summary>
    public static IReadOnlyList<Vector3> ChargePath(Unit unit, Vector3 destination, Unit? target = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Map?.Collision is { } collision && unit.Map is { } map)
        {
            var start = new Vector3(unit.X, unit.Y, unit.Z);
            PathOptions options = ChargeOptions(unit);
            PathResult path = collision.FindPath(start, destination, options);
            if ((path.Type & PathType.NoPath) != 0)
            {
                Vector3 probe = target is null ? destination : new Vector3(target.X, target.Y, target.Z);
                return ChargeSwims(map, start, probe, options) ? [destination] : [];
            }

            if (path.HasPath && (path.Type & PathType.Incomplete) == 0)
            {
                bool reaches = Vector3.Distance(path.End, destination) < 0.5f;
                var corners = new List<Vector3>(path.Points.Count);
                for (int i = 1; i < path.Points.Count - (reaches ? 1 : 0); i++)
                {
                    corners.Add(path.Points[i]);
                }

                if (reaches)
                {
                    corners.Add(destination);
                }
                // Both ends are the charger, standing at the path end and at the destination: its eye at both (vmangos has no such check).
                else if (collision.LineOfSight.IsInLineOfSight(map.MapId, path.End + new Vector3(0, 0, unit.CollisionHeight),
                    destination + new Vector3(0, 0, unit.CollisionHeight)))
                {
                    corners.Add(destination);
                }

                if (corners.Count > 0)
                {
                    if (corners.Count >= 2 && collision.MeshPoints is { } mesh)
                    {
                        corners = CreatureSplineSmoothing.SmoothCorners(
                            map.MapId,
                            start,
                            corners,
                            mesh,
                            (_, x, y, z) => SafeFloor(collision, x, y, z),
                            options);
                    }

                    return SubdivideTerrainContour(collision, start, corners);
                }
            }

            return SubdivideTerrainContour(collision, start, [destination]);
        }

        return [destination];
    }

    private static float? SafeFloor(MapCollision collision, float x, float y, float z)
    {
        float terrain = collision.Map.Terrain.GetHeight(x, y, z);
        float h = collision.GetHeight(x, y, z);
        if (terrain > TerrainTile.InvalidHeight && float.IsFinite(terrain)
            && h > terrain + 0.4f && MathF.Abs(terrain - z) <= 1.0f)
        {
            h = terrain;
        }

        return h > TerrainTile.InvalidHeight && float.IsFinite(h) ? h : null;
    }

    /// <summary>
    /// When a charge segment (<c>&gt;= 8</c> yd) crosses uneven ground whose floor height deviates from the straight chord by at least
    /// <c>0.35</c> yd (a hill crest, slope transition, or terrain dip), insert intermediate ground-hugging points so the client's
    /// linear <c>SMSG_MONSTER_MOVE</c> spline follows the terrain contour rather than clipping underground. Flat or unknown terrain
    /// adds zero points.
    /// </summary>
    private static IReadOnlyList<Vector3> SubdivideTerrainContour(MapCollision collision, Vector3 start, IReadOnlyList<Vector3> corners)
    {
        const float MinSegmentYards = 8.0f;
        const float StepYards = 4.0f;
        const float MinVerticalDeviationYards = 0.35f;
        const float MaxWalkableDeltaYards = 1.6f;
        const int MaxTotalPoints = 16;

        List<Vector3>? expanded = null;
        Vector3 prev = start;
        for (int i = 0; i < corners.Count; i++)
        {
            Vector3 next = corners[i];
            float dx = next.X - prev.X;
            float dy = next.Y - prev.Y;
            float dist2D = MathF.Sqrt((dx * dx) + (dy * dy));
            int currentCount = expanded?.Count ?? i;
            int remaining = corners.Count - i;

            if (dist2D >= MinSegmentYards && currentCount + 2 + remaining <= MaxTotalPoints)
            {
                int steps = Math.Clamp((int)MathF.Round(dist2D / StepYards), 2, 5);
                if (currentCount + (steps - 1) + remaining <= MaxTotalPoints
                    && TrySampleContourSegment(collision, prev, next, steps, MinVerticalDeviationYards, MaxWalkableDeltaYards, out List<Vector3>? samples))
                {
                    if (expanded is null)
                    {
                        expanded = new List<Vector3>(corners.Count + samples.Count);
                        for (int k = 0; k < i; k++)
                        {
                            expanded.Add(corners[k]);
                        }
                    }

                    expanded.AddRange(samples);
                }
            }

            expanded?.Add(next);
            prev = next;
        }

        return expanded ?? corners;
    }

    private static bool TrySampleContourSegment(
        MapCollision collision,
        Vector3 from,
        Vector3 to,
        int steps,
        float minDeviation,
        float maxDelta,
        out List<Vector3> samples)
    {
        samples = [];
        float maxDev = 0f;
        for (int s = 1; s < steps; s++)
        {
            float t = (float)s / steps;
            Vector3 lerp = Vector3.Lerp(from, to, t);
            if (SafeFloor(collision, lerp.X, lerp.Y, lerp.Z) is not { } ground)
            {
                return false;
            }

            float dev = MathF.Abs(ground - lerp.Z);
            if (dev > maxDelta)
            {
                return false;
            }

            if (dev > maxDev)
            {
                maxDev = dev;
            }

            samples.Add(new Vector3(lerp.X, lerp.Y, ground));
        }

        return maxDev >= minDeviation;
    }

    /// <summary>
    /// EffectLeapForward: where a blink of <paramref name="distance"/> yards along the unit's facing ends. While falling with no floor in
    /// reach the destination is the point straight ahead, two yards lower, pulled to the water surface or nearby ground and back from a model
    /// hit (the "falling BLINK" case). Otherwise the way is checked in <see cref="LeapStep"/> yards: each step takes the floor (within
    /// <c>4</c> yards of the estimate), refuses an edge between ground and water of more than 2 yards, follows the water surface
    /// (<see cref="LiquidRange"/> below it), refuses a step a model blocks (segment from 1.5 yards above the previous point) and a slope steeper than
    /// <see cref="LeapMaxSlopeDegrees"/>; the blink ends at the last step that passed. <paramref name="angleOffset"/> turns the direction away from
    /// the facing (target B 48 back, 49 left, 50 right).
    /// </summary>
    public static Vector3 LeapDestination(Unit unit, float distance, float angleOffset = 0f)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var previous = new Vector3(unit.X, unit.Y, unit.Z);
        if (unit.Map is not { } map)
        {
            return previous;
        }

        MapCollision collision = map.Collision;
        float orientation = unit.Orientation + angleOffset;
        float cos = MathF.Cos(orientation);
        float sin = MathF.Sin(orientation);

        float groundZ = previous.Z;
        bool hasFloor = TryFloor(collision, previous.X, previous.Y, ref groundZ, OriginFloorRange);

        // Falling case: no floor in reach and the client says it is in the air.
        if (!hasFloor && unit.Movement.HasFlag(MovementFlags.Jumping))
        {
            var ahead = new Vector3(previous.X + (distance * cos), previous.Y + (distance * sin), previous.Z - 2.0f);
            if (IsInWater(map, ahead, out LiquidData liquidAhead))
            {
                if (MathF.Abs(ahead.Z - liquidAhead.Level) < 10.0f)
                {
                    ahead.Z = liquidAhead.Level - LiquidRange;
                }
            }
            else
            {
                float z = ahead.Z;
                if (TryFloor(collision, ahead.X, ahead.Y, ref z, FallingFloorRange))
                {
                    ahead.Z = z;
                }
            }

            if (collision.LineOfSight.TryGetObjectHit(map.MapId, previous + new Vector3(0, 0, 0.5f), ahead, -0.5f, out Vector3 hit))
            {
                ahead = hit;
            }

            return ahead;
        }

        // A unit that jumped and is near the ground but not on it starts from the ground.
        if (MathF.Abs(previous.Z - groundZ) > 0.5f)
        {
            previous.Z = groundZ;
        }

        // Heights known around the unit at all? When not, no step is refused for want of ground ("no data is open").
        bool dataHere = collision.GetHeight(previous.X, previous.Y, previous.Z) > TerrainTile.InvalidHeight;
        bool previousInLiquid = IsInWater(map, previous, out _);

        float maxSlope = LeapMaxSlopeDegrees / 180.0f * MathF.PI;
        float nextZEstimate = 1.0f;
        int numChecks = (int)MathF.Ceiling(MathF.Abs(distance / LeapStep));
        if (numChecks <= 0)
        {
            return previous;
        }

        float deltaX = distance * cos / numChecks;
        float deltaY = distance * sin / numChecks;
        Vector3 next = previous;
        for (int i = 1; i <= numChecks; i++)
        {
            next = new Vector3(previous.X + deltaX, previous.Y + deltaY, previous.Z + nextZEstimate);
            bool inLiquid = false;
            bool liquidTested = false;
            bool onGround = false;
            LiquidData liquid = default;

            float z = next.Z;
            if (TryFloor(collision, next.X, next.Y, ref z, FloorRange))
            {
                next.Z = z;
                onGround = true;
            }
            else if (IsInWater(map, next, out liquid))
            {
                inLiquid = true;
                liquidTested = true;
            }
            else if (dataHere)
            {
                // Not on the ground and not in water: "maybe flying?" The reference stays where it was.
                next = previous;
                break;
            }
            else
            {
                next.Z = previous.Z;
            }

            if (inLiquid || (!liquidTested && IsInWater(map, next, out liquid)))
            {
                if (!previousInLiquid && MathF.Abs(liquid.Level - previous.Z) > 2.0f)
                {
                    // On the edge of the water with a difference a bit too high to continue.
                    next = previous;
                    break;
                }

                next.Z = (liquid.Level - LiquidRange) > next.Z ? previous.Z : liquid.Level - LiquidRange;
                inLiquid = true;
                float ground = next.Z;
                if (TryFloor(collision, next.X, next.Y, ref ground, FloorRange) && next.Z < ground)
                {
                    next.Z = ground;
                    onGround = true;
                }
            }

            var from = new Vector3(previous.X, previous.Y, previous.Z + EyeHeight);
            var to = new Vector3(next.X, next.Y, next.Z + EyeHeight);
            if (collision.LineOfSight.TryGetObjectHit(map.MapId, from, to, -1.0f, out _))
            {
                next = previous;
                break;
            }

            if (onGround && MathF.Atan(MathF.Abs(previous.Z - next.Z) / LeapStep) > maxSlope)
            {
                next = previous;
                break;
            }

            nextZEstimate = (next.Z - previous.Z) / 2.0f;
            previousInLiquid = inLiquid;
            previous = next;
        }

        return next;
    }

    /// <summary>vmangos WorldObject::GetAngle(target) as seen from <paramref name="from"/>: atan2 of the offset, in [0, 2 pi).</summary>
    public static float BearingFromTo(Unit from, Unit to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        float angle = MathF.Atan2(to.Y - from.Y, to.X - from.X);
        return angle < 0 ? angle + (2 * MathF.PI) : angle;
    }

    private static float Radius(Unit unit) => unit.GetFloat(UpdateFields.UnitFieldBoundingradius);

    /// <summary>A point <paramref name="distance"/> from the anchor along <paramref name="angle"/>, on the ground there (the anchor's z when no height is known).</summary>
    private static Vector3 PointAt(Unit anchor, float distance, float angle, MapCollision? collision)
    {
        float x = anchor.X + (distance * MathF.Cos(angle));
        float y = anchor.Y + (distance * MathF.Sin(angle));
        if (!GridDefines.IsValidMapCoord(x) || !GridDefines.IsValidMapCoord(y))
        {
            return new Vector3(anchor.X, anchor.Y, anchor.Z);
        }

        float z = collision is not null && SafeFloor(collision, x, y, anchor.Z) is { } floor
            ? floor
            : TerrainTile.InvalidHeightValue;
        return new Vector3(x, y, z > TerrainTile.InvalidHeight ? z : anchor.Z);
    }

    /// <summary>Map::GetHeightInRange: the floor under (x, y) when it is within <paramref name="range"/> of <paramref name="z"/>, written to <paramref name="z"/>.</summary>
    private static bool TryFloor(MapCollision collision, float x, float y, ref float z, float range)
    {
        float height = collision.GetHeight(x, y, z);
        if (height <= TerrainTile.InvalidHeight || MathF.Abs(z - height) > range)
        {
            return false;
        }

        z = height;
        return true;
    }

    /// <summary>TerrainInfo::IsInWater: in or under any liquid.</summary>
    private static bool IsInWater(Map map, Vector3 position, out LiquidData liquid)
    {
        LiquidStatus status = map.GetLiquidStatus(position.X, position.Y, position.Z, LiquidTypeFlags.AllLiquids, out liquid);
        return (status & (LiquidStatus.InWater | LiquidStatus.UnderWater)) != 0;
    }
}
