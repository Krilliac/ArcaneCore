using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ArcaneCore.World.Bots;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>How a run's group moves between places: the seam for the follow-pathing lane's catch-up policy.</summary>
internal enum PlayerbotGroupTravelPolicy : byte
{
    /// <summary>Path and engage what is in the way; never teleport (a follower that lags walks and fights to catch up).</summary>
    FightThrough,

    /// <summary>The group is moved to its approach point (<see cref="PlayerbotRunOptions.TeleportToEntrance"/>).</summary>
    Teleport,
}

/// <summary>
/// Dungeon and raid runs (docs/areas/playbots-groups.md, "Dungeon and raid runs"): free autonomous bots whose level fits an instance
/// wing of <see cref="PlayerbotRunCatalogue"/> are matched into a group of the instance's size (five, or a raid of 10, 20 or 40) with the
/// roles the content needs, travel to the entrance fighting what is in the way, enter through the ordinary area trigger and kill the
/// wing's bosses in order, pulling one pack of trash at a time; a wipe is recovered from and retried until
/// <see cref="PlayerbotRunOptions.MaxWipes"/>. All the decisions here read <see cref="PlayerbotGroupOptions.DungeonRuns"/> live.
/// <para>
/// <b>Prerequisites.</b> A bot is matched for a wing only when it passes the very check a player walking into the entrance passes
/// (<see cref="AreaTriggerRequirements.Check"/>: the entrance level, the key item, the attunement quest, the conditions table), and a raid
/// also never takes a bot with a permanent bind to the raid map (it would enter its own save, not the group's). With
/// <see cref="PlayerbotRaidRunOptions.SkipPrerequisites"/> the key item, quest and condition are skipped for matching and the server
/// lets the members of a bot raid run through their raid's own entrance without them (<see cref="Waives"/>); the level never is.
/// </para>
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>The group is given this long after the last boss died to loot before it walks out.</summary>
    internal const uint LootSettleMs = 20_000;

    /// <summary>A run given up for lack of a path or an entrance is not tried again by its bots for this long.</summary>
    internal const uint NoPathSetAsideMs = 3_600_000;

    private readonly Dictionary<(Guid Bot, uint Map, int Segment), uint> _runSetAside = [];
    private readonly Dictionary<(uint Map, int Segment, AreaTriggerRequirement Kind), int> _rejectionsNoted = [];
    private InstanceEncounterCatalog? _encounterCatalog;

    /// <summary>The core's encounter catalogue (a private one in a host without it).</summary>
    private InstanceEncounterCatalog EncounterCatalog => _encounterCatalog ??= services.GetService<InstanceEncounterCatalog>() ?? new InstanceEncounterCatalog();

    private IReadOnlyList<InstanceEncounter> CurrentEncounters => EncounterCatalog.Current;
    private PlayerbotRunCatalogue _catalogue = PlayerbotRunCatalogue.Empty;
    private MapRegistry? _catalogueRegistry;
    private object? _catalogueTriggers;
    private CreatureContent? _catalogueCreatures;
    private int _catalogueVersion = -1;
    private IReadOnlyList<InstanceEncounter>? _catalogueEncounters;
    private int _runsCompleted;
    private int _runsGivenUp;

    /// <summary>The start roll (0..1): a run starts when it is below the scan's share of <see cref="PlayerbotRunOptions.StartChancePerMinute"/> (tests replace it).</summary>
    internal Func<double> RunRoll { get; set; } = () => Random.Shared.NextDouble();

    /// <summary>The runs completed and given up so far (status and tests).</summary>
    internal (int Completed, int GivenUp) RunTotals => (_runsCompleted, _runsGivenUp);

    private readonly int[] _runOutcomes = new int[3];

    /// <summary>How many runs of this coordinator ended with <paramref name="outcome"/> (a <see cref="PlayerbotRunMeters"/> label), counted beside the process-wide runs metric, which every coordinator in the process feeds.</summary>
    internal int RunOutcomeCount(string outcome) => Volatile.Read(ref _runOutcomes[PlayerbotRunMeters.IndexOf(outcome)]);

    /// <summary>
    /// The encounters in force (<see cref="InstanceEncounterCatalog"/>): the <c>instance_encounter</c> table as read when this
    /// coordinator first attached, or by the last <c>.reload instance_encounter</c>.
    /// </summary>
    internal IReadOnlyList<InstanceEncounter> Encounters => CurrentEncounters;

    /// <summary>
    /// Swap the encounters in force (world thread, from <c>.reload instance_encounter</c> through the catalogue); the run catalogue is rebuilt
    /// from them at the next scan. Runs already under way keep the plan they were formed with. Returns the encounters replaced (the reload's rollback).
    /// </summary>
    internal IReadOnlyList<InstanceEncounter> ReplaceEncounters(IReadOnlyList<InstanceEncounter> encounters) => EncounterCatalog.Replace(encounters);

    /// <summary>
    /// Read the <c>instance_encounter</c> rows into the core catalogue (first attach; blocking, as the other content loads of the attach). A
    /// catalogue already loaded (a re-attach, or a reload) is kept. An empty table is an empty catalogue, logged.
    /// </summary>
    private void LoadEncounters()
    {
        if (EncounterCatalog.IsLoaded) return;
        if (services.GetService<IServiceScopeFactory>() is not { } scopes) return;
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IInstanceEncounterStore>() is not { } store)
            {
                logger.LogInformation("Playerbot runs: no instance_encounter store is registered; no dungeon or raid runs");
                return;
            }

            EncounterCatalog.Replace(store.LoadAsync().GetAwaiter().GetResult());
            if (CurrentEncounters.Count == 0)
                logger.LogInformation("Playerbot runs: the instance_encounter table is empty (refresh the world database from a classic-db dump); no dungeon or raid runs");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.RunFault,
                "instance_encounter",
                string.Create(CultureInfo.InvariantCulture, $"instance_encounter table could not be read ({ex.GetType().Name}: {ex.Message})"),
                _world?.Identity);
            logger.LogWarning(ex, "Playerbot runs: the instance_encounter table could not be read ({Type}: {Message}); no dungeon or raid runs", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// The runnable dungeons and raids of the world data in force, rebuilt when the maps, area triggers, creatures or encounters change.
    /// The instances left out are logged once per build with their reason.
    /// </summary>
    internal PlayerbotRunCatalogue Catalogue()
    {
        // Until CreatureWorldFeature installs its content (a call it posts to the world thread at attach) Content is CreatureContent.Empty:
        // a catalogue built from it finds no spawn for any boss and leaves every wing out as no-spawned-boss (logged for all 48 wings at
        // each startup). Build nothing, cache nothing and log nothing until the real spawns are in place.
        if (_world is null || CatalogueCreatures(services.GetService<CreatureWorldFeature>()) is not { } creatures) return PlayerbotRunCatalogue.Empty;
        WorldMaps maps = WorldMaps.Of(_world);
        IReadOnlyCollection<AreaTriggerTemplate> triggers = maps.AreaTriggers;
        if (ReferenceEquals(_catalogueRegistry, maps.Registry) && ReferenceEquals(_catalogueTriggers, triggers)
            && ReferenceEquals(_catalogueCreatures, creatures) && _catalogueVersion == creatures.DefinitionsVersion
            && ReferenceEquals(_catalogueEncounters, CurrentEncounters))
            return _catalogue;

        _catalogue = PlayerbotRunCatalogue.Build(maps.Registry, triggers,
            triggers.Select(t => maps.FindAreaTriggerTeleport(t.Id)).OfType<AreaTriggerTeleport>(), creatures, CurrentEncounters);
        _catalogueRegistry = maps.Registry;
        _catalogueTriggers = triggers;
        _catalogueCreatures = creatures;
        _catalogueVersion = creatures.DefinitionsVersion;
        _catalogueEncounters = CurrentEncounters;
        _rejectionsNoted.Clear();
        logger.LogInformation("Playerbot runs: {Plans} dungeon and raid wings can be run, {Excluded} left out", _catalogue.Plans.Count, _catalogue.Exclusions.Count);
        foreach (PlayerbotRunExclusion exclusion in _catalogue.Exclusions)
            logger.LogInformation("Playerbot runs: {Map} ({MapId}) wing {Segment} is not run: {Reason}", exclusion.MapName, exclusion.MapId, exclusion.Segment, exclusion.Reason);
        return _catalogue;
    }

    /// <summary>
    /// The creature content the catalogue may be built from: the feature's content once it is installed, else null (not yet loaded; the
    /// empty placeholder must not be read as "no boss has a spawn").
    /// </summary>
    internal static CreatureContent? CatalogueCreatures(CreatureWorldFeature? feature)
        => feature is { ContentInstalled: true } ? feature.Content : null;

    // --- starting a run ------------------------------------------------------------------------------------------------

    private readonly record struct RunCandidate(PlayerbotGroupBot Bot, Player Player);

    /// <summary>
    /// Start one catalogue wing with explicitly selected managed bots on the world thread. This skips only the random start roll;
    /// formation, level, entrance, team, role and group ownership checks remain the same as an automatic run.
    /// </summary>
    internal BotGroup? StartRun(PlayerbotRunPlan requested, IReadOnlyList<Guid> botIds)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(botIds);
        if (_world is null || !_options.Groups.Enabled || !_options.Groups.DungeonRuns.Enabled
            || OwnGroupCount >= _options.Groups.MaxGroups || GroupManagerOrNull() is not { } groupManager) return null;
        PlayerbotRunPlan? plan = Catalogue().Plans.FirstOrDefault(p => p.MapId == requested.MapId && p.Segment == requested.Segment);
        if (plan is null || botIds.Count != plan.Size || botIds.Distinct().Count() != botIds.Count) return null;
        PlayerbotRunOptions runs = _options.Groups.DungeonRuns;
        int active = _groups.Count(g => g.Handoff is null && g.Run is { } run && run.Plan.Raid == plan.Raid);
        if (plan.Raid ? !runs.Raids.Enabled || active >= runs.Raids.MaxConcurrentRaids : active >= runs.MaxConcurrentRuns) return null;
        if (WorldMaps.Of(_world).FindAreaTriggerTeleport(plan.EntranceTrigger) is not { } teleport) return null;

        IWorldFeature[] features = [.. services.GetServices<IWorldFeature>()];
        IConditionEvaluator? conditions = features.OfType<IConditionEvaluator>().FirstOrDefault();
        IAreaTriggerGate[] gates = [.. features.OfType<IAreaTriggerGate>()];
        InstanceFeature? instances = features.OfType<InstanceFeature>().FirstOrDefault();
        AreaTriggerWaivers waivers = plan.Raid && runs.Raids.SkipPrerequisites
            ? AreaTriggerWaivers.Item | AreaTriggerWaivers.Quest | AreaTriggerWaivers.Condition : AreaTriggerWaivers.None;
        (byte min, byte max) = plan.Band(runs);
        var candidates = new List<PlayerbotGroupCandidate>(botIds.Count);
        foreach (Guid id in botIds)
        {
            if (!_bots.TryGetValue(id, out PlayerbotGroupBot bot) || !bot.Free || bot.Brain.Stay is not null || _memberOf.ContainsKey(id) || _waiting.ContainsKey(id)
                || bot.Session.Player is not { IsInWorld: true, IsAlive: true } player || (player.Flags & PlayerFlags.Ghost) != 0
                || player.Combat.IsInCombat || player.MapId != plan.EntranceMap || player.Level < min || player.Level > max
                || IsRunSetAside(id, plan) || groupManager.GetGroup(player.Guid) is not null || groupManager.GetInvite(player.Guid) is not null
                || Vector3.Distance(new Vector3(player.X, player.Y, player.Z), plan.Entrance) > runs.MaxTravelYards
                || !AreaTriggerRequirements.Check(player, teleport, conditions, gates, waivers).Verdict.Allowed
                || plan.Raid && instances?.Instances.GetPlayerBind(player.Guid, plan.MapId) is { Permanent: true }) return null;
            candidates.Add(Candidate(id, player, Now));
        }

        if (candidates.Select(c => c.Team).Distinct().Count() != 1) return null;
        (int tanks, int healers) = PlayerbotGroupContent.RunRoleNeeds(plan.Size, plan.Raid, _options.Groups);
        if (PlayerbotGroupContent.Match(candidates, plan.Size, _options.Groups, (tanks, healers)) is not { } formed) return null;
        FormRun(plan, formed, runs);
        return _groups[^1];
    }

    /// <summary>
    /// Every scan: when a dungeon or raid slot is free and the start roll succeeds, pick the wing with the most eligible free bots that
    /// can fill its roles and form its group.
    /// </summary>
    private void StartRuns()
    {
        PlayerbotRunOptions runs = _options.Groups.DungeonRuns;
        uint now = Now;
        // Dictionary.Remove during enumeration is defined since .NET Core 3.0 (it does not invalidate the enumerator): no snapshot per scan.
        foreach (((Guid, uint, int) key, uint until) in _runSetAside)
            if (unchecked((int)(until - now)) <= 0) _runSetAside.Remove(key);
        if (!runs.Enabled || runs.StartChancePerMinute <= 0d || _world is null || OwnGroupCount >= _options.Groups.MaxGroups) return;
        if (GroupManagerOrNull() is not { } groupManager) return;

        int dungeonRuns = 0, raidRuns = 0;
        foreach (BotGroup group in _groups)
            if (group.Run is { } running && group.Handoff is null)
            {
                if (running.Plan.Raid) raidRuns++;
                else dungeonRuns++;
            }

        bool dungeonsOpen = dungeonRuns < runs.MaxConcurrentRuns;
        bool raidsOpen = runs.Raids.Enabled && raidRuns < runs.Raids.MaxConcurrentRaids;
        if (!dungeonsOpen && !raidsOpen) return;
        PlayerbotRunCatalogue catalogue = Catalogue();
        if (catalogue.Plans.Count == 0) return;
        double chance = 1d - Math.Pow(1d - runs.StartChancePerMinute, ScanMs / 60_000d);
        if (RunRoll() >= chance) return;

        WorldMaps maps = WorldMaps.Of(_world);
        var free = new List<RunCandidate>();
        foreach (PlayerbotGroupBot bot in _orderedBots)
        {
            if (!bot.Free || bot.Brain.Stay is not null || _memberOf.ContainsKey(bot.BotId) || _waiting.ContainsKey(bot.BotId)
                || bot.Session.Player is not { IsInWorld: true, IsAlive: true } player
                || (player.Flags & PlayerFlags.Ghost) != 0 || player.Combat.IsInCombat) continue;
            if (maps.Registry.Find(player.MapId) is not { IsContinent: true }) continue;
            if (groupManager.GetGroup(player.Guid) is not null || groupManager.GetInvite(player.Guid) is not null) continue;
            free.Add(new RunCandidate(bot, player));
        }

        if (free.Count < 2) return;

        // The same check a player walking into the entrance passes (TeleportHandlers.HandleAreaTrigger).
        IWorldFeature[] features = [.. services.GetServices<IWorldFeature>()];
        IConditionEvaluator? conditions = features.OfType<IConditionEvaluator>().FirstOrDefault();
        IAreaTriggerGate[] gates = [.. features.OfType<IAreaTriggerGate>()];
        InstanceFeature? instances = features.OfType<InstanceFeature>().FirstOrDefault();

        var offers = new List<(PlayerbotRunPlan Plan, List<RunCandidate> Eligible)>();
        foreach (PlayerbotRunPlan plan in catalogue.Plans)
        {
            if (plan.Raid ? !raidsOpen : !dungeonsOpen) continue;
            if (maps.FindAreaTriggerTeleport(plan.EntranceTrigger) is not { } teleport) continue;
            (byte min, byte max) = plan.Band(runs);
            AreaTriggerWaivers waivers = plan.Raid && runs.Raids.SkipPrerequisites
                ? AreaTriggerWaivers.Item | AreaTriggerWaivers.Quest | AreaTriggerWaivers.Condition : AreaTriggerWaivers.None;
            var rejected = new Dictionary<AreaTriggerRequirement, int>();
            int locked = 0;
            var eligible = new List<RunCandidate>();
            foreach (RunCandidate candidate in free)
            {
                Player player = candidate.Player;
                if (player.MapId != plan.EntranceMap || player.Level < min || player.Level > max || IsRunSetAside(candidate.Bot.BotId, plan)
                    || Vector3.Distance(new Vector3(player.X, player.Y, player.Z), plan.Entrance) > runs.MaxTravelYards) continue;
                AreaTriggerCheck check = AreaTriggerRequirements.Check(player, teleport, conditions, gates, waivers);
                if (!check.Verdict.Allowed)
                {
                    rejected[check.Failed] = rejected.GetValueOrDefault(check.Failed) + 1;
                    continue;
                }

                // A bot with a permanent bind to the raid map would enter its own save, not the group's (InstanceManager.GetBoundSaveForSelfOrGroup).
                if (plan.Raid && instances?.Instances.GetPlayerBind(player.Guid, plan.MapId) is { Permanent: true })
                {
                    locked++;
                    continue;
                }

                eligible.Add(candidate);
            }

            NoteRejections(plan, rejected, locked);
            if (eligible.Count >= plan.Size) offers.Add((plan, eligible));
        }

        foreach ((PlayerbotRunPlan plan, List<RunCandidate> eligible) in offers.OrderByDescending(o => o.Eligible.Count).ThenBy(o => o.Plan.MapId).ThenBy(o => o.Plan.Segment))
        {
            (int tanks, int healers) = PlayerbotGroupContent.RunRoleNeeds(plan.Size, plan.Raid, _options.Groups);
            foreach (var team in eligible.GroupBy(e => e.Player.Team).OrderByDescending(g => g.Count()).ThenBy(g => (int)g.Key))
            {
                if (team.Count() < plan.Size) continue;
                List<PlayerbotGroupCandidate> candidates = [.. team.Select(e => Candidate(e.Bot.BotId, e.Player, now))];
                if (PlayerbotGroupContent.Match(candidates, plan.Size, _options.Groups, (tanks, healers)) is not { } formed) continue;
                FormRun(plan, formed, runs);
                return;
            }
        }
    }

    private void FormRun(PlayerbotRunPlan plan, PlayerbotGroupPlan formed, PlayerbotRunOptions runs)
    {
        (byte min, _) = plan.Band(runs);
        PlayerbotRunBoss first = plan.Bosses[0];
        var goal = new PlayerbotGroupGoal(plan.Raid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Dungeon, 0, first.Entry, plan.MapId,
            plan.EntranceMap, plan.Entrance, plan.EntranceTrigger, first.Position, plan.Size, min, "run");
        var machine = new PlayerbotRunMachine(plan, plan.Raid ? runs.Raids.MaxWipes : runs.MaxWipes);
        Form(goal, formed, machine);
        Note($"run {plan.Label}: {plan.Bosses.Count} bosses ({string.Join(", ", plan.Bosses.Select(b => b.Name))}), {plan.Scripted.Count} scripted");
    }

    private void NoteRejections(PlayerbotRunPlan plan, Dictionary<AreaTriggerRequirement, int> rejected, int locked)
    {
        foreach ((AreaTriggerRequirement kind, int count) in rejected)
        {
            if (_rejectionsNoted.TryGetValue((plan.MapId, plan.Segment, kind), out int noted) && noted == count) continue;
            _rejectionsNoted[(plan.MapId, plan.Segment, kind)] = count;
            Note($"run {plan.Label}: {count} bots in the level band lack the {kind.ToString().ToLowerInvariant()} requirement");
        }

        _rejectionsNoted.TryGetValue((plan.MapId, plan.Segment, AreaTriggerRequirement.None), out int lockedNoted);
        if (locked != lockedNoted)
        {
            _rejectionsNoted[(plan.MapId, plan.Segment, AreaTriggerRequirement.None)] = locked;
            if (locked > 0) Note($"run {plan.Label}: {locked} bots are locked to the raid");
        }
    }

    private bool IsRunSetAside(Guid botId, PlayerbotRunPlan plan)
        => _runSetAside.TryGetValue((botId, plan.MapId, plan.Segment), out uint until) && unchecked((int)(until - Now)) > 0;

    private void SetRunAside(Guid botId, PlayerbotRunMachine run, string reason)
    {
        uint ms = reason.Contains("path", StringComparison.Ordinal) || reason.Contains("entrance", StringComparison.Ordinal) ? NoPathSetAsideMs : SetAsideMs;
        _runSetAside[(botId, run.Plan.MapId, run.Plan.Segment)] = unchecked(Now + ms);
    }

    /// <summary>Whether <paramref name="botId"/> set the wing aside (tests and status).</summary>
    internal bool IsRunSetAside(Guid botId, uint mapId, int segment)
        => _runSetAside.TryGetValue((botId, mapId, segment), out uint until) && unchecked((int)(until - Now)) > 0;

    // --- the run's life -------------------------------------------------------------------------------------------------

    /// <summary>The live limits of the run's wipe count (the options change under the running group), and the wiped step's budget.</summary>
    private void SyncRun(BotGroup group)
    {
        if (group.Run is not { } run) return;
        run.MaxWipes = run.Plan.Raid ? _options.Groups.DungeonRuns.Raids.MaxWipes : _options.Groups.DungeonRuns.MaxWipes;
        // Before the outcome check, so a wipe whose budget has gone is already in retry and is not reported as stuck there.
        run.NoteTime(Now, PlayerbotGroupWatch.WipedBudgetMs);
    }

    private uint GoalTimeoutOf(BotGroup group)
    {
        if (group.WorldBoss is not null) return (uint)Math.Max(1, Wb.MaxRaidMinutes) * 60_000u;
        if (group.Run is not { } run) return unchecked(GoalTimeoutMs + group.ExtraTimeMs);
        int minutes = run.Plan.Raid ? _options.Groups.DungeonRuns.Raids.MaxRunMinutes : _options.Groups.DungeonRuns.MaxRunMinutes;
        return (uint)Math.Max(1, minutes) * 60_000u;
    }

    /// <summary>The longest the current step of <paramref name="group"/> may last: a run's travel budget, an instance group's distance budget, else <see cref="StepTimeoutMs"/>.</summary>
    internal uint StepLimitOf(BotGroup group)
    {
        if ((group.Run is not null || group.WorldBoss is not null) && group.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling or PlayerbotGroupState.Regrouping)
            return (uint)Math.Max(1, _options.Groups.DungeonRuns.TravelTimeoutMinutes) * 60_000u;
        if (group.StepBudgetMs != 0 && group.State is PlayerbotGroupState.Gathering or PlayerbotGroupState.Travelling) return group.StepBudgetMs;
        return StepTimeoutMs;
    }

    /// <summary>Seconds of the travel budget that do not depend on the distance (forming up, resting after a pull, a short fight).</summary>
    internal const float TravelBudgetBaseSeconds = 120f;

    /// <summary>
    /// The factor on the walking time of the straight line: the path is longer than the line (the Moonbrook mine is 320 yards of path for
    /// 22 yards of line), and the group stops to fight and to rest on the way.
    /// </summary>
    internal const float TravelBudgetMargin = 3f;

    /// <summary>
    /// How long a group may take to gather or to walk <paramref name="yards"/> (straight line) at <paramref name="moveSpeed"/> yards a
    /// second: <c>yards / speed * <see cref="TravelBudgetMargin"/> + <see cref="TravelBudgetBaseSeconds"/></c>, at least
    /// <see cref="StepTimeoutMs"/> and at most <paramref name="capMs"/> (<c>TravelTimeoutMinutes</c>; never below the minimum).
    /// </summary>
    internal static uint TravelBudgetMs(float yards, float moveSpeed, uint capMs)
    {
        if (!float.IsFinite(yards) || yards < 0f) return StepTimeoutMs;
        double seconds = (yards / Math.Max(1f, moveSpeed) * TravelBudgetMargin) + TravelBudgetBaseSeconds;
        return (uint)Math.Clamp(seconds * 1000d, StepTimeoutMs, Math.Max(StepTimeoutMs, capMs));
    }

    /// <summary>
    /// An instance group (not a run: a run has its flat travel budget) gets a budget that grows with the distance it has to cover in this
    /// step, measured when the step begins: gathering, the farthest member to the leader plus the leader's walk to the approach point;
    /// travelling, the leader's walk. Open-world quest groups keep <see cref="StepTimeoutMs"/>: their objective is where they met.
    /// The goal's own bound grows by what was granted (<see cref="BotGroup.ExtraTimeMs"/>).
    /// </summary>
    private void BudgetTravel(BotGroup group, PlayerbotGroupState state)
    {
        group.StepBudgetMs = 0;
        if (group.Run is not null || !group.Goal.InInstance || _world is null || group.LeaderPlayer(_world) is not { } leader) return;
        float yards = 0f;
        if (state == PlayerbotGroupState.Gathering)
            foreach (Member member in group.Members.Where(m => m.Joined && !m.Real && m.Guid != leader.Guid))
                if (_world.FindOnlinePlayer(member.Guid) is { IsAlive: true } player && ReferenceEquals(player.Map, leader.Map))
                    yards = Math.Max(yards, Distance(player, leader));
        if (leader.MapId == group.Goal.MeetingMap) yards += Vector3.Distance(new Vector3(leader.X, leader.Y, leader.Z), group.Approach);
        uint cap = (uint)Math.Max(1, _options.Groups.DungeonRuns.TravelTimeoutMinutes) * 60_000u;
        uint budget = TravelBudgetMs(yards, _options.MoveSpeed, cap);
        if (budget <= StepTimeoutMs) return;
        group.StepBudgetMs = budget;
        group.ExtraTimeMs = unchecked(group.ExtraTimeMs + budget - StepTimeoutMs);
    }

    /// <summary>
    /// One line for a gather or travel timeout (the enter timeout sends none): the budget, and for the leader and every bot member its map, position, state and
    /// distances (to the approach point and to the leader). Level and class stand for the bot, as in the stall reports.
    /// </summary>
    internal static string DescribeTimeout(string reason, uint seconds, uint budgetSeconds, string goal, string state,
        IEnumerable<(string Who, uint MapId, Vector3 Position, float ToApproach, float ToLeader, bool Alive, string Step)> members)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{reason} after {seconds}s of {budgetSeconds}s ({state}) for {goal}:");
        foreach ((string who, uint mapId, Vector3 position, float toApproach, float toLeader, bool alive, string step) in members)
            text.Append(CultureInfo.InvariantCulture,
                $" [{who} map {mapId} at {position.X:F1},{position.Y:F1},{position.Z:F1} {toApproach:F0}yd to goal {toLeader:F0}yd to leader {(alive ? "alive" : "dead")} {step}]");
        return text.ToString();
    }

    /// <summary>Note and log <see cref="DescribeTimeout"/> for <paramref name="group"/> before it gives up on <paramref name="reason"/>.</summary>
    private void ReportTimeout(BotGroup group, Player leader, string reason)
    {
        if (_world is null) return;
        var members = new List<(string, uint, Vector3, float, float, bool, string)>();
        var leaderAt = new Vector3(leader.X, leader.Y, leader.Z);
        foreach (Member member in group.Members.Where(m => m.Joined && !m.Real))
        {
            if (_world.FindOnlinePlayer(member.Guid) is not { } player)
            {
                members.Add(($"{member.Role.ToString().ToLowerInvariant()} offline", 0, default, 0f, 0f, false, "none"));
                continue;
            }

            var at = new Vector3(player.X, player.Y, player.Z);
            members.Add(($"L{player.Level} {player.Class} {member.Role.ToString().ToLowerInvariant()}{(member.Guid == leader.Guid ? " leader" : string.Empty)}",
                player.MapId, at, Vector3.Distance(at, group.Approach), Vector3.Distance(at, leaderAt), player.IsAlive, member.AI?.Step ?? "none"));
        }

        string line = DescribeTimeout(reason, unchecked(Now - group.StateSinceMs) / 1000, StepLimitOf(group) / 1000, group.Goal.ToString(),
            group.State.ToString().ToLowerInvariant(), members);
        Note($"group {group.Id} {line}");
        logger.LogWarning("Playerbot group {Group} {Line}", group.Id, line);
    }

    /// <summary>The Engaging state of a run: bosses die, a boss may be absent, the group wipes or finishes and loots.</summary>
    internal void AdvanceRun(BotGroup group, Player leader, PlayerbotRunMachine run, uint now)
    {
        if (run.Step == PlayerbotRunStep.Leave)
        {
            if (group.RunDoneMs == 0) group.RunDoneMs = Math.Max(1u, now);
            bool busy = group.InFight(now) || group.Members.Any(m => m.AI is { Looting: true });
            if (busy && unchecked(now - group.RunDoneMs) <= LootSettleMs) return;
            Note($"group {group.Id} completed {run.Plan.Label}");
            group.Succeeded = true;
            // A handed-over group stays where it is: the lead goes back to the player and the bots follow them again.
            if (group.Handoff is { } handoff) EndHandoff(group, success: true, "completed", handBackTo: handoff.HandedBy);
            else Enter(group, leader.MapId == group.Goal.MapId ? PlayerbotGroupState.Leaving : PlayerbotGroupState.Disbanding);
            return;
        }

        group.UpdateObjective(_world!);
        if (group.ObjectiveKilled && group.ObjectiveCreature != 0)
        {
            uint entry = group.ObjectiveCreature;
            string name = run.Plan.Bosses.FirstOrDefault(b => b.Entry == entry)?.Name ?? entry.ToString(CultureInfo.InvariantCulture);
            run.Apply(PlayerbotRunSignal.BossDead(entry));
            group.ClearObjective();
            Note($"group {group.Id} killed {name} ({run.Killed}/{run.BossCount})");
            if (run.Step == PlayerbotRunStep.Leave) group.RunDoneMs = Math.Max(1u, now);
            return;
        }

        if (CheckWipe(group, leader)) return;
        if (!group.Objective.IsEmpty && run.Step == PlayerbotRunStep.Clear) run.Apply(PlayerbotRunSignal.BossEngaged);

        // At the boss's spawn with none of it alive in sight: killed already (a saved instance), or gone. Counted as done.
        if (run.CurrentBoss is { } current && ObjectiveMissing(group, leader, now))
        {
            run.Apply(PlayerbotRunSignal.BossDead(current.Entry));
            group.ClearObjective();
            Note($"group {group.Id}: {current.Name} is not at its spawn, counted as done ({run.Killed}/{run.BossCount})");
            if (run.Step == PlayerbotRunStep.Leave) group.RunDoneMs = Math.Max(1u, now);
        }
    }

    /// <summary>With <see cref="PlayerbotRunOptions.TeleportToEntrance"/>, the group's living members on the entrance's continent are moved to the approach point.</summary>
    private void TeleportToEntranceIfConfigured(BotGroup group)
    {
        if (group.Run is null || !_options.Groups.DungeonRuns.TeleportToEntrance || _world is null
            || services.GetService<TeleportFeature>()?.Teleports is not { } teleports) return;
        foreach (Member member in group.Members.Where(m => m.Joined && !m.Real))
            if (_world.FindOnlinePlayer(member.Guid) is { IsAlive: true } player && player.MapId == group.Goal.MeetingMap && !teleports.IsBeingTeleported(player)
                && Vector3.Distance(new Vector3(player.X, player.Y, player.Z), group.Approach) > GatherYards)
                teleports.TeleportTo(player, group.Goal.MeetingMap, group.Approach.X, group.Approach.Y, group.Approach.Z, 0f);
        Note($"group {group.Id} was moved to the entrance of {group.Run.Plan.Label} (TeleportToEntrance)");
    }

    private void NoteRunEnded(BotGroup group)
    {
        if (group.Run is not { } run) return;
        if (group.Succeeded && run.Step == PlayerbotRunStep.Leave) run.Apply(PlayerbotRunSignal.Left);
        string? reason = run.Complete ? null : run.Reason ?? group.Reason ?? "disbanded";
        string outcome = PlayerbotRunMeters.OutcomeOf(run.Complete, reason);
        PlayerbotRunMeters.Count(outcome);
        Interlocked.Increment(ref _runOutcomes[PlayerbotRunMeters.IndexOf(outcome)]);
        if (run.Complete) _runsCompleted++;
        else
        {
            _runsGivenUp++;
            ReportRunFailed(group, run, reason!);
        }
    }

    /// <summary>
    /// Whether a run that ended with <paramref name="reason"/> points at content or code and so belongs in bot QA: no path to the entrance or the boss, a step that
    /// really timed out (gather, travel, enter, regroup, run), a boss that is not where the content says, a stalled group, or a group that wiped out its wipes.
    /// Operator and lifecycle ends are not findings (a bot stopped, scaled down, taken over or leashed: <c>leader-stopped</c>, <c>leader-left</c>, <c>leader-gone</c>;
    /// <c>too-few</c>, <c>invites-unanswered</c>, <c>disbanded</c>, <c>aborted</c>); they still count in the runs metric.
    /// </summary>
    internal static bool PointsAtContentOrCode(string reason)
        => reason.StartsWith("no-path", StringComparison.Ordinal) || reason.EndsWith("timeout", StringComparison.Ordinal)
            || reason is "objective-missing" or "stalled" or "wiped";

    /// <summary>
    /// Report a run that ended for a content or code reason (<see cref="PointsAtContentOrCode"/>) to bot QA, straight to the sink (docs/areas/playbots-qa.md): the
    /// instance map and wing, the stage it failed at, the boss it was heading for and the reason. No group, bot or character is named. Once per run (the disband
    /// that calls this happens once), never per tick, and nothing is built while QA is off; a failure of the report is swallowed so it cannot reach the world tick.
    /// </summary>
    private void ReportRunFailed(BotGroup group, PlayerbotRunMachine run, string reason)
    {
        try
        {
            if (!PointsAtContentOrCode(reason)) return;
            if (services.GetService<IPlayerbotQaSink>() is not { Enabled: true } sink) return;
            PlayerbotRunStep stage = run.Step == PlayerbotRunStep.GiveUp ? run.FailedStep : run.Step;
            string stageName = stage.ToString().ToLowerInvariant();
            uint boss = stage is PlayerbotRunStep.Clear or PlayerbotRunStep.Boss ? run.CurrentBoss?.Entry ?? 0 : 0;
            PlayerbotRunPlan plan = run.Plan;
            sink.Report(new QaSignal
            {
                Kind = QaSignalKind.RunFailed, AtUtc = DateTime.UtcNow, Map = plan.MapId, Segment = plan.Segment, Stage = stageName, Target = boss, Reason = reason,
                Detail = plan.MapName,
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"{(plan.Raid ? "raid" : "dungeon")} run ended: {reason} in {plan.MapName} (map {plan.MapId} wing {plan.Segment}) at {stageName}, {run.Killed}/{run.BossCount} bosses dead, {run.Wipes} wipes"),
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            PlayerbotGroupAnomalies.ReportSystem(PlayerbotGroupAnomalies.RunFault,
                string.Create(CultureInfo.InvariantCulture, $"group {group.Id} (leader {group.LeaderGuid.Value:X})"),
                string.Create(CultureInfo.InvariantCulture, $"group {group.Id} run failure report failed ({ex.GetType().Name}: {ex.Message})"),
                _world?.Identity);
            logger.LogDebug(ex, "Playerbot group {Group}: the run failure could not be reported to bot QA", group.Id);
        }
    }

    /// <summary>
    /// Observability only. A completed run's leader asks for the reset the way a client would (CMSG_RESET_INSTANCES through the
    /// ordinary handler) and notes and logs the answer: the reset, or the refusal with its reason (an offline member or an
    /// occupied map). It does not make the next run fresh: the disband that follows resets the group's instances itself
    /// (InstanceManager.OnGroupDisbanding, vmangos Group::Disband), and a refusal here means that reset skips the same save.
    /// Skipped when any member of the server group is not a managed bot, because ResetSave unbinds every player on the save.
    /// vmangos WorldSession::HandleResetInstancesOpcode and Group::ResetInstances(INSTANCE_RESET_ALL).
    /// </summary>
    private void ResetCompletedRun(BotGroup group)
    {
        if (!group.Succeeded || group.Run is not { Step: PlayerbotRunStep.Leave or PlayerbotRunStep.Complete }
            || !_bots.TryGetValue(group.LeaderBotId, out PlayerbotGroupBot bot)
            || bot.Session.Player is not { } leader || leader.Guid != group.LeaderGuid) return;

        if (group.Members.Any(m => m.Real) || GroupManagerOrNull()?.GetGroup(leader.Guid) is { } server
            && server.Members.Any(m => !group.Members.Any(b => !b.Real && b.Guid == m.Guid)))
        {
            Note($"group {group.Id} reset request skipped: a member is not a managed bot");
            return;
        }

        WorldSession session = bot.Session;
        session.DrainManagedPackets(WorldOpcode.SmsgInstanceReset, WorldOpcode.SmsgInstanceResetFailed);
        if (!Act(session, WorldOpcode.CmsgResetInstances, [], budgeted: false))
        {
            Note($"group {group.Id} reset request was not handled");
            logger.LogWarning("Playerbot group {Group} reset request was not handled", group.Id);
            return;
        }

        foreach (ManagedSessionPacket packet in session.DrainManagedPackets(WorldOpcode.SmsgInstanceReset, WorldOpcode.SmsgInstanceResetFailed))
        {
            if (packet.Opcode == WorldOpcode.SmsgInstanceReset && packet.Payload.Length >= 4)
            {
                uint map = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload);
                Note($"group {group.Id} reset instance {map}");
                logger.LogInformation("Playerbot group {Group} reset instance {Map}", group.Id, map);
            }
            else if (packet.Opcode == WorldOpcode.SmsgInstanceResetFailed && packet.Payload.Length >= 8)
            {
                var reason = (InstanceResetFailedReason)BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload);
                uint map = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(4));
                Note($"group {group.Id} reset instance {map} failed: {reason}");
                logger.LogWarning("Playerbot group {Group} reset instance {Map} failed: {Reason}", group.Id, map, reason);
            }
        }
    }

    // --- the policy seam and the entry waiver --------------------------------------------------------------------------

    /// <summary>
    /// How <paramref name="botId"/>'s group moves, or null when it is not on a run. The follow-pathing lane's catch-up consults this: a
    /// run's followers never teleport to catch up (<see cref="PlayerbotGroupTravelPolicy.FightThrough"/>) unless the run was told to
    /// move the group to its entrance.
    /// </summary>
    internal PlayerbotGroupTravelPolicy? TravelPolicyFor(Guid botId)
        => _memberOf.TryGetValue(botId, out BotGroup? group) && group.Run is not null
            ? _options.Groups.DungeonRuns.TeleportToEntrance ? PlayerbotGroupTravelPolicy.Teleport : PlayerbotGroupTravelPolicy.FightThrough
            : null;

    /// <summary>
    /// The entry requirements skipped for <paramref name="player"/> entering through <paramref name="teleport"/>
    /// (<see cref="TeleportHandlers"/> asks every <see cref="IAreaTriggerWaiver"/>): the key item, the quest and the conditions-table
    /// reference, and only when ALL hold: <see cref="PlayerbotRaidRunOptions.SkipPrerequisites"/> is on, the player is a managed bot, it
    /// is a joined bot member of a group on a raid run, and the teleport leads into that run's raid. A real player, a bot outside a raid
    /// run, a dungeon run's bot and any other teleport are checked as always; the level is never waived.
    /// </summary>
    public AreaTriggerWaivers Waives(Player player, AreaTriggerTeleport teleport)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(teleport);
        PlayerbotRunOptions runs = _options.Groups.DungeonRuns;
        if (!runs.Enabled || !runs.Raids.Enabled || !runs.Raids.SkipPrerequisites || player.Session is not WorldSession { IsManaged: true }) return AreaTriggerWaivers.None;
        foreach (BotGroup group in _groups)
        {
            if (group.Run is not { Plan.Raid: true } || teleport.TargetMap != group.Goal.MapId) continue;
            foreach (Member member in group.Members)
                if (member.Guid == player.Guid && member.Joined && !member.Real)
                    return AreaTriggerWaivers.Item | AreaTriggerWaivers.Quest | AreaTriggerWaivers.Condition;
        }

        return AreaTriggerWaivers.None;
    }

    // --- the report -----------------------------------------------------------------------------------------------------

    private string? RunSummary()
    {
        PlayerbotRunOptions runs = _options.Groups.DungeonRuns;
        int dungeons = 0, raids = 0;
        foreach (BotGroup group in _groups)
            if (group.Run is { } run && group.Handoff is null)
            {
                if (run.Plan.Raid) raids++;
                else dungeons++;
            }

        return string.Create(CultureInfo.InvariantCulture,
            $"runs {(runs.Enabled ? "enabled" : "disabled")}: dungeon {dungeons}/{runs.MaxConcurrentRuns} raid {raids}/{(runs.Raids.Enabled ? runs.Raids.MaxConcurrentRaids : 0)} completed {_runsCompleted} given-up {_runsGivenUp}; {_catalogue.Plans.Count} wings known, {_catalogue.Exclusions.Count} left out.");
    }

    private static string DescribeRun(PlayerbotRunMachine run)
    {
        PlayerbotRunPlan plan = run.Plan;
        int shown = run.AllBossesDead ? run.BossCount : run.Killed + 1;
        string boss = run.CurrentBoss?.Name ?? plan.Bosses[^1].Name;
        return string.Create(CultureInfo.InvariantCulture,
            $"run={plan.MapName}({plan.MapId}):{plan.Segment} boss={shown}/{run.BossCount}({boss}) scripted={plan.Scripted.Count} wipes={run.Wipes}/{run.MaxWipes} step={run.StepName} pulls-left={run.PullsLeft}");
    }
}
