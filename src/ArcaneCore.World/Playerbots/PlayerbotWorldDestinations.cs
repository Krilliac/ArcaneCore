using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;

namespace ArcaneCore.World.Playerbots;

/// <summary>World-thread destination hints for static quest/objective spawns.</summary>
internal sealed class PlayerbotWorldDestinations(WorldSession session, PlayerbotOptions options, PlayerbotNavigationExecutor? executor = null)
{
    private const int MaxEntryScan = 128;
    private const uint BackoffMs = 2_000;
    private const uint FarSpawnBlockMs = 30_000;
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly PlayerbotNavigationExecutor? _executor = executor;
    private readonly PlayerbotQuestTripFeasibility _trip = new();
    private readonly List<DestinationEntry> _entries = [];
    private readonly HashSet<uint> _blockedSpawns = [];
    private readonly Dictionary<uint, uint> _farBlockedUntil = [];
    private uint _mapId;
    private int _contentVersion = -1;
    private CreatureContent? _content;
    private QuestStore? _quests;
    private QuestRewardMode _rewardMode;
    private uint[] _rewardQuestIds = [];
    private uint _level;
    private float _cachedMaxDeliveryYards;
    private PlayerbotHomeArea? _cachedHome;
    private uint _preferredEntry = uint.MaxValue;
    private uint _returnQuestId;
    private PlayerbotRoute? _route;
    private uint _routeTarget;
    private uint _routeSpawnGuid;
    private uint _backoffUntil;
    private int _failedPlans;
    private Vector3 _lastPosition;
    private uint _stuckMs;
    private PlayerbotNavigationJob? _pendingNavigation;
    private PendingIdentity? _pendingIdentity;
    private uint _pendingEntry;
    private uint _pendingSpawnGuid;
    private PlayerbotAsyncNavigationPlan? _navigationPlan;
    private IDisposable? _planLease;
    private PlayerbotNavigationRequest? _awaitingRequest;
    private bool _planningDeferred;
    private int _permanentlyStopped;

    internal bool IsPlanning => _navigationPlan is not null || _planningDeferred;

    internal void Stop()
    {
        ClearRoute();
    }

    internal void Dispose()
    {
        Interlocked.Exchange(ref _permanentlyStopped, 1);
        Stop();
    }

    internal void CancelPlanning() => ClearNavigation();

    internal void ValidatePlanning(Player player)
    {
        if (_pendingIdentity is { } pending && (!player.IsAlive || !player.IsInWorld
            || !ReferenceEquals(player, pending.Player) || !ReferenceEquals(player.Map, pending.Map)
            || player.Combat.IsInCombat)) ClearNavigation();
    }

    internal uint TargetEntry { get; private set; }
    internal uint QuestId { get; private set; }

    /// <summary>Whether the route being walked was planned under the completed-return rule (test seam).</summary>
    internal bool RouteIsRelaxed => _route?.CompletedQuestReturn == true;

    /// <summary>
    /// The bot's home area, or null. With one, the givers and enders of the quests worth a trip and the spawns of an objective to hunt
    /// are only those inside it; the way to a completed quest's ender is never restricted.
    /// </summary>
    internal PlayerbotHomeArea? Home { get; set; }

    /// <summary>Quests not worth a trip: the server refused their exchange lately (<see cref="PlayerbotQuestGoals.IsRefused"/>).</summary>
    internal Func<uint, bool>? SkipQuest { get; set; }

    /// <summary>
    /// Whether the quest-giver controller (<see cref="PlayerbotQuestGoals.HasCandidate"/>) has something to take or turn in with the
    /// quest NPCs in sight. Asked once when a trip ends with its NPC in sight (<see cref="SettleArrival"/>).
    /// </summary>
    internal Func<Player, bool>? GiverHasCandidate { get; set; }

    /// <summary>Sets a quest aside for this bot (<see cref="PlayerbotQuestGoals.SetAside"/>, the set-aside <see cref="SkipQuest"/> reads).</summary>
    internal Action<uint>? SetQuestAside { get; set; }

    // The giver and quest of the trip last committed to (route, tram or lift); consumed by SettleArrival.
    private uint _travelEntry;
    private uint _travelQuest;

    /// <summary>Quest givers and enders a stalled bot set aside (<see cref="PlayerbotStallWatch"/>).</summary>
    internal PlayerbotSuspensions? Suspensions { get; set; }

    /// <summary>Optional zone lookup for deterministic synthetic maps; live travel reads the map's area table.</summary>
    internal Func<Vector3, uint>? TramZoneAt { get; set; }

    private bool Suspended(uint entry) => Suspensions?.IsEntrySuspended(entry, _session.World.NowMs) == true;

