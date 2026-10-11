using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A bot's home area: a circle on one map (centre, radius in yards, two-dimensional distance), clipped by default to the zone of its
/// centre. ArcaneCore's own feature, set by <c>.playerbot home</c> and kept in <c>managed_playerbot_home</c>; no reference server
/// anchors a bot to an area, so the 0.75 return hysteresis, the radius bounds (<see cref="ArcaneCore.Kernel.Characters.PlayerbotHomeBounds"/>)
/// and the zone clip are ArcaneCore choices.
/// <para>
/// <b>Zone clip.</b> A circle cannot follow a zone border: homed at Sentinel Hill with radius 750 it reaches Raven Hill in Duskwood
/// (creatures of level 20 and more), where level-14 bots died again and again (QA death-loop findings, map 0 near -10650,450). With
/// the clip a point counts as inside only when it is inside the circle <i>and</i> in the home's zone: the AreaTable zone (the
/// top-level parent area) at that position, from the terrain's area flag (<see cref="PlayerbotZoneLookup"/>; vmangos
/// <c>TerrainInfo::GetZoneAndAreaId</c> through <see cref="ArcaneCore.Game.Maps.Terrain.AreaTable.GetZoneAndAreaId"/>, the lookup
/// <c>Map.GetZoneAndAreaId</c> uses). The home's zone is the zone at its centre. A position whose zone the terrain does not know
/// (0) is outside (fail closed); a clipped home whose own centre has no known zone (<see cref="ZoneUnknown"/>) contains nothing at
/// all, so its filters reject every creature, quest and destination until the operator re-homes it (or homes it with <c>nozone</c>).
/// </para>
/// <para>
/// Thread affinity: world thread only (the quest and entry caches are plain dictionaries, the zone lookup reads the terrain). The area
/// itself is immutable; a bot whose home changes gets a new instance, which is also how the caches of
/// <see cref="PlayerbotWorldDestinations"/> notice the change.
/// </para>
/// </summary>
internal sealed class PlayerbotHomeArea
{
    /// <summary>A walking-back bot stops once it is inside this fraction of the radius, so it does not oscillate at the edge.</summary>
    internal const float ReturnFraction = 0.75f;

    /// <summary>Beyond this fraction of the radius an exploring bot aims its next leg at the centre.</summary>
    internal const float ExploreFraction = 0.8f;

    private readonly Dictionary<uint, bool> _quests = [];
    private readonly Dictionary<uint, bool> _entries = [];
    private readonly PlayerbotZoneLookup? _zones;
    private CreatureContent? _cachedContent;
    private QuestStore? _cachedQuests;
    private int _cachedVersion = -1;

    /// <summary>An unclipped home: the circle alone.</summary>
    internal PlayerbotHomeArea(uint mapId, Vector3 centre, float radius, string? place)
        : this(mapId, centre, radius, place, zoneClipped: false, zones: null)
    {
    }

    /// <summary>
    /// A home; with <paramref name="zoneClipped"/> it is clipped to the zone <paramref name="zones"/> reports at the centre (no lookup,
    /// or a centre whose zone is not known: <see cref="ZoneUnknown"/>, and the area contains nothing).
    /// </summary>
    /// <remarks>
    /// The centre's zone is looked up on first use, not here: brains are built off the world thread too (a bot's start), and the
    /// terrain is world-thread only. <paramref name="zoneUnknown"/> is called once, on the world thread, when that lookup finds no zone.
    /// </remarks>
    internal PlayerbotHomeArea(uint mapId, Vector3 centre, float radius, string? place, bool zoneClipped, PlayerbotZoneLookup? zones,
        Action<PlayerbotHomeArea>? zoneUnknown = null)
    {
        MapId = mapId;
        Centre = centre;
        Radius = radius;
        Place = place;
        ZoneClipped = zoneClipped;
        _zones = zoneClipped ? zones : null;
        _zoneUnknown = zoneUnknown;
        if (!zoneClipped || zones is null) _zoneId = 0;
    }

    private readonly Action<PlayerbotHomeArea>? _zoneUnknown;
    private uint? _zoneId;

    internal uint MapId { get; }
    internal Vector3 Centre { get; }
    internal float Radius { get; }
    internal string? Place { get; }

    /// <summary>Whether the area is clipped to the zone of its centre (the default for a new home; <c>nozone</c> turns it off).</summary>
    internal bool ZoneClipped { get; }

