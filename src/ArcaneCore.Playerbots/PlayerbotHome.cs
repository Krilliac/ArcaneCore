using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ArcaneCore.World.Bots;

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
    private readonly Dictionary<uint, bool> _leaving = [];
    private readonly Dictionary<uint, bool> _valley = [];
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

    /// <summary>
    /// A graduated start home (<see cref="Population.PopulationStartAreas"/>, the <c>zone:</c> stage): quests whose turn-in lies outside
    /// the area may be taken (<see cref="QuestAllowed"/>). False for an operator's home and for a start home still in its valley.
    /// </summary>
    internal bool AllowsLeaveQuests { get; init; }

    /// <summary>
    /// A start-stage home (<see cref="Population.PopulationStartAreas.ValleyRadiusOf"/>): a quest that is not for a class and whose turn-in or
    /// kill targets lie beyond this many yards of the centre (the next town: the level 5 breadcrumbs; a quest with a prerequisite gets <see cref="Population.PopulationStartAreas.ChainSlackYards"/> more) is not taken in the valley. 0: no gate.
    /// </summary>
    internal float ValleyRadius { get; init; }

    /// <summary>
    /// A town home (<see cref="Population.PopulationTownProgression"/>): the bot walks to it from up to this many yards on the same side of the
    /// water (the Auto return mode's teleport distance is at least this); 0 for every other home, which follows the Home options alone.
    /// </summary>
    internal float WalkRangeYards { get; init; }

    /// <summary>A town home with <c>TownProgressionTeleport</c> on: water between the bot and the town makes the return a teleport. Off: the bot walks (the sweep assigns only towns it can reach).</summary>
    internal bool TownTeleports { get; init; }

    /// <summary>Where a town home sits in the capital/secondary cycle: an idle bot lingers at the centre of the town it stays in (<see cref="Population.TownStay.None"/> for other homes).</summary>
    internal Population.TownStay Stay { get; init; }

    /// <summary>The live population options (<see cref="Population.PlayerbotPopulationOptions.LeaveHomeLevelMargin"/>); null reads a margin of 0.</summary>
    internal Population.PlayerbotPopulationOptions? LeavePolicy { get; init; }

    /// <summary>The AreaTable zone of the centre for a clipped home; 0 when unclipped or not known. World thread.</summary>
    internal uint ZoneId
    {
        get
        {
            if (_zoneId is { } known) return known;
            uint zone = _zones is { } zones ? zones.ZoneAt(Centre.X, Centre.Y, Centre.Z) : 0;
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
        => !ZoneClipped || ZoneId is not 0 and var zone && _zones is { } zones && zones.ZoneAt(x, y, z) == zone;

    internal static bool IsNullIsland(uint mapId, float x, float y)
        => mapId is 0 or 1 && MathF.Abs(x) < 1f && MathF.Abs(y) < 1f;

    /// <summary>
    /// Whether a point on <paramref name="map"/> lies inside the area: inside the circle and, for a clipped home, in its zone
    /// (another map: never). The circle is tested first, so the zone is looked up only for points near the home.
    /// </summary>
    internal bool Contains(uint map, float x, float y, float z = 0f)
        => map == MapId
           && float.IsFinite(x)
           && float.IsFinite(y)
           && (!IsNullIsland(map, x, y) || IsNullIsland(MapId, Centre.X, Centre.Y))
           && DistanceTo(x, y) <= Radius
           && InZone(x, y, z);

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

    /// <summary>The farthest a leave-home quest's turn-in may be from the home centre (yards): the same zone or the next band's town, not across the continent.</summary>
    internal float LeaveTurnInYards { get; init; } = 3000f;

    /// <summary>
    /// Whether <paramref name="quest"/> is a leave-home quest: it has creature enders and none of them has a spawn inside, any spawned
    /// creature starter on the home map has a spawn inside, and every creature it asks the bot to kill does (so only the turn-in lies
    /// outside; a quest begun in another zone or sending the bot out to hunt is not one).
    /// Cached per quest like <see cref="QuestInside"/>. Item and object objectives (drops, nodes) are not checked, as in <see cref="QuestInside"/>.
    /// </summary>
    internal bool QuestLeavesHome(QuestStore quests, CreatureContent content, Quest quest)
    {
        Sync(content, quests);
        if (_leaving.TryGetValue(quest.Id, out bool known)) return known;
        bool leaves = false;
        IReadOnlyList<uint> enders = quests.CreatureEndersOf(quest.Id);
        if (enders.Count > 0)
        {
            leaves = true;
            bool near = false;
            foreach (uint ender in enders)
            {
                if (HasSpawnInside(content, ender))
                {
                    leaves = false;
                    break;
                }

                // Only a turn-in within LeaveTurnInYards of the centre (the same zone, or the next band's town) is a leave-home quest.
                foreach (CreatureSpawn spawn in content.GetSpawns(MapId, ender))
                {
                    float dx = spawn.X - Centre.X, dy = spawn.Y - Centre.Y;
                    if ((dx * dx) + (dy * dy) <= LeaveTurnInYards * LeaveTurnInYards) { near = true; break; }
                }
            }

            if (!near) leaves = false;
        }

        if (leaves)
        {
            bool anyStarterSpawned = false, starterInside = false;
            foreach (uint starter in quests.CreatureStartersOf(quest.Id))
            {
                if (content.GetSpawns(MapId, starter).Count == 0) continue;
                anyStarterSpawned = true;
                if (!HasSpawnInside(content, starter)) continue;
                starterInside = true;
                break;
            }

            if (anyStarterSpawned && !starterInside) leaves = false;
        }

        for (int index = 0; leaves && index < quest.ReqCreatureOrGOId.Count; index++)
        {
            int objective = quest.ReqCreatureOrGOId[index];
            if (objective > 0 && !HasSpawnInside(content, (uint)objective)) leaves = false;
        }

        return _leaving[quest.Id] = leaves;
    }

    /// <summary>
    /// The lowest level a bot may take a leave-home quest at: the quest's MinLevel, and its QuestLevel less
    /// <see cref="Population.PlayerbotPopulationOptions.LeaveHomeLevelMargin"/> (a QuestLevel of 0 or less follows the player and adds nothing).
    /// </summary>
    internal uint LeaveLevelOf(Quest quest)
    {
        int margin = LeavePolicy?.LeaveHomeLevelMargin ?? 0;
        return (uint)Math.Max(quest.MinLevel, quest.QuestLevel > 0 ? quest.QuestLevel - margin : 0);
    }

    /// <summary>
    /// Whether a bot of <paramref name="playerLevel"/> may take <paramref name="quest"/> under this home: it is inside
    /// (<see cref="QuestInside"/>), or the home has graduated (<see cref="AllowsLeaveQuests"/>), the quest only leaves it for the
    /// turn-in (<see cref="QuestLeavesHome"/>) and the bot is old enough for it (<see cref="LeaveLevelOf"/>). The one rule of the
    /// travel planner and the quest-giver controller (<see cref="PlayerbotWorldDestinations.MayTakeQuest"/>).
    /// </summary>
    internal bool QuestAllowed(QuestStore quests, CreatureContent content, Quest quest, uint playerLevel)
        => QuestInside(quests, content, quest) && QuestInValley(quests, content, quest)
            || (AllowsLeaveQuests && playerLevel >= LeaveLevelOf(quest) && QuestLeavesHome(quests, content, quest));

    /// <summary>
    /// Whether <paramref name="quest"/> stays in the start valley (<see cref="ValleyRadius"/>): always without a valley gate and for a quest
    /// restricted to classes; otherwise every creature that ends it, and every creature it asks the bot to kill, has a spawn within the valley
    /// radius. Item and object objectives are not checked (their drops and nodes are not indexed by quest here). Cached per quest.
    /// </summary>
    internal bool QuestInValley(QuestStore quests, CreatureContent content, Quest quest)
    {
        if (ValleyRadius <= 0f || quest.Template.RequiredClasses != 0) return true;
        Sync(content, quests);
        if (_valley.TryGetValue(quest.Id, out bool known)) return known;
        bool inValley = true;
        float radius = ValleyRadius + (quest.Template.PrevQuestId != 0 ? Population.PopulationStartAreas.ChainSlackYards : 0f);
        foreach (uint ender in quests.CreatureEndersOf(quest.Id))
            if (inValley && !HasSpawnWithin(this, content, ender, radius)) inValley = false;
        for (int index = 0; inValley && index < quest.ReqCreatureOrGOId.Count; index++)
        {
            int objective = quest.ReqCreatureOrGOId[index];
            if (objective > 0 && !HasSpawnWithin(this, content, (uint)objective, radius)) inValley = false;
        }

        return _valley[quest.Id] = inValley;
    }

    private static bool HasSpawnWithin(PlayerbotHomeArea area, CreatureContent content, uint entry, float radius)
    {
        foreach (CreatureSpawn spawn in content.GetSpawns(area.MapId, entry))
            if (area.DistanceTo(spawn.X, spawn.Y) <= radius) return true;
        return false;
    }

    /// <summary>Drop the caches when the creature content or the quest store changed since they were filled.</summary>
    private void Sync(CreatureContent content, QuestStore? quests)
    {
        if (ReferenceEquals(_cachedContent, content) && _cachedVersion == content.DefinitionsVersion
            && (quests is null || ReferenceEquals(_cachedQuests, quests))) return;
        _entries.Clear();
        _quests.Clear();
        _leaving.Clear();
        _valley.Clear();
        _cachedContent = content;
        _cachedVersion = content.DefinitionsVersion;
        if (quests is not null) _cachedQuests = quests;
    }
}

/// <summary>
/// The AreaTable zone at a position of one map, for the zone clip of <see cref="PlayerbotHomeArea"/>. Over the terrain it is exactly
/// what <c>Map.GetZoneAndAreaId</c> answers (vmangos <c>TerrainInfo::GetZoneAndAreaId</c>: the area flag, the WMO group's area where the position is inside one and the tile's cell otherwise, then
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
        // The lookups run on the world thread (TerrainManager.For is world-thread only).
        var zones = new Dictionary<ushort, uint>();
        AreaTable? cachedAreas = null;
        return new PlayerbotZoneLookup((x, y, z) =>
        {
            // Asked for each lookup, not kept: a map id's terrain goes once its last map is unloaded (TerrainManager.Release).
            TerrainInfo info = terrain.For(mapId);
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
internal sealed partial class PlayerbotHomeReturn
{
    /// <summary>A walking-back bot that has not moved for this long plans again.</summary>
    internal const uint StuckMs = 5_000;

    /// <summary>How long a bot whose way home failed leaves the walk alone before trying again.</summary>
    internal const uint BlockedMs = 30_000;

    /// <summary>Failed plans (or stalls) in a row before the walk is set aside.</summary>
    internal const int MaxFailures = 3;

    /// <summary>
    /// Minimum two-dimensional progress (yards) toward the home's centre required to clear <see cref="_failures"/> or count a completed
    /// route as progress; a partial route that moves sideways along a wall or ridge without closing on the home by at least this much
    /// counts as a failure instead of resetting <see cref="_failures"/> on every step.
    /// </summary>
    internal const float ProgressYards = 4f;

    /// <summary>
    /// The shortest time between two return teleports of one bot (5 minutes, an ArcaneCore choice: no reference server has a home).
    /// A bot sent home that is out again within it is not teleported again: its return is set aside (blocked, tried again every
    /// <see cref="BlockedMs"/>) until the time has passed. A home the bot cannot stay in (an area it keeps leaving, a fight that drags
    /// it out) so costs at most one teleport per 5 minutes per bot instead of one per think; 5 minutes is several times what a bot
    /// needs to walk back across a 750-yard home (about 7 yards a second), so an ordinary stray is never held by it.
    /// </summary>
    internal const uint TeleportCooldownMs = 300_000;

    private PlayerbotRoute? _route;
    private TileWait _tileWait;
    private Vector3 _lastPosition;
    private uint _stuckMs;
    private int _failures;
    private float _bestDistance = float.MaxValue;
    private float _routeStartDistance = float.MaxValue;
    private uint _blockedUntil;
    private bool _walkBlocked;
    private int? _highestAreaLevel;
    private uint _teleportRetryAt;
    private bool _teleportRetryPending;
    private ILogger? _logger;
    private uint _lastTeleportAt;

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

    /// <summary>Whether <see cref="TeleportCooldownMs"/> has passed since the last return teleport (or there was none).</summary>
    internal bool CanTeleportNow(uint nowMs) => Teleports == 0 || unchecked(nowMs - _lastTeleportAt) >= TeleportCooldownMs;

    internal void Reset()
    {
        State = PlayerbotHomeState.Inside;
        Distance = float.NaN;
        _route = null;
        _stuckMs = 0;
        _failures = 0;
        _bestDistance = float.MaxValue;
        _routeStartDistance = float.MaxValue;
        _routePlannedDistance = 0f;
        _blockedUntil = 0;
        _walkBlocked = false;
        _walkBlockedReported = false;
        _legProgressed = false;
        _highestAreaLevel = null;
        _teleportRetryPending = false;
        _returnTracked = false;
        _returnOverdue = false;
        _returnUpdated = false;
    }

    /// <summary>Decide where the bot stands against <paramref name="area"/>; keeps the hysteresis of a walk in progress.</summary>
    internal PlayerbotHomeState Evaluate(Player player, PlayerbotHomeArea area, uint nowMs)
    {
        if (!ReferenceEquals(_validatedArea, area))
        {
            _validatedArea = area;
            CheckArea(player, area);
        }

        PlayerbotHomeState state = EvaluateCore(player, area, nowMs);
        TrackReturn(player, area, state, nowMs);
        return state;
    }

    /// <summary>A walk home (returning, or set aside as blocked) that has run this long (ms) without arriving: a 750-yard home is a couple of minutes' walk.</summary>
    internal const uint ReturnBudgetMs = 900_000;

    private PlayerbotHomeArea? _validatedArea;
    private bool _returnTracked;
    private bool _returnReported;
    private bool _returnOverdue;
    private bool _returnUpdated;
    private bool _legProgressed;
    private bool _walkBlockedReported;
    private float _routePlannedDistance;
    private uint _returnSince;
    private uint _lastTrackMs;
    private float _returnBestDistance = float.MaxValue;

    /// <summary>A home that can hold nothing: no finite centre, no radius, or the null island of a map where that is no place (<c>home/invalid</c>). Once per area.</summary>
    private static void CheckArea(Player player, PlayerbotHomeArea area)
    {
        string? fault = !float.IsFinite(area.Centre.X) || !float.IsFinite(area.Centre.Y) || !float.IsFinite(area.Centre.Z) ? "its centre is not finite"
            : !float.IsFinite(area.Radius) || area.Radius <= 0f ? "its radius is not a positive number"
            : PlayerbotHomeArea.IsNullIsland(area.MapId, area.Centre.X, area.Centre.Y) ? "its centre is the null island"
            : null;
        if (fault is null || PlayerbotNavigationAnomalies.Silenced(player)) return;
        PlayerbotAnomalies.Report(player, "home", "invalid",
            string.Create(CultureInfo.InvariantCulture, $"home {area.Place ?? "(unnamed)"} on map {area.MapId} is invalid: {fault} (centre {area.Centre.X},{area.Centre.Y},{area.Centre.Z}, radius {area.Radius})"));
    }

    /// <summary>
    /// The return began at <c>_returnSince</c>; past <see cref="ReturnBudgetMs"/> it is reported once (<c>home/return-timeout</c>) and
    /// remembered (<see cref="_returnOverdue"/>) so Auto stops walking a town home it will not reach (bot QA #818).
    /// </summary>
    private void TrackReturn(Player player, PlayerbotHomeArea area, PlayerbotHomeState state, uint nowMs)
    {
        if (state is not (PlayerbotHomeState.Returning or PlayerbotHomeState.Blocked))
        {
            _returnTracked = false;
            _returnOverdue = false;
            _returnUpdated = false;
            return;
        }

        if (!_returnTracked)
        {
            _returnTracked = true;
            _returnReported = false;
            _returnOverdue = false;
            _returnUpdated = false;
            _returnSince = nowMs;
            _lastTrackMs = nowMs;
            _returnBestDistance = float.IsFinite(Distance) ? Distance : float.MaxValue;
            return;
        }

        uint dt = unchecked(nowMs - _lastTrackMs);
        _lastTrackMs = nowMs;
        bool wasUpdated = _returnUpdated;
        _returnUpdated = false;
        if (!player.IsAlive || player.Combat.IsInCombat
            || PlayerbotTaxi.IsFlying(player) || PlayerbotTaxi.IsActive(player)
            || PlayerbotTram.IsTraveling(player) || PlayerbotElevator.IsTraveling(player)
            || PlayerbotTravel.JourneyOf(player) is { State: JourneyState.Active })
        {
            _returnSince = unchecked(_returnSince + Math.Min(dt, ReturnBudgetMs));
            return;
        }

        if (_legProgressed || (float.IsFinite(Distance) && Distance <= _returnBestDistance - 25f))
        {
            _legProgressed = false;
            if (float.IsFinite(Distance)) _returnBestDistance = MathF.Min(_returnBestDistance, Distance);
            _returnSince = nowMs;
            return;
        }

        if (state == PlayerbotHomeState.Returning && !wasUpdated && dt is > 100 and <= 15_000)
        {
            _returnSince = unchecked(_returnSince + dt);
            return;
        }

        if (unchecked(nowMs - _returnSince) <= ReturnBudgetMs) return;
        // The walk (or the blocked wait that followed it) used the whole budget and the bot is still not home. Auto teleports
        // on the next judgement, the same as a walk set aside as blocked. A town home's walk range is the whole continent when
        // town teleports are off, so distance alone never chooses the teleport (bot QA #818: 15 min, still thousands of yards out).
        _returnOverdue = true;
        if (_returnReported || PlayerbotNavigationAnomalies.Silenced(player)) return;
        _returnReported = true;
        PlayerbotAnomalies.Report(player, "home", "return-timeout",
            string.Create(CultureInfo.InvariantCulture, $"{state.ToString().ToLowerInvariant()} for {unchecked(nowMs - _returnSince) / 60_000} min, {Distance:F0} yd from the centre of {area.Place ?? "its home"}, and never arrived"));
    }

    private PlayerbotHomeState EvaluateCore(Player player, PlayerbotHomeArea area, uint nowMs)
    {
        uint map = player.Map?.MapId ?? player.MapId;
        if (map != area.MapId)
        {
            _route = null;
            _highestAreaLevel = null;
            _bestDistance = float.MaxValue;
            _routeStartDistance = float.MaxValue;
            Distance = float.NaN;
            return State = PlayerbotHomeState.OffMap;
        }

        Distance = area.DistanceTo(player.X, player.Y);
        bool onNullIsland = PlayerbotHomeArea.IsNullIsland(player.Map?.MapId ?? player.MapId, player.X, player.Y)
            && !PlayerbotHomeArea.IsNullIsland(area.MapId, area.Centre.X, area.Centre.Y);
        // A clipped home counts the bot outside while it stands in another zone, even inside the circle (Raven Hill from Sentinel
        // Hill), so it walks back to the centre, which is in the zone. A home whose own zone is not known cannot be walked into:
        // the walk uses the circle alone (the filters still reject everything, PlayerbotHomeArea.ZoneUnknown).
        bool zoneOk = !onNullIsland && (area.ZoneUnknown || area.InZone(player.X, player.Y, player.Z));
        if (State == PlayerbotHomeState.Blocked)
        {
            if (Distance <= area.Radius && zoneOk) return Arrived();
            if (unchecked((int)(nowMs - _blockedUntil)) < 0) return State;
            _failures = 0;
            _walkBlocked = false;
            _bestDistance = float.MaxValue;
            _routeStartDistance = float.MaxValue;
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
        _walkBlockedReported = false;
        _legProgressed = false;
        _routePlannedDistance = 0f;
        _bestDistance = float.MaxValue;
        _routeStartDistance = float.MaxValue;
        _highestAreaLevel = null;
        return State = PlayerbotHomeState.Inside;
    }

    private IPlayerbotHomeTravel? _travel;

    /// <summary>
    /// Whether this return is a teleport, and why (<see cref="PlayerbotHomeRules.Decide"/>), after <see cref="Evaluate"/>. The area
    /// levels on the straight line home (Auto, within <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/>) are read once per return.
    /// </summary>
    internal PlayerbotHomeTeleportReason Judge(Player player, PlayerbotHomeArea area, PlayerbotHomeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _returnUpdated = true;
        bool onMap = State != PlayerbotHomeState.OffMap;
        uint mapId = player.Map?.MapId ?? player.MapId;
        if (onMap
            && options.ReturnMode != PlayerbotHomeReturnMode.Walk
            && PlayerbotHomeArea.IsNullIsland(mapId, player.X, player.Y)
            && !PlayerbotHomeArea.IsNullIsland(area.MapId, area.Centre.X, area.Centre.Y))
        {
            return LastJudgement = PlayerbotHomeTeleportReason.Island;
        }

        // Past the return budget the walk is given up (TrackReturn). Auto teleports; Walk mode keeps walking.
        if (_returnOverdue && options.ReturnMode == PlayerbotHomeReturnMode.Auto)
            return LastJudgement = PlayerbotHomeTeleportReason.WalkBlocked;

        // A town home is walked to from farther away than a Home-option home (WalkRangeYards), unless water no bot crosses lies between
        // (Teldrassil): then the distance counts as unbounded and the bot takes the return teleport.
        float teleportAt = PlayerbotHomeRules.TeleportDistanceFor(options, area.WalkRangeYards);
        float distance = onMap && area.WalkRangeYards > 0f && area.TownTeleports
            && Population.PopulationTownProgression.SeparatedByWater(mapId, player.X, player.Y, area.MapId, area.Centre.X, area.Centre.Y)
            ? float.PositiveInfinity : Distance;
        int highest = 0;
        // TownProgressionTeleport off: the way to a town home is never judged dangerous (a progression move does not teleport); the other
        // reasons (another map, a blocked walk, the return mode) are the Home options' own returns of a displaced bot and still apply.
        bool noTownTeleport = area.WalkRangeYards > 0f && !area.TownTeleports;
        if (onMap && options.ReturnMode == PlayerbotHomeReturnMode.Auto && !WalkBlocked && distance <= teleportAt && !noTownTeleport
            && area.AreaLevels is { } levels)
            highest = _highestAreaLevel ??= DangerLevelOnLine(new Vector3(player.X, player.Y, player.Z), area, levels);
        var facts = new PlayerbotHomeFacts(onMap, distance, highest, (int)player.Level, WalkBlocked);
        // Resolved once per brain: the lookup would otherwise run on every think of a returning bot.
        _travel ??= (player.Session as WorldSession)?.Services.GetService<IPlayerbotHomeTravel>() ?? DefaultHomeTravel.Instance;
        return LastJudgement = _travel.Decide(facts, options, teleportAt);
    }

    /// <summary>
    /// The highest area level on the straight line home that counts for the danger check: the points inside the home area itself are
    /// skipped, and a level no higher than the area at the home's centre counts as 0. The operator homed the bot there, so its own area
    /// is not a danger on the way to it; without this a bot homed in an area above its level + margin would teleport at every return.
    /// </summary>
    internal static int DangerLevelOnLine(Vector3 from, PlayerbotHomeArea area, PlayerbotAreaLevelLookup levels)
    {
        int highest = levels.HighestOnLine(from, area.Centre,
            at => area.Contains(area.MapId, at.X, at.Y, at.Z));
        return highest > levels.LevelAt(area.Centre.X, area.Centre.Y, area.Centre.Z) ? highest : 0;
    }

    /// <summary>
    /// Teleport the bot to the home's centre. True when a teleport started or is still under way (the bot is busy). False when the bot
    /// may not teleport now: dead or a ghost, in combat or a duel, in a group (whose AI drives it), on a battleground, its home map not
    /// allowed by <see cref="PlayerbotMapPolicy.Allows"/> (<c>AllowedMaps</c>), its home map a dungeon or raid it is not already inside
    /// (the instance rule of <c>.playerbot send</c>: vmangos <c>HandleGroupgoCommand</c> refuses a summon into an instance without the
    /// group, LANG_CANNOT_SUMMON_TO_INST; a lone bot has no group, so only a near teleport inside its own instance is allowed), it
    /// teleported home less than <see cref="TeleportCooldownMs"/> ago, no teleport service, or the service refused it; a refusal is not
    /// retried for <see cref="BlockedMs"/>, and on the home map the return is set aside (state blocked: an unsafe walk is not taken instead).
    /// </summary>
    /// <param name="urgent">
    /// The escape from an unsafe spirit-healer landing (<see cref="PlayerbotHomeTeleportReason.UnsafeLanding"/>): neither the cooldown nor
    /// the wait after a refusal holds it back (it happens at most once per resurrection); every other refusal stands.
    /// </param>
    internal bool TryTeleport(WorldSession session, Player player, PlayerbotHomeArea area, PlayerbotOptions options,
        PlayerbotHomeTeleportReason reason, uint nowMs, bool urgent = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        TeleportService? teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
        if (!urgent && _teleportRetryPending && unchecked((int)(nowMs - _teleportRetryAt)) < 0) return false;
        _teleportRetryPending = false;
        // A bot in the air would land in the air at the other end, as the follower waits for a leader in the air: wait for the landing.
        if ((player.Movement.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) != 0) return false;
        if (RefusalOf(session, teleports, player, area, options) is { } refusal)
        {
            Refuse(player, area, reason, refusal, nowMs);
            return false;
        }

        if (teleports is null) return false;

        // A teleport under way (this one, not yet acknowledged, or any other): wait for it, start nothing. Not for the escape from an
        // unsafe landing: the near teleport pending then is the resurrection's own, INTO the landing (the corpse's graveyard,
        // GraveyardRepopService.TeleportToCorpseGraveyard), and a near teleport is replaced by the next one (TeleportService.TeleportTo).
        if (teleports.IsBeingTeleported(player) && !(urgent && teleports.IsBeingTeleportedNear(player))) return !urgent;
        bool townFallback = area.WalkRangeYards > 0f && (reason == PlayerbotHomeTeleportReason.WalkBlocked || _returnOverdue);
        if (!urgent && !townFallback && !CanTeleportNow(nowMs))
        {
            Refuse(player, area, reason, "teleported home less than 5 minutes ago", nowMs);
            return false;
        }

        Invariant.Check(float.IsFinite(area.Centre.X) && float.IsFinite(area.Centre.Y) && float.IsFinite(area.Centre.Z),
            $"Home teleport destination coordinates must be finite: ({area.Centre.X}, {area.Centre.Y}, {area.Centre.Z})");
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
        _lastTeleportAt = nowMs;
        _route = null;
        _stuckMs = 0;
        _failures = 0;
        _walkBlocked = false;
        _bestDistance = float.MaxValue;
        _routeStartDistance = float.MaxValue;
        _highestAreaLevel = null;
        if (State == PlayerbotHomeState.Blocked) State = PlayerbotHomeState.Returning;
        LoggerOf(session).LogInformation(
            "Playerbot {Bot} teleports home to {Place} ({Reason}; {Distance} yd) from ({X:F1}, {Y:F1}, {Z:F1}) map {Map} to ({HomeX:F1}, {HomeY:F1}, {HomeZ:F1}) map {HomeMap}",
            player.Name, area.Place ?? "its home", PlayerbotHomeRules.Describe(reason),
            float.IsNaN(distance) ? "other map" : distance.ToString("F0", CultureInfo.InvariantCulture),
            from.X, from.Y, from.Z, fromMap, area.Centre.X, area.Centre.Y, area.Centre.Z, area.MapId);
        return true;
    }

    /// <summary>
    /// Why a <see cref="TryTeleport"/> that is not urgent would be refused now, or null, without its side effects (no retry delay, the
    /// return not set aside as blocked): the brain asks before a teleport home that it would otherwise make to the bind point
    /// (PlayerbotBrain.EscapeIsland), so a refusal never marks the walk of a bot inside its home as blocked.
    /// </summary>
    internal string? TeleportRefusal(WorldSession session, Player player, PlayerbotHomeArea area, PlayerbotOptions options, uint nowMs)
    {
        TeleportService? teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
        if (_teleportRetryPending && unchecked((int)(nowMs - _teleportRetryAt)) < 0) return "a refused teleport waits for its retry";
        if ((player.Movement.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) != 0) return "in the air";
        if (RefusalOf(session, teleports, player, area, options) is { } refusal) return refusal;
        if (teleports is null) return "no teleport service";
        if (teleports.IsBeingTeleported(player)) return "a teleport under way";
        return CanTeleportNow(nowMs) ? null : "teleported home less than 5 minutes ago";
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
        // .playerbot send (PlayerbotFleetCommands.SkipReason, vmangos HandleGroupgoCommand, TeleportCommands.cpp:181-266): nobody is
        // summoned into an instance without its group; a bot already inside the very instance stays there (a near teleport, which keeps
        // the bot's own instance). A lone bot has no group, so a home on a dungeon or raid map is reached only from inside it.
        if (template.Instanceable && player.Map?.MapId != area.MapId) return "home map is an instance";
        return null;
    }

    private void Refuse(Player player, PlayerbotHomeArea area, PlayerbotHomeTeleportReason reason, string refusal, uint nowMs)
    {
        _teleportRetryPending = true;
        _teleportRetryAt = unchecked(nowMs + BlockedMs);
        // A bot that is dead, fighting, grouped, dueling, on a battleground or just teleported is told no for a good reason; any other
        // refusal (no teleport service, home map unknown or not allowed or an instance, the service said no) is a fault in the setup.
        if (refusal is not ("dead" or "in combat" or "in a duel" or "in a group" or "on a battleground" or "teleported home less than 5 minutes ago")
            && !PlayerbotNavigationAnomalies.Silenced(player))
            PlayerbotAnomalies.Report(player, "home", "teleport-refused",
                $"the teleport home to {area.Place ?? "its home"} (map {area.MapId}) was refused: {refusal} ({PlayerbotHomeRules.Describe(reason)})");
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
        _returnUpdated = true;
        if (State != PlayerbotHomeState.Returning || elapsedMs == 0) return false;
        float distance = area.DistanceTo(player.X, player.Y);
        Distance = distance;
        if (_bestDistance == float.MaxValue) _bestDistance = distance;
        else if (distance <= _bestDistance - ProgressYards)
        {
            _bestDistance = distance;
            _failures = 0;
        }

        Vector3 position = new(player.X, player.Y, player.Z);
        if (_route is { Complete: false })
        {
            if (Vector3.DistanceSquared(position, _lastPosition) < 0.0025f)
            {
                if (session.ManagedBudget is not { Remaining: <= 0 }
                    && (player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) == 0
                    && !PlayerbotElevator.IsTraveling(player) && !PlayerbotTram.IsTraveling(player))
                    _stuckMs += elapsedMs;
            }
            else _stuckMs = 0;
            if (_stuckMs >= StuckMs)
            {
                _route = null;
                _stuckMs = 0;
                if (Fail(player, area, nowMs, report: false)) return false;
            }
        }
        else if (_route is { Complete: true })
        {
            _route = null;
            bool arrived = distance <= area.ReturnRadius && (area.ZoneUnknown || area.InZone(player.X, player.Y, player.Z));
            if (arrived || distance <= _routeStartDistance - ProgressYards)
            {
                _bestDistance = MathF.Min(_bestDistance, distance);
                _failures = 0;
                _legProgressed = true;
            }
            else if (_routePlannedDistance >= 20f)
            {
                _failures = 0;
                _legProgressed = true;
            }
            else if (Fail(player, area, nowMs, report: false))
            {
                return false;
            }
        }

        _lastPosition = position;
        if (_route is null)
        {
            PlayerbotRoute? walk = null;
            if (PlayerbotTram.TryBegin(session, player, area.Centre)
                || PlayerbotElevator.TryBegin(session, player, area.Centre, options, out walk))
            {
                _routeStartDistance = MathF.Min(distance, _bestDistance);
                _routePlannedDistance = walk?.Distance ?? 0f;
                _stuckMs = 0;
                _legProgressed = true;
                return true;
            }

            PlanOutcome planned = walk is not null
                ? PlanOutcome.Route
                : PlayerbotNavigation.PlanToward(player, area.Centre, options, ref _tileWait, nowMs, out walk);
            _route = walk;
            if (planned != PlanOutcome.Route)
            {
                _route = null;
                PlayerbotMovementControl.Stop(session, player);
                if (PlayerbotElevator.IsHeldOff(session, player, area.Centre) || PlayerbotTram.IsHeldOff(session, player, area.Centre))
                    return false;
                if (player.Map is { } map && PlayerbotTaxi.TryTravelByTaxi(session, player, map.MapId, area.Centre, options) == TaxiTravel.Traveling)
                {
                    _failures = 0;
                    _legProgressed = true;
                    return true;
                }
                // A tile still being read is no failure of the walk home: plan again next think, for TileWait.MaxMs (then it counts).
                if (planned == PlanOutcome.Refused) Fail(player, area, nowMs, report: area.WalkRangeYards <= 0f);
                return false;
            }

            _routeStartDistance = MathF.Min(distance, _bestDistance);
            _routePlannedDistance = walk?.Distance ?? 0f;
            _stuckMs = 0;
        }

        if (_route is not { } routeToAdvance || !PlayerbotNavigation.TryAdvance(session, routeToAdvance, options, elapsedMs, nowMs))
        {
            float afterDistance = area.DistanceTo(player.X, player.Y);
            Distance = afterDistance;
            if (afterDistance <= _bestDistance - ProgressYards)
            {
                _bestDistance = afterDistance;
                _failures = 0;
                _legProgressed = true;
            }

            float finishedDistance = _routePlannedDistance;
            _route = null;
            bool arrived = afterDistance <= area.ReturnRadius && (area.ZoneUnknown || area.InZone(player.X, player.Y, player.Z));
            if (!arrived && session.ManagedBudget is not { Remaining: <= 0 }
                && (player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) == 0)
            {
                if (afterDistance <= _routeStartDistance - ProgressYards || finishedDistance >= 20f)
                {
                    _failures = 0;
                    _legProgressed = true;
                }
                else if (Fail(player, area, nowMs, report: false))
                {
                    return false;
                }
            }
        }

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

    private bool Fail(Player player, PlayerbotHomeArea area, uint nowMs, bool report)
    {
        if (++_failures < MaxFailures) return false;
        MarkBlocked(nowMs);
        if (report && !_walkBlockedReported && !PlayerbotNavigationAnomalies.Silenced(player))
        {
            _walkBlockedReported = true;
            PlayerbotAnomalies.Report(player, "home", "walk-blocked",
                string.Create(CultureInfo.InvariantCulture, $"the walk home to {area.Place ?? "its home"} failed {MaxFailures} times in a row ({Distance:F0} yd from the centre); set aside for {BlockedMs / 1000}s"),
                AnomalySeverity.Info);
        }
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
                    PlayerbotHomeState.Inside => rounded < 0 ? "inside" : string.Create(CultureInfo.InvariantCulture, $"inside:{rounded}"),
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
