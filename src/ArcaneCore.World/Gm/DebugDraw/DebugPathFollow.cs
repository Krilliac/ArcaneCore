using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Gm.DebugDraw;

/// <summary>What one followed unit shows now (<c>.debug vis follow list</c>).</summary>
public readonly record struct DebugFollowEntry(ObjectGuid Unit, string Name, bool IsPlayer, int PathPoints, int Markers);

/// <summary>
/// <c>.debug vis follow</c>: the live path view (docs/areas/debug-draw.md, "Live follow"). A GM follows units (a playerbot or a creature);
/// every <see cref="DebugDrawOptions.FollowIntervalMs"/> each followed unit's current path is read and, only when it changed (another point
/// count, a point moved more than <see cref="SameEpsilon"/> yards, or another move kind), its old markers are destroyed and the new path is
/// drawn as a pinned drawing of <see cref="DebugDrawFeature"/> (client-only markers, the requesting GM alone sees them).
/// <list type="bullet">
/// <item><description>A creature's path is its active spline (<see cref="Creature.Spline"/>: where the spline started, then every corner), its
/// kind <see cref="MotionMaster.CurrentType"/>, its target <see cref="MotionMaster.TargetedUnit"/>.</description></item>
/// <item><description>A bot's path is the route the playerbot pack's <see cref="IBotRouteSource"/> (registered on <see cref="ManagedBotRegistry"/>)
/// copies out: the route the bot's motion walks now, whichever AI planned it. Without the pack attached a bot has no path.</description></item>
/// </list>
/// Only GUIDs are held, resolved on the GM's map each check, so a follow never keeps a unit alive or loaded. A follow ends when the unit
/// is no longer on the GM's map (despawned, logged out, teleported away), when a followed creature dies (a bot is kept while dead: its
/// corpse run is a route), and when the GM logs out, changes map, runs <c>.debug vis follow off</c> or <c>.debug vis clear</c>.
/// <para>Cost: nothing at all while nobody follows (<see cref="Tick"/> returns on an empty table); while following, a check reads the path
/// into a reused buffer and compares it, and allocates only when the path changed. Thread affinity: world thread.</para>
/// </summary>
public sealed class DebugPathFollow
{
    /// <summary>Yards a path point may move before the path counts as changed.</summary>
    public const float SameEpsilon = 0.25f;

    /// <summary>Most route points read from a bot (a longer route is drawn up to here).</summary>
    public const int MaxRoutePoints = 4096;

    /// <summary>Fewest markers one followed unit gets when several share the GM's cap.</summary>
    public const int MinShare = 4;

    private const string Prefix = "DebugDraw: ";
    private const string FaultSite = "gm.debug-draw.follow";

    private readonly DebugDrawFeature _draw;
    private readonly IServiceProvider _services;
    private readonly Dictionary<ObjectGuid, GmFollows> _gms = [];
    private readonly List<ObjectGuid> _ended = [];
    private Vector3[] _scratch = new Vector3[64];
    private ManagedBotRegistry? _bots;
    private CreatureWorldFeature? _creatures;
    private ILogger? _logger;
    private bool _resolved;

    internal DebugPathFollow(DebugDrawFeature draw, IServiceProvider services)
    {
        _draw = draw;
        _services = services;
    }

    /// <summary>GMs that follow at least one unit.</summary>
    public int FollowingGmCount => _gms.Count;

    /// <summary>Paths read since start (a test's proof that nothing is read while nobody follows).</summary>
    public long PathReads { get; private set; }

    /// <summary>Paths redrawn (or removed) because they changed, since start.</summary>
    public long Redraws { get; private set; }

    /// <summary>The units <paramref name="gm"/> follows, in the order the follows started.</summary>
    public IReadOnlyList<DebugFollowEntry> Entries(Player gm)
        => _gms.TryGetValue(gm.Guid, out GmFollows? follows)
            ? [.. follows.Units.Select(f => new DebugFollowEntry(f.Guid, f.Name, f.IsPlayer, f.Points.Length, f.Markers))]
            : [];

    /// <summary>Whether <paramref name="gm"/> follows <paramref name="unit"/>.</summary>
    public bool IsFollowing(Player gm, ObjectGuid unit) => _gms.TryGetValue(gm.Guid, out GmFollows? follows) && follows.Find(unit) is not null;

