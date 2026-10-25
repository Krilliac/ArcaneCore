using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game;
using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Copies one map's state into a <see cref="FeedFrame"/> (docs/areas/map-feed.md, "Capture"). World thread only, and only from the
/// post-barrier <see cref="WorldRuntime.Updated"/> event: every map update of the tick has finished by then, so nothing is read while it
/// is written (the parallel-maps caveat). It reads the one subscribed map and stores values, never references into the world (names are
/// immutable strings), so the frame can be serialized on any thread afterwards. Steady state allocates nothing but the enumerators of
/// the three collections the map exposes as interfaces (one small object each per capture).
/// </summary>
internal static class MapFeedCapture
{
    /// <summary>
    /// Fill <paramref name="frame"/> (cleared) with <paramref name="map"/>: the loaded grids, the totals, and entities up to
    /// <paramref name="maxEntities"/>. Order of priority: players and bots, then creatures that are moving or in an active motion, then
    /// the other creatures, then game objects. <paramref name="botGuids"/> are the players that are managed bots (null: none);
    /// <paramref name="routes"/> is the bot route seam (null: no playerbot pack attached).
    /// </summary>
    public static void Fill(
        FeedFrame frame,
        Map map,
        WorldRuntime world,
        in FeedView view,
        int maxEntities,
        HashSet<ulong>? botGuids,
        IBotRouteSource? routes,
        uint diffMs,
        Action<Exception>? onRouteFault = null)
    {
        CreatureMapSystem? creatureSystem = map.FindUpdater<CreatureMapSystem>();
        GameObjectMapSystem? objectSystem = map.FindUpdater<GameObjectMapSystem>();
        frame.MapId = map.MapId;
        frame.InstanceId = map.InstanceId;
        frame.View = view;
        frame.ClockMs = creatureSystem?.ClockMs ?? (long)world.Uptime.TotalMilliseconds;

        IReadOnlyList<Grid> loaded = map.Grids.LoadedGrids;
        for (int i = 0; i < loaded.Count; i++)
        {
            Grid grid = loaded[i];
            if (grid.ObjectDataLoaded)
            {
                frame.AddGrid(grid.Coord.X, grid.Coord.Y);
            }
        }

        ref FeedCounters counters = ref frame.Counters;
        counters.DiffMs = diffMs;
        counters.Online = world.OnlinePlayerCount;
        counters.Creatures = creatureSystem?.Creatures.Count ?? 0;
        counters.GameObjects = objectSystem?.GameObjects.Count ?? 0;
        if (creatureSystem is not null)
        {
            counters.DeferredPaths = creatureSystem.DeferredPathSearches;
            counters.DirectFallbackPaths = creatureSystem.DirectFallbackPaths;
            counters.NoPathPaths = creatureSystem.NoPathPaths;
            counters.HomePathTeleports = creatureSystem.HomePathTeleports;
        }

        FillPlayers(frame, map, view, maxEntities, botGuids, routes, onRouteFault);
        if (creatureSystem is not null && (view.Kinds & FeedKinds.Creature) != 0)
        {
            FillCreatures(frame, creatureSystem, view, maxEntities);
        }

        if (objectSystem is not null && (view.Kinds & FeedKinds.GameObject) != 0)
        {
            FillGameObjects(frame, objectSystem, maxEntities);
        }
    }

    private static void FillPlayers(
        FeedFrame frame,
        Map map,
        in FeedView view,
        int maxEntities,
        HashSet<ulong>? botGuids,
        IBotRouteSource? routes,
        Action<Exception>? onRouteFault)
    {
        foreach (Player player in map.Players)
        {
            bool isBot = botGuids is not null && botGuids.Contains(player.Guid.Value);
            if (isBot)
            {
                frame.Counters.Bots++;
            }
            else
            {
                frame.Counters.Players++;
            }

            if ((view.Kinds & (isBot ? FeedKinds.Bot : FeedKinds.Player)) == 0)
            {
                continue;
            }

            if (frame.EntityCount >= maxEntities)
            {
                frame.Counters.Truncated++;
                continue;
            }

            if (!AreFinite(player.X, player.Y, player.Z))
            {
                frame.Counters.NonFinite++;
                continue;
            }

            int index = frame.AddEntity();
            ref FeedEntity entity = ref frame.Entities[index];
            entity.Guid = player.Guid.Value;
            entity.Kind = isBot ? FeedEntityKind.Bot : FeedEntityKind.Player;
            entity.Name = player.Name;
            entity.Race = (byte)player.Race;
            entity.Class = (byte)player.Class;
            Place(ref entity, player);
            SetHealth(ref entity, player);
            SetTarget(ref entity, player.Target.Value);
            if (isBot && view.Paths && routes is not null)
            {
                AddRoute(frame, index, routes, player, onRouteFault);
            }
        }
    }