    /// <summary>
    /// The area levels of the home map, for the danger check of an Auto walk home (<see cref="PlayerbotHomeOptions.DangerLevelMargin"/>),
    /// or null: no level is known and no way home counts as dangerous (the distance and the other rules still apply).
    /// </summary>
    internal PlayerbotAreaLevelLookup? AreaLevels { get; init; }

    /// <summary>The AreaTable zone of the centre for a clipped home; 0 when unclipped or not known. World thread.</summary>
    internal uint ZoneId
    {
        get
        {
            if (_zoneId is { } known) return known;
            uint zone = _zones!.ZoneAt(Centre.X, Centre.Y, Centre.Z);
            _zoneId = zone;
            if (zone == 0) _zoneUnknown?.Invoke(this);
            return zone;
        }
    }

    /// <summary>A clipped home whose centre's zone is not known: it contains nothing (fail closed).</summary>
    internal bool ZoneUnknown => ZoneClipped && ZoneId == 0;

    /// <summary>
    /// <see cref="ZoneUnknown"/> as far as it is already known, without looking the zone up: true only once the brain's first use
    /// (world thread) found no zone. Safe for status lines read from any thread.
    /// </summary>
    internal bool ZoneFoundUnknown => ZoneClipped && _zoneId == 0;

    /// <summary>The distance from the centre a walking-back bot stops at.</summary>
    internal float ReturnRadius => Radius * ReturnFraction;

    /// <summary>The distance from the centre beyond which an exploring bot heads for the centre.</summary>
    internal float ExploreRadius => Radius * ExploreFraction;

    /// <summary>The two-dimensional distance from the centre.</summary>
    internal float DistanceTo(float x, float y)
    {
        float dx = x - Centre.X, dy = y - Centre.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Whether a position of the home map is in the home's zone: always for an unclipped home; never when the home's zone is not
    /// known or the terrain does not know the position's zone (fail closed).
    /// </summary>
    internal bool InZone(float x, float y, float z)
        => !ZoneClipped || ZoneId is not 0 and var zone && _zones!.ZoneAt(x, y, z) == zone;

    /// <summary>
    /// Whether a point on <paramref name="map"/> lies inside the area: inside the circle and, for a clipped home, in its zone
    /// (another map: never). The circle is tested first, so the zone is looked up only for points near the home.
    /// </summary>
    internal bool Contains(uint map, float x, float y, float z = 0f)
        => map == MapId && float.IsFinite(x) && float.IsFinite(y) && DistanceTo(x, y) <= Radius && InZone(x, y, z);

    internal bool Contains(Unit unit) => Contains(unit.MapId, unit.X, unit.Y, unit.Z);

    /// <summary>Whether a creature spawn of the home map lies inside the area.</summary>
    internal bool Contains(CreatureSpawn spawn) => Contains(MapId, spawn.X, spawn.Y, spawn.Z);

    /// <summary>The zone part of the long form: <c>zone=40</c>, <c>zone=off</c> or <c>zone=unknown</c>.</summary>
    internal string ZoneText => !ZoneClipped ? "off" : ZoneId == 0 ? "unknown" : ZoneId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether any spawn of creature <paramref name="entry"/> on the home map lies inside the area (cached).</summary>
    internal bool HasSpawnInside(CreatureContent content, uint entry)
    {
        Sync(content, null);
        if (_entries.TryGetValue(entry, out bool known)) return known;
        bool inside = false;
        foreach (CreatureSpawn spawn in content.GetSpawns(MapId, entry))
        {
            if (!Contains(spawn)) continue;
            inside = true;
            break;
        }

        return _entries[entry] = inside;
    }

    /// <summary>
    /// Whether <paramref name="quest"/> is worth doing inside the area: every creature that ends it, and every creature it asks the
    /// bot to kill, has at least one spawn inside. A quest nobody ends by creature counts as outside. Item and object objectives are
    /// not checked. Cached per quest, dropped when the creature content or the quest store changes.
    /// </summary>
    internal bool QuestInside(QuestStore quests, CreatureContent content, Quest quest)
    {
        Sync(content, quests);
        if (_quests.TryGetValue(quest.Id, out bool known)) return known;
        bool inside = true;
        IReadOnlyList<uint> enders = quests.CreatureEndersOf(quest.Id);
        if (enders.Count == 0) inside = false;
        foreach (uint ender in enders)
        {
            if (inside && !HasSpawnInside(content, ender)) inside = false;
        }

        for (int index = 0; inside && index < quest.ReqCreatureOrGOId.Count; index++)
        {
            int objective = quest.ReqCreatureOrGOId[index];
            if (objective > 0 && !HasSpawnInside(content, (uint)objective)) inside = false;
        }

        return _quests[quest.Id] = inside;
    }

    /// <summary>Drop the caches when the creature content or the quest store changed since they were filled.</summary>
    private void Sync(CreatureContent content, QuestStore? quests)
    {
        if (ReferenceEquals(_cachedContent, content) && _cachedVersion == content.DefinitionsVersion
            && (quests is null || ReferenceEquals(_cachedQuests, quests))) return;
        _entries.Clear();
        _quests.Clear();
        _cachedContent = content;
        _cachedVersion = content.DefinitionsVersion;
        if (quests is not null) _cachedQuests = quests;
    }
}

/// <summary>
/// The AreaTable zone at a position of one map, for the zone clip of <see cref="PlayerbotHomeArea"/>. Over the terrain it is exactly
/// what <c>Map.GetZoneAndAreaId</c> answers (vmangos <c>TerrainInfo::GetZoneAndAreaId</c>: the tile's area flag, then
/// <see cref="AreaTable.GetZoneAndAreaId"/>, i.e. <c>TerrainManager::GetZoneAndAreaIdByAreaFlag</c>: the entry itself when it is a
/// zone, else its parent zone), with the flag-to-zone answer cached: the AreaTable search is linear and the brains ask for every
/// candidate creature near a home. 0 where the terrain does not know the area.
/// <para>Thread affinity: world thread (the terrain loads tiles on demand; the cache is a plain dictionary).</para>
/// </summary>
internal sealed class PlayerbotZoneLookup
{
    private readonly Func<float, float, float, uint> _zoneAt;

