using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>One bounded, collision-backed route owned by a server-managed player.</summary>
internal sealed class PlayerbotRoute
{
    internal PlayerbotRoute(IReadOnlyList<Vector3> points, float distance, bool navigated = false)
    {
        Points = points;
        Distance = distance;
        Navigated = navigated;
    }

    internal IReadOnlyList<Vector3> Points { get; }
    internal float Distance { get; }

    /// <summary>
    /// The corners came from the map's navigation mesh: every leg between them is walkable by construction (vmangos
    /// <c>PathFinder</c> moves units along such corners without further checks). The motion still snaps each position to the
    /// floor, but it does not test a straight line of sight between two positions, which across a corner cuts through the very
    /// obstacle the mesh goes around (a pillar, a counter, a doorway's edge). Routes stepped over the terrain without a mesh
    /// (<see cref="PlayerbotNavigation.TryTerrainRoute"/>) carry no such proof and keep the line-of-sight check.
    /// </summary>
    internal bool Navigated { get; }
    internal int NextPoint { get; set; } = 1;
    internal bool Complete => NextPoint >= Points.Count;
}

internal static class PlayerbotNavigation
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, PlayerbotRisk> Guards = [];

    /// <summary>
    /// The risk whose hazards (<see cref="PlayerbotHazards"/>) every route of this bot must keep out of (null: none). The brain sets
    /// it each think; a party bot follows its master wherever he goes and has none.
    /// </summary>
    internal static void Guard(Player player, PlayerbotRisk? risk)
    {
        if (risk is null) Guards.Remove(player);
        else Guards.AddOrUpdate(player, risk);
    }

    /// <summary>The detour offsets tried past a hazard on the way, on either side (yards beyond its radius).</summary>
    private static readonly float[] HazardDetourYards = [5f, 15f];

    internal static bool IsUsablePath(PathResult path, int maxPoints, float maxDistance)
        => path.HasPath && (path.Type & (PathType.NotUsingPath | PathType.DestForced | PathType.FlyPath)) == 0
            && path.Points.Count <= maxPoints
            && path.Points.All(Finite) && float.IsFinite(path.Length)
            && path.Length <= maxDistance;

    internal static bool TryPlan(Player player, Vector3 destination, PlayerbotOptions options,
        out PlayerbotRoute? route)
        => Plan(player, destination, destination, options, partial: false, out route);

    /// <summary>Closer than this to its goal, a partial route ends near enough to count (<see cref="TryPlanToward"/>).</summary>
    internal const float PartialProgressYards = 2f;

    /// <summary>
    /// A route towards <paramref name="goal"/>, which may lie beyond one route: the goal itself when it is within one route's
    /// reach (<see cref="PlayerbotOptions.MaxRouteYards"/>, and <see cref="PlayerbotOptions.MaxPathPoints"/> yards for the terrain
    /// stepper), else the point that far along the straight line to it. When the navigation mesh does not connect that point, the
    /// route goes to the walkable point nearest to it (vmangos <c>PathFinder</c> with <c>PATHFIND_INCOMPLETE</c>), taken when it ends
    /// at least <see cref="PartialProgressYards"/> closer to the goal than the bot stands. One path query, as <see cref="TryPlan"/>.
    /// <para>
    /// The straight-line point on its own is often not walkable in the mountains, and the exact search then fails although the
    /// way is open: on the real Dun Morogh terrain the point 126 yards towards Mirthblade's body (359 yards away) has no exact
    /// path, while the mesh reaches a point 4 yards from it. The ghost was judged stalled after 10 seconds and took the spirit
    /// healer, and a living bot with a far quest giver stood still for good.
    /// </para>
    /// </summary>
    internal static bool TryPlanToward(Player player, Vector3 goal, PlayerbotOptions options, out PlayerbotRoute? route)
    {
        route = null;
        Vector3 origin = new(player.X, player.Y, player.Z);
        if (!Finite(goal) || !Finite(origin)) return false;
        float distance = Vector3.Distance(origin, goal);
        float chunk = MathF.Min(options.MaxRouteYards * 0.9f, Math.Max(1, options.MaxPathPoints - 2));
        Vector3 destination = distance > chunk ? origin + ((goal - origin) * (chunk / distance)) : goal;
        return Plan(player, destination, goal, options, partial: true, out route);
    }

    /// <summary>
    /// One path query from where the bot is now to <paramref name="destination"/>; with <paramref name="partial"/> a route that
    /// stops short of it on the mesh is accepted when it closes on <paramref name="goal"/>.
    /// </summary>
    private static bool Plan(Player player, Vector3 destination, Vector3 goal, PlayerbotOptions options, bool partial,
        out PlayerbotRoute? route)
    {
        if (!PlanDirect(player, destination, goal, options, partial, out route) || route is null) return false;
        if (!Guards.TryGetValue(player, out PlayerbotRisk? risk) || risk.Blocking(player, route.Points) is not { } hazard) return true;

        // The way passes through a hazard: walk round it (by a corner before it and one past it, on either side), or not at all.
        PlayerbotRoute direct = route;
        route = null;
        Vector3 start = direct.Points[0], end = direct.Points[^1];
        Vector3 flat = new(end.X - start.X, end.Y - start.Y, 0);
        if (flat.Length() < 1f || player.Map is not { } map) return false;
        Vector3 along = Vector3.Normalize(flat);
        Vector3 side = new(-along.Y, along.X, 0);
        foreach (float extra in HazardDetourYards)
            foreach (float sign in (float[])[1f, -1f])
            {
                // Round the hazard by its side: a corner before it and a corner past it, each its radius plus the margin off.
                float off = hazard.Radius + extra;
                Vector3 before = hazard.At - (along * off) + (side * sign * off);
                Vector3 past = hazard.At + (along * off) + (side * sign * off);
                if (!PlanDirect(player, before, before, options, partial: false, out PlayerbotRoute? first) || first is null) continue;
                if (Leg(map, first.Points[^1], past, options) is not { } middle || Leg(map, middle[^1], end, options) is not { } last) continue;
                List<Vector3> points = [.. first.Points, .. middle.Skip(1), .. last.Skip(1)];
                float length = 0;
                for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
                if (length > options.MaxRouteYards || points.Count > options.MaxPathPoints * 3) continue;
                var detour = new PlayerbotRoute(points, length, first.Navigated);
                if (risk.Blocking(player, detour.Points) is not null) continue;
                route = detour;
                return true;
            }

        return false;
    }

    /// <summary>A second leg from <paramref name="from"/> (the navigation mesh's, else a stepped terrain line), or null.</summary>
    private static IReadOnlyList<Vector3>? Leg(Map map, Vector3 from, Vector3 to, PlayerbotOptions options)
    {
        PathResult path = map.Collision.FindPath(from, to, new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints),
            Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, MaxSearchNodes = PathOptions.DefaultMaxSearchNodes });
        if ((path.Type & PathType.NotUsingPath) != 0)
            return TryTerrainRoute(from, to, options, (x, y, z) => map.Collision.GetHeight(x, y, z),
                (a, b) => map.Collision.IsInLineOfSight(a.X, a.Y, a.Z + 2, b.X, b.Y, b.Z + 2), out PlayerbotRoute? terrain) ? terrain!.Points : null;
        return IsUsablePath(path, options.MaxPathPoints, options.MaxRouteYards) && (path.Type & PathType.Incomplete) == 0 ? path.Points : null;
    }

    /// <summary>The route without the hazard check (<see cref="Plan"/>).</summary>
    private static bool PlanDirect(Player player, Vector3 destination, Vector3 goal, PlayerbotOptions options, bool partial,
        out PlayerbotRoute? route)
    {
        route = null;
        if (player.Map is not { } map || !Finite(destination)
            || !Finite(new Vector3(player.X, player.Y, player.Z)))
            return false;

        // AllowedMaps limits open-world travel; the dungeon or raid instance the bot is in is always walkable (PlayerbotMapPolicy).
        if (!PlayerbotMapPolicy.MayMoveOn(player, options))
            return false;

        // A destination given up after a loop is refused for a while, so the goal picks another one.
        if (PlayerbotMotion.IsBlacklisted(player, destination))
            return false;

        // A moving bot plans from where it is now, not from its last heartbeat.
        Vector3 start = PlayerbotMotion.CurrentPosition(player);
        // The search budget is vmangos' own (navMeshQuery->init(navMesh, 2048), MoveMap.cpp:350). The old 512 polygons ran out on the
        // real Dun Morogh mesh well inside the route bound: 200 yards from Coldridge Valley to the Rockjaw Raiders (quest 179)
        // answered no path with 512 and an 18-corner path with 2048, so the bot stood still with that goal for good.
        PathResult path = map.Collision.FindPath(start, destination,
            new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints), Mover = PathMover.Player,
                ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = partial, MaxSearchNodes = PathOptions.DefaultMaxSearchNodes });
        // The no-mmap answer carries no walkability proof. Validate each short terrain step
        // before using that route; absent heights, steep terrain and known model obstructions refuse it.
        if ((path.Type & PathType.NotUsingPath) != 0)
        {
            return TryTerrainRoute(start, destination, options,
                (x, y, z) => map.Collision.GetHeight(x, y, z),
                (a, b) => map.Collision.IsInLineOfSight(a.X, a.Y, a.Z + 2, b.X, b.Y, b.Z + 2), out route);
        }
        if (!IsUsablePath(path, options.MaxPathPoints, options.MaxRouteYards))
            return false;
        // A partial answer (the corridor did not reach the destination) must at least close on the goal, and must not end at a
        // place given up after a loop. An end projected from above or below the destination stays at its spot and is exact.
        if (partial && (path.Type & PathType.Incomplete) != 0 && Vector2.Distance(Flat(path.End), Flat(destination)) > 1f
            && (Vector2.Distance(Flat(path.End), Flat(goal)) > Vector2.Distance(Flat(start), Flat(goal)) - PartialProgressYards
                || PlayerbotMotion.IsBlacklisted(player, path.End)))
            return false;

        // The corners are the navigation mesh's, which already proves each leg walkable; they are not re-tested against the
        // collision data. A line of sight at their own height hits the floor model itself inside any building (the Deathknell
        // crypt, the Goldshire inn), and the floor probe finds nothing at some spots the mesh covers (on the crypt's stairs, at
        // 1645.4, 1665.9, 132.6, where Graveweaver stopped): either refused every route from or through such a place, and a bot
        // that started or stood there never moved again. The motion snaps to a floor where it finds one (PlayerbotMotion).
        float distance = 0;
        for (int index = 0; index < path.Points.Count; index++)
        {
            Vector3 point = path.Points[index];
            if (!Finite(point)) return false;
            if (index > 0) distance += Vector3.Distance(path.Points[index - 1], point);
        }

        if (!float.IsFinite(distance) || distance > options.MaxRouteYards)
            return false;

        route = new PlayerbotRoute(path.Points.ToArray(), distance, navigated: true);
        return true;
    }

    private static Vector2 Flat(Vector3 value) => new(value.X, value.Y);

    /// <summary>
    /// Follow <paramref name="route"/> this think (<see cref="PlayerbotMotion.Follow"/>): start, switch or keep moving.
    /// The motion itself advances every world tick, so the distance does not depend on <paramref name="elapsedMs"/>;
    /// <paramref name="serverTimeMs"/> is the current server time. False when the route is finished or cannot be
    /// followed (the caller drops it).
    /// </summary>
    internal static bool TryAdvance(WorldSession session, PlayerbotRoute route, PlayerbotOptions options,
        uint elapsedMs, uint serverTimeMs)
    {
        if (session.Player is not { } player)
            return false;
        if (player.Map is not { } map || options.MoveSpeed <= 0 || elapsedMs == 0)
            return false;
        if (route.Complete)
        {
            PlayerbotMovementControl.Stop(session, player);
            return false;
        }
        if (player.StandState != StandState.Stand)
            return session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u));
        if (!PlayerbotMapPolicy.MayMoveOn(player, options)) return false;
        return PlayerbotMotion.Follow(session, player, route, options, serverTimeMs);
    }

    /// <summary>Propose a step without changing the route; the caller commits after normal admission.</summary>
    internal static bool TryStep(PlayerbotRoute route, Vector3 start, float budget,
        Func<Vector3, Vector3?> validate, out Vector3 next, out int nextPoint)
    {
        next = start;
        nextPoint = route.NextPoint;
        if (!Finite(start) || !float.IsFinite(budget) || budget <= 0 || route.Complete
            || nextPoint < 1) return false;
        float remainingBudget = budget;
        int scanned = 0;
        while (nextPoint < route.Points.Count && remainingBudget > 0.001f && ++scanned <= 64)
        {
            Vector3 destination = route.Points[nextPoint];
            float remaining = Vector3.Distance(next, destination);
            if (!Finite(destination) || !float.IsFinite(remaining)) break;
            if (remaining <= 0.05f) { nextPoint++; continue; }
            bool reachesPoint = remaining <= remainingBudget;
            Vector3 candidate = reachesPoint ? destination
                : next + Vector3.Normalize(destination - next) * remainingBudget;
            Vector3? checkedPoint = validate(candidate);
            if (checkedPoint is not { } accepted || !Finite(accepted)) break;
            float spent = Vector3.Distance(next, accepted);
            if (!float.IsFinite(spent) || spent > remainingBudget + 0.05f) break;
            next = accepted;
            remainingBudget = MathF.Max(0, remainingBudget - spent);
            if (reachesPoint && Vector3.Distance(accepted, destination) <= 0.1f) nextPoint++;
            else break;
        }
        return Vector3.Distance(start, next) > 0.05f;
    }

    internal static bool TryStepOnMap(PlayerbotRoute route, Vector3 start, float budget, Map map,
        out Vector3 next, out int nextPoint)
        => TryStep(route, start, budget, candidate =>
        {
            float floor = map.Collision.GetHeight(candidate.X, candidate.Y, candidate.Z);
            if (!Finite(candidate) || InvalidHeight(floor) || MathF.Abs(floor - candidate.Z) > 2f
                || !map.Collision.IsInLineOfSight(start.X, start.Y, start.Z + 2,
                    candidate.X, candidate.Y, floor + 2)) return null;
            return new Vector3(candidate.X, candidate.Y, floor + 0.05f);
        }, out next, out nextPoint);

    internal static bool TryTerrainRoute(Vector3 start, Vector3 destination, PlayerbotOptions options,
        Func<float, float, float, float> getHeight, Func<Vector3, Vector3, bool> clear,
        out PlayerbotRoute? route)
    {
        route = null;
        if (!Finite(start) || !Finite(destination)) return false;
        float horizontal = Vector2.Distance(new Vector2(start.X, start.Y), new Vector2(destination.X, destination.Y));
        if (!float.IsFinite(horizontal) || horizontal < 0.05f || horizontal > options.MaxRouteYards) return false;
        int steps = (int)MathF.Ceiling(horizontal); // at most one yard between floor probes
        if (steps + 1 > options.MaxPathPoints) return false;
        var points = new List<Vector3>(steps + 1) { start };
        float length = 0;
        for (int i = 1; i <= steps; i++)
        {
            Vector3 previous = points[^1];
            Vector3 next = Vector3.Lerp(start, destination, (float)i / steps);
            float floor = getHeight(next.X, next.Y, previous.Z);
            if (InvalidHeight(floor) || MathF.Abs(floor - previous.Z) > 1f) return false;
            next.Z = floor + 0.05f;
            if (!clear(previous, next)) return false;
            length += Vector3.Distance(previous, next);
            if (!float.IsFinite(length) || length > options.MaxRouteYards) return false;
            points.Add(next);
        }
        route = new PlayerbotRoute(points, length);
        return true;
    }

    internal static byte[] GuidPayload(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    internal static byte[] UseItemPayload(byte bag, byte slot)
    {
        var writer = new PacketWriter(16);
        writer.WriteByte(bag);
        writer.WriteByte(slot);
        writer.WriteByte(0);
        ArcaneCore.Game.Spells.SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool InvalidHeight(float height)
        => !float.IsFinite(height) || height == TerrainTile.InvalidHeight
            || height == TerrainTile.InvalidHeightValue;
}