    private static void FillCreatures(FeedFrame frame, CreatureMapSystem system, in FeedView view, int maxEntities)
    {
        IReadOnlyCollection<Creature> creatures = system.Creatures;
        bool sorted = creatures.Count > maxEntities - frame.EntityCount;
        if (!sorted)
        {
            foreach (Creature creature in creatures)
            {
                AddCreature(frame, creature, view.Paths, system.ClockMs, maxEntities);
            }

            return;
        }

        foreach (Creature creature in creatures)
        {
            if (IsActive(creature))
            {
                AddCreature(frame, creature, view.Paths, system.ClockMs, maxEntities);
            }
        }

        foreach (Creature creature in creatures)
        {
            if (!IsActive(creature))
            {
                AddCreature(frame, creature, view.Paths, system.ClockMs, maxEntities);
            }
        }
    }

    /// <summary>Whether a creature is walking or in a motion that will make it walk (chase, flee, follow, home, point, confused).</summary>
    private static bool IsActive(Creature creature)
        => creature.Spline is not null
           || creature.Motion.CurrentType is not (MovementGeneratorType.Idle or MovementGeneratorType.Random
               or MovementGeneratorType.Waypoint or MovementGeneratorType.Distract);

    private static void AddCreature(FeedFrame frame, Creature creature, bool paths, long clockMs, int maxEntities)
    {
        if (frame.EntityCount >= maxEntities)
        {
            frame.Counters.Truncated++;
            return;
        }

        if (!AreFinite(creature.X, creature.Y, creature.Z))
        {
            frame.Counters.NonFinite++;
            return;
        }

        int index = frame.AddEntity();
        ref FeedEntity entity = ref frame.Entities[index];
        entity.Guid = creature.Guid.Value;
        entity.Kind = FeedEntityKind.Creature;
        entity.Entry = creature.Entry;
        entity.Name = creature.Template.Name;
        Place(ref entity, creature);
        SetHealth(ref entity, creature);
        entity.Motion = (byte)creature.Motion.CurrentType;
        Unit? chased = creature.Motion.TargetedUnit;
        SetTarget(ref entity, chased is not null ? chased.Guid.Value : creature.Target.Value);

        CreatureHome home = creature.Home;
        if (AreFinite(home.X, home.Y, home.Z))
        {
            entity.HomeX = home.X;
            entity.HomeY = home.Y;
            entity.HomeZ = home.Z;
            entity.Parts |= FeedEntityParts.Home;
        }

        if (paths && creature.Spline is { } spline)
        {
            AddSpline(frame, index, spline, clockMs);
        }
    }

    private static void FillGameObjects(FeedFrame frame, GameObjectMapSystem system, int maxEntities)
    {
        foreach (GameObject gameObject in system.GameObjects)
        {
            if (frame.EntityCount >= maxEntities)
            {
                frame.Counters.Truncated++;
                continue;
            }

            if (!AreFinite(gameObject.X, gameObject.Y, gameObject.Z))
            {
                frame.Counters.NonFinite++;
                continue;
            }

            int index = frame.AddEntity();
            ref FeedEntity entity = ref frame.Entities[index];
            entity.Guid = gameObject.Guid.Value;
            entity.Kind = FeedEntityKind.GameObject;
            entity.Entry = gameObject.Entry;
            entity.Name = gameObject.Template.Name;
            Place(ref entity, gameObject);
        }
    }

    private static void Place(ref FeedEntity entity, WorldObject source)
    {
        entity.X = source.X;
        entity.Y = source.Y;
        entity.Z = source.Z;
        entity.Orientation = float.IsFinite(source.Orientation) ? source.Orientation : 0f;
    }

    private static void SetHealth(ref FeedEntity entity, Unit unit)
    {
        entity.HealthPercent = unit.HealthPercent;
        entity.InCombat = (unit.UnitFlags & UnitFlags.InCombat) != 0; // the flag itself: Unit.Combat would allocate its UnitCombat
        entity.DisplayId = unit.DisplayId;
        entity.Parts |= FeedEntityParts.Health;
    }