    /// <summary>A lookup over any function (tests: a fixture of two zones).</summary>
    internal PlayerbotZoneLookup(Func<float, float, float, uint> zoneAt) => _zoneAt = zoneAt;

    /// <summary>The terrain's lookup of <paramref name="mapId"/>.</summary>
    internal static PlayerbotZoneLookup FromTerrain(TerrainManager terrain, uint mapId)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        // Everything here runs on the first lookup, on the world thread (TerrainManager.For is world-thread only).
        TerrainInfo? info = null;
        var zones = new Dictionary<ushort, uint>();
        AreaTable? cachedAreas = null;
        return new PlayerbotZoneLookup((x, y, z) =>
        {
            info ??= terrain.For(mapId);
            // The area table can be replaced (content reload): the cache belongs to the table it was filled from.
            AreaTable areas = terrain.Areas;
            if (!ReferenceEquals(areas, cachedAreas)) { zones.Clear(); cachedAreas = areas; }
            ushort flag = info.GetAreaFlag(x, y, z);
            if (!zones.TryGetValue(flag, out uint zone)) zones[flag] = zone = areas.GetZoneAndAreaId(flag, mapId).ZoneId;
            return zone;
        });
    }

    /// <summary>The zone at a position (0: not known).</summary>
    internal uint ZoneAt(float x, float y, float z) => float.IsFinite(x) && float.IsFinite(y) ? _zoneAt(x, y, z) : 0;
}

/// <summary>Where a bot stands against its home area (<see cref="PlayerbotHomeReturn.Evaluate"/>).</summary>
internal enum PlayerbotHomeState : byte
{
    /// <summary>Inside the radius (or walking back and already inside the return radius), and in the home's zone when it is clipped.</summary>
    Inside,

    /// <summary>Outside the radius, or out of a clipped home's zone, with no errand: walking (or teleporting) back to the centre.</summary>
    Returning,

    /// <summary>
    /// On another map than the home. <see cref="PlayerbotHomeReturnMode.Walk"/>: the bot does not move itself and an operator brings it
    /// back (<c>.playerbot send</c>); the other modes teleport it home when the home map is allowed.
    /// </summary>
    OffMap,

    /// <summary>The way back could not be planned, followed or teleported: set aside for <see cref="PlayerbotHomeReturn.BlockedMs"/>.</summary>
    Blocked,
}