    /// <summary>How many units <paramref name="gm"/> follows.</summary>
    public int Count(Player gm) => _gms.TryGetValue(gm.Guid, out GmFollows? follows) ? follows.Units.Count : 0;

    /// <summary>
    /// Start following <paramref name="unit"/> (on <paramref name="gm"/>'s map) and draw its path now. False when the GM already follows
    /// <see cref="DebugDrawOptions.MaxFollowedUnits"/> units or either is not in that map; true when it already followed it.
    /// </summary>
    public bool Start(Player gm, Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return IsFollowing(gm, unit.Guid) || StartMany(gm, [unit]) == 1;
    }

    /// <summary>
    /// Start following each of <paramref name="units"/> not followed yet (in order, until <see cref="DebugDrawOptions.MaxFollowedUnits"/>)
    /// and draw their paths now. The GM's marker cap is shared by every followed unit: a drawing made before that holds more than the new
    /// share is redrawn within it first. Returns how many follows started.
    /// </summary>
    public int StartMany(Player gm, IReadOnlyList<Unit> units)
    {
        ArgumentNullException.ThrowIfNull(gm);
        ArgumentNullException.ThrowIfNull(units);
        if (gm.Map is not { } map)
        {
            return 0;
        }

        if (!_gms.TryGetValue(gm.Guid, out GmFollows? follows) || !ReferenceEquals(follows.Map, map))
        {
            // Follows of another map: the client dropped their markers with the map.
            follows = new GmFollows(map);
            _gms[gm.Guid] = follows;
        }

        int before = follows.Units.Count;
        var added = new List<(Followed Followed, Unit Unit)>();
        foreach (Unit unit in units)
        {
            if (follows.Units.Count >= _draw.Options.MaxFollowedUnits)
            {
                break;
            }

            if (ReferenceEquals(unit.Map, map) && follows.Find(unit.Guid) is null)
            {
                var followed = new Followed(unit.Guid, NameOf(unit), unit is Player);
                follows.Units.Add(followed);
                added.Add((followed, unit));
            }
        }

        if (added.Count == 0)
        {
            if (follows.Units.Count == 0)
            {
                _gms.Remove(gm.Guid);
            }

            return 0;
        }

        follows.LastCheckMs = _draw.World?.NowMs ?? 0;
        int share = Share(follows);
        for (int i = 0; i < before; i++)
        {
            Followed older = follows.Units[i];
            if (older.Markers > share && Find(follows.Map, older) is { } unit)
            {
                Check(gm, follows, older, unit, force: true);
            }
        }

        foreach ((Followed followed, Unit unit) in added)
        {
            Check(gm, follows, followed, unit);
        }

        return added.Count;
    }

    private int Share(GmFollows follows) => Math.Max(MinShare, _draw.Options.MaxMarkersPerGm / Math.Max(1, follows.Units.Count));

    private static Unit? Find(Map map, Followed followed) => followed.IsPlayer ? map.FindPlayer(followed.Guid) : map.FindObject(followed.Guid) as Unit;

    /// <summary>Stop following <paramref name="unit"/> and remove its markers. False when <paramref name="gm"/> did not follow it.</summary>
    public bool Stop(Player gm, ObjectGuid unit)
    {
        ArgumentNullException.ThrowIfNull(gm);
        if (!_gms.TryGetValue(gm.Guid, out GmFollows? follows) || follows.Find(unit) is not { } followed)
        {
            return false;
        }

        _draw.RemoveDrawing(gm, followed.DrawingId);
        follows.Units.Remove(followed);
        if (follows.Units.Count == 0)
        {
            _gms.Remove(gm.Guid);
        }

        return true;
    }

    /// <summary>Stop every follow of <paramref name="gm"/> and remove their markers. Returns how many follows ended.</summary>
    public int StopAll(Player gm)
    {
        ArgumentNullException.ThrowIfNull(gm);
        if (!_gms.Remove(gm.Guid, out GmFollows? follows))
        {
            return 0;
        }

        foreach (Followed followed in follows.Units)
        {
            _draw.RemoveDrawing(gm, followed.DrawingId);
        }

        return follows.Units.Count;
    }