    private static void SetTarget(ref FeedEntity entity, ulong target)
    {
        if (target != 0)
        {
            entity.Target = target;
            entity.Parts |= FeedEntityParts.Target;
        }
    }

    /// <summary>
    /// The creature's current spline: its start, its points (cut at <see cref="MapFeedProtocol.MaxPathPoints"/>, flagged), and the
    /// clock it started on, moved forward by the time the creature was held (a skipped inactive tick), so that
    /// <c>clockMs - t0</c> is the elapsed time the server itself would use.
    /// </summary>
    private static void AddSpline(FeedFrame frame, int index, CreatureSpline spline, long clockMs)
    {
        if (!AreFinite(spline.StartX, spline.StartY, spline.StartZ))
        {
            frame.Counters.NonFinite++;
            return;
        }

        IReadOnlyList<Vector3> path = spline.Path;
        int total = path.Count == 0 ? 1 : path.Count;
        int room = MapFeedProtocol.MaxFramePoints - frame.PointCount;
        if (room <= 0)
        {
            frame.Counters.PathsOmitted++;
            return;
        }

        int copy = Math.Min(total, Math.Min(room, MapFeedProtocol.MaxPathPoints));
        int offset = frame.PointCount;
        Span<Vector3> span = frame.ReservePoints(copy);
        if (path.Count == 0)
        {
            span[0] = new Vector3(spline.EndX, spline.EndY, spline.EndZ);
        }
        else if (path is Vector3[] array)
        {
            array.AsSpan(0, copy).CopyTo(span);
        }
        else
        {
            for (int i = 0; i < copy; i++)
            {
                span[i] = path[i];
            }
        }

        for (int i = 0; i < copy; i++)
        {
            if (!AreFinite(span[i].X, span[i].Y, span[i].Z))
            {
                frame.Counters.NonFinite++;
                return;
            }
        }

        frame.CommitPoints(copy);
        ref FeedEntity entity = ref frame.Entities[index];
        entity.SplineId = spline.Id;
        entity.SplineStartX = spline.StartX;
        entity.SplineStartY = spline.StartY;
        entity.SplineStartZ = spline.StartZ;
        entity.SplineT0 = clockMs - spline.ElapsedMs(clockMs);
        entity.SplineDuration = spline.DurationMs;
        entity.SplineRun = spline.Run;
        entity.SplineOffset = offset;
        entity.SplineCount = copy;
        entity.Parts |= FeedEntityParts.Spline;
        if (copy < total)
        {
            entity.Parts |= FeedEntityParts.SplineCut;
        }
    }

    /// <summary>
    /// A bot's current route from the playerbot pack's seam. The seam is the pack's code: a failure in it loses this one route (counted
    /// and rate-limited at Warning), never the frame.
    /// </summary>
    private static void AddRoute(FeedFrame frame, int index, IBotRouteSource routes, Player bot, Action<Exception>? onRouteFault)
    {
        int room = MapFeedProtocol.MaxFramePoints - frame.PointCount;
        if (room <= 0)
        {
            frame.Counters.PathsOmitted++;
            return;
        }

        int capacity = Math.Min(room, MapFeedProtocol.MaxPathPoints);
        Span<Vector3> span = frame.ReservePoints(capacity);
        int total;
        BotRouteInfo info;
        try
        {
            total = routes.CopyRoute(bot, span, out info);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.map-feed", ex);
            frame.Counters.PathsOmitted++;
            onRouteFault?.Invoke(ex);
            return;
        }

        if (total <= 0)
        {
            return;
        }

        int copy = Math.Min(total, capacity);
        for (int i = 0; i < copy; i++)
        {
            if (!AreFinite(span[i].X, span[i].Y, span[i].Z))
            {
                frame.Counters.NonFinite++;
                return;
            }
        }

        int offset = frame.PointCount;
        frame.CommitPoints(copy);
        ref FeedEntity entity = ref frame.Entities[index];
        entity.RouteOffset = offset;
        entity.RouteCount = copy;
        entity.RouteNext = Math.Clamp(info.Next, 0, copy);
        entity.RouteFlags = info.Flags;
        entity.Parts |= FeedEntityParts.Route;
        if (copy < total)
        {
            entity.Parts |= FeedEntityParts.RouteCut;
        }
    }

    private static bool AreFinite(float x, float y, float z) => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
}