/// <summary>
/// The way back to a bot's home: a walk through <see cref="PlayerbotNavigation.TryPlanToward"/> in route-sized chunks with the hazard
/// guard and risk model of every other walk, or a teleport to the centre (<see cref="PlayerbotHomeOptions.ReturnMode"/>,
/// <see cref="PlayerbotHomeRules.Decide"/>). The teleport is the one the follower's catch-up and <c>.playerbot send</c> use: the
/// ordinary <see cref="TeleportService.TeleportTo(Player, uint, float, float, float, float)"/> (vmangos <c>Player::TeleportTo</c>), its
/// near and far acknowledgements answered by <see cref="PlayerbotMovementControl"/> as a client's would be.
/// <para>
/// Why the teleport exists: Westfall-homed level-14 bots that had drifted to the Darkshire road in Duskwood (about -10703,-918) and to
/// Loch Modan (about -5886,-2951) walked home across higher-level zones and died in death loops on the way (live world.log 2026-10-11,
/// Wfbotfk "death loop ... attackers=202/531/569"), until the operator sent them home by hand (<c>.playerbot send Wfbot* SentinelHill</c>).
/// </para>
/// <para>Thread affinity: world thread, owned by one <see cref="PlayerbotBrain"/>.</para>
/// </summary>
internal sealed class PlayerbotHomeReturn
{
    /// <summary>A walking-back bot that has not moved for this long plans again.</summary>
    internal const uint StuckMs = 5_000;

    /// <summary>How long a bot whose way home failed leaves the walk alone before trying again.</summary>
    internal const uint BlockedMs = 30_000;

    /// <summary>Failed plans (or stalls) in a row before the walk is set aside.</summary>
    internal const int MaxFailures = 3;

    private PlayerbotRoute? _route;
    private Vector3 _lastPosition;
    private uint _stuckMs;
    private int _failures;
    private uint _blockedUntil;
    private bool _walkBlocked;
    private int? _highestAreaLevel;
    private uint _teleportRetryAt;
    private bool _teleportRetryPending;
    private ILogger? _logger;

    internal PlayerbotHomeState State { get; private set; }

    /// <summary>The two-dimensional distance to the centre at the last <see cref="Evaluate"/> (NaN off the home map).</summary>
    internal float Distance { get; private set; } = float.NaN;

    /// <summary>Return teleports made so far (kept across <see cref="Reset"/>: shown by <c>.playerbot status</c> and inspection).</summary>
    internal int Teleports { get; private set; }

    /// <summary>Why the last return teleport was made, or <see cref="PlayerbotHomeTeleportReason.None"/>.</summary>
    internal PlayerbotHomeTeleportReason LastTeleport { get; private set; }

    /// <summary>The last return decision (<see cref="Judge"/>; tests and inspection).</summary>
    internal PlayerbotHomeTeleportReason LastJudgement { get; private set; }

    /// <summary>The walk home was set aside as blocked (not a refused teleport): Auto teleports such a bot at once.</summary>
    internal bool WalkBlocked => State == PlayerbotHomeState.Blocked && _walkBlocked;

    internal void Reset()
    {
        State = PlayerbotHomeState.Inside;
        Distance = float.NaN;
        _route = null;
        _stuckMs = 0;
        _failures = 0;
        _blockedUntil = 0;
        _walkBlocked = false;
        _highestAreaLevel = null;
        _teleportRetryPending = false;
    }

    /// <summary>Decide where the bot stands against <paramref name="area"/>; keeps the hysteresis of a walk in progress.</summary>
    internal PlayerbotHomeState Evaluate(Player player, PlayerbotHomeArea area, uint nowMs)
    {
        uint map = player.Map?.MapId ?? player.MapId;
        if (map != area.MapId)
        {
            _route = null;
            _highestAreaLevel = null;
            Distance = float.NaN;
            return State = PlayerbotHomeState.OffMap;
        }

        Distance = area.DistanceTo(player.X, player.Y);
        // A clipped home counts the bot outside while it stands in another zone, even inside the circle (Raven Hill from Sentinel
        // Hill), so it walks back to the centre, which is in the zone. A home whose own zone is not known cannot be walked into:
        // the walk uses the circle alone (the filters still reject everything, PlayerbotHomeArea.ZoneUnknown).
        bool zoneOk = area.ZoneUnknown || area.InZone(player.X, player.Y, player.Z);
        if (State == PlayerbotHomeState.Blocked)
        {
            if (Distance <= area.Radius && zoneOk) return Arrived();
            if (unchecked((int)(nowMs - _blockedUntil)) < 0) return State;
            _failures = 0;
            _walkBlocked = false;
            State = PlayerbotHomeState.Returning;
        }

        bool returning = State == PlayerbotHomeState.Returning;
        if (Distance <= (returning ? area.ReturnRadius : area.Radius) && zoneOk) return Arrived();
        return State = PlayerbotHomeState.Returning;
    }