    /// <summary>
    /// Advisory check for the brain's idle decision. This reports an ordinary,
    /// rewardable (Quests:RewardMode) quest destination that is currently eligible and offscreen;
    /// it does not plan a route or claim/accept a quest. The subsequent
    /// <see cref="Update"/> call remains authoritative and preserves the one
    /// path attempt and backoff rules.
    /// </summary>
    internal bool HasQuestCandidate(Player player)
    {
        if (!_options.Enabled || !player.IsInWorld || !player.IsAlive || player.Map is not { } map)
            return false;

        uint now = _session.World.NowMs;
        if (_backoffUntil != 0 && unchecked(now - _backoffUntil) > int.MaxValue)
            return false;
        if (_backoffUntil != 0)
        {
            // A failed route is suppressed only for the bounded backoff window.
            // Clear its blocked marker when the window expires so the advisory
            // seam can authorize one fresh Update/path attempt.
            _backoffUntil = 0;
            _blockedSpawns.Clear();
        }

        // Use the same static quest/map cache as Update, while rechecking
        // live quest state below so stale rows never become ownership.
        RefreshEntries(player, map.MapId, 0, 0);
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        PlayerNpcState? state = services?.StateOf(player);
        LootContent? loot = _session.Services.GetService<GameObjectLootFeature>()?.LootContent;
        bool blockedEligible = false;
        bool liveEligible = false;
        foreach (DestinationEntry entry in _entries.OrderBy(entry => DistanceSquared(player, entry.Spawn)))
        {
            if (Suspended(entry.Entry) || !IsPresent(player, entry.Spawn)) continue;
            if (!Eligible(entry.Entry, 0, player, services, state, 0, SkipQuest, loot, Home, _content, GiverInHome(entry.Spawn), out _)) continue;
            liveEligible = true;
            // A visible eligible giver belongs to ordinary interaction handling.
            // Do not let this advisory seam steal it for travel.
            if (VisibleTarget(player, entry.Entry, 0))
            {
                // The trip ended in sight of its NPC. When the quest-giver controller has nothing here, the quest is set aside
                // so the bot does not walk out of sight and be sent back for it (SettleArrival); another quest of the NPC may remain.
                SettleArrival(player, entry.Entry);
                if (Eligible(entry.Entry, 0, player, services, state, 0, SkipQuest, loot, Home, _content, GiverInHome(entry.Spawn), out _))
                    return false;
                continue;
            }
            if (IsBlocked(entry.Spawn.Guid, now))
            {
                blockedEligible = true;
                continue;
            }
            return true;
        }
        if (liveEligible && blockedEligible)
            Backoff(now);
        return false;
    }

