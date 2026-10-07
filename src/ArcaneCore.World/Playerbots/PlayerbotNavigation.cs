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
    internal PlayerbotRoute(IReadOnlyList<Vector3> points, float distance)
    {
        Points = points;
        Distance = distance;
    }

    internal IReadOnlyList<Vector3> Points { get; }
    internal float Distance { get; }
    internal int NextPoint { get; set; } = 1;
    internal bool Complete => NextPoint >= Points.Count;
}

internal static class PlayerbotNavigation
{
    internal static bool IsUsablePath(PathResult path, int maxPoints, float maxDistance)
        => path.HasPath && (path.Type & (PathType.NotUsingPath | PathType.DestForced | PathType.FlyPath)) == 0
            && path.Points.Count <= maxPoints
            && path.Points.All(Finite) && float.IsFinite(path.Length)
            && path.Length <= maxDistance;

    internal static bool TryPlan(Player player, Vector3 destination, PlayerbotOptions options,
        out PlayerbotRoute? route)
    {
        route = null;
        if (player.Map is not { } map || !Finite(destination)
            || !Finite(new Vector3(player.X, player.Y, player.Z)))
            return false;

        if (options.AllowedMaps is { Length: > 0 } maps && !maps.Contains(map.MapId))
            return false;

        Vector3 start = new(player.X, player.Y, player.Z);
        PathResult path = map.Collision.FindPath(start, destination,
            new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints), Mover = PathMover.Player,
                ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = false, MaxSearchNodes = 512 });
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

        float distance = 0;
        for (int index = 0; index < path.Points.Count; index++)
        {
            Vector3 point = path.Points[index];
            if (!Finite(point) || InvalidHeight(map.Collision.GetHeight(point.X, point.Y, point.Z)))
                return false;
            if (index > 0)
            {
                Vector3 previous = path.Points[index - 1];
                if (!map.Collision.IsInLineOfSight(previous.X, previous.Y, previous.Z,
                        point.X, point.Y, point.Z))
                    return false;
                distance += Vector3.Distance(previous, point);
            }
        }

        if (!float.IsFinite(distance) || distance > options.MaxRouteYards)
            return false;

        route = new PlayerbotRoute(path.Points.ToArray(), distance);
        return true;
    }

    /// <summary>Spend a bounded movement distance and relay one authoritative heartbeat.</summary>
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
        if (options.AllowedMaps is { Length: > 0 } maps && !maps.Contains(map.MapId)) return false;

        Vector3 current = new(player.X, player.Y, player.Z);
        float speed = MathF.Min(options.MoveSpeed, UnitSpeed.Get(player, MoveType.Run));
        if (!float.IsFinite(speed) || speed <= 0 || (player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping)) != 0)
        {
            PlayerbotMovementControl.Stop(session, player);
            return false;
        }
        if ((player.Movement.Flags & MovementFlags.MaskMoving) == 0)
        {
            float startDistance = speed * Math.Min(elapsedMs, 1000u) / 1000f;
            if (!TryStepOnMap(route, new(player.X, player.Y, player.Z), MathF.Max(startDistance, 0.1f), map,
                    out _, out _))
            {
                PlayerbotMovementControl.Stop(session, player);
                return false;
            }
            MovementInfo start = player.Movement;
            start.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.SplineEnabled);
            start.Flags |= MovementFlags.Forward;
            start.Time = serverTimeMs;
            Vector3 toward = route.Points[route.NextPoint] - new Vector3(player.X, player.Y, player.Z);
            if (toward.LengthSquared() > 0.001f)
                start.Orientation = MathF.Atan2(toward.Y, toward.X);
            start.CorrectData();
            var startWriter = new PacketWriter(64);
            start.Write(startWriter);
            bool sent = session.TryManagedAction(WorldOpcode.MsgMoveStartForward, startWriter.ToArray());
            if (sent)
                PlayerbotMovementControl.Track(session, player, route, new(player.X, player.Y, player.Z), route.NextPoint, speed, serverTimeMs);
            return sent;
        }
        float distance = speed * Math.Min(elapsedMs, 1000u) / 1000f;
        if (!TryStepOnMap(route, current, distance, map, out Vector3 next, out int nextPoint))
        {
            PlayerbotMovementControl.Stop(session, player);
            return false;
        }

        MovementInfo movement = player.Movement;
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.SplineEnabled);
        movement.Flags |= MovementFlags.Forward;
        movement.Time = serverTimeMs;
        movement.X = next.X;
        movement.Y = next.Y;
        movement.Z = next.Z;
        movement.Orientation = MathF.Atan2(next.Y - current.Y, next.X - current.X);
        movement.CorrectData();
        var writer = new PacketWriter(64);
        movement.Write(writer);
        if (!session.TryManagedAction(WorldOpcode.MsgMoveHeartbeat, writer.ToArray()))
            return false;
        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), next) > 0.5f) return false;
        route.NextPoint = nextPoint;
        PlayerbotMovementControl.Track(session, player, route, next, route.NextPoint, speed, serverTimeMs);
        if (route.Complete)
            PlayerbotMovementControl.Stop(session, player);
        return true;
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