    private PlayerbotHomeState Arrived()
    {
        _route = null;
        _failures = 0;
        _walkBlocked = false;
        _highestAreaLevel = null;
        return State = PlayerbotHomeState.Inside;
    }

    /// <summary>
    /// Whether this return is a teleport, and why (<see cref="PlayerbotHomeRules.Decide"/>), after <see cref="Evaluate"/>. The area
    /// levels on the straight line home (Auto, within <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/>) are read once per return.
    /// </summary>
    internal PlayerbotHomeTeleportReason Judge(Player player, PlayerbotHomeArea area, PlayerbotHomeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool onMap = State != PlayerbotHomeState.OffMap;
        int highest = 0;
        if (onMap && options.ReturnMode == PlayerbotHomeReturnMode.Auto && !WalkBlocked && Distance <= options.TeleportDistanceYards
            && area.AreaLevels is { } levels)
            highest = _highestAreaLevel ??= levels.HighestOnLine(new Vector3(player.X, player.Y, player.Z), area.Centre);
        var facts = new PlayerbotHomeFacts(onMap, Distance, highest, (int)player.Level, WalkBlocked);
        return LastJudgement = PlayerbotHomeRules.Decide(facts, options);
    }

    /// <summary>
    /// Teleport the bot to the home's centre. True when a teleport started or is still under way (the bot is busy). False when the bot
    /// may not teleport now: dead or a ghost, in combat or a duel, in a group (whose AI drives it), on a battleground, its home map not
    /// allowed (<see cref="PlayerbotMapPolicy.Allows"/>, the same rule as <c>.playerbot send</c>), no teleport service, or the service
    /// refused it; a refusal is not retried for <see cref="BlockedMs"/>, and on the home map the return is set aside (state blocked: an
    /// unsafe walk is not taken instead).
    /// </summary>
    internal bool TryTeleport(WorldSession session, Player player, PlayerbotHomeArea area, PlayerbotOptions options,
        PlayerbotHomeTeleportReason reason, uint nowMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        TeleportService? teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
        if (_teleportRetryPending && unchecked((int)(nowMs - _teleportRetryAt)) < 0) return false;
        _teleportRetryPending = false;
        // A bot in the air would land in the air at the other end, as the follower waits for a leader in the air: wait for the landing.
        if ((player.Movement.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) != 0) return false;
        if (RefusalOf(session, teleports, player, area, options) is { } refusal)
        {
            Refuse(player, area, reason, refusal, nowMs);
            return false;
        }

        // A teleport under way (this one, not yet acknowledged, or any other): wait for it, start nothing.
        if (teleports!.IsBeingTeleported(player)) return true;

        PlayerbotMovementControl.Stop(session, player);
        var from = new Vector3(player.X, player.Y, player.Z);
        uint fromMap = player.MapId;
        float distance = Distance;
        if (!teleports.TeleportTo(player, area.MapId, area.Centre.X, area.Centre.Y, area.Centre.Z, player.Orientation))
        {
            Refuse(player, area, reason, "the teleport service refused it", nowMs);
            return false;
        }

        Teleports++;
        LastTeleport = reason;
        _route = null;
        _stuckMs = 0;
        _failures = 0;
        _walkBlocked = false;
        _highestAreaLevel = null;
        if (State == PlayerbotHomeState.Blocked) State = PlayerbotHomeState.Returning;
        LoggerOf(session).LogInformation(
            "Playerbot {Bot} teleports home to {Place} ({Reason}; {Distance} yd) from ({X:F1}, {Y:F1}, {Z:F1}) map {Map} to ({HomeX:F1}, {HomeY:F1}, {HomeZ:F1}) map {HomeMap}",
            player.Name, area.Place ?? "its home", PlayerbotHomeRules.Describe(reason),
            float.IsNaN(distance) ? "other map" : distance.ToString("F0", CultureInfo.InvariantCulture),
            from.X, from.Y, from.Z, fromMap, area.Centre.X, area.Centre.Y, area.Centre.Z, area.MapId);
        return true;
    }

