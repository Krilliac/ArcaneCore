using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using GridCoord = ArcaneCore.Game.Maps.Grid.GridCoord;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.DebugDraw;

/// <summary>
/// <c>.debug vis</c>: draw what the server computes (line of sight through the vmaps, navmesh paths, creature waypoints, the cell grid,
/// floor heights, ranges, spawn points) as markers only the invoking GM sees (docs/areas/debug-draw.md). Ported from the fork's
/// <c>.debug vis cells|los|path|collision|height|clear</c> and <c>.debug visual</c> (Krilliac/server-Zero feature/debug-visualizers,
/// DebugVisCommands.cpp), but the markers are client-only objects instead of summoned world objects, so other players never see them and
/// no database template pool is needed; every marker's details are printed to chat and again when the marker is right-clicked.
/// </summary>
public sealed class DebugDrawCommands : ICommandGroup
{
    /// <summary>Markers nearer than this to the invoker are left out (they would stand inside the character).</summary>
    public const float SkipNearInvoker = 3.0f;

    /// <summary>Largest <c>.debug vis cells</c> radius: (2·5+1)² = 121 corners.</summary>
    public const int MaxCellRadius = 5;

    /// <summary>Most dots one line (LoS, collision ray) gets.</summary>
    public const int MaxLinePoints = 60;

    /// <summary>Most fill dots between path corners or waypoints.</summary>
    public const int MaxFillPoints = 120;

    /// <summary>Most waypoint nodes drawn.</summary>
    public const int MaxWaypointNodes = 100;

    /// <summary>Most spawn points drawn.</summary>
    public const int MaxSpawns = 50;

    /// <summary>Points on a range circle.</summary>
    public const int RingPoints = 36;

    /// <summary>Longest collision ray and widest spawn/range radius (yards; one grid).</summary>
    public const float MaxDistance = GridDefines.SizeOfGrids;

    /// <summary>Chat lines a command lists before it says how many it left out (every marker still prints its line when clicked).</summary>
    public const int MaxListedLines = 12;

