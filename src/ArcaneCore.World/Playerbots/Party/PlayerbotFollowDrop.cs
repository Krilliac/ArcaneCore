using System.Numerics;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// A drop to a leader below (<see cref="PlayerbotFollowDrop.Find"/>): the walk to the edge (null when the follower stands at it),
/// the edge, the point one step out over it where the fall begins, the floor it lands on, the height and vmangos' fall damage.
/// </summary>
internal sealed record PlayerbotDropPlan(PlayerbotRoute? ToEdge, Vector3 Edge, Vector3 StepOff, Vector3 Landing, float Height, uint Damage)
{
    /// <summary>
    /// How long the fall takes, as a client reports it in MSG_MOVE_FALL_LAND: a free fall of <see cref="Height"/> yards at vmangos'
    /// gravity (<c>Movement::gravity</c>, src/game/Movement/spline/util.cpp:24, 19.29110527 yards/s²).
    /// </summary>
    internal uint FallTimeMs => (uint)MathF.Ceiling(MathF.Sqrt(2f * Height / PlayerbotFollowDrop.Gravity) * 1000f);
}

/// <summary>
/// Finding a drop: the navigation mesh has no off-mesh (drop) connections where it matters (none in the nine tiles round the
/// Deadmines mine, and ArcaneCore's path search does not traverse them), so the drop is found on the collision data itself. From
/// the follower towards its leader below (and in a fan of headings either side, up to 90 degrees), the walkable floor is followed
/// in half-yard steps (a step up or down of at most a yard, in sight) to its edge; one step beyond it, the floor beneath must be at
/// least <see cref="MinDropYards"/> lower, the way out over the edge and the column down must be clear, the fall must cost less
/// than <see cref="PlayerbotFollowOptions.MaxFallHealthPercent"/> of the follower's health (vmangos <c>Player::HandleFall</c>,
/// <see cref="FallDamageCalculator"/>), and the floor it lands on must lead to the leader by the navigation mesh within twice the
/// straight distance (plus 10 yards). The walk to the edge is a stepped terrain route (<see cref="PlayerbotNavigation.TryTerrainRoute"/>),
/// whose every position the motion checks again.
/// </summary>
internal static class PlayerbotFollowDrop
{
    /// <summary>vmangos <c>Movement::gravity</c> (src/game/Movement/spline/util.cpp:24: 19.29110527 yards per second squared).</summary>
    internal const float Gravity = 19.29110527f;

    /// <summary>A leader at least this much lower is below the follower; a fall shorter than this is a step down, not a drop.</summary>
    internal const float MinDropYards = 3f;

    /// <summary>A drop is looked for only to a leader within this many yards across.</summary>
    internal const float SearchYards = 40f;

    private const float StepYards = 0.5f;

    /// <summary>The headings tried, in degrees off the bearing to the leader.</summary>
    private static readonly float[] FanDegrees = [0f, 15f, -15f, 30f, -30f, 45f, -45f, 60f, -60f, 90f, -90f];

    /// <summary>
    /// The drop from <paramref name="origin"/> to a leader at <paramref name="leader"/> below, or null when there is none the follower
    /// survives (health <paramref name="health"/> of <paramref name="maxHealth"/>).
    /// </summary>
    internal static PlayerbotDropPlan? Find(Map map, Vector3 origin, Vector3 leader, uint health, uint maxHealth, PlayerbotOptions options)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(options);
        Vector2 across = new(leader.X - origin.X, leader.Y - origin.Y);
        if (!Finite(origin) || !Finite(leader) || leader.Z > origin.Z - MinDropYards || across.Length() > SearchYards || health == 0) return null;
        float bearing = across.LengthSquared() > 1e-4f ? MathF.Atan2(across.Y, across.X) : 0f;
        MapCollision collision = map.Collision;
        foreach (float degrees in FanDegrees)
        {
            float heading = bearing + (degrees * MathF.PI / 180f);
            if (Along(collision, origin, leader, heading, health, maxHealth, options) is { } plan) return plan;
        }

