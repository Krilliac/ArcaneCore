using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>World-thread destination hints for static quest/objective spawns.</summary>
internal sealed class PlayerbotWorldDestinations(WorldSession session, PlayerbotOptions options)
{
    private const int MaxEntryScan = 128;
    private const uint BackoffMs = 2_000;
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly List<DestinationEntry> _entries = [];
    private readonly HashSet<uint> _blockedSpawns = [];
    private uint _mapId;
    private int _contentVersion = -1;
    private CreatureContent? _content;
    private QuestStore? _quests;
    private QuestRewardMode _rewardMode;
    private uint[] _rewardQuestIds = [];
    private uint _level;
    private uint _preferredEntry = uint.MaxValue;
    private uint _returnQuestId;
    private PlayerbotRoute? _route;
    private uint _routeTarget;
    private uint _routeSpawnGuid;
    private uint _backoffUntil;
    private int _failedPlans;
    private Vector3 _lastPosition;
    private uint _stuckMs;

    internal uint TargetEntry { get; private set; }
    internal uint QuestId { get; private set; }

    /// <summary>Quests not worth a trip: the server refused their exchange lately (<see cref="PlayerbotQuestGoals.IsRefused"/>).</summary>
    internal Func<uint, bool>? SkipQuest { get; set; }