    /// <summary>Forget every follow of <paramref name="gm"/> without a packet (logout and <c>.debug vis clear</c> destroy the markers themselves).</summary>
    internal int Forget(Player gm) => _gms.Remove(gm.Guid, out GmFollows? follows) ? follows.Units.Count : 0;

    internal void OnLoggingOut(Player player)
    {
        if (_gms.Count != 0)
        {
            Forget(player);
        }
    }

    /// <summary>
    /// The units within <paramref name="radius"/> yards of <paramref name="gm"/> that move now and are not followed yet, nearest first: living
    /// creatures on a spline and playerbots walking a route.
    /// </summary>
    public IReadOnlyList<Unit> MovingNear(Player gm, float radius)
    {
        ArgumentNullException.ThrowIfNull(gm);
        if (gm.Map is not { } map)
        {
            return [];
        }

        Resolve();
        var found = new List<(Unit Unit, float Distance)>();
        if (_creatures?.FindSystem(map) is { } system)
        {
            var near = new List<Creature>();
            system.CollectCreaturesNear(gm.X, gm.Y, radius, near);
            foreach (Creature creature in near)
            {
                if (creature.IsAlive && creature.Spline is not null && !IsFollowing(gm, creature.Guid))
                {
                    found.Add((creature, Distance2D(gm, creature)));
                }
            }
        }

        if (_bots?.RouteSource is { } routes)
        {
            foreach (Player player in map.Players)
            {
                float distance = Distance2D(gm, player);
                if (!ReferenceEquals(player, gm) && player.Session.IsPlayerbot && distance <= radius && !IsFollowing(gm, player.Guid)
                    && ReadRoute(routes, player, out _) > 0)
                {
                    found.Add((player, distance));
                }
            }
        }

        return [.. found.OrderBy(f => f.Distance).ThenBy(f => f.Unit.Guid.Value).Select(f => f.Unit)];
    }

    /// <summary>The world tick: check the follows whose interval passed. Returns at once while nobody follows.</summary>
    internal void Tick()
    {
        if (_gms.Count == 0 || _draw.World is not { } world)
        {
            return;
        }

        uint now = world.NowMs;
        uint interval = (uint)_draw.Options.FollowIntervalMs;
        foreach ((ObjectGuid owner, GmFollows follows) in _gms)
        {
            if (unchecked(now - follows.LastCheckMs) < interval)
            {
                continue;
            }

            follows.LastCheckMs = now;
            Player? gm = world.FindOnlinePlayer(owner);
            if (gm is null || !ReferenceEquals(gm.Map, follows.Map))
            {
                // Gone from the world or moved to another map: its client dropped the markers already.
                _ended.Add(owner);
                continue;
            }

            for (int i = follows.Units.Count - 1; i >= 0; i--)
            {
                Followed followed = follows.Units[i];
                Unit? unit = Find(follows.Map, followed);
                string? reason = unit is null || !ReferenceEquals(unit.Map, follows.Map) ? "it is no longer on your map"
                    : unit is Creature { IsAlive: false } ? "it died"
                    : null;
                if (reason is not null)
                {
                    _draw.RemoveDrawing(gm, followed.DrawingId);
                    follows.Units.RemoveAt(i);
                    gm.Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(StoppedNote(followed.Name, reason)));
                    continue;
                }

                Check(gm, follows, followed, unit!);
            }

            if (follows.Units.Count == 0)
            {
                _ended.Add(owner);
            }
        }