    /// <summary>Why the bot may not teleport home now, or null.</summary>
    private static string? RefusalOf(WorldSession session, TeleportService? teleports, Player player, PlayerbotHomeArea area, PlayerbotOptions options)
    {
        if (teleports is null || !session.IsManaged) return "no teleport service";
        if (!player.IsAlive) return "dead";
        if (player.Combat.IsInCombat) return "in combat";
        // vmangos PartyBotAI.cpp:808: no catch-up teleport while the bot duels (!IsInDuel()); the same here.
        if (player.Duel is not null) return "in a duel";
        if (session.Services.GetService<Social.SocialFeature>()?.Context.Groups.GetGroup(player.Guid) is not null) return "in a group";
        if (player.Map?.Template is { IsBattleground: true }) return "on a battleground";
        WorldMaps maps = WorldMaps.Of(session.World);
        MapTemplate? template = maps.Registry.Find(area.MapId);
        if (template is null || template.IsBattleground) return "home map unknown";
        if (!PlayerbotMapPolicy.Allows(options.AllowedMaps, area.MapId, template, current: false)) return "home map not allowed";
        return null;
    }

    private void Refuse(Player player, PlayerbotHomeArea area, PlayerbotHomeTeleportReason reason, string refusal, uint nowMs)
    {
        _teleportRetryPending = true;
        _teleportRetryAt = unchecked(nowMs + BlockedMs);
        if (player.Session is WorldSession session)
            LoggerOf(session).LogDebug("Playerbot {Bot} does not teleport home to {Place} ({Reason}): {Refusal}",
                player.Name, area.Place ?? "its home", PlayerbotHomeRules.Describe(reason), refusal);
        // On the home map the walk was judged unsafe (or is blocked): set the return aside rather than walk it.
        if (State != PlayerbotHomeState.OffMap) MarkBlocked(nowMs, walk: false);
    }

    private ILogger LoggerOf(WorldSession session)
        => _logger ??= session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotHomeReturn>() ?? NullLogger<PlayerbotHomeReturn>.Instance;

    /// <summary>
    /// One think of the walk home. True when a step was attempted (the bot is busy), false when no route could be planned (the goals
    /// carry on; after <see cref="MaxFailures"/> the walk is set aside for <see cref="BlockedMs"/>).
    /// </summary>
    internal bool Update(WorldSession session, Player player, PlayerbotHomeArea area, PlayerbotOptions options, uint elapsedMs, uint nowMs)
    {
        if (State != PlayerbotHomeState.Returning || elapsedMs == 0) return false;
        Vector3 position = new(player.X, player.Y, player.Z);
        if (_route is { Complete: false })
        {
            if (Vector3.DistanceSquared(position, _lastPosition) < 0.0025f) _stuckMs += elapsedMs;
            else { _stuckMs = 0; _failures = 0; }
            if (_stuckMs >= StuckMs)
            {
                _route = null;
                _stuckMs = 0;
                if (Fail(nowMs)) return false;
            }
        }

        _lastPosition = position;
        if (_route is null or { Complete: true })
        {
            if (!PlayerbotNavigation.TryPlanToward(player, area.Centre, options, out _route))
            {
                _route = null;
                PlayerbotMovementControl.Stop(session, player);
                Fail(nowMs);
                return false;
            }

            _stuckMs = 0;
        }

        if (!PlayerbotNavigation.TryAdvance(session, _route!, options, elapsedMs, nowMs)) _route = null;
        return true;
    }

    /// <summary>A stall on the walk (<see cref="PlayerbotStallWatch"/>): set it aside at once.</summary>
    internal void MarkBlocked(uint nowMs) => MarkBlocked(nowMs, walk: true);

    private void MarkBlocked(uint nowMs, bool walk)
    {
        _route = null;
        _stuckMs = 0;
        _walkBlocked = walk;
        State = PlayerbotHomeState.Blocked;
        _blockedUntil = unchecked(nowMs + BlockedMs);
    }

    private bool Fail(uint nowMs)
    {
        if (++_failures < MaxFailures) return false;
        MarkBlocked(nowMs);
        return true;
    }