    private const string Prefix = "DebugDraw: ";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("debug", AccountSecurity.GameMaster, "Syntax: .debug $subcommand\nType .debug to see the list of possible subcommands or .help debug $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("vis", AccountSecurity.GameMaster, "Syntax: .debug vis $subcommand\nDraw server data as markers only you can see (they go after a while, with .debug vis clear, at logout or on a map change). Right-click a marker to print its details.", Children:
            [
                new ChatCommand("los", AccountSecurity.GameMaster, "Syntax: .debug vis los\nDraw the line of sight from you to the selected unit through the vmap collision (green clear; red up to the hit, a reticle at the hit, small red dots for the hidden rest).", Los, RetailLevel: 3),
                new ChatCommand("path", AccountSecurity.GameMaster, "Syntax: .debug vis path\nDraw the path the pathfinder (navmesh) finds from you to the selected unit: blue corners and dots, red corners when it is incomplete, missing or a straight line.", Path, RetailLevel: 3),
                new ChatCommand("waypoints", AccountSecurity.GameMaster, "Syntax: .debug vis waypoints\nDraw the waypoint path of the selected creature (creature_movement, else creature_movement_template), with its nodes listed.", Waypoints, RetailLevel: 3),
                new ChatCommand("cells", AccountSecurity.GameMaster, "Syntax: .debug vis cells [#radius]\nMark the map cell corners (33.3 yards) within #radius cells (default 2, at most 5) on the floor; a grid corner (533.3 yards) has a red flag.", Cells, RetailLevel: 3),
                new ChatCommand("collision", AccountSecurity.GameMaster, "Syntax: .debug vis collision [#yards]\nCast a ray straight ahead at eye height (default 40 yards) and mark where the vmap collision stops it.", Collision, RetailLevel: 3),
                new ChatCommand("height", AccountSecurity.GameMaster, "Syntax: .debug vis height\nMark the floor under you and print the terrain, model, water and floor heights, zone, area and indoor state.", Height, RetailLevel: 3),
                new ChatCommand("range", AccountSecurity.GameMaster, "Syntax: .debug vis range [#yards]\nMark a circle of #yards around you on the floor (default the visibility distance, 100 yards).", Range, RetailLevel: 3),
                new ChatCommand("spawns", AccountSecurity.GameMaster, "Syntax: .debug vis spawns [#yards]\nMark the spawn points of the creatures spawned within #yards (default 40), nearest first.", Spawns, RetailLevel: 3),
                new ChatCommand("kit", AccountSecurity.GameMaster, "Syntax: .debug vis kit #kitid\nPlay a SpellVisualKit.dbc visual on yourself, seen only by you (the id is not checked).", Kit, RetailLevel: 3),
                new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .debug vis list\nList your drawings with their marker counts and the seconds they have left.", List, RetailLevel: 3),
                new ChatCommand("clear", AccountSecurity.GameMaster, "Syntax: .debug vis clear\nRemove all your markers now.", Clear, RetailLevel: 3),
            ], RetailLevel: 3),
        ], RetailLevel: 3),
    ];

    private static DebugDrawFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<DebugDrawFeature>();

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string P(Vector3 v) => F($"({v.X:F1}, {v.Y:F1}, {v.Z:F1})");

    public const string SelectUnit = Prefix + "select a creature or player first.";

    public const string SelectCreature = Prefix + "select a creature first.";

    public const string NotInMap = Prefix + "you are not in a map.";

    public static string Cleared(int count) => F($"{Prefix}removed {count} marker(s).");

    /// <summary>The summary line after a drawing: markers sent, dropped by the cap, older ones removed, lifetime.</summary>
    public static string Drawn(DebugDrawResult result, int lifetimeSeconds)
    {
        var text = new StringBuilder(F($"{Prefix}{result.Placed} marker(s), only you see them; they go in {lifetimeSeconds} s or with .debug vis clear."));
        if (result.Dropped > 0)
        {
            text.Append(F($" {result.Dropped} left out (marker limit)."));
        }

        if (result.Evicted > 0)
        {
            text.Append(F($" {result.Evicted} older marker(s) removed to make room."));
        }

        return text.ToString();
    }

    /// <summary>Draw and reply with the listed lines and the summary.</summary>
    private static void Finish(CommandContext context, string title, List<DebugMarkerRequest> markers, IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            context.Reply(line);
        }

        DebugDrawFeature feature = Feature(context);
        DebugDrawResult result = feature.Draw(context.Player, title, markers);
        context.Reply(Drawn(result, feature.Options.LifetimeSeconds));
    }

    private static IEnumerable<string> Listed(IReadOnlyList<string> lines)
    {
        foreach (string line in lines.Take(MaxListedLines))
        {
            yield return line;
        }

        if (lines.Count > MaxListedLines)
        {
            yield return F($"{Prefix}... and {lines.Count - MaxListedLines} more (right-click a marker for its line).");
        }
    }

    /// <summary>The selected unit of the invoker's map other than the invoker, or null after replying.</summary>
    private static Unit? SelectedUnit(CommandContext context, Map map)
    {
        Player player = context.Player;
        Unit? unit = player.Selection.IsEmpty || player.Selection == player.Guid ? null : map.Combat.FindUnit(player.Selection);
        if (unit is null)
        {
            context.Reply(SelectUnit);
        }

        return unit;
    }

    private static float Spacing(CommandContext context) => Feature(context).Options.Spacing;

    /// <summary>The floor under (x, y) searched from <paramref name="fromZ"/> + 50 yards down 150 yards, or null.</summary>
    private static float? Floor(Map map, float x, float y, float fromZ)
    {
        float z = map.Collision.GetHeight(x, y, fromZ + 50f, useModels: true, maxSearchDistance: 150f);
        return z > TerrainTile.InvalidHeight ? z : null;
    }

    private static Vector3 Eye(WorldObject obj) => new(obj.X, obj.Y, obj.Z + MapCollision.DefaultEyeHeight);

    private static bool Los(CommandContext context, string args)
    {
        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        if (SelectedUnit(context, map) is not { } target)
        {
            return true;
        }

        Vector3 from = Eye(context.Player);
        Vector3 to = Eye(target);
        float total = Vector3.Distance(from, to);
        float spacing = Spacing(context);
        var markers = new List<DebugMarkerRequest>();
        var lines = new List<string>();
        ILineOfSight los = map.Collision.LineOfSight;
        if (!los.Enabled)
        {
            lines.Add(F($"{Prefix}no vmap collision data is loaded: every line is clear (World:Collision)."));
        }

        if (map.Collision.IsInLineOfSight(from.X, from.Y, from.Z, to.X, to.Y, to.Z))
        {
            string label = F($"line of sight to {NameOf(target)} CLEAR, {total:F1} yd (eye height {MapCollision.DefaultEyeHeight:F0} yd, doodads ignored)");
            lines.Add(Prefix + label);
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.LosClear, to, label + " - target end", Emphasis: true));
            foreach (Vector3 point in DebugDrawGeometry.SampleSegment(from, to, spacing, MaxLinePoints, SkipNearInvoker).SkipLast(1))
            {
                markers.Add(new DebugMarkerRequest(DebugMarkerKind.LosClear, point, label));
            }

            Finish(context, "los", markers, lines);
            return true;
        }

        los.TryGetObjectHit(map.MapId, from, to, -0.5f, out Vector3 hit);
        float hitDistance = Vector3.Distance(from, hit);
        string hitLabel = F($"line of sight to {NameOf(target)} BLOCKED at {P(hit)}, {hitDistance:F1} yd from you (target {total:F1} yd)");
        lines.Add(Prefix + hitLabel);
        markers.Add(new DebugMarkerRequest(DebugMarkerKind.HitPoint, hit, hitLabel, Emphasis: true));
        markers.Add(new DebugMarkerRequest(DebugMarkerKind.LosOccluded, to, F($"{NameOf(target)}, hidden: {total:F1} yd from you"), Emphasis: true));
        int blockedShare = Math.Max(2, (int)(MaxLinePoints * Math.Clamp(hitDistance / Math.Max(total, 0.01f), 0.25f, 0.75f)));
        foreach (Vector3 point in DebugDrawGeometry.SampleSegment(from, hit, spacing, blockedShare, SkipNearInvoker).SkipLast(1))
        {
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.LosBlocked, point, F($"line of sight to {NameOf(target)}: before the hit at {P(hit)}")));
        }

        foreach (Vector3 point in DebugDrawGeometry.SampleSegment(hit, to, spacing * 2, MaxLinePoints - blockedShare).Skip(1).SkipLast(1))
        {
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.LosOccluded, point, F($"line of sight to {NameOf(target)}: hidden beyond the hit at {P(hit)}")));
        }

        Finish(context, "los", markers, lines);
        return true;
    }

    private static bool Path(CommandContext context, string args)
    {
        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        if (SelectedUnit(context, map) is not { } target)
        {
            return true;
        }

        Player player = context.Player;
        var start = new Vector3(player.X, player.Y, player.Z);
        var end = new Vector3(target.X, target.Y, target.Z);
        PathResult path = map.Collision.FindPath(start, end, new PathOptions { Mover = PathMover.Player });
        IPathfinder pathfinder = WorldCollision.Of(context.World).PathfinderFor(map.MapId);
        bool good = path.HasPath && (path.Type & (PathType.NoPath | PathType.Incomplete | PathType.NotUsingPath)) == 0;
        DebugMarkerKind cornerKind = good ? DebugMarkerKind.PathCorner : DebugMarkerKind.PathCornerBad;

        var lines = new List<string>
        {
            F($"{Prefix}path to {NameOf(target)}: {path.Type} ({(int)path.Type:X}), {path.Points.Count} corner(s), {path.Length:F1} yd along it, {Vector3.Distance(start, end):F1} yd straight; pathfinder {pathfinder.GetType().Name}{(pathfinder.Enabled ? string.Empty : " (no navmesh data)")}."),
        };
        if (path.HasPath && Vector3.Distance(path.End, end) > 0.5f)
        {
            lines.Add(F($"{Prefix}the path ends {Vector3.Distance(path.End, end):F1} yd short of {NameOf(target)}, at {P(path.End)}."));
        }

        var markers = new List<DebugMarkerRequest>();
        var cornerLines = new List<string>();
        for (int i = 0; i < path.Points.Count; i++)
        {
            Vector3 corner = path.Points[i];
            string label = F($"path corner {i + 1}/{path.Points.Count} at {P(corner)}, {path.Type}");
            cornerLines.Add(Prefix + label);
            if (i > 0 || Vector3.Distance(corner, start) >= SkipNearInvoker)
            {
                markers.Add(new DebugMarkerRequest(cornerKind, corner, label, Emphasis: true));
            }
        }

        foreach (Vector3 point in DebugDrawGeometry.SamplePolylineFill(path.Points, Spacing(context), MaxFillPoints))
        {
            if (Vector3.Distance(point, start) >= SkipNearInvoker)
            {
                markers.Add(new DebugMarkerRequest(DebugMarkerKind.PathFill, point, F($"path to {NameOf(target)}, {path.Type}")));
            }
        }

        Finish(context, "path", markers, lines.Concat(Listed(cornerLines)));
        return true;
    }

    private static bool Waypoints(CommandContext context, string args)
    {
        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        CreatureWorldFeature creatures = context.Session.Services.GetRequiredService<CreatureWorldFeature>();
        if (creatures.GetOrCreateSystem(map).FindCreature(context.Player.Selection) is not { } creature)
        {
            context.Reply(SelectCreature);
            return true;
        }

        CreatureWaypointPath path = creatures.Content.ResolveWaypointPath(creature.Spawn?.Guid ?? 0, creature.Template.Entry);
        string who = F($"{creature.Template.Name} (entry {creature.Template.Entry}, spawn {creature.Spawn?.Guid ?? 0})");
        if (path.Points.Count == 0)
        {
            context.Reply(F($"{Prefix}{who} has no waypoint path (creature_movement or creature_movement_template); its movement type is {creature.MovementType}."));
            return true;
        }

        IReadOnlyList<CreatureWaypoint> nodes = path.Points;
        var lines = new List<string>
        {
            F($"{Prefix}waypoints of {who}: {nodes.Count} node(s) from {(path.Origin == CreatureWaypointOrigin.Guid ? "creature_movement" : "creature_movement_template")}; movement type {creature.MovementType}."),
        };
        var markers = new List<DebugMarkerRequest>();
        var nodeLines = new List<string>();
        var positions = new List<Vector3>(nodes.Count);
        foreach (CreatureWaypoint node in nodes.Take(MaxWaypointNodes))
        {
            var at = new Vector3(node.X, node.Y, node.Z);
            positions.Add(at);
            string label = F($"waypoint {node.Point} of {creature.Template.Name} at {P(at)}, wait {node.WaitTimeMs / 1000.0:F1} s{(node.Run ? ", run" : string.Empty)}");
            nodeLines.Add(Prefix + label);
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.Waypoint, at, label, Emphasis: true));
        }

        // The waypoint generator goes round: after the last node the creature walks back to the first.
        foreach (Vector3 point in DebugDrawGeometry.SamplePolylineFill(positions, Spacing(context) * 2, MaxFillPoints, closed: positions.Count > 2))
        {
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.WaypointFill, point, F($"waypoint path of {who}")));
        }

        Finish(context, "waypoints", markers, lines.Concat(Listed(nodeLines)));
        return true;
    }

    private static bool Cells(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptInt32(out int radius, 2) || !args.IsEmpty)
        {
            return false;
        }

        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        Player player = context.Player;
        CellCoord cell = GridDefines.ComputeCellCoord(player.X, player.Y);
        GridCoord grid = GridDefines.ComputeGridCoord(player.X, player.Y);
        var markers = new List<DebugMarkerRequest>();
        int noFloor = 0;
        foreach (LatticeCorner corner in DebugDrawGeometry.CellLattice(player.X, player.Y, radius, MaxCellRadius))
        {
            // Without a known floor the corner goes at your height (a map without terrain data still shows its lattice).
            float? floor = Floor(map, corner.X, corner.Y, player.Z);
            float z = floor ?? player.Z;
            noFloor += floor is null ? 1 : 0;
            CellCoord at = GridDefines.ComputeCellCoord(corner.X + 0.01f, corner.Y + 0.01f);
            string label = F($"{(corner.IsGridCorner ? "grid" : "cell")} corner ({corner.X:F1}, {corner.Y:F1}) {(floor is null ? "no floor known, at your height" : "floor")} {z:F1}; north-east of it is cell ({at.X}, {at.Y}) of grid ({at.Grid.X}, {at.Grid.Y})");
            markers.Add(new DebugMarkerRequest(corner.IsGridCorner ? DebugMarkerKind.GridCorner : DebugMarkerKind.Cell, new Vector3(corner.X, corner.Y, z), label));
        }

        var lines = new List<string>
        {
            F($"{Prefix}you are in cell ({cell.X}, {cell.Y}) of grid ({grid.X}, {grid.Y}); cells are {GridDefines.SizeOfGridCell:F2} yd, grids {GridDefines.SizeOfGrids:F2} yd."),
        };
        if (noFloor > 0)
        {
            lines.Add(F($"{Prefix}{noFloor} corner(s) have no known floor (no terrain or model data) and stand at your height."));
        }

        Finish(context, "cells", markers, lines);
        return true;
    }

    private static bool Collision(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float distance, 40f) || !args.IsEmpty || !float.IsFinite(distance) || distance < 1f || distance > MaxDistance)
        {
            return false;
        }

        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        Player player = context.Player;
        Vector3 from = Eye(player);
        Vector3 to = DebugDrawGeometry.Ahead(from, player.Orientation, distance);
        float spacing = Spacing(context);
        var markers = new List<DebugMarkerRequest>();
        var lines = new List<string>();
        if (!map.Collision.LineOfSight.Enabled)
        {
            lines.Add(F($"{Prefix}no vmap collision data is loaded: nothing can be hit (World:Collision)."));
        }

        if (map.Collision.LineOfSight.TryGetObjectHit(map.MapId, from, to, -0.5f, out Vector3 hit))
        {
            float ahead = Vector3.Distance(from, hit);
            string label = F($"collision {ahead:F1} yd ahead at {P(hit)}");
            lines.Add(Prefix + label);
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.HitPoint, hit, label, Emphasis: true));
            foreach (Vector3 point in DebugDrawGeometry.SampleSegment(from, hit, spacing, MaxLinePoints, SkipNearInvoker).SkipLast(1))
            {
                markers.Add(new DebugMarkerRequest(DebugMarkerKind.Collision, point, F($"collision ray, hit {ahead:F1} yd ahead")));
            }
        }
        else
        {
            string label = F($"no collision within {distance:F0} yd ahead");
            lines.Add(Prefix + label);
            foreach (Vector3 point in DebugDrawGeometry.SampleSegment(from, to, spacing, MaxLinePoints, SkipNearInvoker))
            {
                markers.Add(new DebugMarkerRequest(DebugMarkerKind.Collision, point, label));
            }
        }

        Finish(context, "collision", markers, lines);
        return true;
    }

    private static bool Height(CommandContext context, string args)
    {
        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        Player player = context.Player;
        float x = player.X, y = player.Y, z = player.Z;
        float terrain = map.Terrain.GetHeight(x, y, z);
        float? model = map.Collision.LineOfSight.GetModelHeight(map.MapId, x, y, z + 2f, MapCollision.DefaultHeightSearch);
        float floor = map.Collision.GetHeight(x, y, z);
        float water = map.Terrain.GetLiquidLevel(x, y);
        (uint zone, uint area) = map.Terrain.GetZoneAndAreaId(x, y, z);
        bool outdoors = map.Collision.IsOutdoors(x, y, z);

        static string H(float h) => h > TerrainTile.InvalidHeight ? h.ToString("F2", CultureInfo.InvariantCulture) : "none";
        var lines = new List<string>
        {
            F($"{Prefix}you are at {P(new Vector3(x, y, z))}, map {map.MapId}, zone {zone}, area {area}, {(outdoors ? "outdoors" : "indoors")}."),
            F($"{Prefix}floor {H(floor)} (you are {(floor > TerrainTile.InvalidHeight ? (z - floor).ToString("F2", CultureInfo.InvariantCulture) : "?")} yd above it); terrain {H(terrain)}; model {(model is { } m ? H(m) : "none")}; water {H(water)}."),
        };

        var markers = new List<DebugMarkerRequest>();
        if (floor > TerrainTile.InvalidHeight)
        {
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.Height, new Vector3(x, y, floor), F($"floor {floor:F2} under {P(new Vector3(x, y, z))}, zone {zone}, area {area}"), Emphasis: true));
        }

        if (terrain > TerrainTile.InvalidHeight && MathF.Abs(terrain - floor) > 0.5f)
        {
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.Generic, new Vector3(x, y, terrain), F($"terrain surface {terrain:F2} (the floor is {H(floor)})")));
        }

        if (water > TerrainTile.InvalidHeight && water > floor)
        {
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.Range, new Vector3(x, y, water), F($"water surface {water:F2}")));
        }

        if (markers.Count == 0)
        {
            lines.Add(Prefix + "no floor is known here (no terrain or model data), so nothing is marked.");
            foreach (string line in lines)
            {
                context.Reply(line);
            }

            return true;
        }

        Finish(context, "height", markers, lines);
        return true;
    }

    private static bool Range(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, Map.VisibilityRange) || !args.IsEmpty || !float.IsFinite(radius) || radius < 1f || radius > MaxDistance)
        {
            return false;
        }

        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        Player player = context.Player;
        var markers = new List<DebugMarkerRequest>();
        int index = 0;
        foreach (Vector3 point in DebugDrawGeometry.Ring(new Vector3(player.X, player.Y, player.Z), radius, RingPoints))
        {
            float z = Floor(map, point.X, point.Y, player.Z) ?? player.Z;
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.Range, point with { Z = z }, F($"{radius:F1} yd from {P(new Vector3(player.X, player.Y, player.Z))}, bearing {index * 360 / RingPoints} degrees")));
            index++;
        }

        string note = MathF.Abs(radius - Map.VisibilityRange) < 0.01f
            ? F($" (the visibility distance; an object already in view leaves at {Map.VisibilityRange + Map.VisibilityGreyDistance:F0} yd plus both bounding radii)")
            : string.Empty;
        Finish(context, "range", markers, [F($"{Prefix}circle of {radius:F1} yd around you{note}.")]);
        return true;
    }

    private static bool Spawns(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, 40f) || !args.IsEmpty || !float.IsFinite(radius) || radius < 1f || radius > MaxDistance)
        {
            return false;
        }

        if (context.Player.Map is not { } map)
        {
            context.Reply(NotInMap);
            return true;
        }

        Player player = context.Player;
        CreatureMapSystem system = context.Session.Services.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(map);
        var found = system.Creatures
            .Where(c => c.Spawn is not null)
            .Select(c => (Creature: c, Spawn: c.Spawn!, Distance: Distance2D(player.X, player.Y, c.Spawn!.X, c.Spawn!.Y)))
            .Where(c => c.Distance <= radius)
            .OrderBy(c => c.Distance).ThenBy(c => c.Spawn.Guid)
            .ToList();
        if (found.Count == 0)
        {
            context.Reply(F($"{Prefix}no creature spawn within {radius:F0} yd."));
            return true;
        }

        var markers = new List<DebugMarkerRequest>();
        var spawnLines = new List<string>();
        foreach ((Creature creature, CreatureSpawn spawn, float distance) in found.Take(MaxSpawns))
        {
            var at = new Vector3(spawn.X, spawn.Y, spawn.Z);
            float away = Distance2D(creature.X, creature.Y, spawn.X, spawn.Y);
            string label = F($"spawn {spawn.Guid}: {creature.Template.Name} (entry {creature.Template.Entry}) at {P(at)}, {distance:F1} yd from you; wander {spawn.WanderDistance:F0} yd, movement {creature.MovementType}; it is {away:F1} yd from its spawn now");
            spawnLines.Add(Prefix + label);
            markers.Add(new DebugMarkerRequest(DebugMarkerKind.Spawn, at, label, Emphasis: true));
        }

        string header = F($"{Prefix}{found.Count} creature spawn(s) within {radius:F0} yd{(found.Count > MaxSpawns ? F($", the nearest {MaxSpawns} marked") : string.Empty)}.");
        Finish(context, "spawns", markers, new[] { header }.Concat(Listed(spawnLines)));
        return true;
    }

    private static bool Kit(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint kit) || kit == 0 || !args.IsEmpty)
        {
            return false;
        }

        context.Session.Send(WorldOpcode.SmsgPlaySpellVisual, DebugMarkerPackets.PlaySpellVisual(context.Player.Guid, kit));
        context.Reply(F($"{Prefix}played SpellVisualKit {kit} on you; only you see it (an id the client does not have shows nothing)."));
        return true;
    }

    private static bool List(CommandContext context, string args)
    {
        IReadOnlyList<(string Title, int Markers, int SecondsLeft)> drawings = Feature(context).Drawings(context.Player);
        if (drawings.Count == 0)
        {
            context.Reply(Prefix + "you have no markers.");
            return true;
        }

        DebugDrawFeature feature = Feature(context);
        context.Reply(F($"{Prefix}{feature.MarkerCount(context.Player)} of {feature.Options.MaxMarkersPerGm} marker(s) in {drawings.Count} drawing(s):"));
        foreach ((string title, int markers, int seconds) in drawings)
        {
            context.Reply(F($"{Prefix}{title}: {markers} marker(s), {seconds} s left"));
        }

        return true;
    }

    private static bool Clear(CommandContext context, string args)
    {
        context.Reply(Cleared(Feature(context).Clear(context.Player)));
        return true;
    }

    private static string NameOf(Unit unit) => unit switch
    {
        Player p => p.Name,
        Creature c => c.Template.Name,
        _ => unit.Guid.ToString(),
    };

    private static float Distance2D(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2;
        float dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