        if (_ended.Count > 0)
        {
            foreach (ObjectGuid owner in _ended)
            {
                _gms.Remove(owner);
            }

            _ended.Clear();
        }
    }

    /// <summary>The chat line when a follow ends on its own.</summary>
    public static string StoppedNote(string name, string reason) => string.Create(CultureInfo.InvariantCulture, $"{Prefix}stopped following {name}: {reason}.");

    /// <summary>Read the unit's path; when it changed, replace its drawing.</summary>
    private void Check(Player gm, GmFollows follows, Followed followed, Unit unit, bool force = false)
    {
        int count = ReadPath(unit, out int signature);
        bool drawnStill = followed.DrawingId == 0 ? count == 0 : _draw.HasDrawing(gm, followed.DrawingId);
        if (!force && drawnStill && !Changed(followed, count, signature))
        {
            return;
        }

        Redraws++;
        _draw.RemoveDrawing(gm, followed.DrawingId);
        followed.DrawingId = 0;
        followed.Markers = 0;
        followed.Signature = signature;
        followed.Points = count == 0 ? [] : _scratch.AsSpan(0, count).ToArray();
        if (count == 0)
        {
            return;
        }

        (string source, bool bad) = Describe(unit, followed);
        DebugDrawResult result = _draw.DrawPinned(gm, "follow " + followed.Name, Requests(followed, source, bad), Share(follows), out long drawingId);
        followed.DrawingId = drawingId;
        followed.Markers = result.Placed;
    }

    private bool Changed(Followed followed, int count, int signature)
    {
        if (count != followed.Points.Length || signature != followed.Signature)
        {
            return true;
        }

        for (int i = 0; i < count; i++)
        {
            if (Vector3.DistanceSquared(followed.Points[i], _scratch[i]) > SameEpsilon * SameEpsilon)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copy <paramref name="unit"/>'s current path into <see cref="_scratch"/> (the unit's start or route start first, the goal last) and
    /// return its point count; 0 when it stands. <paramref name="signature"/> changes with the move kind.
    /// </summary>
    private int ReadPath(Unit unit, out int signature)
    {
        PathReads++;
        signature = 0;
        int count;
        if (unit is Player player)
        {
            Resolve();
            BotRouteInfo info = default;
            count = _bots?.RouteSource is { } routes ? ReadRoute(routes, player, out info) : 0;
            signature = count == 0 ? 0 : 0x10000 | (int)info.Flags;
        }
        else if (unit is Creature creature && creature.Spline is { } spline)
        {
            IReadOnlyList<Vector3> path = spline.Path;
            count = 1 + Math.Max(1, path.Count);
            Ensure(count);
            _scratch[0] = new Vector3(spline.StartX, spline.StartY, spline.StartZ);
            if (path.Count == 0)
            {
                _scratch[1] = new Vector3(spline.EndX, spline.EndY, spline.EndZ);
            }
            else
            {
                for (int i = 0; i < path.Count; i++)
                {
                    _scratch[i + 1] = path[i];
                }
            }

            signature = (int)creature.Motion.CurrentType | (creature.Motion.IsReachable ? 0 : 0x100);
        }
        else
        {
            count = 0;
        }

        return count;
    }

    /// <summary>The bot's route through the pack's seam into <see cref="_scratch"/>; a fault in the pack loses this read, never the tick.</summary>
    private int ReadRoute(IBotRouteSource routes, Player bot, out BotRouteInfo info)
    {
        info = default;
        int total;
        try
        {
            total = routes.CopyRoute(bot, _scratch, out info);
            if (total > _scratch.Length && _scratch.Length < MaxRoutePoints)
            {
                Ensure(Math.Min(total, MaxRoutePoints));
                total = routes.CopyRoute(bot, _scratch, out info);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(FaultSite, ex);
            (_logger ?? NullLogger.Instance).LogFault("debug-draw-follow", ex, "the bot route source failed for {Bot}", bot.Name);
            info = default;
            return 0;
        }

        int count = Math.Clamp(total, 0, _scratch.Length);
        for (int i = 0; i < count; i++)
        {
            Vector3 p = _scratch[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            {
                count = i; // draw the finite part only
                break;
            }
        }

        return count;
    }

    private void Ensure(int count)
    {
        if (count > _scratch.Length)
        {
            Array.Resize(ref _scratch, Math.Max(count, _scratch.Length * 2));
        }
    }

    private void Resolve()
    {
        if (_resolved)
        {
            return;
        }

        _bots = _services.GetService<ManagedBotRegistry>();
        _creatures = _services.GetService<CreatureWorldFeature>();
        _logger = _services.GetService<ILogger<DebugPathFollow>>();
        _resolved = true;
    }

    /// <summary>The source part of every label (bot route kind or creature move type) and whether the path is incomplete or a straight line.</summary>
    private (string Source, bool Bad) Describe(Unit unit, Followed followed)
    {
        var text = new StringBuilder();
        bool bad;
        if (unit is Creature creature)
        {
            text.Append("creature ").Append(creature.Motion.CurrentType.ToString().ToLowerInvariant()).Append(" move");
            if (creature.Motion.TargetedUnit is { } target)
            {
                text.Append(", after ").Append(NameOf(target));
            }

            bool unreachable = !creature.Motion.IsReachable;
            bool noMesh = creature.Map is { } map && _draw.World is { } world && !WorldCollision.Of(world).PathfinderFor(map.MapId).Enabled;
            if (unreachable)
            {
                text.Append(", target unreachable");
            }

            if (noMesh)
            {
                text.Append(", no navmesh: straight lines");
            }

            bad = unreachable || noMesh;
        }
        else
        {
            BotRouteFlags flags = (BotRouteFlags)(followed.Signature & 0xFF);
            bool navigated = (flags & BotRouteFlags.Navigated) != 0;
            text.Append("bot route, ").Append(navigated ? "navmesh" : "direct steps");
            if ((flags & BotRouteFlags.Chase) != 0)
            {
                text.Append(", chase");
            }

            if ((flags & BotRouteFlags.MeshEndedShort) != 0)
            {
                text.Append(", mesh ended short");
            }

            if ((flags & BotRouteFlags.PartialToGoal) != 0)
            {
                text.Append(", partial to goal");
            }

            if ((flags & BotRouteFlags.QuestReturn) != 0)
            {
                text.Append(", quest return");
            }

            bad = !navigated || (flags & (BotRouteFlags.MeshEndedShort | BotRouteFlags.PartialToGoal)) != 0;
        }

        return (text.ToString(), bad);
    }

    /// <summary>The goal first, then the corners, then the fill dots, so a small share keeps the key markers.</summary>
    private List<DebugMarkerRequest> Requests(Followed followed, string source, bool bad)
    {
        Vector3[] points = followed.Points;
        int n = points.Length;
        float length = 0f;
        for (int i = 1; i < n; i++)
        {
            length += Vector3.Distance(points[i - 1], points[i]);
        }

        var requests = new List<DebugMarkerRequest>(n + DebugDrawCommands.MaxFillPoints + 1)
        {
            new(DebugMarkerKind.PathGoal, points[^1], F($"{followed.Name} ({source}): goal at {P(points[^1])}, {length:F1} yd along {n - 1} leg(s)"), Emphasis: true),
        };
        DebugMarkerKind corner = bad ? DebugMarkerKind.PathCornerBad : DebugMarkerKind.PathCorner;
        for (int i = 0; i < n - 1; i++)
        {
            string what = i == 0 ? "start" : F($"corner {i}/{n - 2}");
            requests.Add(new DebugMarkerRequest(corner, points[i], F($"{followed.Name} ({source}): {what} at {P(points[i])}"), Emphasis: true));
        }

        string fill = F($"{followed.Name} ({source}): path");
        foreach (Vector3 point in DebugDrawGeometry.SamplePolylineFill(points, _draw.Options.Spacing, DebugDrawCommands.MaxFillPoints))
        {
            requests.Add(new DebugMarkerRequest(DebugMarkerKind.PathFill, point, fill));
        }

        return requests;
    }

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string P(Vector3 v) => F($"({v.X:F1}, {v.Y:F1}, {v.Z:F1})");

    private static float Distance2D(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    internal static string NameOf(Unit unit) => unit switch
    {
        Player p => p.Name,
        Creature c => c.Template.Name,
        _ => unit.Guid.ToString(),
    };

    private sealed class GmFollows(Map map)
    {
        public Map Map { get; } = map;

        public List<Followed> Units { get; } = [];

        public uint LastCheckMs { get; set; }

        public Followed? Find(ObjectGuid guid)
        {
            foreach (Followed followed in Units)
            {
                if (followed.Guid == guid)
                {
                    return followed;
                }
            }

            return null;
        }
    }

    private sealed class Followed(ObjectGuid guid, string name, bool isPlayer)
    {
        public ObjectGuid Guid { get; } = guid;

        public string Name { get; } = name;

        public bool IsPlayer { get; } = isPlayer;

        /// <summary>The path drawn last (empty: the unit stood).</summary>
        public Vector3[] Points { get; set; } = [];

        public int Signature { get; set; }

        public long DrawingId { get; set; }

        public int Markers { get; set; }
    }
}