    /// <summary>Returns true while travel owns the intent: a movement attempt or pending navigation; false permits another goal.</summary>
    internal bool Update(Player player, uint preferredCreatureEntry, uint elapsedMs, uint returnQuestId = 0)
    {
        if (Volatile.Read(ref _permanentlyStopped) != 0 || !_options.Enabled || !player.IsInWorld || !player.IsAlive
            || player.Map is not { } map || elapsedMs == 0)
        {
            Stop();
            return false;
        }
        _planningDeferred = false;
        uint now = _session.World.NowMs;
        if (_backoffUntil != 0 && unchecked(now - _backoffUntil) > int.MaxValue) return false;
        if (_backoffUntil != 0) _backoffUntil = 0;

        RefreshEntries(player, map.MapId, preferredCreatureEntry, returnQuestId);
        if (ConsumeNavigation(player, map.MapId, preferredCreatureEntry, returnQuestId)) return true;
        if (_pendingNavigation is not null) return true;
        LootContent? loot = _session.Services.GetService<GameObjectLootFeature>()?.LootContent;
        if (_route is not null)
        {
            if (_route.Complete)
            {
                ClearRoute(keepTrip: true);
            }
            else
            {
            QuestNpcServices? currentServices = _session.Services.GetService<QuestNpcFeature>()?.Services;
            PlayerNpcState? currentState = currentServices?.StateOf(player);
            if ((preferredCreatureEntry != 0 && preferredCreatureEntry != _routeTarget)
                || !Eligible(_routeTarget, preferredCreatureEntry, player, currentServices, currentState, returnQuestId, SkipQuest, loot,
                    Home, _content, Home is null || _content is not null && Home.HasSpawnInside(_content, _routeTarget), out _))
            {
                ClearRoute();
                return false;
            }
            if (VisibleTarget(player, _routeTarget, returnQuestId))
            {
                if (returnQuestId == 0 && preferredCreatureEntry == 0) SettleArrival(player, _routeTarget);
                ClearRoute();
                return false;
            }
            Vector3 position = new(player.X, player.Y, player.Z);
            _stuckMs = Vector3.DistanceSquared(position, _lastPosition) < 0.0025f
                ? _stuckMs + elapsedMs : 0;
            _lastPosition = position;
            if (_stuckMs >= 5_000)
            {
                _blockedSpawns.Add(_routeSpawnGuid);
                ClearRoute();
                Backoff(now);
                return false;
            }
            if (PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now)) return true;
            _blockedSpawns.Add(_routeSpawnGuid);
            ClearRoute();
            Backoff(now);
            return false;
            }
        }

        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        PlayerNpcState? state = services?.StateOf(player);
        if (_entries.Count > 0 && _entries.Where(entry => preferredCreatureEntry == 0 || entry.Entry == preferredCreatureEntry)
            .All(entry => IsBlocked(entry.Spawn.Guid, now)))
        {
            _blockedSpawns.Clear();
            Backoff(now);
            return false;
        }
        int scanned = 0;
        bool attempted = false;
        foreach (DestinationEntry entry in _entries
            .Where(entry => !IsBlocked(entry.Spawn.Guid, now) && IsPresent(player, entry.Spawn))
            .Where(entry => preferredCreatureEntry == 0 || entry.Entry == preferredCreatureEntry)
            .OrderBy(entry => DistanceSquared(player, entry.Spawn)))
        {
            if (++scanned > MaxEntryScan) break;
            if (Suspended(entry.Entry)) continue;
            if (!Eligible(entry.Entry, preferredCreatureEntry, player, services, state, returnQuestId, SkipQuest, loot,
                    Home, _content, GiverInHome(entry.Spawn), out uint questId)) continue;
            if (VisibleTarget(player, entry.Entry, returnQuestId))
            {
                if (returnQuestId == 0 && preferredCreatureEntry == 0) SettleArrival(player, entry.Entry);
                return false;
            }
            attempted = true;
            Vector3 destination = new(entry.Spawn.X, entry.Spawn.Y, entry.Spawn.Z);
            // Across the Deeprun Tram (Stormwind and Ironforge sides of the continent): the brain drives the trip from the next think,
            // and the destination plans again from the other platform's exit (PlayerbotTram).
            if (_navigationPlan is null && PlayerbotTram.TryBegin(_session, player, destination, TramZoneAt))
            {
                ClearRoute();
                TargetEntry = entry.Entry;
                QuestId = questId;
                ArmTrip(entry.Entry, questId, returnQuestId, preferredCreatureEntry);
                return true;
            }

            // On the other level of a lift (the Great Lift between Thousand Needles and The Barrens, the Thunder Bluff lifts): the brain
            // drives the ride from the next think, and the destination plans again from the other landing (PlayerbotElevator). When the
            // lift's own mesh check found the way on foot, that route is walked: still one path query this think.
            PlayerbotRoute? walk = null;
            if (_navigationPlan is null && PlayerbotElevator.TryBegin(_session, player, destination, _options, out walk))
            {
                ClearRoute();
                TargetEntry = entry.Entry;
                QuestId = questId;
                ArmTrip(entry.Entry, questId, returnQuestId, preferredCreatureEntry);
                return true;
            }

            PlayerbotRoute? route = walk;
            bool completedQuestReturn = returnQuestId != 0 && returnQuestId == questId;
            bool planned = route is not null;
            if (_navigationPlan is { } plan)
            {
                if (_pendingEntry != entry.Entry || _pendingSpawnGuid != entry.Spawn.Guid)
                {
                    ClearNavigation();
                    _planningDeferred = true;
                    return true;
                }
                if (!planned)
                {
                    planned = PlayerbotNavigation.TryPlanTowardPrepared(player, destination, _options, plan,
                        out route, out bool deferred, completedQuestReturn: completedQuestReturn);
                    if (deferred)
                    {
                        _awaitingRequest = plan.NextRequest;
                        if (_awaitingRequest is null) ClearNavigation();
                        else SubmitAwaitingNavigation();
                        _planningDeferred = true;
                        return true;
                    }
                }
                ClearNavigation();
            }
            else if (!planned && TryBeginNavigation(player, map, destination, entry, questId, preferredCreatureEntry, returnQuestId))
            {
                return true;
            }
            else if (!planned)
                planned = PlayerbotNavigation.TryPlanToward(player, destination, _options, out route,
                    completedQuestReturn: completedQuestReturn);
            if (!planned)
            {
                // A far failed search is expensive. Defer the next far spawn until the backoff expires instead of
                // running another capped search on the very next think; nearby alternatives still rotate promptly.
                float longRoute = _options.MaxRouteYards * 0.5f;
                if (DistanceSquared(player, entry.Spawn) > longRoute * longRoute)
                {
                    _farBlockedUntil[entry.Spawn.Guid] = unchecked(now + FarSpawnBlockMs);
                    Backoff(now);
                }
                else _blockedSpawns.Add(entry.Spawn.Guid);
                // A completed quest with just one indexed ender cannot be reached by retrying a plan that keeps failing from the same
                // spot. Leave quests with alternative enders to the ordinary spawn rotation. The set-aside is local to this bot.
                // Both the near and the far branch count: far failures arrive once per backoff and still confirm.
                if (returnQuestId == questId && questId != 0 && _entries.Count == 1)
                    ConfirmUnclimbable(player, destination, questId, entry.Entry, now);
                return false; // Nearby alternatives rotate next think; far failures wait for backoff.
            }
            if (questId == _failQuest) _failCount = 0;
            _route = route;
            _routeTarget = entry.Entry;
            _routeSpawnGuid = entry.Spawn.Guid;
            TargetEntry = entry.Entry;
            QuestId = questId;
            ArmTrip(entry.Entry, questId, returnQuestId, preferredCreatureEntry);
            _failedPlans = 0;
            _lastPosition = new(player.X, player.Y, player.Z);
            _stuckMs = 0;
            return PlayerbotNavigation.TryAdvance(_session, route!, _options, elapsedMs, now);
        }