    /// <summary>
    /// The short form of the status line: <c>inside:120</c>, <c>returning:810</c>, <c>off-map</c> or <c>blocked:810</c>, with
    /// <c>,teleports=N</c> once the bot has teleported home; the distance is rounded to 10 yards so a walking bot does not change its
    /// status line at every step. The text is rebuilt only when it changes (the feature reads it every tick for every bot).
    /// </summary>
    internal string Report
    {
        get
        {
            int rounded = float.IsNaN(Distance) ? -1 : (int)(MathF.Round(Distance / 10f) * 10f);
            if (_report is null || _reportState != State || _reportRounded != rounded || _reportTeleports != Teleports)
            {
                _reportState = State;
                _reportRounded = rounded;
                _reportTeleports = Teleports;
                string state = State switch
                {
                    PlayerbotHomeState.OffMap => "off-map",
                    PlayerbotHomeState.Returning => string.Create(CultureInfo.InvariantCulture, $"returning:{rounded}"),
                    PlayerbotHomeState.Blocked => string.Create(CultureInfo.InvariantCulture, $"blocked:{rounded}"),
                    _ => rounded < 0 ? "inside" : string.Create(CultureInfo.InvariantCulture, $"inside:{rounded}"),
                };
                _report = Teleports == 0 ? state : string.Create(CultureInfo.InvariantCulture, $"{state},teleports={Teleports}");
            }

            return _report;
        }
    }

    private string? _report;
    private PlayerbotHomeState _reportState;
    private int _reportRounded;
    private int _reportTeleports;

    /// <summary>
    /// The long form for inspection: place, map, centre, radius, zone (<c>off</c>, <c>unknown</c> or the id), distance, state, the return
    /// mode, the return teleports so far and why the last one was made.
    /// </summary>
    internal string Describe(PlayerbotHomeArea area, PlayerbotHomeReturnMode mode)
        => string.Create(CultureInfo.InvariantCulture,
            $"place={area.Place ?? "none"} map={area.MapId} centre={area.Centre.X:F1},{area.Centre.Y:F1},{area.Centre.Z:F1} radius={area.Radius:F0} zone={area.ZoneText} distance={(float.IsNaN(Distance) ? "n/a" : Distance.ToString("F1", CultureInfo.InvariantCulture))} state={State.ToString().ToLowerInvariant()} mode={mode.ToString().ToLowerInvariant()} teleports={Teleports}{(LastTeleport == PlayerbotHomeTeleportReason.None ? string.Empty : " last-teleport=" + LastTeleport.ToString().ToLowerInvariant())}");
}

/// <summary>
/// Which bots a <c>.playerbot home</c> names: one bot by id or name, or every bot whose name starts with a prefix (<c>Wfbot*</c>).
/// A prefix is 2 to 11 letters and one trailing <c>*</c>, so a stray <c>a*</c> cannot anchor the whole fleet. Matching ignores case
/// and covers stopped bots too.
/// </summary>
internal readonly record struct PlayerbotHomeSelector(string Text, bool Prefix, Guid? Id)
{
    internal const int MinPrefixLength = 2;
    internal const int MaxPrefixLength = 11;

    /// <summary>The longest bot name (a character name is at most 12 letters).</summary>
    internal const int MaxNameLength = 12;

    internal static bool TryParse(string? text, out PlayerbotHomeSelector selector)
    {
        selector = default;
        string value = text?.Trim() ?? string.Empty;
        if (value.Length == 0) return false;
        if (Guid.TryParse(value, out Guid id)) { selector = new(value, false, id); return true; }
        if (value[^1] == '*')
        {
            string prefix = value[..^1];
            if (prefix.Length is < MinPrefixLength or > MaxPrefixLength || !prefix.All(char.IsLetter)) return false;
            selector = new(prefix, true, null);
            return true;
        }

        if (value.Length > MaxNameLength || value.Contains('*', StringComparison.Ordinal)) return false;
        selector = new(value, false, null);
        return true;
    }

    internal bool Matches(PlayerbotStatus status)
        => Id is { } id ? status.BotId == id
            : Prefix ? status.Name.StartsWith(Text, StringComparison.OrdinalIgnoreCase)
            : status.Name.Equals(Text, StringComparison.OrdinalIgnoreCase);
}