        return null;
    }

    private static PlayerbotDropPlan? Along(MapCollision collision, Vector3 origin, Vector3 leader, float heading, uint health, uint maxHealth,
        PlayerbotOptions options)
    {
        Vector2 direction = new(MathF.Cos(heading), MathF.Sin(heading));
        float start = collision.GetHeight(origin.X, origin.Y, origin.Z + 0.5f);
        Vector3 edge = ValidHeight(start) && MathF.Abs(start - origin.Z) <= 2f ? origin with { Z = start } : origin;
        for (float travelled = StepYards; travelled <= SearchYards; travelled += StepYards)
        {
            float x = origin.X + (direction.X * travelled), y = origin.Y + (direction.Y * travelled);
            float floor = collision.GetHeight(x, y, edge.Z + 1f);
            if (ValidHeight(floor) && MathF.Abs(floor - edge.Z) <= 1f)
            {
                // Still on the upper floor: one more step along it, in sight.
                if (!collision.IsInLineOfSight(edge.X, edge.Y, edge.Z + 2f, x, y, floor + 2f)) return null;
                edge = new Vector3(x, y, floor);
                continue;
            }

            // Past the edge: the floor beneath (searched far enough down for any fall the follower could survive when the ordinary
            // probe finds none). A floor above the edge is a rise or a wall, a step down shorter than a drop no edge at all.
            if (ValidHeight(floor) && floor > edge.Z) return null;
            float below = ValidHeight(floor) ? floor : collision.GetHeight(x, y, edge.Z + 1f, useModels: true, maxSearchDistance: 120f);
            if (!ValidHeight(below) || edge.Z - below < MinDropYards) return null;
            var stepOff = new Vector3(x, y, edge.Z);
            var landing = new Vector3(x, y, below);
            if (!collision.IsInLineOfSight(edge.X, edge.Y, edge.Z + 1.5f, x, y, edge.Z + 1.5f)
                || !collision.IsInLineOfSight(x, y, edge.Z + 1f, x, y, below + 0.5f)) return null;
            float height = edge.Z - below;
            uint damage = FallDamageCalculator.Damage(height, 0, maxHealth);
            if (damage > 0 && (ulong)damage * 100 >= (ulong)health * (ulong)options.Follow.MaxFallHealthPercent) return null;
            if (!LeadsTo(collision, landing, leader, options)) return null;
            PlayerbotRoute? toEdge = null;
            if (Vector2.Distance(new(edge.X, edge.Y), new(origin.X, origin.Y)) > 0.3f
                && !PlayerbotNavigation.TryTerrainRoute(origin, edge, options, (px, py, pz) => collision.GetHeight(px, py, pz),
                    (a, b) => collision.IsInLineOfSight(a.X, a.Y, a.Z + 2, b.X, b.Y, b.Z + 2), out toEdge))
                return null;
            return new PlayerbotDropPlan(toEdge, edge, stepOff, landing, height, damage);
        }

        return null;
    }

    /// <summary>Whether the navigation mesh leads from <paramref name="landing"/> to the leader within twice the straight distance plus 10 yards.</summary>
    private static bool LeadsTo(MapCollision collision, Vector3 landing, Vector3 leader, PlayerbotOptions options)
    {
        PathResult path = collision.FindPath(landing, leader, new PathOptions { MaxPoints = Math.Max(2, options.MaxPathPoints),
            Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, AllowPartial = false,
            MaxSearchNodes = PathOptions.DefaultMaxSearchNodes, LoadTiles = true });
        if (!path.HasPath || (path.Type & (PathType.NotUsingPath | PathType.Incomplete | PathType.NoPath)) != 0) return false;
        return path.Length <= (Vector3.Distance(landing, leader) * 2f) + 10f;
    }

    private static bool ValidHeight(float height)
        => float.IsFinite(height) && height != TerrainTile.InvalidHeight && height != TerrainTile.InvalidHeightValue;

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