        if (attempted) Backoff(now);
        return false;
    }

    private bool GiverInHome(CreatureSpawn spawn) => Home is not { } home || home.Contains(spawn);

    /// <summary>
    /// The backstop for a disagreement between the travel rule and the quest-giver controller. Called when a trip's NPC is in sight:
    /// the quest-giver controller takes over from here, and if it finds nothing to take or turn in (<see cref="GiverHasCandidate"/>),
    /// the quest the trip was for is set aside for this bot (<see cref="SetQuestAside"/>, about 10 minutes), so the bot does not walk
    /// out of sight again and be sent back for the same quest. Consumes the trip; a giver reached without a trip of ours is left alone.
    /// </summary>
    private void SettleArrival(Player player, uint entry)
    {
        if (_travelEntry != entry || _travelQuest == 0) return;
        uint quest = _travelQuest;
        _travelEntry = 0;
        _travelQuest = 0;
        // A quest the bot has since taken (or finished) is not a disagreement: the trip did its job and the journal says so.
        if (_session.Services.GetService<QuestNpcFeature>()?.Services.StateOf(player)?.Quests.Get(quest) is not null) return;
        if (GiverHasCandidate is null || SetQuestAside is null || GiverHasCandidate(player)) return;
        SetQuestAside(quest);
        ILogger? log = _session.Services.GetService<ILogger<PlayerbotWorldDestinations>>();
        log?.LogInformation("Playerbot {Bot} sets aside quest {Quest}: its trip to {Entry} ended in sight with nothing the quest giver would take or turn in",
            player.Name, quest, entry);
    }

    /// <summary>
    /// Whether the spawn may be in the world now: a spawn listed in <c>game_event_creature</c> is there only while its event says so
    /// (the map's spawn gate, vmangos GameEventMgr), so the giver or ender of an out-of-season quest (Tormek Stoneriver and Colara Dean
    /// of "Dearest Colara,", Love is in the Air; Greatfather Winter, Winter Veil) is not a destination while its event is off.
    /// </summary>
    internal static bool IsPresent(Player player, CreatureSpawn spawn)
        => player.Map?.FindUpdater<CreatureMapSystem>()?.SpawnGate?.AllowsCreature(spawn.Guid) != false;

    internal static bool IsFiniteDestination(CreatureSpawn spawn)
        => float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z);

    /// <summary>Failed plans of one completed quest from one spot (within <see cref="SpotYards"/>) before the route refusal is diagnosed.</summary>
    internal const int ConfirmFailures = 3;

    /// <summary>The quest's set-asides for this bot after which it is abandoned instead of set aside again.</summary>
    internal const int AbandonAfterSetAsides = 3;

    private const float SpotYards = 3f;

    private uint _failQuest;
    private Vector3 _failSpot;
    private int _failCount;
    private bool _failDiagnosed;
    private readonly Dictionary<uint, int> _setAsides = [];

    /// <summary>
    /// One more failed plan of the completed quest <paramref name="questId"/> (a single ender). A single failure proves nothing
    /// (<see cref="PlayerbotNavigation.TryPlanToward"/> also refuses a bot momentarily on a steep patch): only the
    /// <see cref="ConfirmFailures"/>th from the same spot is diagnosed for a steep ascent or a hazard on a connected route,
    /// once per quest and spot; a bot that moved starts the count again. A confirmed refusal sets the quest and its
    /// ender aside for this bot; the <see cref="AbandonAfterSetAsides"/>th set-aside of a quest abandons it through the client's
    /// abandon packet instead (the ender cannot be reached from where this bot keeps ending up).
    /// <para>
    /// With the brain's distant-quest abandon (<c>PlayerbotQuestGoals.AbandonDistantCompletedQuest</c>, which runs first and
    /// drops a completed quest with no ender in the home or within 600 yards), this only meets quests whose ender is near on the map
    /// but behind steep ground or guarded by mobs it cannot fight.
    /// </para>
    /// </summary>
    private void ConfirmUnclimbable(Player player, Vector3 destination, uint questId, uint entry, uint now)
    {
        Vector3 spot = new(player.X, player.Y, player.Z);
        if (questId != _failQuest || Vector3.DistanceSquared(spot, _failSpot) > SpotYards * SpotYards)
        {
            _failQuest = questId;
            _failSpot = spot;
            _failCount = 0;
            _failDiagnosed = false;
        }

        if (++_failCount < ConfirmFailures || _failDiagnosed || Suspensions is not { } suspensions) return;
        _failDiagnosed = true;
        bool steep = PlayerbotNavigation.RequiresSteepAscent(player, destination, _options, out float ascent);
        PlayerbotHazard hazard = default;
        if (!steep && !PlayerbotNavigation.HasBlockingReturnHazard(player, destination, _options, out hazard)) return;
        string reason = steep ? $"the mesh route climbs {ascent:F1} yards of steep ground"
            : $"the route crosses a {hazard.Kind} hazard";
        _failCount = 0;
        _failDiagnosed = false;
        int times = _setAsides.GetValueOrDefault(questId) + 1;
        _setAsides[questId] = times;
        TargetEntry = entry;
        QuestId = questId;
        ILogger? log = _session.Services.GetService<ILogger<PlayerbotWorldDestinations>>();
        if (times >= AbandonAfterSetAsides && PlayerbotQuestGoals.TryAbandonCompletedQuest(_session, player, questId, out _))
        {
            _setAsides.Remove(questId);
            suspensions.MarkUnreachableQuest(questId);
            log?.LogWarning(
                "Playerbot {Bot} abandons completed quest {Quest} (ender {Entry}) after {Times} set-asides: no walkable route from {X:F1},{Y:F1},{Z:F1} map {Map}; {Reason}",
                player.Name, questId, entry, times, player.X, player.Y, player.Z, player.MapId, reason);
            return;
        }

        suspensions.SuspendQuest(questId, now);
        suspensions.SuspendEntry(entry, now);
        log?.LogWarning(
            "Playerbot {Bot} sets aside quest {Quest} target {Entry} (set-aside {Times}): no walkable route from {X:F1},{Y:F1},{Z:F1} map {Map}; {Reason}",
            player.Name, questId, entry, times, player.X, player.Y, player.Z, player.MapId, reason);
    }

    internal static bool IsDestinationEntry(CreatureContent content, QuestStore quests, CreatureSpawn spawn, uint preferredEntry)
    {
        if (!IsFiniteDestination(spawn) || (preferredEntry != 0 && spawn.Entry != preferredEntry)) return false;
        CreatureTemplate? template = content.FindTemplate(spawn.Entry);
        return template is not null && (preferredEntry != 0 || template.NpcFlags != 0
            && (quests.StartersOf(spawn.Entry).Count != 0 || quests.EndersOf(spawn.Entry).Count != 0));
    }

    private void RefreshEntries(Player player, uint mapId, uint preferredCreatureEntry, uint returnQuestId)
    {
        CreatureContent? content = _session.Services.GetService<CreatureWorldFeature>()?.Content;
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        QuestStore? quests = services?.Quests;
        if (content is null || quests is null || services is null) { ClearRoute(); _entries.Clear(); return; }
        QuestRewardMode mode = services.Options.RewardMode;
        uint[] allowed = services.Options.OrdinaryRewardQuestIds;
        uint level = player.Level;
        float maxDeliveryYards = Home is null ? _options.MaxDeliveryYards : 0;
        if (_mapId == mapId && ReferenceEquals(_content, content) && _contentVersion == content.DefinitionsVersion
            && ReferenceEquals(_quests, quests) && _preferredEntry == preferredCreatureEntry
            && ReferenceEquals(_cachedHome, Home)
            && _returnQuestId == returnQuestId && _rewardMode == mode && _level == level
            && _cachedMaxDeliveryYards == maxDeliveryYards
            && _rewardQuestIds.SequenceEqual(allowed)) return;
        bool contentChanged = _mapId != mapId || !ReferenceEquals(_content, content) || _contentVersion != content.DefinitionsVersion;
        ClearRoute();
        _mapId = mapId;
        _content = content;
        _quests = quests;
        _contentVersion = content.DefinitionsVersion;
        _preferredEntry = preferredCreatureEntry;
        _returnQuestId = returnQuestId;
        _rewardMode = mode;
        _level = level;
        _cachedMaxDeliveryYards = maxDeliveryYards;
        _cachedHome = Home;
        _rewardQuestIds = [.. allowed];
        _entries.Clear();
        _blockedSpawns.Clear();
        if (contentChanged) _farBlockedUntil.Clear();
        if (returnQuestId != 0 || preferredCreatureEntry != 0)
        {
            IEnumerable<uint> entries = returnQuestId != 0 ? quests.CreatureEndersOf(returnQuestId).Take(MaxEntryScan)
                : [preferredCreatureEntry];
            foreach (uint entry in entries)
            {
                // Cache static relations only. Live eligibility can change after a quest
                // reward or level gain and is rechecked for every movement decision.
                foreach (CreatureSpawn spawn in content.GetSpawns(mapId, entry)
                    .Where(spawn => IsDestinationEntry(content, quests, spawn, preferredCreatureEntry))
                    .Where(spawn => returnQuestId != 0 || Home is not { } home || home.Contains(spawn))
                    .OrderBy(spawn => DistanceSquared(player, spawn)).Take(MaxEntryScan - _entries.Count))
                    _entries.Add(new DestinationEntry(entry, spawn));
                if (_entries.Count >= MaxEntryScan) break;
            }
            return;
        }

        // Quest givers and enders of the quests the server settles (Quests:RewardMode): every supported quest by default,
        // the allowlist only under AllowlistOnly. The nearest spawns win, whatever the quest ids.
        HashSet<uint> givers = [];
        PlayerbotHomeArea? area = Home;
        foreach (uint questId in CandidateQuests(services, level))
        {
            if (services.Quests.Get(questId) is not { } candidate) continue;
            if (_trip.IsInfeasible(mapId, player.X, player.Y, quests, content, questId,
                maxDeliveryYards, player))
            {
                continue;
            }
            if (area is not null && !area.QuestInside(quests, content, candidate)) continue;
            givers.UnionWith(quests.CreatureStartersOf(questId));
            givers.UnionWith(quests.CreatureEndersOf(questId));
        }
        foreach (DestinationEntry entry in givers.Order()
            .SelectMany(entry => content.GetSpawns(mapId, entry)
                .Where(spawn => IsDestinationEntry(content, quests, spawn, 0) && (area is null || area.Contains(spawn)))
                .Select(spawn => new DestinationEntry(entry, spawn)))
            .OrderBy(entry => DistanceSquared(player, entry.Spawn)).ThenBy(entry => entry.Spawn.Guid)
            .Take(MaxEntryScan))
            _entries.Add(entry);
    }

    /// <summary>
    /// The quests worth a trip: under AllowlistOnly the allowlist; otherwise every quest the server settles that the character
    /// is old enough for and that is not gray (vmangos MaNGOS::XP::GetGrayLevel; QuestLevel 0 or less follows the player).
    /// Live eligibility (<see cref="QuestNpcServices.CanTakeQuest(Player, uint)"/>, the journal) is checked per decision.
    /// </summary>
    internal static IEnumerable<uint> CandidateQuests(QuestNpcServices services, uint level)
    {
        if (services.Options.RewardMode == QuestRewardMode.AllowlistOnly)
            return services.Options.OrdinaryRewardQuestIds.Where(services.IsRewardable);
        uint gray = ArcaneCore.Game.Progression.ExperienceFormulas.GrayLevel(level);
        return services.Quests.All
            .Where(quest => quest.MinLevel <= level && (quest.QuestLevel <= 0 || quest.QuestLevel > gray))
            .Select(quest => quest.Id).Order()
            .Where(services.IsRewardable);
    }

    /// <summary>
    /// An item-only repeatable is deferred only when it lacks its items and the loaded content has no loot
    /// or vendor row for any missing item. Quest-chance drops require the quest in the log (QuestNpcServices.HasQuestForItem,
    /// LootSeams); vmangos Player::CanCompleteRepeatableQuest checks items for completion, not acceptance
    /// (Player.cpp:12663-12680). Missing loot content is not evidence of an unobtainable item.
    /// </summary>
    internal static bool CanPursueStarter(Player player, Quest quest, LootContent? loot, NpcStore? npcs)
    {
        if (!quest.IsRepeatable || !quest.HasSpecialFlag(QuestSpecialFlags.Deliver)
            || quest.ReqCreatureOrGOCountTotal != 0 || quest.ReqSpell.Any(id => id != 0)) return true;
        if (loot is null || loot.RowCount == 0) return true;
        bool missing = false;
        for (int i = 0; i < quest.ReqItemId.Count; i++)
        {
            uint id = quest.ReqItemId[i];
            if (id == 0) continue;
            uint supplied = quest.Template.SrcItemId == id ? Math.Max(1u, quest.Template.SrcItemCount) : 0;
            if ((ulong)player.Inventory.GetItemCount(id) + supplied >= quest.ReqItemCount[i]) continue;
            missing = true;
            if (supplied != 0 || loot.HasLootItem(id) || npcs?.SellsItem(id) == true) return true;
        }
        return !missing;
    }

    /// <summary>
    /// The one rule for "may this bot take this quest here", shared by the travel planner (<see cref="Eligible"/>) and the quest-giver
    /// controller (<c>PlayerbotQuestGoals.FindCandidate</c>) so the two cannot disagree: the home (<paramref name="giverInHome"/>, and
    /// <see cref="PlayerbotHomeArea.QuestInside"/>: its enders and kill targets have spawns inside; without creature content nothing is),
    /// not an item-only repeatable the bot cannot supply (<see cref="CanPursueStarter"/>), and the journal and prerequisites
    /// (<see cref="QuestNpcServices.CanTakeQuest(Player, uint)"/>, no running or complete status), and last the trip's feasibility
    /// (<see cref="PlayerbotQuestTripFeasibility.IsInfeasible"/>, by the caller's own <paramref name="trip"/>).
    /// </summary>
    internal static bool MayTakeQuest(Player player, QuestNpcServices services, PlayerNpcState state, CreatureContent? content,
        LootContent? loot, PlayerbotHomeArea? home, bool giverInHome, Quest quest, PlayerbotQuestTripFeasibility trip, float maxDeliveryYards)
    {
        if (home is not null && !(giverInHome && content is not null && home.QuestInside(services.Quests, content, quest))) return false;
        // Cheapest first: this runs per quest of each candidate NPC every think; the trip check walks spawns and paths.
        if (services.CanTakeQuest(player, quest.Id) != true
            || state.Quests.Get(quest.Id) is { Status: QuestStatus.Incomplete or QuestStatus.Complete }) return false;
        if (!CanPursueStarter(player, quest, loot, services.Npcs)) return false;
        return content is null || !trip.IsInfeasible(player.MapId, player.X, player.Y, services.Quests, content, quest.Id,
            maxDeliveryYards, player);
    }

    private bool Eligible(uint entry, uint preferred, Player player, QuestNpcServices? services,
        PlayerNpcState? state, uint returnQuestId, Func<uint, bool>? skipQuest, LootContent? loot,
        PlayerbotHomeArea? home, CreatureContent? content, bool giverInHome, out uint questId)
    {
        questId = 0;
        if (returnQuestId != 0)
        {
            if (services is not null && state is { Loaded: true }
                && services.IsRewardable(returnQuestId)
                && services.Quests.Ends(entry, returnQuestId)
                && state.Quests.Get(returnQuestId) is { Status: QuestStatus.Complete, Rewarded: false })
            { questId = returnQuestId; return true; }
            return false;
        }
        if (preferred != 0) return true;
        if (services is null || state is not { Loaded: true }) return false;
        foreach (uint id in services.Quests.EndersOf(entry))
        {
            if (services.IsRewardable(id) && skipQuest?.Invoke(id) != true
                && state.Quests.Get(id) is { Status: QuestStatus.Complete, Rewarded: false })
            { questId = id; return true; }
        }
        foreach (uint id in services.Quests.StartersOf(entry))
        {
            if (services.IsRewardable(id) && skipQuest?.Invoke(id) != true
                && services.Quests.Get(id) is { } quest
                // Travel is stricter than the giver: a quest the journal has ever held (rewarded repeatable, abandoned, removed by a GM)
                // is not worth a trip, whatever its status now.
                && state.Quests.Get(id) is null
                && MayTakeQuest(player, services, state, content, loot, home, giverInHome, quest, _trip,
                    home is null ? _options.MaxDeliveryYards : 0)) { questId = id; return true; }
        }
        return false;
    }

    private bool VisibleTarget(Player player, uint entry, uint returnQuestId)
        => player.VisibleObjects.Any(guid => player.Map?.FindObject(guid) is Creature creature
            && creature.Entry == entry && creature.IsInWorld && creature.IsAlive
            && (returnQuestId == 0 || Vector3.Distance(new(player.X, player.Y, player.Z),
                new(creature.X, creature.Y, creature.Z)) <= 4f));

    /// <summary>Remember the ordinary quest trip just committed to; return and objective trips are not ours to settle.</summary>
    private void ArmTrip(uint entry, uint questId, uint returnQuestId, uint preferredCreatureEntry)
    {
        if (returnQuestId != 0 || preferredCreatureEntry != 0) return;
        _travelEntry = entry;
        _travelQuest = questId;
    }

    /// <summary>Drops the route and, unless <paramref name="keepTrip"/> (the route completed: the arrival is still to be settled), the trip.</summary>
    private void ClearRoute(bool keepTrip = false)
    {
        if (!keepTrip) { _travelEntry = 0; _travelQuest = 0; }
        ClearNavigation();
        _route = null;
        _routeTarget = default;
        _routeSpawnGuid = 0;
    }

    private void ClearNavigation()
    {
        _pendingNavigation?.Dispose();
        _pendingNavigation = null;
        _pendingIdentity = null;
        _navigationPlan = null;
        _awaitingRequest = null;
        _planLease?.Dispose();
        _planLease = null;
        _planningDeferred = false;
    }

    private bool TryBeginNavigation(Player player, Map map, Vector3 destination, DestinationEntry entry, uint questId,
        uint preferredEntry, uint returnQuestId)
    {
        if (Volatile.Read(ref _permanentlyStopped) != 0) return true;
        if (_executor is null || Vector3.Distance(new(player.X, player.Y, player.Z), destination) <= PlayerbotNavigation.NearYards)
            return false;
        if (_navigationPlan is not null) return true;
        if (map.Collision.Pathfinder is not NavMeshPathfinder nav) return false;
        PathOptions pathOptions = new() { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes,
            AllowPartial = true, MaxPoints = Math.Max(2, _options.MaxPathPoints), MaxSearchNodes = PlayerbotNavigation.MaxSearchNodes,
            LoadTiles = true };
        NavRegionGraph? graph = nav.RegionGraph(map.MapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        var request = new PlayerbotNavigationRequest(map.MapId, PlayerbotMotion.CurrentPosition(player), destination, pathOptions,
            graph, _options.MaxRouteYards, _options.MaxPathPoints, nav.Dataset);
        TargetEntry = entry.Entry;
        QuestId = questId;
        if (!_executor.TryAcquirePlan(out IDisposable lease))
        {
            _planningDeferred = true;
            return true;
        }
        _planLease = lease;
        _navigationPlan = new PlayerbotAsyncNavigationPlan(map.MapId, request.Start, request.Dataset);
        _awaitingRequest = request;
        _pendingIdentity = new(player, map, nav, _content, _contentVersion, _quests, Home, player.Level,
            preferredEntry, returnQuestId, questId, request.Start, graph, _options.MaxRouteYards, _options.MaxPathPoints);
        _pendingEntry = entry.Entry;
        _pendingSpawnGuid = entry.Spawn.Guid;
        SubmitAwaitingNavigation();
        if (Volatile.Read(ref _permanentlyStopped) != 0) ClearNavigation();
        return true;
    }

    private void SubmitAwaitingNavigation()
    {
        if (_pendingNavigation is null && _awaitingRequest is { } request && _executor is not null
            && Volatile.Read(ref _permanentlyStopped) == 0
            && _executor.TrySubmit(request, out PlayerbotNavigationJob job))
        {
            _pendingNavigation = job;
            if (Volatile.Read(ref _permanentlyStopped) != 0) ClearNavigation();
        }
    }

    private bool ConsumeNavigation(Player player, uint mapId, uint preferredEntry, uint returnQuestId)
    {
        if (_navigationPlan is not { } plan) return false;
        if (_pendingIdentity is not { } identity || !ReferenceEquals(player, identity.Player)
            || !ReferenceEquals(player.Map, identity.Map) || !player.IsInWorld || !player.IsAlive
            || !ReferenceEquals(player.Map?.Collision.Pathfinder, identity.Pathfinder)
            || !ReferenceEquals(_content, identity.Content) || _contentVersion != identity.ContentVersion
            || !ReferenceEquals(_quests, identity.Quests) || !ReferenceEquals(Home, identity.Home)
            || player.Level != identity.Level || preferredEntry != identity.PreferredEntry || returnQuestId != identity.ReturnQuestId
            || _options.MaxRouteYards != identity.MaxRouteYards || _options.MaxPathPoints != identity.MaxPathPoints
            || Suspended(_pendingEntry)
            || Vector3.DistanceSquared(PlayerbotMotion.CurrentPosition(player), identity.Start) > 1f)
        {
            ClearNavigation();
            _planningDeferred = true;
            return true;
        }
        if (_pendingNavigation is not { } job)
        {
            if (_awaitingRequest is null) return false;
            SubmitAwaitingNavigation();
            return true;
        }
        if (!job.Completion.IsCompleted) return true;
        PlayerbotNavigationResult result = job.Completion.GetAwaiter().GetResult();
        job.Dispose();
        _pendingNavigation = null;
        if (result.Status != PlayerbotNavigationResultStatus.Completed || result.Path is null
            || !ReferenceEquals(identity.RegionGraph,
                identity.Pathfinder.RegionGraph(mapId, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)))
        {
            ClearNavigation();
            _planningDeferred = true;
            return true;
        }
        DestinationEntry? current = _entries.FirstOrDefault(entry => entry.Spawn.Guid == _pendingSpawnGuid && entry.Entry == _pendingEntry);
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        LootContent? loot = _session.Services.GetService<GameObjectLootFeature>()?.LootContent;
        if (current is null || !IsPresent(player, current.Spawn) || VisibleTarget(player, current.Entry, returnQuestId)
            || !Eligible(current.Entry, preferredEntry, player, services, services?.StateOf(player), returnQuestId, SkipQuest, loot,
                Home, _content, GiverInHome(current.Spawn), out uint questId)
            || questId != identity.QuestId)
        {
            ClearNavigation();
            _planningDeferred = true;
            return true;
        }
        if (_awaitingRequest is not { } completed || !plan.AddResult(completed, result))
        {
            ClearNavigation();
            _planningDeferred = true;
            return true;
        }
        _awaitingRequest = null;
        return false;
    }

    private sealed record PendingIdentity(Player Player, Map Map, NavMeshPathfinder Pathfinder,
        CreatureContent? Content, int ContentVersion, QuestStore? Quests, PlayerbotHomeArea? Home, uint Level,
        uint PreferredEntry, uint ReturnQuestId, uint QuestId, Vector3 Start, NavRegionGraph? RegionGraph,
        float MaxRouteYards, int MaxPathPoints);

    private void Backoff(uint now)
    {
        _travelEntry = 0;
        _travelQuest = 0;
        if (++_failedPlans >= 3) { _failedPlans = 0; TargetEntry = 0; QuestId = 0; }
        _backoffUntil = unchecked(now + BackoffMs);
    }

    private bool IsBlocked(uint spawnGuid, uint now)
    {
        if (_blockedSpawns.Contains(spawnGuid)) return true;
        if (!_farBlockedUntil.TryGetValue(spawnGuid, out uint until)) return false;
        if (unchecked((int)(until - now)) > 0) return true;
        _farBlockedUntil.Remove(spawnGuid);
        return false;
    }

    private static float DistanceSquared(Player player, CreatureSpawn spawn)
        => Vector3.DistanceSquared(new(player.X, player.Y, player.Z), new(spawn.X, spawn.Y, spawn.Z));

    private sealed record DestinationEntry(uint Entry, CreatureSpawn Spawn);
}
