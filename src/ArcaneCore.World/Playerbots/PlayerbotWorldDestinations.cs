using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Npc;
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
    private readonly PlayerbotNavigationAnomalies.TripWatch _tripWatch = new();
    private readonly List<DestinationEntry> _entries = [];
    private readonly HashSet<uint> _blockedSpawns = [];
    private readonly Dictionary<uint, uint> _farBlockedUntil = [];
    private readonly Dictionary<uint, uint> _visited = [];
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

    /// <summary>
    /// Whether a live creature of the hunted entry in sight is no target for this bot (the brain's skip rules: unreachable, entry set
    /// aside, outside the home; tapped by or fighting someone else; left alone as someone else's). Such a creature does not count as
    /// "something to hunt here", so it neither ends a walk nor keeps a spawn from being visited. Null: every live one counts.
    /// </summary>
    internal Func<Player, Creature, bool>? Untakeable { get; set; }

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

    /// <summary>How long one entry's skip stays unreported after it was reported (a bot meets the same unjoined spawn every think).</summary>
    internal const uint UnreachableReportMs = 600_000;

    private const int MaxReportedEntries = 256;
    private readonly Dictionary<uint, uint> _unreachableReportedAt = [];

    /// <summary>Skips reported so far (<see cref="UnreachableReportMs"/> apart per entry).</summary>
    internal int UnreachableReports { get; private set; }

    /// <summary>The most reports waiting to be logged; a skip met while the queue is full is not marked reported, so a later think reports it.</summary>
    internal const int MaxPendingReports = 8;

    private readonly Queue<UnreachableSkip> _pendingReports = new();

    /// <summary>Whether a reported skip waits to be logged (<see cref="TryTakeUnreachable"/>).</summary>
    internal bool HasUnreachableReports => _pendingReports.Count != 0;

    /// <summary>The oldest skip waiting to be logged, in the order they were met: one think can skip several entries and each is reported.</summary>
    internal bool TryTakeUnreachable(out UnreachableSkip skip) => _pendingReports.TryDequeue(out skip);

    /// <summary>The goal the brain holds, read when a skip is met (so a report keeps the goal it was met under); null: unknown.</summary>
    internal Func<PlayerbotGoalKind>? GoalOf { get; set; }

    /// <summary>A goal skipped for lying where no walk reaches: the goal held, what, from where, and the two region-graph components.</summary>
    internal readonly record struct UnreachableSkip(PlayerbotGoalKind Goal, uint Entry, uint Quest, uint Map, Vector3 At, int FromComponent, int ToComponent);

    /// <summary>
    /// Skips the spawn when its place is on a part of the map no walk joins to the bot's (<see cref="PlayerbotNavigation.IsUnreachableOnFoot"/>,
    /// which answers false for a bot on a fragment, indoors, or without a walking region within reach of it: PlanToward's escapes run first;
    /// a steep-ground bot within a few yards of walkable ground snaps to that region and is judged from it): no path search, no
    /// step toward it, the spawn blocked for this bot. Never for a completed quest's return (<paramref name="returning"/>: its hazard and
    /// set-aside rules are its own). When no other present spawn of the entry is left to try, the entry is set aside
    /// (<paramref name="entrySetAside"/>); the caller then sets aside the quests whose last open objective it was
    /// (<see cref="SetAsideQuestsOfUnreachable"/>), neither counting towards abandoning a quest (<see cref="PlayerbotSuspensions.SuspendQuest"/>).
    /// Reported once per entry per <see cref="UnreachableReportMs"/>, queued (<see cref="TryTakeUnreachable"/>). World thread.
    /// </summary>
    internal bool SkipUnreachable(Player player, uint entry, uint spawnGuid, Vector3 destination, uint questId, uint now, bool returning,
        out bool entrySetAside)
    {
        entrySetAside = false;
        if (returning || !PlayerbotNavigation.IsUnreachableOnFoot(player, destination, out int from, out int to)) return false;
        _blockedSpawns.Add(spawnGuid);
        if (!_entries.Any(other => other.Entry == entry && !IsBlocked(other.Spawn.Guid, now) && IsPresent(player, other.Spawn)))
        {
            entrySetAside = true;
            if (Suspensions is { } suspensions)
            {
                if (suspensions.EntryStrikeCount(entry) > 0)
                    suspensions.SuspendEntryEscalating(entry, now);
                else
                    suspensions.SuspendEntry(entry, now);
            }
        }

        if (_unreachableReportedAt.TryGetValue(entry, out uint last) && unchecked(now - last) < UnreachableReportMs) return true;
        if (_pendingReports.Count >= MaxPendingReports) return true; // not marked reported: a later think reports it
        if (_unreachableReportedAt.Count >= MaxReportedEntries) _unreachableReportedAt.Clear();
        _unreachableReportedAt[entry] = now;
        UnreachableReports++;
        _pendingReports.Enqueue(new UnreachableSkip(GoalOf?.Invoke() ?? default, entry, questId, player.MapId, PlayerbotMotion.CurrentPosition(player), from, to));
        return true;
    }

    /// <summary>The quest of an entry just set aside as unreachable follows it when nothing else of the quest is left; not counted towards abandoning it.</summary>
    internal void SetQuestAsideUnreachable(uint questId, uint now, bool questHasOtherObjective)
    {
        if (!questHasOtherObjective) Suspensions?.SuspendQuest(questId, now, countsAsSetAside: false);
    }

    /// <summary>
    /// After an entry was set aside as unreachable: the quests it served. With a <paramref name="questId"/> (a giver or ender trip) that
    /// quest; without one (hunting an objective) every incomplete quest of the bot's log with the entry as a creature objective or as a
    /// dropper of an item objective. A quest in the log is set aside only when none of its other open objectives (creature, game object,
    /// item) can still be served (<see cref="PlayerbotQuestObjectives.HasOtherOpenObjective"/>); a quest not yet taken or completed is
    /// set aside with its giver or ender.
    /// </summary>
    private void SetAsideQuestsOfUnreachable(uint entry, uint questId, uint now, Player player, QuestNpcServices? services, PlayerNpcState? state, LootContent? loot)
    {
        if (Suspensions is null || services is null) return;
        if (questId != 0)
        {
            QuestStatusData? status = state?.Quests.Get(questId);
            bool open = status is { Status: QuestStatus.Incomplete } && services.Quests.Get(questId) is { } quest
                && PlayerbotQuestObjectives.HasOtherOpenObjective(quest, status, entry, e => Suspended(e), item => player.Inventory.GetItemCount(item), item => DroppersOf(loot, item));
            SetQuestAsideUnreachable(questId, now, open);
            return;
        }

        if (state is null) return;
        foreach ((uint id, QuestStatusData status) in state.Quests.Statuses)
        {
            if (status.Status != QuestStatus.Incomplete || services.Quests.Get(id) is not { IsActive: true } quest
                || !PlayerbotQuestObjectives.Serves(quest, status, entry, item => player.Inventory.GetItemCount(item), item => DroppersOf(loot, item))) continue;
            SetQuestAsideUnreachable(id, now, PlayerbotQuestObjectives.HasOtherOpenObjective(quest, status, entry, e => Suspended(e),
                item => player.Inventory.GetItemCount(item), item => DroppersOf(loot, item)));
        }
    }

    private static IReadOnlyList<uint>? DroppersOf(LootContent? loot, uint item) => loot is null ? null : PlayerbotDropIndex.DroppersOf(loot, item);

    /// <summary>
    /// The last <see cref="Update"/> found no route only because a navigation tile on the way was still being read in the background
    /// (<see cref="PathOptions.NonBlockingTiles"/>): the destination was not judged, nothing was blacklisted or backed off, and the
    /// brain keeps its goal and asks again on a later think (see <see cref="TileWait.MaxMs"/>).
    /// </summary>
    internal bool WaitingForTiles { get; private set; }

    private TileWait _tileWait;

    /// <summary>Test seam: run on the world thread right after an answered async plan was replayed (a test records a pending tile lookup that the replay would have met here).</summary>
    internal Action? PreparedPlanProbe { get; set; }

    /// <summary>Returns true while travel owns the intent: a movement attempt or pending navigation; false permits another goal.</summary>
    internal bool Update(Player player, uint preferredCreatureEntry, uint elapsedMs, uint returnQuestId = 0, uint preferredQuestId = 0)
    {
        WaitingForTiles = false;
        if (Volatile.Read(ref _permanentlyStopped) != 0 || (player.IsInWorld && !player.IsAlive))
        {
            // Stopped for good, or dead: the route and the trip are over.
            Stop();
            return false;
        }

        // Transient guards (disabled, not in the world for a moment, no map, a zero step) leave the route and the trip alone, as before the lanes.
        if (!_options.Enabled || !player.IsInWorld || player.Map is not { } map || elapsedMs == 0) return false;
        _planningDeferred = false;
        uint now = _session.World.NowMs;
        if (_backoffUntil != 0 && unchecked(now - _backoffUntil) > int.MaxValue) return false;
        if (_backoffUntil != 0) _backoffUntil = 0;

        if (_transitHeld.Count != 0) ReleaseTransitHeld(player);
        RefreshEntries(player, map.MapId, preferredCreatureEntry, returnQuestId);
        bool hunting = preferredCreatureEntry != 0 && returnQuestId == 0;
        if (hunting)
        {
            NoteArrival(player, preferredCreatureEntry, now);
            if (_route is not null && _routeTarget == preferredCreatureEntry && IsVisited(_routeSpawnGuid, now)) ClearRoute();
        }
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
            if (Seen(player, _routeTarget, returnQuestId, hunting))
            {
                if (returnQuestId == 0 && preferredCreatureEntry == 0) SettleArrival(player, _routeTarget);
                else if (returnQuestId != 0) ConfirmArrivedEnderUnserved(player, returnQuestId, _routeTarget, now);
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
            _tripWatch.Note(player, _routeTarget, _routeSpawnGuid, now);
            if (PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now)) return true;
            _blockedSpawns.Add(_routeSpawnGuid);
            ClearRoute();
            Backoff(now);
            return false;
            }
        }

        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        PlayerNpcState? state = services?.StateOf(player);
        if (hunting && _visited.Count > 0 && AllVisitedOrBlocked(preferredCreatureEntry, now, out bool anyBlocked))
        {
            // Every known spawn of the entry was just found empty (or cannot be reached): no destination until the earliest
            // respawn, instead of walking back to the nearest one. The brain falls through to exploring meanwhile. A spawn that
            // was only transiently blocked is retried after the backoff, as when every spawn is blocked (below).
            TargetEntry = 0;
            QuestId = 0;
            if (anyBlocked)
            {
                _blockedSpawns.Clear();
                Backoff(now);
            }

            return false;
        }

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
            .Where(entry => !hunting || !IsVisited(entry.Spawn.Guid, now))
            .OrderBy(entry => DistanceSquared(player, entry.Spawn)))
        {
            if (++scanned > MaxEntryScan) break;
            if (Suspended(entry.Entry)) continue;
            if (!Eligible(entry.Entry, preferredCreatureEntry, player, services, state, returnQuestId, SkipQuest, loot,
                    Home, _content, GiverInHome(entry.Spawn), out uint questId)) continue;
            if (questId == 0 && preferredCreatureEntry != 0) questId = preferredQuestId;
            // A spawn of another map (or with no place) is no destination: the entries are listed per map, so this is a content or cache fault.
            if (PlayerbotNavigationAnomalies.DestinationFault(map.MapId, player.FactionTemplate, entry.Spawn, 0, null, wantsFriendly: false) is { } badSpawn)
            {
                PlayerbotNavigationAnomalies.ReportDestination(player, badSpawn, entry.Entry, entry.Spawn);
                _blockedSpawns.Add(entry.Spawn.Guid);
                continue;
            }
            if (Seen(player, entry.Entry, returnQuestId, hunting))
            {
                if (returnQuestId == 0 && preferredCreatureEntry == 0) SettleArrival(player, entry.Entry);
                else if (returnQuestId != 0) ConfirmArrivedEnderUnserved(player, returnQuestId, entry.Entry, now);
                return false;
            }
            bool attemptedBefore = attempted;
            attempted = true;
            Vector3 destination = new(entry.Spawn.X, entry.Spawn.Y, entry.Spawn.Z);
            // Across the Deeprun Tram (Stormwind and Ironforge sides of the continent): the brain drives the trip from the next think,
            // and the destination plans again from the other platform's exit (PlayerbotTram). A low-level bot with no home takes no
            // tram or lift trip (TransitAllowed): the live fresh launch sent about 76 level 1 bots to Stormwind quests by the tram.
            bool transit = TransitAllowed(Home, player.Level, _options.Population);
            if (_navigationPlan is null && transit && PlayerbotTram.TryBegin(_session, player, destination, TramZoneAt))
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
            if (_navigationPlan is null && transit && PlayerbotElevator.TryBegin(_session, player, destination, _options, out walk))
            {
                ClearRoute();
                TargetEntry = entry.Entry;
                QuestId = questId;
                ArmTrip(entry.Entry, questId, returnQuestId, preferredCreatureEntry);
                return true;
            }

            // A long trip between two flight path nodes the bot knows and can pay for (PlayerbotTaxi): the brain drives it from the next
            // think (walk to the flight master, fly, land), and the destination plans again from where the bot lands. A lift's own walk
            // (above) is taken as it is; the tram and the lift keep their precedence.
            if (_navigationPlan is null && walk is null && PlayerbotTaxi.TryTravelByTaxi(_session, player, player.Map!.MapId, destination, _options) == TaxiTravel.Traveling)
            {
                ClearRoute();
                TargetEntry = entry.Entry;
                QuestId = questId;
                ArmTrip(entry.Entry, questId, returnQuestId, preferredCreatureEntry);
                return true;
            }

            // The tram or a lift is the way there but a failed leg backed it off (PlayerbotTransitBackoff): the walk to the far side is refused
            // for that reason alone, so it is neither judged unreachable nor set aside and no quest is abandoned for it. The destination waits
            // a short retry; the backoff (or the retry delay after the failure) ends by time.
            if (_navigationPlan is null && transit && walk is null && TransitHeldOff(player, destination))
            {
                Backoff(now);
                return false;
            }

            PlayerbotRoute? route = walk;
            bool returning = returnQuestId != 0 && returnQuestId == questId;
            bool friendlyTown = returning && FriendlyTownEnder(player, entry.Entry);
            // A spawn in a part of the map no walk joins to the bot's (the region graph's components) is skipped without a path query or a
            // step toward it: the walk would end at the point nearest it, bot after bot (bot QA cluster C3). Not for a completed quest's
            // return (the hazard and set-aside rules there are their own). The skip costs no path search, so the next spawn is judged in
            // this think; when no other spawn of the entry is left to try, the entry and the quest are set aside at once without
            // counting towards abandoning the quest (PlayerbotSuspensions.SuspendQuest).
            if (route is null && _navigationPlan is null && SkipUnreachable(player, entry.Entry, entry.Spawn.Guid, destination, questId, now, returning, out bool entrySetAside))
            {
                // Only when every objective of the quest is set aside too is the quest: another creature of it may still be reachable.
                if (entrySetAside) SetAsideQuestsOfUnreachable(entry.Entry, questId, now, player, services, state, loot);
                attempted = attemptedBefore;
                continue;
            }

            PlanOutcome planned = route is not null ? PlanOutcome.Route : PlanOutcome.Refused;
            if (_navigationPlan is { } plan)
            {
                if (_pendingEntry != entry.Entry || _pendingSpawnGuid != entry.Spawn.Guid)
                {
                    ClearNavigation();
                    _planningDeferred = true;
                    return true;
                }
                if (planned != PlanOutcome.Route)
                {
                    long pendingMark = PlayerbotNavigation.PendingMark;
                    bool prepared = PlayerbotNavigation.TryPlanTowardPrepared(player, destination, _options, plan,
                        out route, out bool deferred, completedQuestReturn: returning, friendlyTownEnder: friendlyTown, nowMs: now);
                    PreparedPlanProbe?.Invoke();
                    if (!deferred)
                    {
                        // The world-thread parts of a prepared plan (the region graph's locate) can still meet a tile being read.
                        if (prepared) _tileWait.Reset();
                        planned = prepared ? PlanOutcome.Route
                            : PlayerbotNavigation.PendingSince(pendingMark) && _tileWait.Hold(now) ? PlanOutcome.Pending : PlanOutcome.Refused;
                    }

                    if (deferred)
                    {
                        _awaitingRequest = plan.NextRequest;
                        if (_awaitingRequest is null) ClearNavigation();
                        else SubmitAwaitingNavigation();
                        _planningDeferred = true;
                        return true;
                    }
                }
                // A pending replay keeps the answered plan: the next think replays it (the tile is read by then) instead of asking the worker
                // again, so the wait (_tileWait) runs on from its first hold and is bounded by TileWait.MaxMs.
                if (planned != PlanOutcome.Pending) ClearNavigation();
            }
            else if (planned != PlanOutcome.Route && TryBeginNavigation(player, map, destination, entry, questId, preferredCreatureEntry, returnQuestId))
            {
                return true;
            }
            else if (planned != PlanOutcome.Route)
                planned = PlayerbotNavigation.PlanToward(player, destination, _options, ref _tileWait, now, out route,
                    completedQuestReturn: returning, friendlyTownEnder: friendlyTown);
            if (planned == PlanOutcome.Pending)
            {
                // A tile on the way is still being read: not a verdict on this spawn. Keep the goal, try again next think.
                WaitingForTiles = true;
                return false;
            }
            if (planned == PlanOutcome.Refused)
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
                    ConfirmUnclimbable(player, destination, questId, entry.Entry, now, friendlyTown);
                return false; // Nearby alternatives rotate next think; far failures wait for backoff.
            }
            _tileWait.Reset();
            if (questId == _failQuest) _failCount = 0;
            // The chosen giver or ender of a trip must not be an enemy's: a hostile faction template is a town the bot should not walk into.
            if (!hunting && PlayerbotNavigationAnomalies.DestinationFault(map.MapId, player.FactionTemplate, entry.Spawn,
                    _content?.FindTemplate(entry.Entry)?.Faction ?? 0,
                    _session.Services.GetService<QuestNpcFeature>()?.FactionTemplates ?? _session.Services.GetService<FactionTemplateCatalog>(),
                    wantsFriendly: true) is { } chosenFault)
                PlayerbotNavigationAnomalies.ReportDestination(player, chosenFault, entry.Entry, entry.Spawn);
            _tripWatch.Note(player, entry.Entry, entry.Spawn.Guid, now);
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

    /// <summary>A spawn point the bot reached with nothing of the hunted entry alive in sight is a visit: how near (yards) the bot must stand to it.</summary>
    internal const float ArrivalYards = 8f;

    /// <summary>Spawns of one entry this near a visited spawn (yards) are one camp and are visited with it.</summary>
    internal const float CampYards = 10f;

    /// <summary>Shortest and longest time (ms) a visited spawn is skipped, whatever its respawn delay.</summary>
    internal const uint MinVisitMs = 30_000, MaxVisitMs = 600_000;

    /// <summary>The most visited spawns remembered: as many as spawns are scanned for one entry, so a full camp list can be held. Only expired visits are dropped to make room; when full of live ones no new visit is marked.</summary>
    internal const int MaxVisits = MaxEntryScan;

    /// <summary>Visited spawns still remembered (tests, inspection).</summary>
    internal int VisitedCount => _visited.Count;

    internal bool IsVisited(uint spawnGuid, uint now)
    {
        if (!_visited.TryGetValue(spawnGuid, out uint until)) return false;
        if (unchecked((int)(until - now)) > 0) return true;
        _visited.Remove(spawnGuid);
        return false;
    }

    /// <summary>
    /// The bot stands at a spawn point of the hunted entry and sees no live creature of it: the point (and the spawns within
    /// <see cref="CampYards"/>, one camp) is skipped until its creatures are due back (the spawn's respawn delay), so the walk
    /// goes on to the next spawn instead of back to the nearest, which is the one the bot is standing at.
    /// </summary>
    private void NoteArrival(Player player, uint preferred, uint now)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        CreatureSpawn? reached = null;
        foreach (DestinationEntry entry in _entries)
        {
            if (entry.Entry != preferred || IsVisited(entry.Spawn.Guid, now)) continue;
            if (Vector3.DistanceSquared(here, new(entry.Spawn.X, entry.Spawn.Y, entry.Spawn.Z)) > ArrivalYards * ArrivalYards) continue;
            reached = entry.Spawn;
            break;
        }

        if (reached is null || TakeableInSight(player, preferred)) return;
        Vector3 center = new(reached.X, reached.Y, reached.Z);
        foreach (DestinationEntry entry in _entries)
        {
            if (entry.Entry != preferred || IsVisited(entry.Spawn.Guid, now)) continue;
            if (Vector3.DistanceSquared(center, new(entry.Spawn.X, entry.Spawn.Y, entry.Spawn.Z)) > CampYards * CampYards) continue;
            if (_visited.Count >= MaxVisits && !PruneVisits(now)) return;
            ulong respawnMs = ((ulong)entry.Spawn.SpawnTimeMinSeconds + entry.Spawn.SpawnTimeMaxSeconds) / 2 * 1000;
            _visited[entry.Spawn.Guid] = unchecked(now + (uint)Math.Clamp(respawnMs, MinVisitMs, MaxVisitMs));
        }
    }

    /// <summary>Drops the expired visits; true when there is room for one more.</summary>
    private bool PruneVisits(uint now)
    {
        List<uint>? expired = null;
        foreach ((uint guid, uint until) in _visited)
            if (unchecked((int)(until - now)) <= 0) (expired ??= []).Add(guid);
        if (expired is not null)
            foreach (uint guid in expired) _visited.Remove(guid);
        return _visited.Count < MaxVisits;
    }

    /// <summary>A live creature of <paramref name="entry"/> in sight that this bot would take (<see cref="Untakeable"/>).</summary>
    private bool TakeableInSight(Player player, uint entry)
    {
        foreach (var guid in player.VisibleObjects)
            if (player.Map?.FindObject(guid) is Creature creature && creature.Entry == entry && creature.IsInWorld && creature.IsAlive
                && Untakeable?.Invoke(player, creature) != true) return true;
        return false;
    }

    private bool Seen(Player player, uint entry, uint returnQuestId, bool hunting)
        => hunting ? TakeableInSight(player, entry) : VisibleTarget(player, entry, returnQuestId);

    private bool AllVisitedOrBlocked(uint preferred, uint now, out bool anyBlocked)
    {
        bool any = false, visited = false;
        anyBlocked = false;
        foreach (DestinationEntry entry in _entries)
        {
            if (entry.Entry != preferred) continue;
            any = true;
            if (IsVisited(entry.Spawn.Guid, now)) visited = true;
            else if (IsBlocked(entry.Spawn.Guid, now)) anyBlocked = true;
            else return false;
        }

        return any && visited;
    }

    /// <summary>
    /// Whether every present in-home spawn of <paramref name="entry"/> on the bot's map is currently marked visited (empty camp waiting for respawn).
    /// </summary>
    internal bool HasAllSpawnsVisited(Player player, uint entry, uint now)
    {
        if (entry == 0 || _visited.Count == 0) return false;
        CreatureContent? content = _content ?? _session.Services.GetService<CreatureWorldFeature>()?.Content;
        if (content is null) return false;
        IReadOnlyList<CreatureSpawn> spawns = content.GetSpawns(player.MapId, entry);
        if (spawns.Count == 0) return false;
        bool any = false;
        for (int i = 0; i < spawns.Count; i++)
        {
            CreatureSpawn spawn = spawns[i];
            if (!IsPresent(player, spawn) || (Home is { } home && !home.Contains(spawn))) continue;
            any = true;
            if (!IsVisited(spawn.Guid, now)) return false;
        }

        return any;
    }

    private bool GiverInHome(CreatureSpawn spawn) => Home is not { } home || home.Contains(spawn);

    /// <summary>
    /// Whether a bot may begin a Deeprun Tram or lift trip: a bot with a home always (its home already confines its goals), one without
    /// only from <see cref="Population.PlayerbotPopulationOptions.TramMinLevel"/> (0 = always).
    /// </summary>
    internal static bool TransitAllowed(PlayerbotHomeArea? home, uint level, Population.PlayerbotPopulationOptions population)
        => home is not null || population.TramMinLevel == 0 || level >= population.TramMinLevel;

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

    /// <summary>How near its ender (yards, same map) the bot's bind point must be for a completed quest it cannot walk to to be reached by teleporting there.</summary>
    internal const float BindNearEnderYards = 250f;

    /// <summary>The brain's bind-point teleport (not urgent, on its cooldown): true when the bot was sent. Null: none.</summary>
    internal Func<Player, string, bool>? TeleportToBind { get; set; }

    /// <summary>Failed plans of one completed quest from one spot (within <see cref="SpotYards"/>) before the route refusal is diagnosed.</summary>
    internal const int ConfirmFailures = 3;

    /// <summary>The set-asides of a quest for this bot after which a quest that cannot be held (an unfinished one, or a steep-ground refusal) is abandoned instead of set aside again.</summary>
    internal const int AbandonAfterSetAsides = 3;

    /// <summary>
    /// The set-asides (ten minutes each, so about two hours) after which a completed quest held against a strong hazard is abandoned
    /// all the same when the bot has gained no level since the first of them. In memory only: a restart starts the count again.
    /// </summary>
    internal const int MaxHeldSetAsides = 12;

    /// <summary>A bot this near its bind point (yards) is taken to be at it: teleporting there would change nothing.</summary>
    internal const float BindArrivedYards = 50f;

    private const float SpotYards = 3f;

    private uint _failQuest;
    private Vector3 _failSpot;
    private int _failCount;
    private bool _failDiagnosed;
    private readonly Dictionary<uint, int> _setAsides = [];
    private readonly Dictionary<uint, uint> _heldAtLevel = [];

    /// <summary>
    /// One more failed plan of the completed quest <paramref name="questId"/> (a single ender). A single failure proves nothing
    /// (<see cref="PlayerbotNavigation.TryPlanToward"/> also refuses a bot momentarily on a steep patch): only the
    /// <see cref="ConfirmFailures"/>th from the same spot is diagnosed for a steep ascent or a hazard on a connected route,
    /// once per quest and spot; a bot that moved starts the count again. A confirmed refusal sets the quest and its
    /// ender aside for this bot; the <see cref="AbandonAfterSetAsides"/>th set-aside of a quest abandons it through the client's
    /// abandon packet instead (the ender cannot be reached from where this bot keeps ending up). A completed quest refused for a hazard
    /// is held instead (<see cref="TeleportToBind"/> when the bind point is beside the ender), until <see cref="MaxHeldSetAsides"/>
    /// set-asides pass with no level gained. A death or retreat mark beside the road is not that hazard when
    /// <paramref name="friendlyTownEnder"/>: it must not set a friendly town turn-in aside.
    /// <para>
    /// With the brain's distant-quest abandon (<c>PlayerbotQuestGoals.AbandonDistantCompletedQuest</c>, which runs first and
    /// drops a completed quest with no ender in the home or within 600 yards), this only meets quests whose ender is near on the map
    /// but behind steep ground or guarded by mobs it cannot fight.
    /// </para>
    /// </summary>
    private uint _arrivedQuest;
    private Vector3 _arrivedSpot;
    private int _arrivedCount;

    private void ConfirmArrivedEnderUnserved(Player player, uint questId, uint entry, uint now)
    {
        TargetEntry = entry;
        QuestId = questId;
        if (SetQuestAside is null || player.Combat.IsInCombat) return;
        Creature? ender = FindArrivedEnder(player, entry, out float distance);
        if (ender is { Combat.IsInCombat: true }) return;
        Vector3 spot = new(player.X, player.Y, player.Z);
        if (questId != _arrivedQuest || Vector3.DistanceSquared(spot, _arrivedSpot) > SpotYards * SpotYards)
        {
            _arrivedQuest = questId;
            _arrivedSpot = spot;
            _arrivedCount = 0;
        }
        if (++_arrivedCount < ConfirmFailures) return;
        _arrivedCount = 0;
        Suspensions?.SuspendQuest(questId, now);
        Suspensions?.SuspendEntry(entry, now);
        SetQuestAside.Invoke(questId);
        ILogger? log = _session.Services.GetService<ILogger<PlayerbotWorldDestinations>>();
        log?.LogWarning(
            "Playerbot {Bot} sets aside completed quest {Quest} at arrived ender {Entry} ({X:F1},{Y:F1},{Z:F1} map {Map}): ender in sight within 4 yd (dist={Distance:F1} yd, npcFlags=0x{NpcFlags:X}, inCombat={InCombat}) but turn-in did not start",
            player.Name, questId, entry, player.X, player.Y, player.Z, player.MapId,
            ender is not null ? distance : -1f, ender?.NpcFlags ?? 0, ender?.Combat.IsInCombat ?? false);
        if (!PlayerbotNavigationAnomalies.Silenced(player))
            PlayerbotAnomalies.Report(player, "destination", "arrived-unserved",
                $"quest {questId}: arrived within 4 yd of ender {entry} (found: {ender is not null}) {ConfirmFailures} times and the turn-in never started");
    }

    private void ConfirmUnclimbable(Player player, Vector3 destination, uint questId, uint entry, uint now, bool friendlyTownEnder)
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
        int fromComp = -1, toComp = -1;
        bool unreachable = !steep && PlayerbotNavigation.IsUnreachableOnFoot(player, destination, out fromComp, out toComp);
        PlayerbotHazard hazard = default;
        if (!steep && !unreachable && !PlayerbotNavigation.HasBlockingReturnHazard(player, destination, _options, out hazard, friendlyTownEnder))
        {
            // No steep ground or hazard: for a bot that may not take the tram or a lift the ender across it is the reason, but only
            // when the tram or a lift really is the way there (any other failed plan is not a transit block, and is never set aside here).
            if (!TransitAllowed(Home, player.Level, _options.Population) && TransitWayTo(player, destination) is { } transit)
                ConfirmTransitBlocked(player, questId, entry, now, transit);
            return;
        }
        if (unreachable && (!_unreachableReportedAt.TryGetValue(entry, out uint last) || unchecked(now - last) >= UnreachableReportMs)
            && _pendingReports.Count < MaxPendingReports)
        {
            if (_unreachableReportedAt.Count >= MaxReportedEntries) _unreachableReportedAt.Clear();
            _unreachableReportedAt[entry] = now;
            UnreachableReports++;
            _pendingReports.Enqueue(new UnreachableSkip(GoalOf?.Invoke() ?? default, entry, questId, player.MapId, spot, fromComp, toComp));
        }
        string reason = steep ? $"the mesh route climbs {ascent:F1} yards of steep ground"
            : unreachable ? $"destination is in disconnected region component {toComp} from {fromComp}"
            : $"the route crosses a {hazard.Kind} hazard";
        _failCount = 0;
        _failDiagnosed = false;
        int times = _setAsides.GetValueOrDefault(questId) + 1;
        _setAsides[questId] = times;
        TargetEntry = entry;
        QuestId = questId;
        ILogger? log = _session.Services.GetService<ILogger<PlayerbotWorldDestinations>>();
        bool finished = _session.Services.GetService<QuestNpcFeature>()?.Services.StateOf(player)?.Quests.Get(questId)
            is { Status: QuestStatus.Complete, Rewarded: false };
        bool hold = finished && !steep && !unreachable;
        if (hold)
        {
            // A level gained since the first hold restarts the count (the pack that blocked the bot may be crossable now); with none, the
            // hold ends at MaxHeldSetAsides and the quest is abandoned below, so a permanent hazard cannot cost path queries forever.
            if (_heldAtLevel.TryGetValue(questId, out uint heldLevel) && player.Level > heldLevel)
            {
                times = 1;
                _setAsides[questId] = 1;
            }
            _heldAtLevel[questId] = Math.Max(player.Level, _heldAtLevel.GetValueOrDefault(questId));
            hold = times < MaxHeldSetAsides;
        }

        if (hold && times >= AbandonAfterSetAsides)
        {
            // Finished work is not thrown away for a strong hazard on the way: live 2026-10-12 (bot QA #283) 47 completed quests were
            // abandoned in one 4-hour window. A bind point beside the ender is a way round (unless the bot already stands at it: the
            // teleport would change nothing); either way the quest is held and set
            // aside for the ordinary window (the count keeps growing, so every later diagnosis comes back here).
            HomeBind bind = player.Home;
            float bx = bind.X - destination.X, by = bind.Y - destination.Y;
            float px = bind.X - player.X, py = bind.Y - player.Y;
            bool teleported = bind != default && bind.MapId == player.MapId
                && bx * bx + by * by <= BindNearEnderYards * BindNearEnderYards
                && px * px + py * py > BindArrivedYards * BindArrivedYards
                && TeleportToBind?.Invoke(player, $"cannot reach the ender of completed quest {questId}: {reason}") == true;
            suspensions.SuspendQuest(questId, now);
            suspensions.SuspendEntry(entry, now);
            log?.LogWarning(
                "Playerbot {Bot} keeps completed quest {Quest} (ender {Entry}) after {Times} set-asides{Teleport}: no walkable route from {X:F1},{Y:F1},{Z:F1} map {Map}; {Reason}",
                player.Name, questId, entry, times, teleported ? " and teleports to its bind point beside the ender" : " and sets it aside again",
                player.X, player.Y, player.Z, player.MapId, reason);
            return;
        }

        if (times >= AbandonAfterSetAsides && PlayerbotQuestGoals.TryAbandonCompletedQuest(_session, player, questId, out _))
        {
            _setAsides.Remove(questId);
            _heldAtLevel.Remove(questId);
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

    /// <summary>
    /// The ender of a completed return is a friendly town NPC (Innkeeper Renee, Tannok Frosthammer): neither faction template is
    /// hostile and one is friendly. An unresolved or neutral template is not, so a mark beside the road still blocks that walk.
    /// </summary>
    private bool FriendlyTownEnder(Player player, uint entry)
    {
        uint faction = _content?.FindTemplate(entry)?.Faction ?? 0;
        FactionTemplateCatalog? factions = _session.Services.GetService<QuestNpcFeature>()?.FactionTemplates
            ?? _session.Services.GetService<FactionTemplateCatalog>();
        return PlayerbotNavigation.IsFriendlyTownNpc(factions, player.FactionTemplate, faction);
    }

    /// <summary>"the Deeprun Tram" or "a lift" when that is the way to <paramref name="destination"/>, else null. Only valid once the walk was refused.</summary>
    private string? TransitWayTo(Player player, Vector3 destination)
        => PlayerbotTram.IsWayTo(_session, player, destination, TramZoneAt) ? "the Deeprun Tram"
            : PlayerbotElevator.IsWayTo(_session, player, destination) ? "a lift" : null;

    /// <summary>
    /// A completed quest whose ender the bot cannot reach because it may not take the tram or a lift yet (<see cref="TransitAllowed"/>):
    /// the quest and its ender are set aside for the ordinary window without counting towards abandoning (a level is all the bot lacks, so it
    /// is never abandoned and never marked unreachable), and held until the bot is allowed to ride: the set-aside is lifted at the first
    /// <see cref="Update"/> after <see cref="TransitAllowed"/> turns true (<see cref="ReleaseTransitHeld"/>). Always returns false (nothing
    /// is abandoned).
    /// </summary>
    internal bool ConfirmTransitBlocked(Player player, uint questId, uint entry, uint now, string transit = "the tram or a lift")
    {
        if (Suspensions is not { } suspensions) return false;
        _failCount = 0;
        _failDiagnosed = false;
        ILogger? log = _session.Services.GetService<ILogger<PlayerbotWorldDestinations>>();
        suspensions.SuspendQuest(questId, now, countsAsSetAside: false);
        suspensions.SuspendEntry(entry, now);
        bool first = !_transitHeld.ContainsKey(questId);
        if (first && _transitHeld.Count >= MaxTransitHeld) _transitHeld.Clear();
        _transitHeld[questId] = entry;
        if (first)
        {
            log?.LogInformation("Playerbot {Bot} holds completed quest {Quest} (ender {Entry}) until level {Required}: level {Level} is below the tram level and the ender lies across {Transit}",
                player.Name, questId, entry, _options.Population.TramMinLevel, player.Level, transit);
        }

        return false;
    }

    /// <summary>Completed quests held for want of the tram level (<see cref="ConfirmTransitBlocked"/>) and their enders; bounded by <see cref="MaxTransitHeld"/>.</summary>
    private readonly Dictionary<uint, uint> _transitHeld = [];

    private const int MaxTransitHeld = 32;

    /// <summary>Held quests (<see cref="ConfirmTransitBlocked"/>) whose bot may ride now: their set-asides are lifted so they are taken up again.</summary>
    private void ReleaseTransitHeld(Player player)
    {
        if (!TransitAllowed(Home, player.Level, _options.Population)) return;
        foreach ((uint quest, uint entry) in _transitHeld)
        {
            Suspensions?.ReleaseQuest(quest);
            Suspensions?.ReleaseEntry(entry);
        }

        // The far plans refused while the bot could not ride blocked the enders for a while: they are tried afresh.
        _blockedSpawns.Clear();
        _farBlockedUntil.Clear();
        _transitHeld.Clear();
    }

    /// <summary>The tram or a lift is the way to <paramref name="destination"/> but waiting out a failed leg (<see cref="PlayerbotTransitBackoff"/>).</summary>
    private bool TransitHeldOff(Player player, Vector3 destination)
        => PlayerbotTram.IsHeldOff(_session, player, destination, TramZoneAt) || PlayerbotElevator.IsHeldOff(_session, player, destination);

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
            if (!PlayerbotQuestGoals.CanExecuteObjectives(_session, player, services, content, candidate)) continue;
            if (_trip.IsInfeasible(mapId, player.X, player.Y, quests, content, questId,
                maxDeliveryYards, player))
            {
                continue;
            }
            if (area is not null && !area.QuestAllowed(quests, content, candidate, level)) continue;
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
    /// <see cref="PlayerbotHomeArea.QuestAllowed"/>: its enders and kill targets have spawns inside, or a graduated home's leave-home quest
    /// the bot is old enough for; without creature content nothing is),
    /// supported objective mechanics (<see cref="PlayerbotQuestGoals.CanExecuteObjectives"/>),
    /// not an item-only repeatable the bot cannot supply (<see cref="CanPursueStarter"/>), and the journal and prerequisites
    /// (<see cref="QuestNpcServices.CanTakeQuest(Player, uint)"/>, no running or complete status), and last the trip's feasibility
    /// (<see cref="PlayerbotQuestTripFeasibility.IsInfeasible"/>, by the caller's own <paramref name="trip"/>).
    /// </summary>
    internal static bool MayTakeQuest(Player player, QuestNpcServices services, PlayerNpcState state, CreatureContent? content,
        LootContent? loot, PlayerbotHomeArea? home, bool giverInHome, Quest quest, PlayerbotQuestTripFeasibility trip, float maxDeliveryYards)
    {
        if (home is not null && !(giverInHome && content is not null && home.QuestAllowed(services.Quests, content, quest, player.Level))) return false;
        // Cheapest first: this runs per quest of each candidate NPC every think; the trip check walks spawns and paths.
        if (services.CanTakeQuest(player, quest.Id) != true
            || state.Quests.Get(quest.Id) is { Status: QuestStatus.Incomplete or QuestStatus.Complete }) return false;
        if (!PlayerbotQuestGoals.CanExecuteObjectives(player.Session as WorldSession, player, services, content, quest)) return false;
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
        => returnQuestId != 0
            ? FindArrivedEnder(player, entry, out _) is not null
            : player.VisibleObjects.Any(guid => player.Map?.FindObject(guid) is Creature creature
                && creature.Entry == entry && creature.IsInWorld && creature.IsAlive);

    private static Creature? FindArrivedEnder(Player player, uint entry, out float distance)
    {
        Vector3 playerPos = new(player.X, player.Y, player.Z);
        Creature? best = null;
        distance = float.MaxValue;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (player.Map?.FindObject(guid) is not Creature { IsInWorld: true, IsAlive: true } creature
                || creature.Entry != entry)
                continue;
            float d = Vector3.Distance(playerPos, new(creature.X, creature.Y, creature.Z));
            if (d <= 4f && d < distance)
            {
                best = creature;
                distance = d;
            }
        }
        return best;
    }

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
        if (!keepTrip) { _travelEntry = 0; _travelQuest = 0; _tripWatch.Clear(); }
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
        _navigationPlan = new PlayerbotAsyncNavigationPlan(map.MapId, request.Start, request.Dataset, player, destination);
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