    /// <summary>Quest givers and enders a stalled bot set aside (<see cref="PlayerbotStallWatch"/>).</summary>
    internal PlayerbotSuspensions? Suspensions { get; set; }

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
        bool blockedEligible = false;
        bool liveEligible = false;
        foreach (DestinationEntry entry in _entries.OrderBy(entry => DistanceSquared(player, entry.Spawn)))
        {
            if (Suspended(entry.Entry)) continue;
            if (!Eligible(entry.Entry, 0, player, services, state, 0, SkipQuest, out _)) continue;
            liveEligible = true;
            // A visible eligible giver belongs to ordinary interaction handling.
            // Do not let this advisory seam steal it for travel.
            if (VisibleTarget(player, entry.Entry, 0)) return false;
            if (_blockedSpawns.Contains(entry.Spawn.Guid))
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

    /// <summary>Returns true only when a normal movement action was attempted.</summary>
    internal bool Update(Player player, uint preferredCreatureEntry, uint elapsedMs, uint returnQuestId = 0)
    {
        if (!_options.Enabled || !player.IsInWorld || !player.IsAlive || player.Map is not { } map || elapsedMs == 0)
            return false;
        uint now = _session.World.NowMs;
        if (_backoffUntil != 0 && unchecked(now - _backoffUntil) > int.MaxValue) return false;
        if (_backoffUntil != 0) _backoffUntil = 0;

        RefreshEntries(player, map.MapId, preferredCreatureEntry, returnQuestId);
        if (_route is not null)
        {
            if (_route.Complete)
            {
                ClearRoute();
            }
            else
            {
            QuestNpcServices? currentServices = _session.Services.GetService<QuestNpcFeature>()?.Services;
            PlayerNpcState? currentState = currentServices?.StateOf(player);
            if ((preferredCreatureEntry != 0 && preferredCreatureEntry != _routeTarget)
                || !Eligible(_routeTarget, preferredCreatureEntry, player, currentServices, currentState, returnQuestId, SkipQuest, out _))
            {
                ClearRoute();
                return false;
            }
            if (VisibleTarget(player, _routeTarget, returnQuestId))
            {
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
            .All(entry => _blockedSpawns.Contains(entry.Spawn.Guid)))
        {
            _blockedSpawns.Clear();
            Backoff(now);
            return false;
        }
        int scanned = 0;
        bool attempted = false;
        foreach (DestinationEntry entry in _entries
            .Where(entry => !_blockedSpawns.Contains(entry.Spawn.Guid))
            .Where(entry => preferredCreatureEntry == 0 || entry.Entry == preferredCreatureEntry)
            .OrderBy(entry => DistanceSquared(player, entry.Spawn)))
        {
            if (++scanned > MaxEntryScan) break;
            if (Suspended(entry.Entry)) continue;
            if (!Eligible(entry.Entry, preferredCreatureEntry, player, services, state, returnQuestId, SkipQuest, out uint questId)) continue;
            if (VisibleTarget(player, entry.Entry, returnQuestId)) return false;
            attempted = true;
            Vector3 destination = new(entry.Spawn.X, entry.Spawn.Y, entry.Spawn.Z);
            if (!PlayerbotNavigation.TryPlanToward(player, destination, _options, out PlayerbotRoute? route))
            {
                _blockedSpawns.Add(entry.Spawn.Guid);
                return false; // Rotate to another indexed spawn on the next think; one path attempt per think.
            }
            _route = route;
            _routeTarget = entry.Entry;
            _routeSpawnGuid = entry.Spawn.Guid;
            TargetEntry = entry.Entry;
            QuestId = questId;
            _failedPlans = 0;
            _lastPosition = new(player.X, player.Y, player.Z);
            _stuckMs = 0;
            return PlayerbotNavigation.TryAdvance(_session, route!, _options, elapsedMs, now);
        }

        if (attempted) Backoff(now);
        return false;
    }

    internal static bool IsFiniteDestination(CreatureSpawn spawn)
        => float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z);

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
        if (_mapId == mapId && ReferenceEquals(_content, content) && _contentVersion == content.DefinitionsVersion
            && ReferenceEquals(_quests, quests) && _preferredEntry == preferredCreatureEntry
            && _returnQuestId == returnQuestId && _rewardMode == mode && _level == level
            && _rewardQuestIds.SequenceEqual(allowed)) return;
        ClearRoute();
        _mapId = mapId;
        _content = content;
        _quests = quests;
        _contentVersion = content.DefinitionsVersion;
        _preferredEntry = preferredCreatureEntry;
        _returnQuestId = returnQuestId;
        _rewardMode = mode;
        _level = level;
        _rewardQuestIds = [.. allowed];
        _entries.Clear();
        _blockedSpawns.Clear();
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
                    .OrderBy(spawn => DistanceSquared(player, spawn)).Take(MaxEntryScan - _entries.Count))
                    _entries.Add(new DestinationEntry(entry, spawn));
                if (_entries.Count >= MaxEntryScan) break;
            }
            return;
        }

        // Quest givers and enders of the quests the server settles (Quests:RewardMode): every supported quest by default,
        // the allowlist only under AllowlistOnly. The nearest spawns win, whatever the quest ids.
        HashSet<uint> givers = [];
        foreach (uint questId in CandidateQuests(services, level))
        {
            givers.UnionWith(quests.CreatureStartersOf(questId));
            givers.UnionWith(quests.CreatureEndersOf(questId));
        }
        foreach (DestinationEntry entry in givers.Order()
            .SelectMany(entry => content.GetSpawns(mapId, entry)
                .Where(spawn => IsDestinationEntry(content, quests, spawn, 0))
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

    private static bool Eligible(uint entry, uint preferred, Player player, QuestNpcServices? services,
        PlayerNpcState? state, uint returnQuestId, Func<uint, bool>? skipQuest, out uint questId)
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
                && services.CanTakeQuest(player, id) == true && state.Quests.Get(id) is null) { questId = id; return true; }
        }
        return false;
    }

    private bool VisibleTarget(Player player, uint entry, uint returnQuestId)
        => player.VisibleObjects.Any(guid => player.Map?.FindObject(guid) is Creature creature
            && creature.Entry == entry && creature.IsInWorld && creature.IsAlive
            && (returnQuestId == 0 || Vector3.Distance(new(player.X, player.Y, player.Z),
                new(creature.X, creature.Y, creature.Z)) <= 4f));

    private void ClearRoute()
    {
        _route = null;
        _routeTarget = default;
        _routeSpawnGuid = 0;
    }

    private void Backoff(uint now)
    {
        if (++_failedPlans >= 3) { _failedPlans = 0; TargetEntry = 0; QuestId = 0; }
        _backoffUntil = unchecked(now + BackoffMs);
    }

    private static float DistanceSquared(Player player, CreatureSpawn spawn)
        => Vector3.DistanceSquared(new(player.X, player.Y, player.Z), new(spawn.X, spawn.Y, spawn.Z));

    private sealed record DestinationEntry(uint Entry, CreatureSpawn Spawn);
}
