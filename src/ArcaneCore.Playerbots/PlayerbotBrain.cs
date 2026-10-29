using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Events;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using ArcaneCore.World.Bots;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A deterministic, world-thread autonomous player. The feature/host owns scheduling and
/// persistence; this controller owns only bounded ordinary gameplay decisions for one session.
/// </summary>
internal sealed partial class PlayerbotBrain(WorldSession session, PlayerbotOptions options,
    PlayerbotSharedSetAsides? shared = null, PlayerbotClaims? claims = null, PlayerbotNavigationExecutor? navigationExecutor = null)
{
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly PlayerbotContention _contention = new(options, claims);

    /// <summary>The bots' social memory seam (set by the feature; null in a host or a test without it).</summary>
    internal BotSocial.PlayerbotSocialRecorder? Social { get; set; }
    private PlayerbotRoute? _route;
    private bool _routeAbandoned;

    /// <summary>The reason of the latest transit leg given up (a tram trip, a lift ride, a travel leg or journey; <see cref="GiveUpTransitLeg"/>); null: none yet (inspection, tests).</summary>
    internal string? LastTransitFailure { get; private set; }

    /// <summary>Transit legs given up so far (<see cref="GiveUpTransitLeg"/>).</summary>
    internal int TransitFailureCount { get; private set; }

    /// <summary>
    /// A tram trip, lift ride or travel leg (a ship, the hearthstone, a mage leg, a journey) failed (its driver reported the anomaly once and backed the failing point off, <see cref="PlayerbotTransitBackoff"/>):
    /// only that leg is given up. Logged, the stall watch reset, the route dropped; the quest and its giver stay as they are, and no dead-end
    /// pin is counted (a failed leg says nothing about the cell the bot stands in). The goal plans again next think and meets the backoff:
    /// it walks, or waits, instead of re-entering the failed point.
    /// </summary>
    private void GiveUpTransitLeg(string why)
    {
        LastTransitFailure = why;
        TransitFailureCount++;
        _session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotBrain>()
            .LogInformation("Playerbot transit leg given up, the goal plans again: {Why}", why);
        _stall.Reset();
        _target = null;
        AbandonRoute();
        _attacking = false;
    }

    /// <summary>
    /// What the brain does with one travel driver's answer this think (<see cref="PlayerbotShip.Drive"/>, <see cref="PlayerbotHearthstone.Drive"/>,
    /// <see cref="PlayerbotMageService.Drive"/>, <see cref="PlayerbotTravel.Drive"/>): true when the think ends here. Traveling: the leg owns the
    /// bot (<see cref="HoldForTravel"/>). Failed: only the leg is given up (<see cref="GiveUpTransitLeg"/>; the driver reported it). Arrived: the
    /// stall watch starts afresh and the think goes on. Idle: the think goes on.
    /// </summary>
    private bool TravelOwns(TravelLegStep step, string? failure, string driver)
    {
        switch (step)
        {
            case TravelLegStep.Traveling:
                HoldForTravel();
                return true;
            case TravelLegStep.Failed:
                GiveUpTransitLeg(failure ?? $"{driver}: the leg failed (no reason recorded)");
                return true;
            case TravelLegStep.Arrived:
                _stall.Reset();
                return false;
            default:
                return false;
        }
    }

    /// <summary>A travel leg owns the bot this think: no stall is judged, no target held, the brain's own route given up.</summary>
    private void HoldForTravel()
    {
        _stall.Reset();
        _target = null;
        AbandonRoute();
        _attacking = false;
    }

    /// <summary>
    /// The brain gives up its route. The motion keeps walking a route for its lease (<see cref="PlayerbotMotion"/>: seconds, as long as
    /// the bot's think gap), so a route given up and not replaced is told to the host (<see cref="TakeRouteAbandoned"/>), which stops
    /// the bot when no think followed another route.
    /// </summary>
    private void AbandonRoute()
    {
        if (_route is not null) _routeAbandoned = true;
        _route = null;
    }

    /// <summary>True once after a think gave up a route (<see cref="AbandonRoute"/>).</summary>
    internal bool TakeRouteAbandoned()
    {
        bool abandoned = _routeAbandoned;
        _routeAbandoned = false;
        return abandoned;
    }
    private PlayerbotRoute? _waterRoute;
    private uint _waterRetryAt;
    private uint _dryMs;
    private Creature? _target;
    private uint _thinkElapsed;
    private bool _attacking;
    private bool _lootOpened;
    private uint _restUntilMs;
    private PlayerbotConsumableKind _restKind;
    private int _roamDirection;
    private int _exploreRefusals;

    /// <summary>
    /// The phase the last think that got past the think-interval gate ended in (the stall watch fires inside such a think, before its
    /// decision, so this is where the stalled work was). An <see cref="Update"/> call that returns at the gate does no work and does not set it.
    /// </summary>
    private string _lastWorkPhase = "start";

    /// <summary>The running <see cref="Update"/> call got past the think-interval gate (a real think).</summary>
    private bool _ranWorkThink;

    /// <summary>Set by <see cref="ReportStall"/>: the phase the stalled work was in; cleared at the next think. Read by the stall diagnostic.</summary>
    private string? _stalledWorkPhase;

    /// <summary>The phase the stall diagnostic names while the stall of this think is being reported (null otherwise).</summary>
    internal string? StalledWorkPhase => _stalledWorkPhase;

    /// <summary>"wander-refused" has been traced for the current refusal episode (cleared when a wander leg is accepted).</summary>
    private bool _exploreRefusalTraced;

    /// <summary>Picks the mesh points of the wander fallback (<see cref="TryWanderToMeshPoint"/>); seeded from the guid, think-thread only.</summary>
    private Random? _wanderRandom;

    /// <summary>The last explore leg was refused for a strong creature's reach, not by the mesh: the next ray stays full length (<see cref="WanderLegEnd"/>).</summary>
    private bool _exploreRingRefused;
    private bool _exploreIslandWatchStarted;
    private bool _earlyExploreIslandChecked;
    private System.Numerics.Vector3 _exploreIslandAnchor;
    private uint _exploreIslandMap;
    private uint _exploreIslandSince;
    private PlayerbotExploreAtlas.Candidate? _exploreCandidate;
    private readonly Dictionary<(uint Map, int X, int Y), uint> _exploreBlockedCells = [];
    private readonly List<(uint Map, int X, int Y)> _expiredExploreCells = new(64);
    private System.Numerics.Vector3 _exploreLastPosition;
    private uint _exploreLastMap;
    private bool _exploreHasPosition;
    private Player? _exploreSearchPlayer;
    private bool _exploreSearchSkipIndoor;
    private CreatureContent? _exploreSearchContent;
    private PlayerbotExploreAtlas? _exploreSearchAtlas;
    private FactionTemplateCatalog? _exploreSearchFactions;
    private FactionTemplateRecord? _exploreSearchSelf;
    private ArcaneCore.Game.Reputation.INpcReactionSource? _exploreSearchReactions;
    private PlayerbotHomeArea? _exploreSearchHome;
    private IReadOnlyList<PlayerbotHazard> _exploreSearchHazards = [];
    private uint _exploreSearchMap;
    private Func<PlayerbotExploreAtlas.Candidate, bool>? _exploreEligibleFilter;
    private Func<uint, bool>? _exploreFactionFilter;
    private Func<uint, bool>? _exploreThreatFilter;
    private Func<int, int, bool>? _exploreBlockedFilter;
    private uint _exploreSearchAfter;
    private long _exploreSearchStartedAt;
    private int _exploreCandidateChecks;
    private long _thinkStartedAt;
    private PlayerbotGoalKind _goal = PlayerbotGoalKind.Explore;
    private PlayerbotGoalKind? _planningGoal;
    private long _lootStartedMs;
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _lootActions = new();
    private bool _lootResponseReceived;
    private readonly HashSet<ObjectGuid> _lootedCorpses = [];
    private readonly PlayerbotQuestGoals _quests = new(session, options);
    private readonly PlayerbotTownGoals _town = new(session, options);
    private readonly PlayerbotEconomy _economy = new(session, options);
    private readonly PlayerbotBank _bank = new(session, options);

    /// <summary>The bot's bank errand at its capital home (<see cref="PlayerbotBank"/>; tests, inspection).</summary>
    internal PlayerbotBank Bank => _bank;
    private readonly PlayerbotBudget _budget = new(session, options);
    private readonly PlayerbotRidingTrips _riding = new(session, options);
    private readonly PlayerbotTrainerDestinations _trainers = new(session, options);
    private readonly PlayerbotWorldDestinations _destinations = new(session, options, navigationExecutor);
    private readonly PlayerbotCombatSpells _combatSpells = new(session);
    private PlayerbotKite? _kite;

    /// <summary>The kite of this brain's fights (<see cref="PlayerbotKite"/>; <c>World:Playerbots:Combat:Kite</c>).</summary>
    internal PlayerbotKite Kite => _kite ??= new PlayerbotKite(_session, _options, _combatSpells);
    private readonly Combat.PlayerbotInvincibleWatch _invincibleWatch = new();
    private PlayerbotPvpFight? _pvpFight;
    private PlayerbotPvpFight Pvp => _pvpFight ??= new PlayerbotPvpFight(session, _combatSpells);
    private readonly Progression.PlayerbotCrafting _crafting = new(session, options);
    private PlayerbotOpportunities? _opportunities;
    private readonly PlayerbotRecovery _recovery = new(session, options);
    private readonly PlayerbotHomeReturn _homeReturn = new();
    private PlayerbotHomeArea? _home;
    private PlayerbotRisk? _risk;

    /// <summary>
    /// The bot's home area (<see cref="PlayerbotHomeArea"/>), or null for none. While set, goal selection prefers what lies inside it
    /// and a bot that strays out of it with no errand walks back (<see cref="PlayerbotHomeReturn"/>). Setting a home drops the current
    /// intent and the walk home in progress. World thread (or before the bot is started).
    /// </summary>
    internal PlayerbotHomeArea? Home
    {
        get => _home;
        set
        {
            if (ReferenceEquals(_home, value)) return;
            _home = value;
            _homeReturn.Reset();
            ReleaseEventSpot(PlayerbotEventMeters.Done); // the claim is the old town's: its coordinates, map and place
            _townIdle.Rehomed();
            _quests.Home = value;
            _destinations.Home = value;
            _crafting.Home = value;
            DropIntent();
        }
    }

    /// <summary>
    /// The in-area filters (quest takers, quest and errand destinations) apply only while the bot is on its home map. A bot on
    /// another map is off-map (<see cref="PlayerbotHomeState.OffMap"/>): the area has no meaning there, and applying it would skip every
    /// creature and take no quest, so it behaves as a bot with no home until it is on the home map again.
    /// </summary>
    private void SyncHomeFilters(Player player)
    {
        PlayerbotHomeArea? active = _home is { } home && (player.Map?.MapId ?? player.MapId) == home.MapId ? home : null;
        _quests.Home = active;
        _destinations.Home = active;
        _crafting.Home = active;
    }

    /// <summary>The home the in-area filters use right now: the home while the bot is on its map, else null (tests).</summary>
    internal PlayerbotHomeArea? ActiveFilterHome => _quests.Home;

    /// <summary>The home line <c>.playerbot status</c> shows (<c>inside:120</c>, <c>returning:812</c>, <c>off-map</c>, <c>blocked:812</c>), null without a home.</summary>
    internal string? HomeReport => _home is null ? null : _home.ZoneFoundUnknown ? "zone-unknown" : _homeReturn.Report;

    /// <summary>The home line BOTINSPECT shows (place, map, centre, radius, distance, state), null without a home.</summary>
    internal string? HomeDescription => _home is null ? null : _homeReturn.Describe(_home, _options.Home.ReturnMode);

    /// <summary>The walk back to the home (inspection and tests).</summary>
    internal PlayerbotHomeReturn HomeReturn => _homeReturn;

    /// <summary>Risk against reward and retreat (<see cref="PlayerbotRisk"/>; inspection and tests).</summary>
    internal PlayerbotRisk Risk => _risk ??= NewRisk();

    private PlayerbotRisk NewRisk()
    {
        var risk = new PlayerbotRisk(_session, _options, _combatSpells);
        _recovery.Hazards = risk.HazardThreats;
        return risk;
    }

    /// <summary>The risk line BOTINSPECT and <c>.playerbot status</c> show (<see cref="PlayerbotRisk.Report"/>); while a defender waits out its resurrection sickness it is prefixed with <c>decision=defend-wait-sickness; </c>, the real risk line follows.</summary>
    internal string RiskReport => _defendWaitsSickness ? DefendWaitSicknessReport + "; " + (_options.Risk.Enabled ? Risk.Report : "decision=off") : _options.Risk.Enabled ? Risk.Report : "decision=off";

    /// <summary>The risk line of a defender that holds where it stands until its resurrection sickness ends (<see cref="DefendWaitsSickness"/>).</summary>
    internal const string DefendWaitSicknessReport = "decision=defend-wait-sickness";

    /// <summary>What the bot saves for (<see cref="PlayerbotBudget"/>; inspection and tests).</summary>
    internal PlayerbotBudget Budget => _budget;

    /// <summary>The budget line BOTINSPECT shows (<see cref="PlayerbotBudget.Report"/>).</summary>
    internal string BudgetReport(Player player) => _budget.Report(player);

    /// <summary>The ghost recovery (inspection and tests).</summary>
    internal PlayerbotRecovery Recovery => _recovery;
    private readonly PlayerbotEquipment _equipment = new(session);
    private readonly PlayerbotTraceLog _trace = new();

    /// <summary>Recent decision, route, and loop transitions for QA diagnostics.</summary>
    internal PlayerbotTraceLog Trace => _trace;

    /// <summary>Thinks that reached the explore's own wander plan, past the town idle (tests, inspection).</summary>
    internal int ExploreWanderPlans { get; private set; }

    /// <summary>The last <see cref="PlayerbotThinkProbe"/> phase seen while this brain was updating.</summary>
    internal string LastThinkPhase { get; private set; } = "start";

    /// <summary>
    /// The opportunity controller drove the latest think (it walked, opened, waited or chose a guard to pull). The brain's goal is
    /// <see cref="PlayerbotGoalKind.Loot"/> for a chest and for a corpse alike, so the policy telemetry labels a decision
    /// <see cref="Policy.PlayerbotPolicyGoal.Opportunity"/> from this and not from the goal kind.
    /// </summary>
    internal bool OpportunityDriven { get; private set; }

    /// <summary>
    /// The quest destinations controller planned the latest think's trip (the ladder's quest-travel rung, or a promoted
    /// <see cref="Policy.PlayerbotPolicyGoal.QuestTravel"/>). The trip's goal kind is <see cref="PlayerbotGoalKind.Quest"/>, the same as a quest
    /// worked in sight, so the policy telemetry labels the decision <see cref="Policy.PlayerbotPolicyGoal.QuestTravel"/> from this.
    /// </summary>
    internal bool QuestTravelDriven { get; private set; }

    /// <summary>Single-line diagnostic snapshot of the bot's state and recent trace history.</summary>
    internal string FormatDiagnosticSnapshot(Player player) => _trace.FormatSnapshot(player, this);

    /// <summary>A goal this bot's stall set aside for every bot (<see cref="PlayerbotSharedSetAsides.Report"/>), until the feature logs it.</summary>
    internal PlayerbotSharedSetAside? SharedSetAside { get; set; }

    /// <summary>
    /// Whether the bot was outside its home area when it last gave a goal up for a stall (<see cref="GiveUpStalledGoal"/>): that stall
    /// was not reported to the shared set-asides, and the feature logs it as made outside the home.
    /// </summary>
    internal bool StallOutsideHome { get; private set; }

    /// <summary>
    /// The quest whose walk home the bot last gave up for a stall (<see cref="GiveUpStalledGoal"/>), or zero. It is captured at the give-up,
    /// before the quest is suspended: the same think then re-evaluates the quest return and rewrites <see cref="PlayerbotQuestGoals.ReturnQuest"/>,
    /// and the feature builds the QA context only after the think (<see cref="QaReportContext"/>).
    /// </summary>
    internal uint StallReturnQuest { get; private set; }
    // The creature entries attacking the bot at its last living update: its killers when it dies (the server clears a dead
    // player's attackers before the brain sees the death).
    private readonly HashSet<uint> _attackerEntries = [];
    private readonly Dictionary<uint, QaAttackerFact> _attackerFacts = [];
    private readonly Dictionary<uint, QaAttackerFact> _recentDeathAttackerFacts = [];
    private readonly List<(uint AtMs, uint Map, System.Numerics.Vector3 Position, string Source)> _recentDeathSources = [];
    private uint _lastQaDeathMs;
    private string _lastQaDeathGoal = "unknown";
    private uint _lastQaDeathQuest;
    private uint _lastQaDeathMap;
    private System.Numerics.Vector3 _lastQaDeathPosition;
    private string _lastQaDeathSource = "Unobserved";
    private string _killingBlowSource = "Unobserved";
    private readonly object _killWatchGate = new();
    private PlayerbotKillObserver? _killObserver;
    private Player? _observedPlayer;
    // The last errand the bot was travelling for (quest, trainer, vendor): what led it where it keeps dying.
    private (PlayerbotGoalKind Goal, uint Entry, uint Quest, uint AtMs) _errand;
    // The creatures attacking the bot at its last living update (guid and entry): the risk's danger memory of its killers.
    private readonly List<(ObjectGuid Guid, uint Entry)> _attackers = [];
    private bool _needsRest;

    /// <summary>The bot sat down to wait for health without food (<see cref="PlayerbotRisk.KeepWaiting"/>); it stands up when the wait ends.</summary>
    private bool _satToWait;

    /// <summary>The most health and mana seen while resting (<see cref="Fingerprint"/>): a rise is progress for the stall watch.</summary>
    private ulong _restMark;
    private bool _deathIntentRetired;
    private int _stopped;
    // Creatures the bot could not path to (or looped trying to reach): skipped for a while, so the next think does
    // not pick the same nearest unreachable target again.
    private readonly Dictionary<ObjectGuid, uint> _unreachable = [];
    private const uint UnreachableTargetMs = 30_000;
    private const float MeleeRange = PlayerbotClassRotation.MeleeRange;
    // A ranged bot standing at its range without anything to cast at a target that is not fighting it closes in after this long
    // (the group AI uses it too: PlayerbotGroupAI.Fight).
    internal const uint RangedIdleLimitMs = 8_000;
    private uint _rangedIdleMs;

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        lock (_killWatchGate)
        {
            if (_killObserver is { } observer && _observedPlayer is { } player) observer.Remove(player);
            _killObserver = null;
            _observedPlayer = null;
        }
        // The claims registry and the bot's claim are world-thread state; Stop runs on any thread, so the release is posted.
        if (_session.World is { IsStopped: false } world)
        {
            if (world.IsWorldThread)
            {
                _contention.ReleaseClaim();
                ReleaseEventSpot(PlayerbotEventMeters.Done);
            }
            else _ = world.InvokeAsync(() => { _contention.ReleaseClaim(); ReleaseEventSpot(PlayerbotEventMeters.Done); return true; });
        }
        _destinations.Dispose();
    }

    internal PlayerbotGoalKind Goal => _goal;
    internal void CancelNavigationPlanning() => _destinations.CancelPlanning();
    internal Creature? InspectionTarget => _target;

    /// <summary>The player the bot fights back or defends against (<see cref="PlayerbotPvpFight"/>; inspection and tests).</summary>
    internal Unit? InspectionPvpTarget => _pvpFight?.Target;
    internal uint TargetEntry { get; private set; }
    internal uint QuestId { get; private set; }

    /// <summary>The current stall (<see cref="PlayerbotStallWatch"/>), or null while the bot makes progress.</summary>
    internal string? StallReport => _stall.Report;

    /// <summary>The last stall reported, kept after progress resumed.</summary>
    internal string? LastStall => _stall.LastReport;

    /// <summary>Stalls reported so far.</summary>
    internal int StallCount => _stall.Count;

    /// <summary>The stall watch (inspection and tests).</summary>
    internal PlayerbotStallWatch StallWatch => _stall;

    /// <summary>
    /// The skips of goals no walk reaches reported since the last call, each as the log line bot QA parses (<see cref="PlayerbotQaSignalParser"/>):
    /// the goal held when it was met, what it names, where the bot stood and the two region-graph components. One think can skip several
    /// entries; every one is returned (<see cref="PlayerbotWorldDestinations.TryTakeUnreachable"/>).
    /// </summary>
    internal IReadOnlyList<string> TakeUnreachableReports()
    {
        if (!_destinations.HasUnreachableReports) return [];
        var lines = new List<string>();
        while (_destinations.TryTakeUnreachable(out PlayerbotWorldDestinations.UnreachableSkip skip))
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"goal unreachable on foot: goal={skip.Goal} target={skip.Entry} quest={skip.Quest} at {skip.At.X:F1},{skip.At.Y:F1},{skip.At.Z:F1} map {skip.Map} components={skip.FromComponent}/{skip.ToComponent}"));
        return lines;
    }

    /// <summary>The last turn-in refusal recorded by the quest controller (inspection and tests).</summary>
    internal (uint QuestId, uint EnderEntry, string Reason, string Code)? LastTurnInRefusal => _quests.LastTurnInRefusal;

    /// <summary>Context for the warning the QA log tap parses; no bot identity or issue-key fields are added.</summary>
    internal string QaReportContext(Player player, string report)
    {
        bool death = report.StartsWith("death loop:", StringComparison.Ordinal);
        int attackerStart = death ? report.IndexOf("attackers=", StringComparison.Ordinal) : -1;
        string listedAttackers = attackerStart >= 0 ? report[(attackerStart + "attackers=".Length)..] : string.Empty;
        HashSet<uint> entries = [];
        foreach (string value in listedAttackers.Split('/'))
            if (uint.TryParse(value, out uint entry)) entries.Add(entry);
        var graveyard = death ? player.LastReleasedGraveyard : null;
        float? distance = graveyard is not null && graveyard.MapId == _lastQaDeathMap
            ? System.Numerics.Vector3.Distance(_lastQaDeathPosition,
                new System.Numerics.Vector3(graveyard.X, graveyard.Y, graveyard.Z)) : null;
        uint reportQuest = death ? _lastQaDeathQuest : QuestFromReport(report) ?? QuestId;
        string? turnInRefusal = !death && reportQuest != 0 && _quests.LastTurnInRefusal is { } refusal && refusal.QuestId == reportQuest
            ? refusal.Code : null;
        var observation = new QaBotObservation(player.Level, player.Race.ToString(), player.Team.ToString(), player.Class.ToString(),
            death ? _lastQaDeathGoal : GoalFromReport(report) ?? _goal.ToString(),
            reportQuest,
            graveyard?.Id ?? 0, distance, death ? _lastQaDeathSource : "None",
            death ? [.. _recentDeathAttackerFacts.Values.Where(f => entries.Contains(f.Entry)).OrderBy(f => f.Entry)] : [],
            death ? [.. _recentDeathSources.Where(d => d.Map == _lastQaDeathMap
                && System.Numerics.Vector3.Distance(d.Position, _lastQaDeathPosition) <= PlayerbotStallWatch.DeathLoopYards)
                .Select(d => d.Source)] : null,
            turnInRefusal);
        // A stall of the walk home to a completed quest's ender, as captured at its give-up (StallReturnQuest): the report names the quest
        // that was being returned. A stall on the way to take a quest from an NPC that also ends it is not marked
        // (PlayerbotQaSignalParser.ReturnTripToken).
        bool returnTrip = !death && StallReturnQuest != 0 && report.StartsWith("stalled ", StringComparison.Ordinal)
            && GoalFromReport(report) == nameof(PlayerbotGoalKind.Quest) && QuestFromReport(report) == StallReturnQuest;
        return (returnTrip ? ArcaneCore.World.Playerbots.Qa.PlayerbotQaSignalParser.ReturnTripToken : string.Empty) + observation.ToLogSuffix();
    }

    private static string? GoalFromReport(string report)
    {
        int start = report.IndexOf("goal=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 5;
        int end = report.IndexOf(' ', start);
        return end > start ? report[start..end] : null;
    }

    private static uint? QuestFromReport(string report)
    {
        int start = report.IndexOf("quest=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 6;
        int end = report.IndexOf(' ', start);
        return uint.TryParse(end > start ? report[start..end] : report[start..], out uint quest) ? quest : null;
    }

    /// <summary>Goals given up for stalls so far (<see cref="PlayerbotStallWatch.GiveUps"/>).</summary>
    internal int StallGiveUps => _stall.GiveUps;

    /// <summary>
    /// The quest objectives (quest, creature entry) this bot leaves to a group while it waits for partners or does them with its group
    /// (<see cref="Groups.PlayerbotGroupCoordinator"/>): the brain does not hunt them alone.
    /// </summary>
    internal HashSet<(uint Quest, uint Entry)> GroupHeld { get; } = [];

    /// <summary>
    /// The bot comes back from its group (the group AI drove it): whatever route, target, loot or rest the brain had belongs to
    /// another place. Its quests, suspensions and memories stay.
    /// </summary>
    internal void ResumeAfterGroup()
    {
        DropIntent();
        _waterRoute = null; // planned where the group took over, not here
        _waterRetryAt = 0;
        _dryMs = 0;
        _lootActions.Clear();
        _lootResponseReceived = false;
        _restUntilMs = 0;
        _needsRest = false;
        _satToWait = false;
        _thinkElapsed = 0;
        _combatSpells.Reset();
        _kite?.Reset(_session.Player);
        _stall.Reset();
        _homeReturn.Reset();
        _goal = PlayerbotGoalKind.Explore;
        // The group or battleground AI drove the bot meanwhile (a match lasts minutes): that silence is no think the brain missed.
        // Left armed, the first think back reported "no think for 195..929 s" (bot-brain/think-starved, live 2026-10-17).
        _thinkClockArmed = false;
    }

    /// <summary>Whether goals skip creatures of <paramref name="entry"/> for now (a stall or a death loop set it aside; tests).</summary>
    internal bool IsEntrySetAside(uint entry) => _suspensions.IsEntrySuspended(entry, _session.World.NowMs);

    /// <summary>How this bot shares mobs with the others: its claim, the mobs it left alone, the help it gives (inspection and tests).</summary>
    internal PlayerbotContention Contention => _contention;

    /// <summary>The bot's spell helper (tests).</summary>
    internal PlayerbotCombatSpells CombatSpells => _combatSpells;

    /// <summary>The fleet's soft claims on untagged mobs (<see cref="PlayerbotClaims"/>; inspection and tests).</summary>
    internal PlayerbotClaims Claims => _contention.Claims;

    /// <summary>A think (one <see cref="Update"/> call) longer than this overran its tick budget many times over (<see cref="CheckThinkOverran"/>).</summary>
    internal const double ThinkOverranMs = 500;

    /// <summary>A bot in the world that has not thought for this long (world ms) is starved or wedged (<see cref="CheckThinkClock"/>).</summary>
    internal const uint ThinkStarvedMs = 180_000;

    /// <summary>The overrun and starvation reports of one bot are at least this far apart (world ms).</summary>
    internal const uint ThinkAnomalyQuietMs = 60_000;

    private bool _thinkClockArmed;
    private uint _lastThinkMs;

    /// <summary>
    /// The bot's thinks in the world so far, every kind (combat, rest, the goal ladder): the goal policy tells the think right after a
    /// snapshot's from one with other thinks in between by it (<see cref="Policy.PlayerbotPolicyAgent.Decide"/>).
    /// </summary>
    internal long ThinkCount { get; private set; }
    private bool _starvedReported;
    private bool _overranReported;
    private uint _overranReportMs;

    /// <summary>
    /// Note that the bot thought (an alive think, or a dead bot's recovery think) at <paramref name="now"/>: the starvation clock restarts
    /// and a starvation report may be made again.
    /// </summary>
    private void NoteThink(uint now)
    {
        _thinkClockArmed = true;
        _lastThinkMs = now;
        _starvedReported = false;
    }

    /// <summary>
    /// The bot is in the world and enabled at <paramref name="now"/>: report once when it has not thought for <see cref="ThinkStarvedMs"/>
    /// since its last think (or since the first look, for a bot that never thought). Where the wait is not the bot's (loading screen,
    /// disabled, taxi flight) the clock is restarted by the caller. A compare per call.
    /// </summary>
    internal void CheckThinkClock(Player player, uint now)
    {
        if (!_thinkClockArmed)
        {
            NoteThink(now);
            return;
        }

        uint silent = unchecked(now - _lastThinkMs);
        if (silent < ThinkStarvedMs || silent > int.MaxValue || _starvedReported || _session.IsInChaosProbe) return;
        _starvedReported = true;
        PlayerbotAnomalies.Report(player, "brain", "think-starved",
            string.Create(CultureInfo.InvariantCulture,
                $"no think for {silent / 1000} s of world time (goal {_goal}, last phase {LastThinkPhase}, think interval {_options.ThinkIntervalMs} ms)"));
    }

    /// <summary>
    /// One <see cref="Update"/> that took <paramref name="elapsedMs"/> of wall time, of which the collector held the process
    /// <paramref name="gcPauseMs"/>: report when it overran (<see cref="ThinkOverranMs"/>) without those pauses (a collector pause is not
    /// the bot's slowness: the think-slow log excludes it the same way, PlayerbotSlowThinkLog.Observe; bot QA #1483), once per
    /// <see cref="ThinkAnomalyQuietMs"/>.
    /// </summary>
    internal void CheckThinkOverran(Player player, double elapsedMs, uint now, double gcPauseMs = 0)
    {
        if (gcPauseMs > 0) elapsedMs = Math.Max(0, elapsedMs - gcPauseMs);
        if (!(elapsedMs >= ThinkOverranMs)) return;
        if (_overranReported && unchecked(now - _overranReportMs) < ThinkAnomalyQuietMs) return;
        _overranReported = true;
        _overranReportMs = now;
        string slowest = PlayerbotThinkProbe.Active is { } probe ? $", slowest phase {probe.SlowestPhaseNow}" : string.Empty;
        string collector = gcPauseMs > 0 ? string.Create(CultureInfo.InvariantCulture, $", {gcPauseMs:F0} ms of collector pause not counted") : string.Empty;
        PlayerbotAnomalies.Report(player, "brain", "think-overran",
            string.Create(CultureInfo.InvariantCulture,
                $"one think took {elapsedMs:F0} ms (the bots share a think budget per tick, at least {PlayerbotThinkRotation.TickBudgetMs:F0} ms by default; goal {_goal}, last phase {LastThinkPhase}{slowest}{collector})"));
    }

    internal void Update(uint elapsedMs)
    {
        if (!_options.Enabled || _stopped != 0 || _session.Player is not { } player)
        {
            _thinkClockArmed = false;
            return;
        }
        OpportunityDriven = false;
        QuestTravelDriven = false;
        _destinations.SkipQuest ??= _quests.IsRefused;
        _destinations.Untakeable ??= IsUntakeable;
        _destinations.GiverHasCandidate ??= _quests.HasCandidate;
        _destinations.SetQuestAside ??= _quests.SetAside;
        _destinations.TeleportToBind ??= TeleportToBindPointForQuest;
        _destinations.GoalOf ??= () => _planningGoal ?? _goal;
        _quests.Suspensions = _town.Suspensions = _trainers.Suspensions = _destinations.Suspensions = _suspensions;
        _town.Budget = _trainers.Budget = _riding.Budget = _budget; _economy.SpendBudget = _budget;
        _riding.Suspensions = _bank.Suspensions = _suspensions;
        _quests.Contention ??= _contention;
        _quests.OnContentionExhausted ??= RaiseContentionExhausted;
        _quests.HeldForGroup ??= (quest, entry) => GroupHeld.Contains((quest, entry)) || GroupHeld.Contains((0, entry));
        _quests.AllSpawnsVisited ??= _destinations.HasAllSpawnsVisited;
        _equipment.RepairTripDue ??= _town.HasUrgentRepairOrSell;
        _destinations.ValidatePlanning(player);
        if (!player.IsInWorld)
        {
            // A loading screen between maps is no think the brain missed.
            _thinkClockArmed = false;
            return;
        }
        PlayerbotThinkProbe.Phase("brain");
        _stalledWorkPhase = null;
        _ranWorkThink = false;
        LastThinkPhase = "brain";
        _trace.ObserveState(this, _session.World.NowMs);
        CheckThinkClock(player, _session.World.NowMs);
        ThinkCount++;
        CheckAssistCredit(player);
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();
        _thinkStartedAt = PlayerbotThinkProbe.Timestamp(); // on the running think's clock, as HasOptionalThinkBudget reads it
        try
        {
            UpdateCore(player, elapsedMs);
        }
        finally
        {
            _thinkStartedAt = 0;
            // A think that ran any goal but Explore (a quest, combat, an errand, rest, a retreat...) broke the walk to the inn; the choke
            // point for all of them. Explore thinks that skip the town idle decision report it in Explore().
            if (_goal != PlayerbotGoalKind.Explore)
            {
                _townIdle.Interrupted();
                ReleaseEventSpot(PlayerbotEventMeters.Done);
            }

            if (PlayerbotThinkProbe.CurrentPhase is { } currentPhase) LastThinkPhase = currentPhase;
            if (_ranWorkThink) _lastWorkPhase = LastThinkPhase;
            _trace.ObserveState(this, _session.World.NowMs);
            CheckThinkOverran(player, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, _session.World.NowMs,
                (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds);
        }
    }

    internal bool HasOptionalThinkBudget(double maxElapsedMs = 15.0)
        => (PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true)
            && !(PlayerbotThinkProbe.Active is { } running && running.RunningMs >= maxElapsedMs)
            && (_thinkStartedAt == 0 || PlayerbotThinkProbe.MsSince(_thinkStartedAt) < maxElapsedMs);

    private void UpdateCore(Player player, uint elapsedMs)
    {
        PlayerbotNavigation.SetStallWatch(player, _stall);
        WatchKills(player);
        // The engagement ends with the bot's death, a change of target, the mob's death, or the bot leaving combat (not in combat and not on the mob's threat list).
        if (_engagedMob != 0 && (!player.IsAlive || _target is not { IsAlive: true } engagedTarget || engagedTarget.Guid.Value != _engagedMob
            || !player.Combat.IsInCombat && !player.Combat.ThreatenedBy.Contains(engagedTarget))) ClearEngagement();
        SyncHomeFilters(player);
        _recovery.UnsafeBody ??= JudgeBody;
        _recovery.LandingUnsafe ??= (bot, at) => Risk.TooStrongAt(bot, at) is not null;
        _recovery.EscapeLanding ??= EscapeLanding;

        bool dead = !player.IsAlive;
        if (dead)
        {
            _destinations.Stop();
            _waterRoute = null;
            _waterRetryAt = 0;
            _stall.Reset();
            // The walk home planned from where the bot fell: after the resurrection (maybe far away) it plans afresh.
            _homeReturn.Reset();
            if (!_deathIntentRetired)
            {
                RecordDeath(player);
                _opportunities?.BotDied();
                RetireDeathIntent();
                // A death ends a tram trip (its stage, car and entrance consent belong to the living bot): the ghost's corpse run may
                // walk the tram map to a body there (PlayerbotTram.AllowsWalkingOn), and a bot revived there takes the nearest exit.
                PlayerbotTram.Cancel(player);
                // A taxi trip that has not left yet is dropped with its walker; a flight in the air is the server's and goes on.
                PlayerbotTaxi.Cancel(player);
                // And a lift ride: the corpse loses the transport. This must run BEFORE the acknowledgements below
                // (PlayerbotMovementControl.Update): the death root's ack then carries the cleared block, relayed as MSG_MOVE_ROOT.
                PlayerbotElevator.Cancel(player);
                // And the journey with its legs: a hearthstone or Teleport cast, a portal request, a mage's service end; a ship leg not
                // boarded is dropped, while aboard the ship carries the body on to the next port (PlayerbotShip.Cancel).
                PlayerbotTravel.CancelLegs(_session, player);
                _deathIntentRetired = true;
            }
            _goal = PlayerbotGoalKind.Recover;
            _defendWaitsSickness = false;
        }
        else
        {
            // Alive again: the next death is a new one, even when pending movement orders hold this update below. The recovery is
            // reset here too, before that early return: a death in the window it held would otherwise start in the last death's
            // spirit-healer fallback (PlayerbotRecovery.Update goes straight to it), never release its new body, and fault.
            if (_deathIntentRetired) PlayerbotElevator.SettleAfterRevive(player);
            _deathIntentRetired = false;
            _recovery.Reset();
            RememberAttackers(player);
            // The fight with a find's guards is the brain's, not the goal's: its claim on the find is kept alive meanwhile (and an
            // episode a higher goal has held for too long, or that ran past its limit, ends here). A chest the bot is still looting
            // is not that: the next think is this goal's own, and a live think gap is longer than the claim (8 s), so lapsing it here
            // dropped the chest and the same think began it again (bot QA #962).
            bool lootingChest = _goal == PlayerbotGoalKind.Loot && _target is null && _opportunities?.ActiveKey is not null;
            if (!lootingChest && _opportunities?.KeepClaim(player) is { } lapsed) ReleaseOpportunityGuard(player, lapsed);
            // Sickness may begin while a pull route or cast is pending. Retire that intent before a movement-order acknowledgement
            // can defer the ordinary goal decision, and before class upkeep can use PullTarget for an opener.
            if (!player.Combat.IsInCombat && player.Combat.Victim is null && _target is { IsAlive: true }
                && PlayerbotSickness.Holder(_session, player) is { IsPermanent: false, Duration: > 0 })
            {
                if (_attacking) _session.TryManagedAction(WorldOpcode.CmsgAttackstop, []);
                DropIntent();
                _combatSpells.PullTarget = null;
                _goal = PlayerbotGoalKind.Rest;
                _stall.Reset();
                PlayerbotMovementControl.Stop(_session, player);
            }
        }
        // Out of breath while still swimming: the movement acknowledgement below would otherwise consume every think, and the
        // bot would follow its underwater route until the breath timer killed it (bot QA #271). In combat too: the breath timer
        // runs whatever the fight (LiquidEnvironment), so a bot held by a murloc or a crocolisk below the surface swims out with its
        // attackers behind it rather than drowning where it stands; with breath to spare the fight below the surface goes on.
        if (!dead && PlayerbotWater.Submerged(player) && PlayerbotWater.BreathRunningOut(player))
        {
            EscapeWater(player, elapsedMs);
            // A fighting bot that found no way out yet (the next search waits two seconds) fights on meanwhile rather than standing idle.
            if (!player.Combat.IsInCombat || _waterRoute is not null) return;
        }

        // Death cleanup must happen before this seam, but pending movement orders still
        // require their ordinary acknowledgements while alive or ghosted.
        if (PlayerbotMovementControl.Update(_session, player)) return;

        if (dead)
        {
            _thinkElapsed += elapsedMs;
            if (_thinkElapsed >= _options.ThinkIntervalMs)
            {
                uint recoveryInterval = _thinkElapsed;
                _thinkElapsed = 0;
                NoteThink(_session.World.NowMs);
                PlayerbotThinkProbe.Phase("recovery hazard scan");
                Risk.ScanHazards(player); // a ghost keeps watching the creatures round its body (the revive spot)
                PlayerbotThinkProbe.Phase("recovery update");
                _recovery.Update(player, recoveryInterval);
            }
            return;
        }
        _deathIntentRetired = false;
        _recovery.Reset();

        // In the air on a taxi flight the server moves the bot (UNIT_FLAG_TAXI_FLIGHT, PlayerbotTaxi): it neither thinks nor moves, and
        // the stall watch does not judge it. The think after the landing continues its goal.
        if (PlayerbotTaxi.IsFlying(player))
        {
            NoteThink(_session.World.NowMs); // the server flies the bot: nothing to think
            _stall.Reset();
            _target = null;
            _route = null;
            _attacking = false;
            return;
        }

        // The water escape owns a bot that would otherwise stand still under water: a rest or a loot wait, or an escape already
        // under way. A sick bot still walks its quest returns and errands, so sickness alone is not a reason: StandDownWhileSick stops
        // it on an earlier think than the idle check after the trips and the retreat, and that check then takes the standing bot out.
        // A bot that is walking a route or riding a trip is never diverted by this block. Movement itself continues on world ticks;
        // a route is planned only on entry or after it ends.
        bool submerged = PlayerbotWater.Submerged(player);
        if (!submerged)
        {
            _waterRoute = null;
            // The retry bound is cleared only after the bot has stayed dry for a while, so a ford edge does not re-search every second.
            if (_dryMs < 10_000) _dryMs += elapsedMs;
            if (_dryMs >= 10_000) _waterRetryAt = 0;
        }
        else
        {
            _dryMs = 0;
            if (!player.Combat.IsInCombat
                && (_waterRoute is not null || _restUntilMs != 0 || _lootActions.Count > 0))
            {
                EscapeWater(player, elapsedMs);
                return;
            }
        }

        if (_restUntilMs != 0)
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            SpellFeature? restingSpells = _session.Services.GetService<SpellFeature>();
            bool activeRecovery = restingSpells is not null
                && PlayerbotConsumables.IsEatingOrAboutTo(player, restingSpells.System);
            if (!player.Combat.IsInCombat && activeRecovery && PlayerbotConsumables.NeedsRecovery(player, _restKind)
                && unchecked((int)(_restUntilMs - _session.World.NowMs)) > 0)
                return;
            if (!_session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u))) return;
            _restUntilMs = 0;
            _goal = PlayerbotGoalKind.Explore;
            return;
        }

        if (_lootActions.TryPeek(out var nextLoot))
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            if (_session.TryManagedAction(nextLoot.Opcode, nextLoot.Payload)) _lootActions.Dequeue();
            return;
        }

        // The window of a gather, skin or disenchant cast likewise, on the update after it came rather than on the crafting rung's next
        // think, which higher rungs, other loot readers and the bounded packet ring stood between (GitHub #774).
        if (_crafting.UpdateGatherLoot(player)) return;

        _thinkElapsed = checked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs)
            return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;
        _ranWorkThink = true;
        NoteThink(_session.World.NowMs);
        ObserveDeferWakes(player);

        // A tram trip (or a bot found on the Deeprun Tram map) owns the bot until it stands on the continent again (PlayerbotTram): nothing
        // else moves it meanwhile, and the stall watch does not judge a bot waiting for its car. A fight on the way to the entrance ends the
        // trip (the goal that began it begins it again afterwards).
        if (player.IsAlive)
        {
            // Far travel (docs/areas/playbots.md, Travel): the leg drivers first, each owning the bot while its leg is under way (a ship from the
            // walk to the dock until the bot stepped off at the other port; the hearthstone and a mage's Teleport through cast and teleport; a
            // portal asked for until the requester went through; a mage serving a portal request), then the journey, which walks its walk legs
            // and only begins the others. A fight before the bot is aboard (on the way to the dock, waiting for the ship, stepping up the
            // gangway) ends that ship leg, and the journey begins it again after the fight; aboard, the ship carries the bot on. A fight
            // during a hearthstone or Teleport cast cancels that cast (its driver). A failed leg defers only that leg (GiveUpTransitLeg): the
            // journey replans it once.
            PlayerbotThinkProbe.Phase("travel legs");
            // The journey's bound first: it drops the leg under way (not aboard a ship), whose driver would otherwise own this think.
            PlayerbotTravel.EnforceDeadline(_session, player);
            if (player.Combat.IsInCombat && player.Transport is null
                && PlayerbotShip.TripOf(player) is { Stage: ShipStage.ToDock or ShipStage.WaitShip or ShipStage.Board })
                PlayerbotShip.Cancel(player);
            if (TravelOwns(PlayerbotShip.Drive(_session, player, _options, interval, out string? shipFailure), shipFailure, "ship")) return;
            if (TravelOwns(PlayerbotHearthstone.Drive(_session, player, _options, interval, out string? hearthFailure), hearthFailure, "hearth")) return;
            if (TravelOwns(PlayerbotMageService.Drive(_session, player, _options, interval, out string? mageFailure), mageFailure, "mage")) return;
            if (PlayerbotMageService.DriveMage(_session, player, _options, interval) == TravelLegStep.Traveling)
            {
                HoldForTravel();
                return;
            }

            if (TravelOwns(PlayerbotTravel.Drive(_session, player, _options, interval, out string? travelFailure), travelFailure, "travel")) return;

            // A taxi trip (PlayerbotTaxi) owns the bot from the walk to the flight master until it has landed. A fight on the way to the
            // master ends it; a failed trip only backs taxi use off, and the goal that asked for it walks.
            if (player.Combat.IsInCombat && PlayerbotTaxi.TripOf(player) is { Stage: TaxiStage.ToMaster }) PlayerbotTaxi.Cancel(player);
            TaxiStep taxi = PlayerbotTaxi.Drive(_session, player, _options, interval, out string? taxiFailure);
            if (taxi == TaxiStep.Traveling)
            {
                _stall.Reset();
                _target = null;
                _route = null;
                _attacking = false;
                return;
            }

            if (taxi is TaxiStep.Arrived or TaxiStep.Failed or TaxiStep.MasterDead)
            {
                _stall.Reset();
                if (taxi is TaxiStep.Failed or TaxiStep.MasterDead)
                    _session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotBrain>()
                        .LogInformation("Playerbot taxi trip given up, the goal walks: {Why}", taxiFailure);
            }

            PlayerbotThinkProbe.Phase("tram");
            if (player.Combat.IsInCombat && PlayerbotTram.TripOf(player) is { Stage: TramStage.ToEntrance }) PlayerbotTram.Cancel(player);
            TramStep tram = PlayerbotTram.Drive(_session, player, _options, interval, out string? tramFailure);
            if (tram == TramStep.Traveling)
            {
                _stall.Reset();
                _target = null;
                AbandonRoute();
                _attacking = false;
                return;
            }

            if (tram == TramStep.Failed)
            {
                GiveUpTransitLeg(tramFailure ?? "tram: the trip failed (no reason recorded)");
                return;
            }

            // A lift ride (PlayerbotElevator) owns the bot the same way, until it stands on the other level; a fight on the way to the
            // landing ends it. A failed ride is the bot's own set-aside, like a failed tram trip: the lift did not carry it.
            if (player.Combat.IsInCombat && PlayerbotElevator.TripOf(player) is { Stage: ElevatorStage.ToStop }) PlayerbotElevator.Cancel(player);
            ElevatorStep lift = PlayerbotElevator.Drive(_session, player, _options, interval, out string? liftFailure);
            if (lift == ElevatorStep.Traveling)
            {
                _stall.Reset();
                _target = null;
                AbandonRoute();
                _attacking = false;
                return;
            }

            if (lift == ElevatorStep.Failed)
            {
                GiveUpTransitLeg(liftFailure ?? "elevator: the ride failed (no reason recorded)");
                return;
            }

            // A journey under way owns the bot between its legs too (a tram, lift or taxi trip of a walk leg just ended: the journey walks on
            // at the next think). In a fight the journey yields and the brain fights. A journey resting between two legs (a bot low on health
            // or mana, PlayerbotTravel.IsResting) has the brain rest the bot meanwhile.
            if (!player.Combat.IsInCombat && PlayerbotTravel.IsJourneying(player))
            {
                HoldForTravel();
                if (PlayerbotTravel.IsResting(player)) RestForJourney(player);
                return;
            }
        }

        // A flight master within reach whose node the bot does not know yet teaches it (CMSG_TAXIQUERYAVAILABLENODES, PlayerbotTaxi).
        if (player.IsAlive) PlayerbotTaxi.Discover(_session, player, _options);

        PlayerbotThinkProbe.Phase("stall watch");
        if (_destinations.IsPlanning)
        {
            _stall.Reset();
        }
        else if (player.IsAlive && _stall.Observe(player, _goal, TargetEntry, QuestId, _session.World.NowMs,
                (uint)Math.Clamp(_options.StallSeconds, 10, 86_400) * 1000u, Fingerprint))
        {
            ReportStall(player);
            GiveUpStalledGoal(player);
        }

        if (PlayerbotMotion.ConsumeLoop(player))
        {
            _trace.Record(_session.World.NowMs, "loop-consumed");
            // The motion gave up a loop (PlayerbotMotion): drop this intent, skip its target for a while, and let the
            // goals choose again; the looped destinations are refused by PlayerbotNavigation.TryPlan meanwhile.
            if (_target is { } looped) MarkUnreachable(looped);
            _target = null;
            AbandonRoute();
            _attacking = false;
            TargetEntry = 0;
            _goal = PlayerbotGoalKind.Explore;
            return;
        }

        if (!player.IsAlive)
        {
            if (!_deathIntentRetired)
            {
                RecordDeath(player);
                RetireDeathIntent();
                _deathIntentRetired = true;
            }
            _goal = PlayerbotGoalKind.Recover;
            _defendWaitsSickness = false; // dead or a ghost: the sickness wait is not the decision any more
            PlayerbotThinkProbe.Phase("recovery update");
            _recovery.Update(player, interval);
            return;
        }

        // A defend post (.playerbot defend) owns the live bot: nothing below it (retreat, quests, grind, explore) runs.
        if (TryDefend(player, interval)) return;

        // A retreat owns the bot until nothing threatens it (PlayerbotRetreat); out of combat the bot lays the trail back.
        PlayerbotThinkProbe.Phase("risk");
        if (Risk.UpdateRetreat(player, interval))
        {
            DropIntent();
            _goal = PlayerbotGoalKind.Retreat;
            return;
        }

        // Still under water with nothing else owning the bot, or already out of breath while swimming: leave. A brief dip while
        // walking is no breath risk (the timer has not run down), so a moving bot with breath to spare is left alone.
        if (submerged && !player.Combat.IsInCombat
            && ((player.Movement.Flags & MovementFlags.MaskMoving) == 0 || PlayerbotWater.BreathRunningOut(player)))
        {
            EscapeWater(player, interval);
            return;
        }

        Risk.Track(player);
        // Every walk of this bot keeps out of its hazards (PlayerbotHazards), unless the estimate is off.
        PlayerbotNavigation.Guard(player, _options.Risk.Enabled ? Risk : null);

        // The way out of a ring of strong creatures (PlayerbotRisk.Refused) owns the bot, out of combat, until walked or bounded.
        if (!player.Combat.IsInCombat && Risk.EscapeRoute is { } escape)
        {
            DropIntent();
            _goal = PlayerbotGoalKind.Escape;
            if (!PlayerbotNavigation.TryAdvance(_session, escape, _options, interval, _session.World.NowMs)) Risk.EndEscape();
            return;
        }

        if (!player.Combat.IsInCombat
            && PlayerbotHomeArea.IsNullIsland(player.MapId, player.X, player.Y)
            && TryEscapeNullIsland(player))
        {
            return;
        }

        if (_lootOpened && _target is { } pendingLoot)
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            if (_lootResponseReceived || _session.World.Uptime.TotalMilliseconds - _lootStartedMs >= 10_000)
            {
                if (!_lootResponseReceived) _session.TryManagedAction(WorldOpcode.CmsgLootRelease,
                    PlayerbotNavigation.GuidPayload(pendingLoot.Guid.Value));
                RememberLootedCorpse(pendingLoot.Guid);
                _lootOpened = false;
                _lootResponseReceived = false;
                _target = null;
                TargetEntry = 0;
                AbandonRoute();
                _attacking = false;
                _goal = PlayerbotGoalKind.Explore;
                Rest(player);
            }
            else _lootResponseReceived = LootManagedPackets(player, pendingLoot.Guid.Value);
            return;
        }

        PlayerbotThinkProbe.Phase("pvp");
        if (TryFightPlayer(player, interval)) return;

        // A scenario stay (PlayerbotStay) owns the bot while its lease lives: no quest, grind or explore step chooses a destination.
        if (TryStay(player, interval)) return;

        PlayerbotThinkProbe.Phase("quest return");
        if (_quests.AbandonDistantCompletedQuest(player))
        {
            _goal = PlayerbotGoalKind.Quest;
            return;
        }
        uint completedReturn = _quests.CompletedReturnQuest(player);
        // Entering the walk to a completed quest's ender drops the target of whatever came before (a mob the bot was fighting, a quest NPC
        // it had been serving): until the destinations name the ender, the stall watch and GiveUpStalledGoal would report and set aside that.
        if (_quests.ReturnQuest != completedReturn)
        {
            _quests.ReturnQuest = completedReturn;
            TargetEntry = 0;
            if (completedReturn == 0) QuestId = 0;
        }
        if (completedReturn != 0)
        {
            _contention.ReleaseClaim(); // the walk to the ender holds no mob
            _goal = PlayerbotGoalKind.Quest;
            QuestId = completedReturn;
            // Leave ongoing spell execution to its ordinary completion. Travel does
            // not clear casts/cooldowns or manufacture an out-of-combat state.
            if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }) return;
            if (_attacking || player.Combat.Victim is not null)
            {
                if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                { _attacking = false; _target = null; AbandonRoute(); }
                return;
            }
            if (!player.Combat.IsInCombat
                && (_quests.NeedsBagSpaceForTurnIn || (PlayerbotTownGoals.HasBrokenWornGear(player) && _town.HasUrgentRepairOrSell(player))))
            {
                _town.BeginThink();
                if (_town.Update(player, interval))
                {
                    RetireIdleCombatRoute();
                    _goal = _town.Goal;
                    QuestId = 0;
                    TargetEntry = _town.TargetEntry;
                    return;
                }
            }
            if (!player.Combat.IsInCombat && _quests.Update(player, interval))
            { QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            if (_quests.IsRefused(completedReturn))
            {
                _quests.ReturnQuest = 0;
                QuestId = 0;
                TargetEntry = 0;
            }
            else if (!player.Combat.IsInCombat && _quests.IsBackingOff(_session.World.NowMs))
            {
                return;
            }
            else
            {
                _target = null;
                AbandonRoute();
                PlayerbotThinkProbe.Phase("quest destinations");
                // The trip's target is the ender the destinations chose for this quest, or none yet: never the entry of an earlier goal. A
                // failed plan leaves both TargetEntry values as they were, and the stall watch then reported, and GiveUpStalledGoal set aside,
                // a creature this trip has nothing to do with (GitHub #186: quest 1111's return stalled with target 4147, a Saltstone Basilisk
                // the bot had been fighting, and 11259, the ender of quest 5361 it had been walking to before).
                _destinations.Update(player, 0, interval, completedReturn);
                if (_quests.IsRefused(completedReturn) || _suspensions.IsQuestSuspended(completedReturn, _session.World.NowMs))
                {
                    _quests.ReturnQuest = 0;
                    QuestId = 0;
                    TargetEntry = 0;
                }
                else
                {
                    TargetEntry = _destinations.QuestId == completedReturn ? _destinations.TargetEntry : 0u;
                    // Wait/retry at the real giver while combat or a normal interaction
                    // refusal remains. A completed quest must not reopen opportunistic grind.
                    return;
                }
            }
        }

        // A bot with no target holds no claim (the paths below that clear the target and wander, travel or explore leave it here).
        if (_target is null) _contention.ReleaseClaim();
        PlayerbotThinkProbe.Phase("threats");
        Creature? outgoingVictim = player.Combat.Victim is Creature victim
            && player.Map is { } map && victim.IsInWorld && victim.IsAlive && ReferenceEquals(victim.Map, map)
            && map.Combat.Hooks.CanAttack(player, victim)
            ? victim
            : null;
        if (outgoingVictim is not null)
        {
            bool changed = !ReferenceEquals(_target, outgoingVictim);
            _target = outgoingVictim;
            if (changed)
            {
                AbandonRoute();
                _attacking = false;
            }
            _lootOpened = false;
            TargetEntry = outgoingVictim.Entry;
        }

        bool hasDeadLootTarget = _target is { IsAlive: false } && ReferenceEquals(_target.Map, player.Map);
        Creature? defensiveAttacker = outgoingVictim is null && !hasDeadLootTarget && player.Combat.Victim is null
            ? FindDefensiveAttacker(player)
            : null;
        if (defensiveAttacker is not null)
        {
            bool changed = !ReferenceEquals(_target, defensiveAttacker);
            _target = defensiveAttacker;
            if (changed)
            {
                AbandonRoute();
                _attacking = false;
            }
            _lootOpened = false;
            TargetEntry = defensiveAttacker.Entry;
        }
        // A caster or hunter fights without a melee swing, so its victim is not Combat.Victim: a living target that has the
        // bot on its threat list (or attacks it) is still the bot's fight, not a combat linger.
        bool rangedFight = defensiveAttacker is null && outgoingVictim is null && _target is { IsAlive: true } engaged
            && ReferenceEquals(engaged.Map, player.Map)
            && (player.Combat.ThreatenedBy.Contains(engaged) || ReferenceEquals(engaged.Combat.Victim, player));
        if (defensiveAttacker is null && !rangedFight && player.Combat.IsInCombat && player.Combat.Victim is null
            && !hasDeadLootTarget)
        {
            uint lingerNow = _session.World.NowMs;
            if (!_lingering) (_lingering, _lingerSinceMs) = (true, lingerNow);
            _target = null;
            _attacking = false;
            _lootOpened = false;
            TargetEntry = 0;
            if (unchecked(lingerNow - _lingerSinceMs) < CombatLingerLimitMs)
            {
                PlayerbotMovementControl.Stop(_session, player);
                // Combat linger after an ordinary attack stop must not reopen an optional
                // grind target. Let the authoritative combat timer/handler clear the state.
                AbandonRoute();
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            // Past the server's own combat timer (CombatConstants.PvpCombatTimerMs and its check interval) with nothing to fight: what holds
            // the flag is nothing the bot can reach (an evading creature, a phantom state, bot QA #834). It walks on as an exploring bot
            // would, which also takes it out of reach of whatever still holds it, instead of standing for the stall watch (bot QA #580,
            // #894: "stalled 120s: goal=Combat target=0", motion moving:no dest:none, phase threats).
            PlayerbotThinkProbe.Phase("explore");
            Explore(player, interval);
            return;
        }

        _lingering = false;

        if (!player.Combat.IsInCombat && outgoingVictim is null && !hasDeadLootTarget)
        {
            SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
            // After a retreat, or before a pull the bot is too hurt for: recover to World:Playerbots:Risk:RecoverHealthPct first.
            if (Risk.KeepWaiting(player))
            {
                uint wanted = (uint)Math.Clamp(_options.Risk.RecoverHealthPct, 10f, 100f);
                DropIntent();
                _goal = PlayerbotGoalKind.Rest;
                if (spellFeature is not null && PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, wanted, wanted, out _))
                    Rest(player, wanted);
                else if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                // No food or drink: sit, as a player waiting for health would (vmangos Player::RegenerateHealth: half as much again
                // per tick when not standing up).
                else if (player.StandState == StandState.Stand
                         && _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Sit)))
                    _satToWait = true;
                return;
            }

            // The wait is over: stand up from it before anything else (a sitting player cannot cast, and the client stands
            // before it moves).
            if (_satToWait)
            {
                if (player.StandState != StandState.Sit) _satToWait = false;
                else if (_session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand)))
                    _satToWait = false;
                return;
            }

            if (_needsRest || spellFeature is not null && PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, out _))
            {
                Rest(player);
                return;
            }
            // A request from the social pass to lend a hand to a stranger (docs/areas/playbots-social.md, Helping): carried out ahead of the goal ladder.
            if (_helpKind != PlayerbotHelpKind.None)
            {
                PlayerbotThinkProbe.Phase("help");
                int help = TakeHelpErrand(player);
                if (help == 2) goto afterGoalLadder;
                if (help == 1) return;
            }

            PlayerbotThinkProbe.Phase("equipment");
            if (_equipment.Update(player))
            {
                _target = null;
                AbandonRoute();
                _attacking = false;
                TargetEntry = 0;
                _goal = PlayerbotGoalKind.Explore;
                return;
            }
            // Class upkeep between fights (forms, buffs on the bot and its group, the pet, heals, stealth before a pull),
            // vmangos UpdateOutOfCombatAI_<Class>; the creature the bot is closing in on is its pull target.
            PlayerbotThinkProbe.Phase("class upkeep");
            _combatSpells.PullTarget = _target is { IsAlive: true } pull && ReferenceEquals(pull.Map, player.Map) ? pull : null;
            if (_combatSpells.UpdateOutOfCombat(player, interval)) return;
            PlayerbotThinkProbe.Phase("home");
            if (_home is { } home && TryReturnHome(player, home, interval)) return;
            PlayerbotThinkProbe.Phase("town");
            _town.BeginThink();
            if (_quests.NeedsBagSpaceForTurnIn || (PlayerbotTownGoals.HasBrokenWornGear(player) && _town.HasUrgentRepairOrSell(player)))
            {
                if (_town.Update(player, interval))
                { RetireIdleCombatRoute(); _goal = _town.Goal; QuestId = 0; TargetEntry = _town.TargetEntry; return; }
            }
            // The goal policy may put another open rung ahead of the ladder's choice (PlayerbotBrain.Policy.cs); its controller rechecks the live rules.
            PlayerbotThinkProbe.Phase("policy");
            if (TryPolicyGoal(player, interval)) { if (_policyPulled) goto afterGoalLadder; return; }
            PlayerbotThinkProbe.Phase("quests");
            if (_quests.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _quests.Goal; QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            QuestId = 0;
            PlayerbotThinkProbe.Phase("town");
            if (_town.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            // Under resurrection sickness no new fight: the quest exchanges and town errands above (a repair among them) still run.
            if (StandDownWhileSick(player)) return;
            PlayerbotThinkProbe.Phase("crafting");
            _crafting.Suspensions = _suspensions;
            _crafting.GatherVeto = Opportunities.NodeVeto;
            if (_crafting.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _crafting.Goal; TargetEntry = _crafting.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("economy");
            if (_economy.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _economy.Goal; TargetEntry = _economy.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("bank");
            // Staying at its capital home (town progression): deposit what it does not need, buy a bank slot when the bank is nearly full.
            // A Vendor errand: a stall sets the banker's entry aside like a vendor's.
            if (_home is { Stay: Population.TownStay.Capital } capitalHome
                && _bank.Update(player, capitalHome, item => _town.IsProtected(player, item), interval))
            { RetireIdleCombatRoute(); _goal = PlayerbotGoalKind.Vendor; TargetEntry = _bank.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("opportunities");
            if (TakeOpportunity(player, interval, out bool opportunityPull)) { if (opportunityPull) goto afterGoalLadder; return; }
            bool visibleQuestTarget = _quests.PreferredCreatureEntry != 0
                && FindTarget(player, _quests.PreferredCreatureEntry, IsSkipped) is not null;
            PlayerbotThinkProbe.Phase("trainers");
            if (!visibleQuestTarget && (PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true) && _trainers.HasCandidate(player))
            {
                // An abandoned idle-grind target must not keep reopening combat instead
                // of travelling to a useful trainer. Stop through the ordinary handler;
                // active combat, casts, quest returns and pending loot keep priority.
                if (_attacking || player.Combat.Victim is not null)
                {
                    if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                    { _attacking = false; _target = null; AbandonRoute(); }
                    _goal = PlayerbotGoalKind.Train;
                    return;
                }
                _target = null;
                AbandonRoute();
                if (_trainers.Update(player, interval))
                { _goal = PlayerbotGoalKind.Train; TargetEntry = _trainers.TargetEntry; return; }
            }
            PlayerbotThinkProbe.Phase("riding");
            if (!visibleQuestTarget && (PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true) && _riding.HasCandidate(player))
            {
                // Saved for riding training or a mount and the money is there: the same priority as a class trainer trip.
                if (_attacking || player.Combat.Victim is not null)
                {
                    if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                    { _attacking = false; _target = null; AbandonRoute(); }
                    _goal = PlayerbotGoalKind.Mount;
                    return;
                }
                _target = null;
                AbandonRoute();
                if (_riding.Update(player, interval))
                { _goal = PlayerbotGoalKind.Mount; TargetEntry = _riding.TargetEntry; return; }
            }
            PlayerbotThinkProbe.Phase("quest destinations");
            if (_quests.PreferredCreatureEntry == 0 && player.Combat.Victim is null
                && (PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true)
                && _destinations.HasQuestCandidate(player))
            {
                // Available quest travel precedes optional idle grinding. Actual combat,
                // visible interactions, completed returns, supplies and trainers keep their
                // existing priority; path failure still permits the ordinary fallback.
                if (spellFeature?.System.GetState(player.Guid)?.CurrentCast is
                    { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }) return;
                if (_session.ManagedBudget is { Remaining: <= 0 }) return;
                _planningGoal = PlayerbotGoalKind.Quest;
                bool questPlanned = _destinations.Update(player, 0, interval);
                _planningGoal = null;
                if (questPlanned)
                {
                    QuestTravelDriven = true;
                    _target = null;
                    AbandonRoute();
                    _attacking = false;
                    _goal = PlayerbotGoalKind.Quest;
                    TargetEntry = _destinations.TargetEntry;
                    QuestId = _destinations.QuestId;
                    return;
                }
                if (_destinations.WaitingForTiles) return; // the route's tiles are being read: keep the goal, plan again next think
            }
        }

        afterGoalLadder:
        // A mob another player tagged while the bot walked to it is no longer the bot's to take: help its tagger or leave it alone, then
        // choose afresh in this same think (docs/areas/playbots.md, Contention).
        if (_target is { IsAlive: true } held && ReferenceEquals(held.Map, player.Map)) DropIfSomeoneElses(player, held);

        if (_target is null || _target.Map != player.Map)
        {
            PlayerbotThinkProbe.Phase("choose target");
            _invincibleWatch.Reset();
            Creature? previousTarget = _target;
            uint materialDropper = _quests.PreferredCreatureEntry == 0 && _quests.PreferredItemUseId == 0
                ? _crafting.PreferredMaterialDropperEntry(player)
                : 0u;
            uint preferred = _quests.PreferredItemUseId != 0
                ? 0u
                : (_quests.PreferredCreatureEntry != 0 ? _quests.PreferredCreatureEntry : materialDropper);
            _contention.ReleaseClaim();
            _contention.EndAssist();
            _contention.BlockedSeen = 0;
            _target = _quests.PreferredItemUseId != 0
                ? null
                : (_options.Risk.Enabled
                    ? Risk.ChooseTarget(player, preferred, IsSkipped, questObjective: preferred != 0, _contention)
                    : FindTarget(player, preferred, IsSkipped, _contention));
            if (_target is not null) _contention.Claim(player.Guid.Value, _target, _session.World.NowMs);
            else if (preferred != 0 && _contention.BlockedSeen > 0 && !AnyFreeOfEntry(player, preferred)) _contention.NoteContested(preferred, _session.World.NowMs);
            _rangedIdleMs = 0;
            // Keep an exploration route across decisions; otherwise every thought changes
            // direction after only its first terrain step and the player never travels.
            if (_target is not null || previousTarget is not null) AbandonRoute();
            if (_target is not null && _destinations.TargetEntry == _target.Entry) _destinations.Stop();
            _attacking = false;
            _lootOpened = false;
            if (_target is null)
            {
                uint huntEntry = _quests.PreferredCreatureEntry != 0 ? _quests.PreferredCreatureEntry : materialDropper;
                if (HasOptionalThinkBudget(15.0) && GrindPlanDue(huntEntry))
                {
                    PlayerbotThinkProbe.Phase("grind destinations");
                    PlayerbotGoalKind huntGoal = huntEntry != 0 && _quests.PreferredItemUseId == 0
                        ? PlayerbotGoalKind.Grind : PlayerbotGoalKind.Quest;
                    _planningGoal = huntGoal;
                    bool huntPlanned = _destinations.Update(player, huntEntry, interval, preferredQuestId: _quests.PreferredQuestId);
                    _planningGoal = null;
                    NoteGrindPlan(huntEntry, huntPlanned, _destinations.LastPlanRefused);
                    if (huntPlanned)
                    {
                        AbandonRoute();
                        _goal = huntGoal;
                        TargetEntry = _destinations.TargetEntry;
                        if (_destinations.QuestId != 0) QuestId = _destinations.QuestId;
                        else if (_quests.PreferredItemUseQuestId != 0) QuestId = _quests.PreferredItemUseQuestId;
                        else if (_quests.PreferredQuestId != 0) QuestId = _quests.PreferredQuestId;
                        return;
                    }
                    if (_destinations.WaitingForTiles) return; // the route's tiles are being read: keep the goal, plan again next think
                }
                PlayerbotThinkProbe.Phase("explore");
                Explore(player, interval);
                return;
            }
            TargetEntry = _target.Entry;
            // The way round a pack on the approach (PlayerbotRisk.FindDetour): followed before the direct chase.
            if (Risk.Detour is { } detour) { _route = detour; Risk.Detour = null; }
        }
        Creature target = _target;

        TargetEntry = target.Entry;

        PlayerbotThinkProbe.Phase("combat");
        if (target.IsAlive)
        {
            // The soft claim on the mob being walked to (refreshed each think; ended once it is tagged).
            _contention.Claim(player.Guid.Value, target, _session.World.NowMs);

            // A tagger the bot decided to help: heal it first when the bot can, else fight the mob like any target.
            if (_contention.IsAssisting(target.Guid) && TryHealTagger(player))
            {
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            // A losing fight is given up (PlayerbotRiskModel.Judge) before the next blow.
            if (player.Combat.IsInCombat && Risk.ObserveFight(player, target))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Retreat;
                Risk.UpdateRetreat(player, interval);
                return;
            }

            if (FightGoesNowhere(player, target) || FightsInvincibleTarget(player, target))
            {
                // Neither a blow landed nor a step was made at this target for FightNoProgressMs: it stands where the bot cannot reach it
                // (a ledge, a cave floor, deep water) or cannot hurt it (an evading creature). It is set aside (StalledTargetMs), also as an
                // attacker (MarkFruitless), and the bot walks on at once rather than standing out the combat linger beside it.
                MarkUnreachable(target, StalledTargetMs);
                MarkFruitless(target);
                (_lingering, _lingerSinceMs) = (true, unchecked(_session.World.NowMs - CombatLingerLimitMs));
                if (_attacking || player.Combat.Victim is not null) _session.TryManagedAction(WorldOpcode.CmsgAttackstop, []);
                _contention.ReleaseClaim();
                _target = null;
                AbandonRoute();
                _attacking = false;
                TargetEntry = 0;
                PlayerbotThinkProbe.Phase("explore");
                Explore(player, interval);
                return;
            }

            // A hurt bot that can attack from range slows the creature on it and backs away to its casting range (PlayerbotKite). The risk
            // judge above ran first and a retreat always wins; kiting is the fight-on branch, also after a cornered retreat.
            if (Kite.Update(player, target, interval, null, 0f, Risk))
            {
                _route = null; // a chase route in hand is stale once the kite moved the bot (not AbandonRoute: the host would stop the kite's walk)
                _rangedIdleMs = 0;
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            if (_combatSpells.Update(player, target, interval))
            {
                if (player.Locomotion.ActiveSpline is not null || (_route is not null && !PlayerbotMotion.IsActive(player)))
                {
                    AbandonRoute();
                }

                _rangedIdleMs = 0;
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            float distance = Distance(player, target);
            bool fightingBot = player.Combat.ThreatenedBy.Contains(target) || ReferenceEquals(target.Combat.Victim, player);
            PlayerbotFightPosition position = DecidePosition(Kite.HoldRange(player, _combatSpells.PreferredRange(player)), distance,
                player.Class == Game.Class.Hunter, ReferenceEquals(target.Combat.Victim, player),
                _rangedIdleMs >= RangedIdleLimitMs && !fightingBot, MapCombat.CanReachWithMeleeAutoAttack(player, target));
            if (position == PlayerbotFightPosition.Hold)
            {
                // In range: stand and let the rotation cast (or wand) at the next decision.
                _rangedIdleMs = fightingBot ? 0 : _rangedIdleMs + interval;
                AbandonRoute();
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            if (position is PlayerbotFightPosition.ChaseToRange or PlayerbotFightPosition.ChaseToMelee)
            {
                PlayerbotThinkProbe.Phase("chase");
                // An enemy already fighting the bot (or holding it on its threat list) has been weighed by ObserveFight: no hazard mark
                // may refuse or stop the chase and leave the bot standing in combat without a target.
                if (fightingBot) PlayerbotNavigation.Guard(player, null);
                var chaseGoal = new System.Numerics.Vector3(target.X, target.Y, target.Z);
                // A route walked to its end, or whose end the target has left by more than ChaseReplanYards, is planned again in
                // this think (it was dropped here at the next think, and planned at the one after: two think gaps per re-chase).
                if (_route is null || ChaseRouteStale(player, _route, chaseGoal))
                {
                    PlanOutcome chase = PlayerbotNavigation.PlanExact(player, chaseGoal, _options, ref _tileWait,
                        _session.World.NowMs, out PlayerbotRoute? fresh, chase: true);
                    if (chase == PlanOutcome.Pending)
                    {
                        // A tile of the way is being read: keep the target, ask again next think, and keep walking the route still in
                        // hand (its lease renewed) rather than standing for the wait.
                        if (_route is { Complete: false } inHand
                            && !PlayerbotNavigation.TryAdvance(_session, inHand, _options, interval, _session.World.NowMs))
                            AbandonRoute();
                        return;
                    }

                    if (chase == PlanOutcome.Refused)
                    {
                        MarkUnreachable(target);
                        _target = null;
                        AbandonRoute();
                        PlayerbotMovementControl.Stop(_session, player);
                        if (fightingBot) _goal = PlayerbotGoalKind.Combat;
                        return;
                    }

                    if (_route is not null && !ReferenceEquals(_route, fresh)) AbandonRoute(); // the old route is replaced, not kept
                    if (fresh is not null)
                    {
                        System.Numerics.Vector3 end = fresh.Points[^1];
                        float endFlat = System.Numerics.Vector2.Distance(new(end.X, end.Y), new(chaseGoal.X, chaseGoal.Y));
                        bool incomplete = (endFlat > 1f || MathF.Abs(end.Z - chaseGoal.Z) > PlayerbotNavigation.ProjectedEndYards)
                            && fresh.Distance < _options.MaxRouteYards * 0.9f
                            && fresh.Points.Count < _options.MaxPathPoints;
                        if (incomplete)
                        {
                            float progress = System.Numerics.Vector2.Distance(new(player.X, player.Y), new(end.X, end.Y));
                            bool endsShort = !fightingBot && (position == PlayerbotFightPosition.ChaseToMelee
                                ? endFlat > PlayerbotNavigationAnomalies.PathEndShortYards || MathF.Abs(end.Z - chaseGoal.Z) > PlayerbotNavigation.ProjectedEndYards
                                : System.Numerics.Vector3.Distance(end, chaseGoal) > Kite.HoldRange(player, _combatSpells.PreferredRange(player)));
                            if (progress < PlayerbotNavigation.PartialProgressYards || endsShort)
                            {
                                MarkUnreachable(target);
                                _target = null;
                                AbandonRoute();
                                PlayerbotMovementControl.Stop(_session, player);
                                if (fightingBot) _goal = PlayerbotGoalKind.Combat;
                                return;
                            }
                        }

                        // The loop watch ignores a chase (PlayerbotMotion.Follow); the goal it was planned to is what "the target moved" is
                        // measured from, and a route cut short of it (MaxRouteYards, MaxPathPoints) is re-planned only once mostly walked.
                        fresh.Chase = true;
                        _chaseGoal = chaseGoal;
                        _chaseTruncated = endFlat > _options.ChaseReplanYards;
                    }
                    _route = fresh;
                }

                _goal = PlayerbotGoalKind.Grind;
                if (_route is null || !PlayerbotNavigation.TryAdvance(_session, _route, _options, interval, _session.World.NowMs))
                    AbandonRoute();
                return;
            }

            // The server refuses a swing at a target outside the auto-attack arc (SMSG_ATTACKSWING_BADFACING) and a managed player
            // never turns by itself: face the victim first, as the client does when it attacks.
            if (!FacesTarget(player, target))
            {
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0 && !PlayerbotMovementControl.Stop(_session, player)) return;
                _goal = PlayerbotGoalKind.Combat;
                // Turned in this think, the swing follows in it (it waited for the next think, a gap of seconds, before).
                if (!PlayerbotMotion.Face(_session, player, MathF.Atan2(target.Y - player.Y, target.X - player.X))) return;
            }

            if (!_attacking || player.Combat.Victim is null)
            {
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0 && !PlayerbotMovementControl.Stop(_session, player)) return;
                _goal = PlayerbotGoalKind.Combat;
                _attacking = _session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(target.Guid.Value));
            }
            return;
        }

        // A kill the bot helped with, or one that went to another player's tap, is no loot or credit of the bot's: no request, no wait
        // for a response that will not come, and the spawn is left alone for a while.
        if (!HasLootRights(player, target))
        {
            _contention.EndAssist();
            _contention.ReleaseClaim();
            _contention.MarkSomeoneElses(target.Guid.Value, _session.World.NowMs);
            _contention.NoteLostMob(target.Entry);
            _target = null;
            AbandonRoute();
            _attacking = false;
            _lootOpened = false;
            TargetEntry = 0;
            _goal = PlayerbotGoalKind.Explore;
            return;
        }

        bool hasBagSpace = HasFreeBagSlot(player);
        if (!hasBagSpace)
        {
            _trace.Record(_session.World.NowMs, "loot-bag-full");
        }

        bool hasGoldOnCorpse = player.Map is { } lootMap
            && _session.Services.GetService<GameObjects.GameObjectLootFeature>()
                ?.FindSystem(lootMap)
                ?.Loot
                ?.FindLoot(target.Guid) is { Gold: > 0 };

        if (_lootedCorpses.Contains(target.Guid)
            || Distance(player, target) > 5f
            || (!hasBagSpace && !hasGoldOnCorpse))
        {
            RememberLootedCorpse(target.Guid);
            _target = null;
            AbandonRoute();
            _attacking = false;
            _lootOpened = false;
            _lootResponseReceived = false;
            TargetEntry = 0;
            _goal = PlayerbotGoalKind.Explore;
            Rest(player);
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _goal = PlayerbotGoalKind.Loot;
        _lootOpened = _session.TryManagedAction(WorldOpcode.CmsgLoot,
            PlayerbotNavigation.GuidPayload(target.Guid.Value));
        if (_lootOpened)
        {
            RememberLootedCorpse(target.Guid);
            _lootStartedMs = (long)_session.World.Uptime.TotalMilliseconds;
        }
        else
        {
            RememberLootedCorpse(target.Guid);
            _target = null;
            AbandonRoute();
            TargetEntry = 0;
            _goal = PlayerbotGoalKind.Explore;
        }
        return;
    }

    /// <summary>
    /// The walk back to the home area, in the out-of-combat block after the class upkeep and before the quest, town and trainer
    /// steps. A bot on the home map, outside the radius (or still walking back and outside the return radius) with no errand drops its
    /// intent and walks to the centre. Errands that are really needed keep their priority and may take the bot anywhere: a vendor or
    /// trainer that has something useful to do (<see cref="PlayerbotTownGoals.HasCandidate"/>, <see cref="PlayerbotTrainerDestinations.HasCandidate"/>)
    /// and a quest exchange already under way. A completed quest's turn-in, combat, looting, rest and retreat run earlier in
    /// <see cref="Update"/> and are not touched; a dead bot follows the ghost recovery and walks home after the resurrection. A bot on
    /// another map is teleported home unless the return mode is <see cref="PlayerbotHomeReturnMode.Walk"/> (then it is reported off-map
    /// and left alone).
    /// <para>
    /// Walk or teleport (<see cref="PlayerbotHomeOptions.ReturnMode"/>, <see cref="PlayerbotHomeReturn.Judge"/>): Auto teleports a bot on
    /// another map, farther than <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/>, whose straight line home crosses an area far
    /// above its level, or whose walk was set aside as blocked, and walks otherwise. The teleport is never made in combat (this block runs
    /// out of combat only, and <see cref="PlayerbotHomeReturn.TryTeleport"/> checks again), dead or as a ghost (the dead branch of
    /// <see cref="Update"/> returns before this), in a group, or to a map <see cref="PlayerbotMapPolicy"/> does not allow. A party AI, a
    /// bot-led group, the battlegrounds coordinator or a scripted controller drives the bot instead of this brain, so the home (and its
    /// teleport) is suspended then (ManagedPlayerbotFeature.Think).
    /// </para>
    /// </summary>
    private bool TryReturnHome(Player player, PlayerbotHomeArea home, uint interval, bool errandsFirst = true)
    {
        uint now = _session.World.NowMs;
        PlayerbotHomeOptions homeOptions = _options.Home;
        PlayerbotHomeState state = _homeReturn.Evaluate(player, home, now);
        switch (state)
        {
            case PlayerbotHomeState.Inside:
            case PlayerbotHomeState.OffMap when homeOptions.ReturnMode == PlayerbotHomeReturnMode.Walk:
            case PlayerbotHomeState.Blocked when !(homeOptions.ReturnMode == PlayerbotHomeReturnMode.Auto && _homeReturn.WalkBlocked):
                return false;
            case PlayerbotHomeState.Returning:
            case PlayerbotHomeState.OffMap:
            case PlayerbotHomeState.Blocked:
            default:
                break;
        }

        if (errandsFirst && (_quests.Busy || _town.HasCandidate(player)
            || _quests.PreferredCreatureEntry == 0 && (_trainers.HasCandidate(player) || _riding.HasCandidate(player)))) return false;
        if (TryTravelHome(player, home, state))
        {
            DropIntent();
            _goal = PlayerbotGoalKind.Home;
            return true;
        }

        PlayerbotHomeTeleportReason teleport = _homeReturn.Judge(player, home, homeOptions);
        if (teleport != PlayerbotHomeTeleportReason.None)
        {
            if (!_homeReturn.TryTeleport(_session, player, home, _options, teleport, now)) return false;
            DropIntent();
            _goal = PlayerbotGoalKind.Home;
            return true;
        }

        if (state != PlayerbotHomeState.Returning) return false;
        DropIntent();
        if (!_homeReturn.Update(_session, player, home, _options, interval, now)) return false;
        _goal = PlayerbotGoalKind.Home;
        return true;
    }

    /// <summary>
    /// A home on another map, or across water on this one (Teldrassil), is travelled to the way a player would (docs/areas/playbots.md,
    /// Travel): a journey by boat, zeppelin, flight path, hearthstone or portal (<see cref="IPlayerbotTravel.TryBegin"/>), which the brain
    /// drives from the next think. Only where the return would otherwise teleport (not <see cref="PlayerbotHomeReturnMode.Walk"/>, whose
    /// off-map bot is left to the operator); when no journey can begin, the return rules decide as before (the config-gated teleport).
    /// </summary>
    private bool TryTravelHome(Player player, PlayerbotHomeArea home, PlayerbotHomeState state)
    {
        if (_options.Home.ReturnMode == PlayerbotHomeReturnMode.Walk || !_options.Travel.Enabled) return false;
        PlayerbotTravel.PublishStaticRoutes(_session.World);
        uint mapId = player.Map?.MapId ?? player.MapId;
        bool far = state == PlayerbotHomeState.OffMap
            || Population.PopulationTownProgression.SeparatedByWater(mapId, player.X, player.Y, home.MapId, home.Centre.X, home.Centre.Y);
        if (!far || _session.Services.GetService<IPlayerbotTravel>() is not { } travel) return false;
        if (travel.IsTraveling(player)) return true;
        uint now = _session.World.NowMs;
        // A journey home that failed (its anomaly is reported once by the journey) or could not begin waits the transit backoff before
        // the next one, doubling while it keeps failing; only a journey home that arrived starts the count again. Journeys others began
        // (.playerbot travel) do not count. The return rules (the teleport fallback) answer meanwhile.
        if (_travelHomeJourney is { } last && PlayerbotTravel.JourneyOf(player) is { } current && ReferenceEquals(last, current))
        {
            if (current.State == JourneyState.Arrived) _travelHomeFailures = 0;
            else if (current.State != JourneyState.Active)
                _travelHomeRetryAt = unchecked(now + _options.Travel.BackoffMs(++_travelHomeFailures));
            _travelHomeJourney = null;
        }
        else if (_travelHomeJourney is not null && !ReferenceEquals(_travelHomeJourney, PlayerbotTravel.JourneyOf(player)))
        {
            _travelHomeJourney = null; // replaced by a journey another caller began: it neither counts nor clears
        }

        if (_travelHomeFailures > 0 && unchecked((int)(_travelHomeRetryAt - now)) > 0) return false;
        // An attempt that began no journey (none could begin, or the bot already stands at the goal) waits too: each attempt builds the
        // travel facts, which read up to PlayerbotTravel.BlockingLocates navmesh tiles synchronously on the world thread.
        if (travel.TryBegin(_session, player, home.MapId, home.Centre, out _) && travel.IsTraveling(player))
        {
            _travelHomeJourney = PlayerbotTravel.JourneyOf(player) is { State: JourneyState.Active } begun ? begun : null;
            return true;
        }

        _travelHomeRetryAt = unchecked(now + _options.Travel.BackoffMs(++_travelHomeFailures));
        return false;
    }

    // The journey-home retry wait (TryTravelHome): failures in a row, when the next try may begin, and the journey home under way.
    private int _travelHomeFailures;
    private uint _travelHomeRetryAt;
    private TravelJourney? _travelHomeJourney;

    /// <summary>
    /// A new stall (<see cref="PlayerbotStallWatch"/>): set the goal's target aside for <see cref="PlayerbotSuspensions.SuspendMs"/>
    /// and drop the intent, so the next decision picks something else. A quest goal sets its quest and its giver aside (a completed
    /// quest whose ender cannot be reached no longer holds the bot at its giver for good), a vendor or trainer goal that NPC, a
    /// fight its target; an exploring bot turns anywhere.
    /// <para>
    /// The stall counts towards the goals set aside for every bot (<see cref="PlayerbotSharedSetAsides"/>) only while the bot is inside
    /// its home area, or has none. A homed bot outside it (on another map, on its way back, or on a turn-in or capital errand) stalls
    /// where its home does not keep it: that says nothing about the goal for the bots whose home it is (live 2026-10-11: quest 109,
    /// ended at Sentinel Hill inside every bot's home, was set aside for all of them by bots stalled in Redridge and at Raven Hill).
    /// Its own set-aside is unchanged. <see cref="StallOutsideHome"/> tells the feature's log line (and so bot QA) which it was.
    /// </para>
    /// <para>
    /// A failed tram trip (<see cref="PlayerbotTram"/>, <paramref name="shareable"/> false) is the bot's own too: it says the tram did not
    /// carry the bot (no car, no stop, no way to the car's side), not that the goal beyond it is unreachable, and the place it is reported
    /// (the exit the bot left by) is not where the goal failed. Shared, three bots behind one stopped car would set every bot's goal on the
    /// other side aside, and bot QA would read it as that quest's fault. Its log line is never marked outside the home either
    /// (ManagedPlayerbotFeature.LogStall marks only "stalled" lines).
    /// </para>
    /// </summary>
    internal void GiveUpStalledGoal(Player player, bool shareable = true)
    {
        uint now = _session.World.NowMs;
        _trace.Record(now, string.Create(CultureInfo.InvariantCulture, $"stall-giveup:{_goal}/{TargetEntry}/{QuestId}"));
        // Asked before this bot reports: a cell other bots already found a dead end is one at this bot's first stall.
        bool worldPin = _stall.IsWorldPin(player.MapId, player.Map?.InstanceId ?? 0, player.X, player.Y, now);
        int pinStalls = _stall.RecordPinStall(player.MapId, player.X, player.Y, now);
        bool ownPin = pinStalls >= PlayerbotStallWatch.PinStallsToEscalate;
        bool deadEndPin = ownPin || worldPin;
        if (ownPin)
        {
            PlayerbotAnomalies.Report(player, "brain", "dead-end-pin",
                string.Create(CultureInfo.InvariantCulture,
                    $"stalled {pinStalls} times within {PlayerbotStallWatch.PinCellYards:F0} yd of here; giving up goal {_goal} target {TargetEntry} quest {QuestId} did not free it"));
            if (CanShareDeadEndPin(player) && ReportWorldPin(player, now) is { } newPin)
                PlayerbotAnomalies.Report(player, "brain", "world-dead-end-pin", $"several bots dead-end-pinned the same cell; every bot avoids it: {newPin}");
        }
        else if (worldPin)
        {
            PlayerbotAnomalies.Report(player, "brain", "world-pin-escape",
                string.Create(CultureInfo.InvariantCulture,
                    $"stalled inside a world dead-end pin within {PlayerbotStallWatch.PinCellYards:F0} yd of here; giving up goal {_goal} target {TargetEntry} quest {QuestId} and leaving"));
        }
        StallReturnQuest = _goal == PlayerbotGoalKind.Quest && QuestId != 0 && QuestId == _quests.ReturnQuest ? QuestId : 0;
        StallOutsideHome = _home is { } home && !home.Contains(player);
        if (shareable && !StallOutsideHome && shared?.Report(_goal, TargetEntry, QuestId, player.Guid.Value, now, player.MapId,
                new System.Numerics.Vector3(player.X, player.Y, player.Z)) is { } everyone)
            SharedSetAside = everyone;
        switch (_goal)
        {
            case PlayerbotGoalKind.Quest:
                _suspensions.SuspendQuest(QuestId, now);
                if (deadEndPin)
                {
                    _suspensions.SuspendEntryEscalating(TargetEntry, now);
                    if (ownPin && QuestId != 0 && _suspensions.QuestSetAsideCount(QuestId) >= PlayerbotStallWatch.PinStallsToEscalate)
                        _suspensions.MarkUnreachableQuest(QuestId, now);
                }
                else
                {
                    _suspensions.SuspendEntry(TargetEntry, now);
                }
                break;
            case PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Train or PlayerbotGoalKind.Mount or PlayerbotGoalKind.Gather:
                if (deadEndPin) _suspensions.SuspendEntryEscalating(TargetEntry, now);
                else _suspensions.SuspendEntry(TargetEntry, now);
                break;
            case PlayerbotGoalKind.Grind or PlayerbotGoalKind.Combat or PlayerbotGoalKind.Loot:
                // The creature the bot stalled on is skipped for about a respawn (StalledTargetMs), not the 30 seconds of a refused chase:
                // chosen again at once, it held the bot for the next stall (bot QA #697, #700, #862).
                if (_target is { } target) MarkUnreachable(target, StalledTargetMs);
                // A chest has no creature (the opportunity retires the combat target; TargetEntry is the gameobject). Giving the goal up
                // without ending that episode lets the same think take the chest again, so the give-up never frees the bot
                // (live 2026-10-17, bot QA #962: stall-giveup:Loot/19019, then phase opportunities with the goal still Loot/19019).
                bool stalledChest = false;
                if (_target is null && _goal == PlayerbotGoalKind.Loot && _opportunities?.ActiveKey is ulong chest)
                {
                    _opportunities.Ledger.Defer(chest, now);
                    _opportunities.Update(player, 0, interrupting: false); // elapsed 0 ends the episode; the ledger keeps it from being begun again
                    stalledChest = true;
                }
                // A grind walk with no creature in sight (the destinations' spawn walk) has no target to set aside: set the entry it
                // was walking to aside, or the next decision plans the same camp again (live 2026-10-12, bot QA #368: 177 stalls
                // at the empty kobold camp of quest 21). The quest's objective is then not hunted until the set-aside ends. A camp that
                // stalls the bot again once its set-aside is over is set aside for longer each time (SuspendEntryEscalating: 20, 40, 80
                // minutes): with the same 10 minutes every time, the camp of #368 stalled 326 bots 5 085 times in four days.
                if (_goal is PlayerbotGoalKind.Grind or PlayerbotGoalKind.Combat && TargetEntry != 0)
                {
                    if (deadEndPin || _suspensions.EntryStrikeCount(TargetEntry) > 0) _suspensions.SuspendEntryEscalating(TargetEntry, now);
                    else _suspensions.SuspendEntry(TargetEntry, now);
                }
                else if (deadEndPin && TargetEntry != 0)
                {
                    _suspensions.SuspendEntryEscalating(TargetEntry, now);
                }
                if (stalledChest) TargetEntry = 0;
                break;
            case PlayerbotGoalKind.Home:
                _homeReturn.MarkBlocked(now);
                break;
            case PlayerbotGoalKind.Explore:
                if (_exploreCandidate is { } explore) BlockExploreCell(explore);
                _exploreRefusals = Math.Max(_exploreRefusals, 3);
                _exploreRefusalTraced = false; // the refusals after the give-up are a new episode and are traced
                if (TargetEntry != 0)
                {
                    if (deadEndPin || _suspensions.EntryStrikeCount(TargetEntry) > 0) _suspensions.SuspendEntryEscalating(TargetEntry, now);
                    else _suspensions.SuspendEntry(TargetEntry, now);
                }
                if (_home is null && IsUnderLevelledForArea(player))
                {
                    uint leftZone = player.ZoneId;
                    if (TryTeleportToBindPoint(player, "stalled exploring where nothing fits its level", urgent: false, safety: true)) RememberEscapedZone(leftZone); // among creatures too strong for it
                }
                break;
            case PlayerbotGoalKind.Rest:
            case PlayerbotGoalKind.Recover:
            case PlayerbotGoalKind.Follow:
            case PlayerbotGoalKind.Assist:
            case PlayerbotGoalKind.Retreat:
            case PlayerbotGoalKind.Group:
            case PlayerbotGoalKind.Battleground:
            case PlayerbotGoalKind.Escape:
            default:
                _exploreRefusals = 3;
                _exploreRefusalTraced = false;
                break;
        }

        if (_goal != PlayerbotGoalKind.Quest && QuestId != 0)
        {
            _suspensions.SuspendQuest(QuestId, now);
            // Only the bot's own pin says the quest is unreachable; a first stall in a world pin leaves with the suspension alone.
            if (ownPin && _suspensions.QuestSetAsideCount(QuestId) >= PlayerbotStallWatch.PinStallsToEscalate)
                _suspensions.MarkUnreachableQuest(QuestId, now);
        }

        if (_attacking || player.Combat.Victim is not null)
            _session.TryManagedAction(WorldOpcode.CmsgAttackstop, []);
        _destinations.AbandonStalledTarget(now);
        _target = null;
        AbandonRoute();
        _exploreCandidate = null;
        _attacking = false;
        _lootOpened = false;
        TargetEntry = 0;
        QuestId = 0;
        PlayerbotMovementControl.Stop(_session, player);
        // Sharing describes the stalled goal, not whether this bot can leave its current ground. A failed tram trip can begin
        // on an island too; suppressing the shared set-aside must not suppress local escape.
        EscapeIsland(player, out _, deadEndPin, worldPinOnly: !ownPin && worldPin);
    }

    /// <summary>
    /// Whether this bot's dead-end pin may count towards a world pin: only on an open-world map (never a dungeon, raid or battleground, whose
    /// stalls are the instance's) and outside a capital or city, where bots crowd round quest givers and vendors and stall for reasons that
    /// say nothing about the ground. Fails closed when the area table is missing.
    /// </summary>
    private bool CanShareDeadEndPin(Player player)
    {
        if (player.Map is not { } map || map.Template is not { } template || template.Instanceable) return false;
        ArcaneCore.Game.Maps.Terrain.AreaTable areas = ArcaneCore.Game.Maps.Templates.WorldMaps.Of(_session.World).Areas;
        uint area = player.ZoneId == 0 ? 0 : map.GetZoneAndAreaId(player.X, player.Y, player.Z).AreaId;
        return CanShareDeadEndPin(template, areas, player.ZoneId, area);
    }

    /// <summary>Tell the world this bot dead-end-pinned its cell, unless the cell is a cut region (<see cref="PlayerbotStallWatch.ReportDeadEndPin"/>). The region lookup runs only for a bot that may share.</summary>
    private string? ReportWorldPin(Player player, uint now)
    {
        PlayerbotNavigation.TryRegionAt(player, new System.Numerics.Vector3(player.X, player.Y, player.Z), out ArcaneCore.Game.Maps.Collision.MMaps.NavRegionGraph? graph, out int region);
        return _stall.ReportDeadEndPin(player.Guid.Value, player.MapId, player.Map?.InstanceId ?? 0, player.X, player.Y, now, graph, region, _options.CutRegionMaxPolygons);
    }

    /// <summary>
    /// The rule of <see cref="CanShareDeadEndPin(Player)"/> on its inputs: an open-world map (not instanceable), a known zone, and neither
    /// the zone nor the sub-area (<paramref name="areaId"/>, 0 when unknown) flagged as a capital, city or town
    /// (<c>Capital</c>, <c>City</c>, <c>SlaveCapital</c>, <c>SlaveCapital2</c>), so Goldshire, Sentinel Hill or Booty Bay inside an open zone do not pin.
    /// </summary>
    internal static bool CanShareDeadEndPin(ArcaneCore.Kernel.WorldData.MapTemplate? template, ArcaneCore.Game.Maps.Terrain.AreaTable areas, uint zoneId, uint areaId)
    {
        if (template is null || template.Instanceable || zoneId == 0 || areas.GetById(zoneId) is not { } zone) return false;
        const uint townFlags = (uint)(ArcaneCore.Game.WorldState.Zones.AreaFlags.Capital | ArcaneCore.Game.WorldState.Zones.AreaFlags.City
            | ArcaneCore.Game.WorldState.Zones.AreaFlags.SlaveCapital | ArcaneCore.Game.WorldState.Zones.AreaFlags.SlaveCapital2);
        if ((zone.Flags & townFlags) != 0) return false;
        return areaId == 0 || areas.GetById(areaId) is not { } area || (area.Flags & townFlags) == 0;
    }

    /// <summary>
    /// The stall watch asked for a goal to be given up (called just before <see cref="GiveUpStalledGoal"/>): the stall itself is the
    /// <c>stall</c> report; a further give-up inside the same unresolved stall (the goal given up before did not free the bot) is
    /// <c>stall-unresolved</c>.
    /// </summary>
    private void ReportStall(Player player)
    {
        // The stall watch runs before this think's decision, so the work that stalled is the last real think's (calls that returned at the
        // think-interval gate did none).
        _stalledWorkPhase = _lastWorkPhase;
        string detail = _stall.Report ?? "stalled (no report text)";
        bool repeated = _stall.LastGiveUpRepeated;
        PlayerbotAnomalies.Report(player, "brain", repeated ? "stall-unresolved" : "stall",
            repeated ? $"giving the goal up again did not free the bot: {detail}" : detail);
    }

    /// <summary>The rings of explore-atlas cells (160 yards each) around an unhomed bot that must hold a creature of its level before it is not stranded (about 480 yards).</summary>
    internal const int StrandedExploreRings = 3;

    /// <summary>How many levels above the bot a creature in those rings must be for the bot to count as under-levelled for the area.</summary>
    internal const int StrandedLevelMargin = 2;

    /// <summary>A bot this near its bind point (yards) is taken to be at it: the stranded teleport would change nothing.</summary>
    internal const float BindArrivedYards = 100f;

    /// <summary>
    /// Whether the bot is under-levelled for the ground it stands on and a teleport to its bind point would help: the explore atlas
    /// lists no creature for its level within <see cref="StrandedExploreRings"/> rings on its map, yet creatures more than
    /// <see cref="StrandedLevelMargin"/> levels above it stand there (live 2026-10-12, bot QA #286 and others: level 7 to 8 bots in
    /// Silverpine and Dun Morogh among level 10 to 14 creatures, the nearest fit over 1000 yards away). An over-levelled bot in a low zone or
    /// a city has nothing to fight either, but nothing here is too strong for it, so it is left to wander. Not when the bot is already
    /// at its bind point, or the bind point is stranded for the bot's level too (the teleport would only move it to the same trouble).
    /// </summary>
    private bool IsUnderLevelledForArea(Player player)
    {
        var atlas = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.ExploreAtlas;
        if (atlas is null || !atlas.HasIndexedMap(player.MapId)) return false;
        var here = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        if (atlas.HasCellNear(player.MapId, player.Level, here, StrandedExploreRings)
            || !atlas.HasStrongerNear(player.MapId, player.Level, here, StrandedExploreRings, StrandedLevelMargin))
            return false;
        HomeBind bind = player.Home;
        if (bind == default) return true; // the teleport itself refuses with "no bind point"
        if (bind.MapId == player.MapId)
        {
            float dx = bind.X - player.X, dy = bind.Y - player.Y;
            if (dx * dx + dy * dy <= BindArrivedYards * BindArrivedYards) return false;
        }

        return !atlas.HasIndexedMap(bind.MapId)
            || atlas.HasCellNear(bind.MapId, player.Level, new(bind.X, bind.Y, bind.Z), StrandedExploreRings)
            || !atlas.HasStrongerNear(bind.MapId, player.Level, new(bind.X, bind.Y, bind.Z), StrandedExploreRings, StrandedLevelMargin);
    }

    /// <summary>
    /// A stall gave a goal up on a navigation-mesh island (<see cref="PlayerbotNavigation.IsOnMeshIsland"/>): no route leaves it, so
    /// the next goal would stall the same, and so would every one after it. Out of combat and alive, the bot is moved off as from an
    /// unsafe landing (<see cref="EscapeLanding"/>): a homed bot teleports to its home's centre
    /// (<see cref="PlayerbotHomeTeleportReason.Island"/>, at most once per home-teleport cooldown), a bot without a home to its bind point.
    /// Only in the open world: a dungeon's own map is left to its group and the instance.
    /// <para>
    /// Live 2026-10-11 (bot QA #180, live-w23-r1, main 85023136): Wfbotac and Wfbotaj stood inside the round wall of the Molsen Farm
    /// (Westfall), on an island the mesh joins to nothing, and gave up goal after goal there for hours, across restarts (Wfbotac was saved
    /// there, and stalled within two minutes of every login). True when the bot was moved.
    /// </para>
    /// </summary>
    private bool EscapeIslandCore(Player player, out bool onIsland, bool deadEndPin = false, System.Numerics.Vector3? confinedFrom = null,
        bool worldPinOnly = false)
    {
        onIsland = false;
        if (!player.IsAlive || player.Combat.IsInCombat || player.Map is not { InstanceId: 0 }) return false;
        // A fragment of mesh (the region graph's component of a few regions, PlayerbotNavigation.IsOnMeshFragment) is an island whose
        // one straight hop off (TryPlanToward) found no ground beside it, or was not walkable: the same escape applies.
        // A repeated stall in the same 10-yard dead-end pin cell (deadEndPin) also triggers this escape when local walking did not free the bot.
        // confinedFrom (EscapeConfinedExplore): where the confinement began. A bot wandering an island ends its 15 seconds anywhere on
        // it, often at its rim, and from the rim the far side of an island up to twice IslandYards across lies beyond IslandYards; the
        // walk from that point stayed within IslandYards of it, so the point's ground is the bot's. Asked only when the bot moved off it.
        bool island = PlayerbotHomeArea.IsNullIsland(player.MapId, player.X, player.Y)
            || PlayerbotNavigation.IsOnMeshIsland(player, _options)
            || (confinedFrom is { } from
                && System.Numerics.Vector2.DistanceSquared(new(from.X, from.Y), new(player.X, player.Y)) > ConfinedFromMinYards * ConfinedFromMinYards
                && PlayerbotNavigation.IsOnMeshIsland(player, from, _options))
            || PlayerbotNavigation.IsOnMeshFragment(player);
        if (!island && !deadEndPin) return false;
        onIsland = island;
        uint now = _session.World.NowMs;
        var playerPos = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        // Home first, but only when the teleport home would be made and actually moves the bot beyond ProgressYards: a refused
        // TryTeleport would set the walk of a bot inside its home aside as blocked (PlayerbotHomeReturn.Refuse), and a Home.Centre
        // at the stall spot is a no-op that leaves the bot pinned; the bind point is the way out then, as from an unsafe landing.
        if (_home is { } home)
        {
            _homeReturn.Evaluate(player, home, now);
            bool homeEscapes = home.MapId != player.MapId
                || System.Numerics.Vector3.Distance(playerPos, home.Centre) > PlayerbotStallWatch.ProgressYards;
            if (homeEscapes
                && _homeReturn.TeleportRefusal(_session, player, home, _options, now) is null
                && _homeReturn.TryTeleport(_session, player, home, _options, PlayerbotHomeTeleportReason.Island, now))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Home;
                return true;
            }
        }

        bool bindEscapes = player.Home != default && (player.Home.MapId != player.MapId
            || System.Numerics.Vector3.Distance(playerPos, new System.Numerics.Vector3(player.Home.X, player.Home.Y, player.Home.Z)) > PlayerbotStallWatch.ProgressYards);
        if (bindEscapes && TryTeleportToBindPoint(player,
                deadEndPin && !island ? (worldPinOnly ? "first stall in a world dead-end pin" : "stalled repeatedly at a dead-end pin") : "stalled on ground no route leaves",
                urgent: deadEndPin, safety: true))
            return true;

        if (_home is { } urgentHome && (urgentHome.MapId != player.MapId
                || System.Numerics.Vector3.Distance(playerPos, urgentHome.Centre) > PlayerbotStallWatch.ProgressYards)
            && _homeReturn.TryTeleport(_session, player, urgentHome, _options, PlayerbotHomeTeleportReason.Island, now, urgent: true))
        {
            DropIntent();
            _goal = PlayerbotGoalKind.Home;
            return true;
        }

        return false;
    }

    private readonly HashSet<uint> _escapedZones = [];

    internal void RememberEscapedZone(uint zone)
    {
        if (zone != 0 && _escapedZones.Count < 8) _escapedZones.Add(zone);
    }

    /// <summary>
    /// <see cref="EscapeIslandCore"/>, remembering the zone the bot left when it was moved so the population can veto relocating it back
    /// there (<see cref="TakeEscapedZones"/>).
    /// </summary>
    private bool EscapeIsland(Player player, out bool onIsland, bool deadEndPin = false, System.Numerics.Vector3? confinedFrom = null,
        bool worldPinOnly = false)
    {
        uint zone = player.ZoneId;
        bool moved = EscapeIslandCore(player, out onIsland, deadEndPin, confinedFrom, worldPinOnly);
        if (moved) RememberEscapedZone(zone);
        return moved;
    }

    /// <summary>The zones of the dead-end, island and too-strong-creatures escapes since the last call; the population records them as relocation vetoes. World thread.</summary>
    internal uint[] TakeEscapedZones()
    {
        if (_escapedZones.Count == 0) return [];
        uint[] zones = [.. _escapedZones];
        _escapedZones.Clear();
        return zones;
    }

    /// <summary>What counts as progress for the stall watch besides moving.</summary>
    private PlayerbotStallWatch.Fingerprint Fingerprint(Player player)
    {
        PlayerbotThinkProbe.Phase("stall inventory");
        uint items = 0;
        foreach (Item item in player.Inventory.AllItems)
            items = unchecked(items + (item.Count * ((item.Entry * 397u) | 1u)));
        PlayerbotThinkProbe.Phase("stall quests");
        int quests = 0, rewarded = 0;
        uint objectives = 0;
        if (_session.Services.GetService<Npc.QuestNpcFeature>()?.Services.StateOf(player) is { Loaded: true } state)
        {
            foreach (ArcaneCore.Game.Quests.QuestStatusData status in state.Quests.Statuses.Values)
            {
                quests++;
                if (status.Rewarded) rewarded++;
                foreach (uint count in status.CreatureOrGOCount) objectives += count;
            }
        }

        PlayerbotThinkProbe.Phase("stall spells");
        int spells = _session.Services.GetService<SpellFeature>()?.Spellbook.GetSpells(player).Count ?? 0;
        PlayerbotThinkProbe.Phase("stall vitals");
        // Resting is standing still by design: health or mana rising above the best seen during the rest is its progress.
        ulong vitals = (ulong)player.Health + (player.PowerType == PowerType.Mana ? ArcaneCore.Game.Spells.SpellSystem.GetPower(player, PowerType.Mana) : 0u);
        _restMark = _goal == PlayerbotGoalKind.Rest ? Math.Max(_restMark, vitals) : 0;
        return new(player.Level, player.GetUInt32(UpdateFields.PlayerXp), player.Money, items, quests, rewarded, objectives, spells, _restMark);
    }

    /// <summary>
    /// With the risk estimate on, a bot killed within <see cref="DeadlyErrandMs"/> of walking an errand (the way to a trainer,
    /// vendor or quest destination led through something that killed it) sets that errand aside at the first death, not only in a
    /// death loop (<see cref="RecordDeath"/>).
    /// </summary>
    internal const uint DeadlyErrandMs = 30_000;

    /// <summary>The goals set aside for a while (stalls, deadly errands; inspection and tests).</summary>
    internal PlayerbotSuspensions Suspensions => _suspensions;

    /// <summary>The quest exchanges (inspection and tests).</summary>
    internal PlayerbotQuestGoals QuestGoals => _quests;

    /// <summary>Set the goal the brain holds, as its decisions would (tests of <see cref="GiveUpStalledGoal"/>).</summary>
    internal void HoldGoalForTests(PlayerbotGoalKind goal, uint entry, uint quest)
    {
        _goal = goal;
        TargetEntry = entry;
        QuestId = quest;
    }

    /// <summary>Keep the errand the bot is on (each think; tests call it directly).</summary>
    internal void NoteErrand(PlayerbotGoalKind goal, uint entry, uint quest)
    {
        if (goal is PlayerbotGoalKind.Quest or PlayerbotGoalKind.Train or PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Mount or PlayerbotGoalKind.Gather && (entry != 0 || quest != 0))
            _errand = (goal, entry, quest, _session.World.NowMs);
    }

    private void RetireDeathIntent()
    {
        if (_pvpFight is { } pvp)
        {
            if (pvp.Target is not null && _session.Player is { } self) pvp.Stop(self);
            (pvp.Target, pvp.Attacking) = (null, false);
        }
        // Death starts a new gameplay lifetime. Drop only controller-owned, transient
        // intent; the recovery helper still owns the ordinary release/reclaim flow.
        _contention.ReleaseClaim();
        _contention.EndAssist();
        _target = null;
        TargetEntry = 0;
        AbandonRoute();
        _exploreCandidate = null;
        _exploreHasPosition = false;
        _attacking = false;
        _lootOpened = false;
        _lootResponseReceived = false;
        _lootActions.Clear();
        _restUntilMs = 0;
        _restKind = default;
        _needsRest = false;
        _satToWait = false;
        _thinkElapsed = 0;
        _combatSpells.Reset();
        _kite?.Reset(_session.Player);
    }

    private void RetireIdleCombatRoute()
    {
        // Service/quest controllers own separate routes. Do not later resume a
        // previous grind path from a different position after their work finishes.
        _target = null;
        AbandonRoute();
        _exploreCandidate = null;
        _exploreHasPosition = false;
        _attacking = false;
    }

    /// <summary>The chest, rare and node-risk goal (<see cref="PlayerbotOpportunities"/>, docs/areas/playbots-opportunities.md; inspection and tests).</summary>
    internal PlayerbotOpportunities Opportunities => _opportunities ??= new PlayerbotOpportunities(_session, _options, () => Risk, claims,
        (opcode, payload) => _lootActions.Enqueue((opcode, payload))) { Untakeable = IsUntakeable };

    /// <summary>
    /// Offers the opportunity goal to the ladder (after the urgent goals, before the optional travel and grinding): true when it took the
    /// think. <paramref name="pull"/> says it chose a creature to fight: the target is set and the think goes on to the combat path.
    /// A quest or grind goal is interrupted only for a find that clears the higher bar of <see cref="PlayerbotOpportunities.Update"/>.
    /// </summary>
    private bool TakeOpportunity(Player player, uint interval, out bool pull)
    {
        pull = false;
        Opportunities.Home = _quests.Home;
        OpportunityStep step = Opportunities.Update(player, interval, interrupting: _goal is PlayerbotGoalKind.Quest or PlayerbotGoalKind.Grind);
        // An episode that handed over a guard and then ended without the fight (refused, a player at the chest, no way to the guard)
        // takes the guard back: left as the target, the ordinary combat path below would go on with the pull the goal gave up.
        if (step.Abandon is { } given) ReleaseOpportunityGuard(player, given);

        if (step.Pull is { } creature)
        {
            if (!ReferenceEquals(_target, creature))
            {
                _contention.ReleaseClaim();
                AbandonRoute();
                _attacking = false;
                _lootOpened = false;
            }

            _target = creature;
            TargetEntry = creature.Entry;
            _goal = PlayerbotGoalKind.Combat;
            pull = true;
            OpportunityDriven = true;
            return true;
        }

        if (!step.Busy) return false;
        OpportunityDriven = true;
        RetireIdleCombatRoute();
        _goal = step.Goal;
        TargetEntry = step.TargetEntry;
        return true;
    }

    /// <summary>Lets go of a guard the opportunity goal handed over and gave up, unless it is the target and the bot is not yet in combat with it.</summary>
    private void ReleaseOpportunityGuard(Player player, Creature given)
    {
        if (!given.IsAlive || !ReferenceEquals(_target, given)
            || player.Combat.ThreatenedBy.Contains(given) || ReferenceEquals(given.Combat.Victim, player)) return;
        _contention.ReleaseClaim();
        AbandonRoute();
        _attacking = false;
        _target = null;
        TargetEntry = 0;
    }

    internal void DropIntent()
    {
        // A PvP swing in progress is stopped on the wire too (CMSG_ATTACKSTOP), not only forgotten.
        if (_pvpFight is { } pvp)
        {
            if (pvp.Target is not null && _session.Player is { } self) pvp.Stop(self);
            else (pvp.Target, pvp.Attacking) = (null, false);
        }

        _destinations.CancelPlanning();
        _target = null;
        AbandonRoute();
        _exploreCandidate = null;
        _exploreHasPosition = false;
        _attacking = false;
        _lootOpened = false;
        TargetEntry = 0;
        QuestId = 0;
    }

    private void EscapeWater(Player player, uint elapsedMs)
    {
        _goal = PlayerbotGoalKind.Escape;
        _stall.Reset();
        _restUntilMs = 0;
        if (player.StandState != StandState.Stand)
        {
            _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand));
            return;
        }
        if (_waterRoute is null)
        {
            uint now = _session.World.NowMs;
            if (unchecked((int)(_waterRetryAt - now)) > 0) return;
            if (_lootOpened && _target is { } loot)
                _session.TryManagedAction(WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(loot.Guid.Value));
            DropIntent();
            _lootActions.Clear();
            _lootResponseReceived = false;
            _waterRoute = PlayerbotWater.FindExit(player, _options);
            if (_waterRoute is null) _waterRetryAt = unchecked(now + 2_000);
        }
        if (_waterRoute is { } route && !PlayerbotNavigation.TryAdvance(_session, route, _options, elapsedMs, _session.World.NowMs))
        {
            _waterRoute = null;
            _waterRetryAt = unchecked(_session.World.NowMs + 2_000);
        }
    }

    /// <summary>
    /// While the bot carries resurrection sickness (<see cref="PlayerbotSickness"/>), it pulls nothing and
    /// travels nowhere for a fight: it stands with goal Rest until the sickness runs out (at most ten minutes), and that wait is no
    /// stall. Called after the quest exchanges and the town errands (a repair, food) and before the trainer and quest travel, the
    /// economy, exploring and the pull; a creature that attacks it is fought as ever (earlier in <see cref="Update"/>). Under the
    /// sickness every blow and spell deals a quarter and every stat's base is a quarter (spell 15007's three -75% auras), and a
    /// warrior, rogue, hunter or shaman regenerates no health (<see cref="PlayerbotRisk.FruitlessWaitMs"/>): a pull then is the next
    /// death, and the next spirit healer the next sickness. Live 2026-10-11 (analysis bot-xp-analysis.md, 4.1): 61% of the running
    /// level 11+ population bots were sick at any moment, at 7.5 healer revivals per running bot-hour.
    /// </summary>
    private bool StandDownWhileSick(Player player)
    {
        if (PlayerbotSickness.Holder(_session, player) is not { IsPermanent: false, Duration: > 0 }) return false;
        DropIntent();
        _goal = PlayerbotGoalKind.Rest;
        _stall.Reset(); // waiting the sickness out is bounded by its duration, not a stall
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
        return true;
    }

    /// <summary>
    /// The rest a journey takes between two legs (<see cref="PlayerbotTravel.IsResting"/>): food or drink up to
    /// <see cref="PlayerbotRiskOptions.RecoverHealthPct"/> when the bot carries some, otherwise it stops and sits (as the brain's own wait to
    /// recover); the journey stands it up when the rest ends.
    /// </summary>
    private void RestForJourney(Player player)
    {
        _goal = PlayerbotGoalKind.Rest;
        uint wanted = (uint)Math.Clamp(_options.Risk.RecoverHealthPct, 10f, 100f);
        SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
        if (spellFeature is not null && PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, wanted, wanted, out _))
            Rest(player, wanted);
        else if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
        else if (player.StandState == StandState.Stand
                 && _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Sit)))
            _satToWait = true;
    }

    /// <param name="wantedPct">Eat or drink below this percentage (the ordinary 45/35 when 0).</param>
    private void Rest(Player player, uint wantedPct = 0)
    {
        SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
        if (spellFeature is null || player.Combat.IsInCombat
            || spellFeature.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }
            || PlayerbotConsumables.IsEatingOrAboutTo(player, spellFeature.System)
            || !PlayerbotConsumables.TryFindRecovery(player, spellFeature.System, wantedPct == 0 ? 45 : wantedPct,
                wantedPct == 0 ? 35 : wantedPct, out PlayerbotConsumable consumable))
        { _needsRest = false; return; }

        Item item = consumable.Item;

        _goal = PlayerbotGoalKind.Rest;
        _needsRest = true;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        { PlayerbotMovementControl.Stop(_session, player); return; }
        if (player.StandState != StandState.Sit)
        { _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(1u)); return; }
        if (!_session.TryManagedAction(WorldOpcode.CmsgUseItem,
            PlayerbotNavigation.UseItemPayload(item.BagSlot, item.Slot))) return;
        _restKind = consumable.Kind;
        _restUntilMs = unchecked(_session.World.NowMs + 20_000);
        _needsRest = false;
    }

    private static bool HasFreeBagSlot(Player player)
        => PlayerbotTownGoals.FreeBagSlots(player) > 0;

    private void RememberLootedCorpse(ObjectGuid guid)
    {
        if (_lootedCorpses.Count >= 64)
        {
            _lootedCorpses.Clear();
        }
        _lootedCorpses.Add(guid);
    }

    private bool TryEscapeNullIsland(Player player)
    {
        uint now = _session.World.NowMs;
        if (!player.IsAlive || player.Combat.IsInCombat || player.Map is not { InstanceId: 0 })
        {
            return false;
        }

        _stall.Reset();
        _trace.Record(now, "null-island-escape");
        if (_home is { } home && !PlayerbotHomeArea.IsNullIsland(home.MapId, home.Centre.X, home.Centre.Y))
        {
            _homeReturn.Evaluate(player, home, now);
            if (_homeReturn.TryTeleport(_session, player, home, _options, PlayerbotHomeTeleportReason.Island, now, urgent: true))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Home;
                return true;
            }
        }

        return TryTeleportToBindPoint(player, "spawned at null-island (0, 0, 0)", urgent: true);
    }

    private bool LootManagedPackets(Player player, ulong target)
    {
        bool hasBagSpace = HasFreeBagSlot(player);
        if (!hasBagSpace)
        {
            _trace.Record(_session.World.NowMs, "loot-bag-full");
        }

        bool opened = false;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Opcode != WorldOpcode.SmsgLootResponse || packet.Payload.Length < 9)
                continue;

            var reader = new PacketReader(packet.Payload);
            if (reader.ReadUInt64() != target)
                continue;
            opened = true;
            if (reader.ReadByte() != 1 || packet.Payload.Length < 14)
                continue;
            uint coins = reader.ReadUInt32();
            byte count = reader.ReadByte();
            for (int index = 0; index < count && reader.Remaining >= 22; index++)
            {
                byte slot = reader.ReadByte();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadInt32();
                byte slotType = reader.ReadByte();
                if (slotType == 0 && hasBagSpace)
                    _lootActions.Enqueue((WorldOpcode.CmsgAutostoreLootItem, [slot]));
            }

            if (coins > 0)
            {
                _lootActions.Enqueue((WorldOpcode.CmsgLootMoney, []));
            }
            _lootActions.Enqueue((WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(target)));
        }

        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootReleaseResponse))
        {
            if (packet.Opcode == WorldOpcode.SmsgLootReleaseResponse && packet.Payload.Length >= 8)
            {
                var reader = new PacketReader(packet.Payload);
                if (reader.ReadUInt64() == target)
                {
                    opened = true;
                }
            }
        }
        return opened;
    }

    internal static Creature? FindTarget(Player player, uint preferredEntry = 0, Func<Creature, bool>? skip = null,
        PlayerbotContention? contention = null)
    {
        PlayerbotWeighedTarget[] slot = t_oneTarget ??= new PlayerbotWeighedTarget[1];
        int found = CollectTargets(player, preferredEntry, skip, 1, contention, slot);
        Creature? best = found > 0 ? slot[0].Creature : null;
        slot[0] = default;
        return best;
    }

    // The single-slot buffer of FindTarget: one per thread (the world thread in a live server), so the call allocates nothing once warm.
    [ThreadStatic]
    private static PlayerbotWeighedTarget[]? t_oneTarget;

    /// <summary>
    /// The best candidates of <see cref="FindTarget"/>, best first, into <paramref name="best"/> (its length is how many are kept): one
    /// pass over the creatures the bot sees, keeping the lowest weights, with no allocation. A candidate weighs its distance in yards; with
    /// a <paramref name="contention"/> it also weighs 25 yards (<see cref="PlayerbotContentionOptions.ClaimPenaltyYards"/>) more for each
    /// other bot already walking to it while it is untagged, and a stable per-(bot, mob) offset of up to 10 yards, so a crowd of bots
    /// spreads over the mobs near it instead of all picking the nearest. A non-objective may be at most <paramref name="maxLevelsAbove"/>
    /// levels above the bot (1 without the risk estimate); the named quest objective may be any level when the risk estimate weighs it
    /// (<paramref name="maxLevelsAbove"/> above 1). Returns how many were kept.
    /// </summary>
    internal static int CollectTargets(Player player, uint preferredEntry, Func<Creature, bool>? skip, int maxLevelsAbove,
        PlayerbotContention? contention, PlayerbotWeighedTarget[] best)
    {
        if (player.Map is not { } map || best.Length == 0)
            return 0;

        GroupManager? groups = GroupsOf(player);
        uint grayLevel = ArcaneCore.Game.Progression.ExperienceFormulas.GrayLevel(player.Level);
        uint now = (player.Session as WorldSession)?.World.NowMs ?? 0;
        ulong botGuid = player.Guid.Value;
        int count = 0;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature) continue;
            // Picking a player's pet, guardian or totem is a PvP decision, not grinding.
            if (creature.CharmerOrOwnerGuid.IsPlayer) continue;
            if (skip is not null && skip(creature)) continue;
            if (!creature.IsAlive || !map.Combat.Hooks.CanAttack(player, creature)) continue;
            if (creature.Level > player.Level + maxLevelsAbove
                && !(maxLevelsAbove > 1 && preferredEntry != 0 && creature.Entry == preferredEntry)) continue;
            // Grey creatures give no experience (XP::GetGrayLevel); idle grinding leaves them alone. A named quest
            // objective is still allowed: the quest asks for that creature whatever its level.
            if (!(preferredEntry != 0 && creature.Entry == preferredEntry || creature.Level > grayLevel)) continue;
            if (!(preferredEntry != 0 ? creature.Entry == preferredEntry : creature.Template.CreatureType != 8)) continue;
            if (contention is null ? IsSomeoneElses(player, creature, groups) : contention.Blocks(player, creature, groups, now)) continue;

            // Ordinary idle grinding must not initiate attacks on town/service NPCs.
            // Explicit quest objectives and the existing defensive-victim path keep
            // their normal authoritative combat checks.
            if (preferredEntry == 0 && creature.Template.NpcFlags != 0) continue;
            if (!(float.IsFinite(creature.X) && float.IsFinite(creature.Y) && float.IsFinite(creature.Z))) continue;

            float distance = Distance(player, creature);
            float weight = contention is null ? distance : contention.Weigh(botGuid, creature, distance, now);
            if (count == best.Length && !(weight < best[count - 1].Weight)) continue;
            int at = count < best.Length ? count++ : count - 1;
            while (at > 0 && weight < best[at - 1].Weight)
            {
                best[at] = best[at - 1];
                at--;
            }

            best[at] = new PlayerbotWeighedTarget { Creature = creature, Weight = weight };
        }

        return count;
    }

    /// <summary>
    /// Whether a chase route no longer leads to the target: walked to its end, or ending more than
    /// <see cref="PlayerbotOptions.ChaseReplanYards"/> (flat) from where the target stands now.
    /// </summary>
    private bool ChaseRouteStale(Player player, PlayerbotRoute route, System.Numerics.Vector3 goal)
    {
        if (route.Complete || route.Points.Count == 0) return true;
        if (!route.Chase)
        {
            // A route this chase did not plan (a hazard detour): ends at the target or is replaced.
            System.Numerics.Vector3 end = route.Points[^1];
            return System.Numerics.Vector2.Distance(new(end.X, end.Y), new(goal.X, goal.Y)) > _options.ChaseReplanYards;
        }

        if (System.Numerics.Vector2.Distance(new(_chaseGoal.X, _chaseGoal.Y), new(goal.X, goal.Y)) <= _options.ChaseReplanYards) return false;
        if (!_chaseTruncated) return true;
        // The route stops short of where the target was planned to (cut at the route bound): its rest is walked before another plan.
        return !PlayerbotMotion.TryYardsToEnd(player, route, out float left) || left <= route.Distance * 0.25f;
    }

    private System.Numerics.Vector3 _chaseGoal;
    private bool _chaseTruncated;

    /// <summary>
    /// Whether a living, attackable creature of <paramref name="entry"/> in the bot's sight is free (not another player's, not set aside).
    /// A bot that finds none to take while one is free (unreachable, suspended, outside its home, refused by the risk estimate) is not
    /// being contended for it: only a bot with every creature of the entry taken by others accrues contested time.
    /// </summary>
    private bool AnyFreeOfEntry(Player player, uint entry)
    {
        if (player.Map is not { } map) return false;
        GroupManager? groups = GroupsOf(player);
        uint now = _session.World.NowMs;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature || creature.Entry != entry || !creature.IsAlive
                || !map.Combat.Hooks.CanAttack(player, creature)) continue;
            if (!_contention.IsSetAside(creature.Guid.Value, now) && !IsSomeoneElses(player, creature, groups)) return true;
        }

        return false;
    }

    private static GroupManager? GroupsOf(Player player)
        => (player.Session as WorldSession)?.Services.GetService<Social.SocialFeature>()?.Context.Groups;

    /// <summary>Whether <paramref name="target"/> is inside the arc the server swings in (<see cref="CombatConstants.AutoAttackArc"/>).</summary>
    private static bool FacesTarget(Player player, Creature target)
        => MathF.Sqrt(MathF.Pow(target.X - player.X, 2) + MathF.Pow(target.Y - player.Y, 2)) <= CombatConstants.NoFacingChecksDistance
            || MapCombat.HasInArc(player, target, CombatConstants.AutoAttackArc);

    /// <summary>
    /// Where a bot fights its target from (vmangos PartyBotAI.cpp:755-766, GetDistancingTarget / RunAwayFromTarget :146-185 and the
    /// caster chase distance of SetCasterChaseDistance): a bot whose preferred range is melee closes to melee; a caster, healer or
    /// hunter closes only to its preferred range and holds there. A hunter inside its 8-yard Auto Shot dead zone, a ranged bot the
    /// enemy already reached in melee, and a ranged bot that found nothing to cast for a while at a target that is not fighting it
    /// fight in melee instead; nobody ranged otherwise runs into melee.
    /// <para>
    /// <paramref name="inMeleeReach"/> is the server's own swing reach (<see cref="MapCombat.CanReachWithMeleeAutoAttack"/>: the 2D
    /// distance within the combat reach and less than 6 yards of height between them). A target that reach allows is in melee whatever
    /// the 3D <paramref name="distance"/> says: on a slope the 3D distance stays above <see cref="MeleeRange"/> although the bot already
    /// stands at the target (2 yards apart, 3.9 yards of height), the chase has nowhere closer to go, and the bot stood there without
    /// swinging while the creature, which the same reach rule lets hit, wore it down (Dawnrover and the Kobold Laborers on the Elwynn
    /// hillside, ttk=860s reason=behind in the 2026-10-08 replays).
    /// </para>
    /// </summary>
    internal static PlayerbotFightPosition DecidePosition(float preferredRange, float distance, bool hunter, bool targetOnBot,
        bool idleTooLong, bool inMeleeReach = false)
    {
        bool closeEnough = distance <= MeleeRange || inMeleeReach;
        bool melee = preferredRange <= MeleeRange
            || (hunter && distance < PlayerbotClassRotation.HunterDeadZone)
            || (closeEnough && targetOnBot)
            || idleTooLong;
        if (!melee) return distance > preferredRange ? PlayerbotFightPosition.ChaseToRange : PlayerbotFightPosition.Hold;
        return closeEnough ? PlayerbotFightPosition.Melee : PlayerbotFightPosition.ChaseToMelee;
    }

    /// <summary>
    /// Whether <paramref name="creature"/> belongs to another player's fight: tapped by someone outside the bot's group (the
    /// client's grey name: UNIT_DYNFLAG_TAPPED without TAPPED_BY_PLAYER as the bot sees it, LootService's viewer filter over
    /// Creature.LootTapper), or fighting a player who is neither the bot nor in its group (its victim, or any player on
    /// its threat list). vmangos bots take such targets only from their leader's fight (PartyBotAI::SelectAttackTarget).
    /// </summary>
    internal static bool IsSomeoneElses(Player player, Creature creature, GroupManager? groups)
    {
        uint seen = creature.GetValueFor(UpdateFields.UnitDynamicFlags, player);
        if ((seen & Game.Loot.LootService.UnitDynFlagTapped) != 0 && (seen & Game.Loot.LootService.UnitDynFlagTappedByPlayer) == 0)
            return true;
        if (creature.Combat.Victim is Player victim && IsStranger(player, victim, groups))
            return true;
        if (!creature.Combat.HasThreatList) return false;
        IReadOnlyList<ThreatEntry> entries = creature.Combat.Threat.Entries;
        for (int index = 0; index < entries.Count; index++) // indexed: an enumerator over the interface would be boxed for every candidate
        {
            if (entries[index].Target is Player other && IsStranger(player, other, groups))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The player behind a creature's tag or fight: the player who tapped it (or the owner of the pet that did), else the stranger it
    /// fights or holds on its threat list. Null when nobody can be found.
    /// </summary>
    internal static Player? TaggerOf(Player player, Creature creature, GroupManager? groups)
    {
        if (creature.Map is not { } map) return null;
        ObjectGuid tap = creature.LootTapper;
        if (!tap.IsEmpty && TapperOf(map, tap) is { } tapper) return tapper;

        if (creature.Combat.Victim is Player victim && IsStranger(player, victim, groups)) return victim;
        if (!creature.Combat.HasThreatList) return null;
        IReadOnlyList<ThreatEntry> entries = creature.Combat.Threat.Entries;
        for (int index = 0; index < entries.Count; index++)
            if (entries[index].Target is Player other && IsStranger(player, other, groups)) return other;
        return null;
    }

    /// <summary>The player a tap belongs to: the tapper itself, or the owner of the pet that tapped. Null when neither is on the map.</summary>
    private static Player? TapperOf(Map map, ObjectGuid tap)
    {
        if (map.FindPlayer(tap) is { } tapper) return tapper;
        if (map.FindObject(tap) is Unit pet && pet.CharmerOrOwnerGuid.IsPlayer && map.FindPlayer(pet.CharmerOrOwnerGuid) is { } owner) return owner;
        return null;
    }

    /// <summary>
    /// The kill-steal rule (spec 12.3: only when the other side started second). Tracks the mob the bot is engaged with (on its threat list while nobody
    /// outside the bot's group holds its tap) in <see cref="_engagedMob"/>, and records the steal when an outsider takes the tag while the bot is still on the list.
    /// Tag and group are sampled once per think: a groupmate who tags and leaves the group (or is kicked) within one think is seen first as a stranger holding the tag,
    /// which differs from the stored tapper (none yet), and so can be blamed for a steal.
    /// </summary>
    private void NoteEngagement(Player player, Creature held, bool onThreatList, GroupManager? groups)
    {
        ulong mob = held.Guid.Value;
        if (!onThreatList)
        {
            // Not (or no longer) on its threat list: the bot was walking up, or the mob evaded or reset. Whatever tags it next took nothing from the bot.
            if (_engagedMob == mob) ClearEngagement();
            return;
        }

        ObjectGuid tap = held.LootTapper;
        Player? tapper = tap.IsEmpty || held.Map is not { } map ? null : TapperOf(map, tap);
        if (tap.IsEmpty || tapper is not null && !IsStranger(player, tapper, groups))
        {
            // Untagged, or tagged by the bot or its group: the bot has it. The tapper is stored as it is now: a tap that is unchanged when the group
            // changes under it (the player leaves, kicks the bot, the group disbands) turns that tapper into a stranger, but takes nothing from the bot.
            _engagedMob = mob;
            _engagedTapper = tapper?.Guid.Value ?? 0;
            return;
        }

        // An outsider holds the tag. It is a steal only if the tag CHANGED to them since the bot engaged (from untagged or from the own side):
        // the bot engaged first and the other side started second.
        if (tapper is not null && _engagedMob == mob && tapper.Guid.Value != _engagedTapper)
        {
            ClearEngagement(); // once per engagement
            Social?.Note(player.Guid, tapper.Guid, BotSocial.PlayerbotSocialEvent.KillSteal, tapper.Team != player.Team);
        }
    }

    /// <summary>
    /// Whether the corpse of <paramref name="creature"/> is the bot's to loot and count: the viewer's own tapped flag (the tap is the
    /// bot's or its group's), and not a tap that is another's (<see cref="Game.Loot.LootService.UnitDynFlagTapped"/> without
    /// <see cref="Game.Loot.LootService.UnitDynFlagTappedByPlayer"/> as the bot sees it) or a kill that earns nobody credit. A corpse with
    /// no tap on record is left to the loot request, as before.
    /// </summary>
    internal static bool HasLootRights(Player player, Creature creature)
    {
        if (!creature.KillCreditAllowed) return false;
        uint seen = creature.GetValueFor(UpdateFields.UnitDynamicFlags, player);
        if ((seen & Game.Loot.LootService.UnitDynFlagTappedByPlayer) != 0) return true;
        return (seen & Game.Loot.LootService.UnitDynFlagTapped) == 0;
    }

    private static bool IsStranger(Player player, Player other, GroupManager? groups)
        => other.Guid != player.Guid && groups?.AreInSameGroup(player.Guid, other.Guid) != true;

    /// <summary>The creature a bot in combat without a victim of its own fights back (internal for the allocation test).</summary>
    internal Creature? FindDefensiveAttacker(Player player, bool defend = false)
    {
        if (player.Map is not { } map) return null;
        // A creature swinging at the bot comes first; if none is, any living creature that still holds the bot on its
        // threat list (fighting a pet, a party/camp fellow, or held at range) is the fight keeping Combat.IsInCombat on.
        return FirstDefensiveCandidate(player, map, player.Combat.Attackers, defend)
            ?? FirstDefensiveCandidate(player, map, player.Combat.ThreatenedBy, defend);
    }

    /// <summary>
    /// The defensive target among <paramref name="units"/>: a living, attackable creature on the bot's map, reachable ones first, then the
    /// bot's current target, then the nearest; of equals the first met wins. One pass with no allocation: it runs at every think of a
    /// fighting bot, and the LINQ chain it replaced (OfType, Where, OrderBy, two ThenBy) allocated about 140 MB in 40 s at 400 bots
    /// (perf-tick 2026-10-20). The live sets are hash sets, walked with their own enumerator.
    /// </summary>
    private Creature? FirstDefensiveCandidate(Player player, Map map, IReadOnlySet<Unit> units, bool defend)
    {
        DefensiveCandidate best = default;
        if (units is HashSet<Unit> set)
        {
            foreach (Unit unit in set)
                best = BetterDefensiveCandidate(player, map, unit, best, defend);
        }
        else
        {
            foreach (Unit unit in units)
                best = BetterDefensiveCandidate(player, map, unit, best, defend);
        }

        return best.Creature;
    }

    private DefensiveCandidate BetterDefensiveCandidate(Player player, Map map, Unit unit, DefensiveCandidate best, bool defend)
    {
        if (unit is not Creature creature || !creature.IsInWorld || !creature.IsAlive || creature.IsInEvadeMode || IsFruitless(creature)
            || !ReferenceEquals(creature.Map, map)
            || !map.Combat.Hooks.CanAttack(player, creature) || (defend && DefendSkips(player, creature))) return best;
        var candidate = new DefensiveCandidate(creature, IsUnreachable(creature), ReferenceEquals(creature, _target) ? 0 : 1, Distance(player, creature));
        if (best.Creature is null) return candidate;
        int order = candidate.Unreachable.CompareTo(best.Unreachable);
        if (order == 0) order = candidate.TargetRank.CompareTo(best.TargetRank);
        if (order == 0) order = candidate.Distance.CompareTo(best.Distance);
        return order < 0 ? candidate : best;
    }

    /// <summary>One candidate of <see cref="FirstDefensiveCandidate"/> with its sort keys.</summary>
    private readonly record struct DefensiveCandidate(Creature? Creature, bool Unreachable, int TargetRank, float Distance);

    private void MarkUnreachable(Creature creature, uint forMs = UnreachableTargetMs)
    {
        uint now = _session.World.NowMs;
        foreach (ObjectGuid expired in _unreachable.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _unreachable.Remove(expired);
        if (_unreachable.Count < 64 || _unreachable.ContainsKey(creature.Guid)) _unreachable[creature.Guid] = unchecked(now + forMs);
    }

    /// <summary>
    /// How long a target the bot stalled on, or fought without a blow or a step for <see cref="FightNoProgressMs"/>, is skipped: about a
    /// respawn, not the 30 seconds of a refused chase (<see cref="UnreachableTargetMs"/>), after which the same creature was chosen again
    /// and held the bot for another stall (bot QA #697, #700, #862: the same target entry stalled again within a minute or two).
    /// </summary>
    internal const uint StalledTargetMs = 300_000;

    /// <summary>The longest a bot holds a living target without its health falling and without moving <see cref="FightProgressYards"/> (<see cref="FightGoesNowhere"/>).</summary>
    internal const uint FightNoProgressMs = 20_000;

    /// <summary>A step this long (yards) at a target counts as progress of the fight (a chase, a reposition).</summary>
    internal const float FightProgressYards = 3f;

    private ObjectGuid _fightWatchTarget;
    private uint _fightWatchHealth;
    private System.Numerics.Vector3 _fightWatchAt;
    private uint _fightWatchSince;
    private uint _fightWatchSeen;

    /// <summary>A fight not looked at for this long (world ms: the bot rested, travelled or did something else) is watched afresh.</summary>
    private const uint FightWatchGapMs = 15_000;

    /// <summary>
    /// The fight with <paramref name="target"/> makes no progress: since the bot took it (or since its last progress) its health has not
    /// fallen and the bot has not moved <see cref="FightProgressYards"/>, for <see cref="FightNoProgressMs"/>. A bot that chases, swings
    /// or casts at a target it can reach makes progress well within that; one that stands under a target on a ledge, or re-plans a
    /// chase that ends where it stands, does not (bot QA #697, #700, #862: <c>goal=Combat</c> stalls with motion moving:no dest:none,
    /// phase combat). True once, then the watch starts over.
    /// </summary>
    private bool FightGoesNowhere(Player player, Creature target)
    {
        uint now = _session.World.NowMs;
        var at = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        bool gap = unchecked(now - _fightWatchSeen) > FightWatchGapMs;
        _fightWatchSeen = now;
        if (gap || target.Guid != _fightWatchTarget || target.Health < _fightWatchHealth
            || System.Numerics.Vector3.Distance(at, _fightWatchAt) >= FightProgressYards)
        {
            (_fightWatchTarget, _fightWatchHealth, _fightWatchAt, _fightWatchSince) = (target.Guid, target.Health, at, now);
            return false;
        }

        _fightWatchHealth = target.Health; // a target healing back up is no progress, and none taken back either
        if (unchecked(now - _fightWatchSince) < FightNoProgressMs) return false;
        _fightWatchTarget = default;
        return true;
    }

    /// <summary>
    /// The target's health has been held at its death-prevention threshold for <c>Contention.InvincibleTargetAbandonSeconds</c> (a creature
    /// an EventAI script keeps at 1 health, quest 2681's servants): the bot cannot kill it. True once, with the anomaly reported; the caller
    /// sets the creature aside the way it does a fight that goes nowhere (<see cref="Combat.PlayerbotInvincibleWatch"/>).
    /// </summary>
    private bool FightsInvincibleTarget(Player player, Creature target)
        => _invincibleWatch.Observe(player, target, _session.World.NowMs,
            (uint)Math.Clamp(_options.Contention.InvincibleTargetAbandonSeconds, 1, 600) * 1000u);

    /// <summary>
    /// Whether the creature's EventAI holds it invincible from spawn until a particular spell hits it
    /// (<see cref="ArcaneCore.Kernel.WorldData.Creatures.CreatureAiContent.IsInvincibleUntilSpellHit"/>): no target for a bot's grind or hunt.
    /// </summary>
    private bool IsHeldInvincibleEntry(Creature creature)
    {
        if (!_deferFeaturesLooked)
        {
            _deferCreatures = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>();
            _deferQuests = _session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>();
            _deferFeaturesLooked = true;
        }

        return _deferCreatures?.Content?.Ai.IsInvincibleUntilSpellHit(creature.Entry) == true;
    }

    private bool IsUnreachable(Creature creature)
        => _unreachable.TryGetValue(creature.Guid, out uint until) && unchecked((int)(until - _session.World.NowMs)) > 0;

    /// <summary>
    /// Creatures the bot fought without progress (<see cref="FightGoesNowhere"/>) until when (world ms): no defensive target either while
    /// the mark lasts (<see cref="StalledTargetMs"/>). An attacker is otherwise always the bot's fight, unreachable or not, so the one the bot
    /// had just set aside, still swinging at it, was taken back on the next think and held it for the next 20 seconds.
    /// </summary>
    private readonly Dictionary<ObjectGuid, uint> _fruitless = [];

    // The mob the bot is on the threat list of while nobody outside its group holds the tap (NoteEngagement): the only mob another player's tag can steal.
    // Cleared when the bot's target changes or dies (UpdateCore), and when the bot is off the mob's threat list (evade, reset, walking up).
    private ulong _engagedMob;

    // The player behind the tap of _engagedMob when the engagement was last noted (pets resolved to their owner), 0 when the mob was untagged.
    // A KillSteal needs the current tapper to be a stranger who differs from it.
    private ulong _engagedTapper;

    private void ClearEngagement()
    {
        _engagedMob = 0;
        _engagedTapper = 0;
    }

    private void MarkFruitless(Creature creature)
    {
        uint now = _session.World.NowMs;
        foreach (ObjectGuid expired in _fruitless.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _fruitless.Remove(expired);
        if (_fruitless.Count < 16 || _fruitless.ContainsKey(creature.Guid)) _fruitless[creature.Guid] = unchecked(now + StalledTargetMs);
    }

    private bool IsFruitless(Creature creature)
        => _fruitless.Count != 0 && _fruitless.TryGetValue(creature.Guid, out uint until) && unchecked((int)(until - _session.World.NowMs)) > 0;

    /// <summary>Whether a live creature in sight is no target for the bot (the grind filter's rules; <see cref="PlayerbotWorldDestinations.Untakeable"/>).</summary>
    internal bool IsUntakeable(Player player, Creature creature)
        => _quests.PreferredItemUseId != 0 && creature.Entry == _quests.PreferredCreatureEntry
            ? IsUnreachable(creature) || _suspensions.IsEntrySuspended(creature.Entry, _session.World.NowMs)
                || _contention.IsEntrySuspended(creature.Entry, _session.World.NowMs)
                || _home is { } home && creature.MapId == home.MapId && !home.Contains(creature)
                || _quests.IsUnavailableItemTarget(creature, _session.World.NowMs)
            : IsSkipped(creature) || _contention.IsSetAside(creature.Guid.Value, _session.World.NowMs)
                || IsSomeoneElses(player, creature, GroupsOf(player))
                || player.Map is { } map && !map.Combat.Hooks.CanAttack(player, creature);

    /// <summary>
    /// A creature the goals do not choose now: unreachable, of an entry set aside (stall, death loop), or outside the bot's home area
    /// (outside the circle or, for a zone-clipped home, out of the home's zone: <see cref="PlayerbotHomeArea.Contains(Unit)"/>; a
    /// creature on another map than the home is not judged: the bot is off-map and behaves as one with no home). This holds wherever
    /// the bot is on the home map, so a bot away on an errand (a vendor in Stormwind) picks no fight on the road (fail closed).
    /// The defensive-attacker path does not use this: a bot always fights back.
    /// </summary>
    private bool IsSkipped(Creature creature)
        => IsUnreachable(creature) || IsHeldInvincibleEntry(creature) || _suspensions.IsEntrySuspended(creature.Entry, _session.World.NowMs)
            || _contention.IsEntrySuspended(creature.Entry, _session.World.NowMs)
            || _quests.PreferredItemUseId != 0 && creature.Entry == _quests.PreferredCreatureEntry
            || _home is { } home && creature.MapId == home.MapId && !home.Contains(creature);

    /// <summary>
    /// The held mob turned out to be another player's (a tag or a fight the bot is not part of, <see cref="IsSomeoneElses"/>; the bot is
    /// not on its threat list). Leave it alone for <see cref="PlayerbotContentionOptions.SomeoneElsesMemorySeconds"/> and let the caller
    /// choose again, unless the bot decides to help its tagger (<see cref="PlayerbotContention.Decide"/>, at most once per mob per memory
    /// window): then it keeps the mob as an assist-only target, no credit and no loot, until the mob dies or the tagger stops fighting.
    /// </summary>
    private void DropIfSomeoneElses(Player player, Creature held)
    {
        uint now = _session.World.NowMs;
        ulong mob = held.Guid.Value;
        bool mine = player.Combat.ThreatenedBy.Contains(held) || ReferenceEquals(held.Combat.Victim, player);
        GroupManager? groups = GroupsOf(player);
        // Before the early exit below: a bot still on the threat list is "mine", and that is exactly when a tag taken from it is a steal.
        // (_attacking says only that a swing was sent; it can be stale.)
        NoteEngagement(player, held, mine, groups);

        if (_contention.IsAssisting(held.Guid))
        {
            // The mob evaded and the bot tagged it afresh: it is the bot's own now, kill and loot included.
            if (mine && held.LootTapper == player.Guid) { _contention.EndAssist(); return; }
            if (player.Map?.FindPlayer(_contention.AssistTagger) is { IsAlive: true } helped && helped.Combat.IsInCombat) return;
            _contention.EndAssist();
            if (!mine) LeaveSomeoneElses(player, held, now);
            return;
        }

        if (mine) return;
        if (_contention.IsSetAside(mob, now)) { LeaveSomeoneElses(player, held, now); return; }
        if (!IsSomeoneElses(player, held, groups)) return;

        // Only a tagged mob has a tagger to help: an untagged one that merely fights a stranger is left alone (helping would take its tag).
        Player? tagger = held.LootTapper.IsEmpty ? null : TaggerOf(player, held, groups);
        bool shares = tagger is not null && _quests.SharesKillQuest(player, tagger, held.Entry);
        var facts = new PlayerbotHelpFacts(tagger is not null,
            tagger is null ? float.PositiveInfinity : Distance(player, tagger),
            player.MaxHealth == 0 ? 0f : player.Health * 100f / player.MaxHealth,
            _quests.ReturnQuest != 0 || Risk.Retreat.Active || Risk.Waiting,
            tagger is not null && TaggerIsLosing(tagger),
            tagger?.Session is WorldSession { IsManaged: false },
            shares);
        if (tagger is not null && shares) RaiseContentionSignal(player, tagger, held, groups, now);
        if (_contention.Decide(facts, player.Guid.Value, mob, now) == PlayerbotHelpDecision.Help && tagger is not null)
        {
            _contention.BeginAssist(held.Guid, tagger.Guid);
            Social?.NoteHelp(player, tagger);
            _contention.ReleaseClaim();
            return;
        }

        LeaveSomeoneElses(player, held, now);
    }

    private void LeaveSomeoneElses(Player player, Creature mob, uint now)
    {
        _contention.MarkSomeoneElses(mob.Guid.Value, now);
        _contention.NoteLostMob(mob.Entry);
        _contention.ReleaseClaim();
        if (_attacking || ReferenceEquals(player.Combat.Victim, mob)) _session.TryManagedAction(WorldOpcode.CmsgAttackstop, []);
        _target = null;
        AbandonRoute();
        _attacking = false;
        _lootOpened = false;
        TargetEntry = 0;
    }

    /// <summary>The tagger is under <see cref="PlayerbotContention.LosingHealthPct"/> of its health, or has <see cref="PlayerbotContention.LosingEnemies"/> enemies on it.</summary>
    private static bool TaggerIsLosing(Player tagger)
        => tagger.MaxHealth > 0 && tagger.Health * 100f < PlayerbotContention.LosingHealthPct * tagger.MaxHealth
            || Math.Max(tagger.Combat.Attackers.Count, tagger.Combat.ThreatenedBy.Count) >= PlayerbotContention.LosingEnemies;

    /// <summary>
    /// Both the bot and the tagger are ungrouped and have the creature as an unfinished kill objective of the same quest: ask for a
    /// group (source "contention"; the coordinator matches by objective, and with <see cref="PlayerbotGroupOptions.InvitePlayers"/> a
    /// real player nearby is invited, PlayerbotGroupCoordinator.MatchWithPlayer).
    /// </summary>
    private void RaiseContentionSignal(Player player, Player tagger, Creature mob, GroupManager? groups, uint now)
    {
        if (!_options.Groups.Enabled || groups is null || Distance(player, tagger) > PlayerbotContention.HelpYards) return;
        if (groups.GetGroup(player.Guid) is not null || groups.GetGroup(tagger.Guid) is not null) return;
        Risk.RaiseGroupSignal(new PlayerbotGroupSignal(mob.Entry, 2, player.MapId, new System.Numerics.Vector3(mob.X, mob.Y, mob.Z), now,
            PlayerbotGroupSignal.ContentionSource));
    }

    /// <summary>The bot gained no credit on its quest creature <paramref name="entry"/> and no other objective is left: ask for a group.</summary>
    private void RaiseContentionExhausted(uint entry, uint quest)
    {
        if (!_options.Groups.Enabled || _session.Player is not { } player) return;
        Risk.RaiseGroupSignal(new PlayerbotGroupSignal(entry, 2, player.MapId, new System.Numerics.Vector3(player.X, player.Y, player.Z),
            _session.World.NowMs, PlayerbotGroupSignal.ContentionSource, quest));
    }

    private static bool IsPvpFlagged(Player player) => (player.UnitFlags & UnitFlags.Pvp) != 0 || (player.Flags & PlayerFlags.FfaPvp) != 0;

    /// <summary>
    /// Whether the bot may cast a heal at <paramref name="tagger"/>: alive, on the bot's map, of the bot's team and friendly (never
    /// hostile), not flagged for PvP unless the bot is too (a heal on a flagged player flags the healer), and in line of sight. Range
    /// and the rest of castability are the rotation's own check. A server refusal would also bar that spell from the bot's own rotation
    /// for <see cref="PlayerbotCombatSpells.RefusalBackoffMs"/>, so a cast that would be refused is never sent.
    /// </summary>
    internal static bool IsSafeHealTarget(Player player, Player tagger)
    {
        if (!tagger.IsAlive || !tagger.IsInWorld || !ReferenceEquals(tagger.Map, player.Map) || player.Map is not { } map) return false;
        if (tagger.Team != player.Team || !map.Combat.Hooks.IsFriendly(player, tagger) || map.Combat.Hooks.IsHostileTo(player, tagger)) return false;
        if (IsPvpFlagged(tagger) && !IsPvpFlagged(player)) return false;
        return player.IsWithinLineOfSight(tagger);
    }

    /// <summary>
    /// A healer class with a direct heal tops the tagger it helps up (below 85% health) before it fights the mob, when
    /// <see cref="IsSafeHealTarget"/> holds. The combat view is built once for the heals (<see cref="PlayerbotCombatSpells.FirstCastable"/>).
    /// True: a heal was accepted (or a cast is under way) and the think is spent.
    /// </summary>
    internal bool TryHealTagger(Player player)
    {
        if (player.Class is not (Game.Class.Priest or Game.Class.Paladin or Game.Class.Druid or Game.Class.Shaman)) return false;
        if (player.Map?.FindPlayer(_contention.AssistTagger) is not { } tagger || !IsSafeHealTarget(player, tagger)
            || tagger.MaxHealth == 0 || tagger.Health * 100f >= 85f * tagger.MaxHealth) return false;
        // A self-only heal such as Desperate Prayer would heal the bot, not its tagger; a scripted (Holy Light) or chain (Healing Wave) heal lands on the target.
        PlayerbotAbilities abilities = _combatSpells.Abilities(player);
        bool healed = _combatSpells.FirstCastable(player, abilities.DirectHeals, tagger, abilities.HealsAnotherUnit) is { } heal && _combatSpells.CastAt(player, heal, tagger);
        if (healed) Social?.NoteHelp(player, tagger);
        return healed;
    }

    private void RememberAttackers(Player player)
    {
        NoteErrand(_goal, TargetEntry, QuestId);
        _attackerEntries.Clear();
        _attackerFacts.Clear();
        _attackers.Clear();
        // Out of a fight (nearly every think of nearly every bot) there is nothing to walk: skip the Concat iterator it would allocate.
        if (player.Combat.Attackers.Count == 0 && player.Combat.ThreatenedBy.Count == 0) return;
        foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
        {
            if (unit is not Creature creature || creature.CharmerOrOwnerGuid.IsPlayer) continue;
            _attackerEntries.Add(creature.Entry);
            CaptureAttacker(player, creature);
            if (!_attackers.Contains((creature.Guid, creature.Entry)))
                _attackers.Add((creature.Guid, creature.Entry));
        }
    }

    private void CaptureAttacker(Player player, Creature creature)
    {
        if (_attackerFacts.TryGetValue(creature.Entry, out QaAttackerFact? known))
        {
            if (known.Level == creature.Level) return; // already captured in this update (attacker and threat, or the killing blow): skip the repeat. RememberAttackers clears the facts each living update, so hostility stays fresh.
        }
        else if (_attackerFacts.Count >= 16) return;
        var template = creature.Template;
        bool guard = (template.Behaviour & CreatureBehaviourFlags.Guard) != 0;
        bool hostile = player.Map?.Combat.Hooks.IsHostileTo(creature, player) ?? false;
        _attackerFacts[creature.Entry] = new QaAttackerFact(creature.Entry, creature.Level, template.MinLevel, template.MaxLevel,
            template.Rank, guard, hostile);
    }

    // A creature can land its first lethal hit between two bot updates; the living snapshot above
    // never sees that attacker. The map's kill observer carries the killer and the pack that was
    // attacking when the blow landed (taken while the victim was still alive: UnitKilled runs after
    // those lists are cleared) before RecordDeath runs.
    internal void ObserveKillingBlows(Player player) => WatchKills(player);

    private void WatchKills(Player player)
    {
        MapCombat? combat = player.Map?.Combat;
        PlayerbotKillObserver? observer = combat is null ? null : PlayerbotKillObserver.For(combat);
        lock (_killWatchGate)
        {
            if (Volatile.Read(ref _stopped) != 0
                || ReferenceEquals(_killObserver, observer) && ReferenceEquals(_observedPlayer, player)) return;
            if (_killObserver is { } previous && _observedPlayer is { } oldPlayer) previous.Remove(oldPlayer);
            _killObserver = observer;
            _observedPlayer = player;
            observer?.Add(player, RememberKiller);
        }
    }

    private void RememberKiller(Unit? killer, IReadOnlyList<Unit> engaged)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        _killingBlowSource = killer switch
        {
            null => "Unknown",
            _ when ReferenceEquals(killer, _observedPlayer) => "Self",
            Creature { CharmerOrOwnerGuid.IsPlayer: true } => "PlayerPet",
            Creature => "Creature",
            Player => "Player",
            _ => "Other",
        };
        RememberDanger(killer);
        foreach (Unit unit in engaged) RememberDanger(unit);
    }

    /// <summary>A creature that killed the bot or was on it when the blow landed. A player's pet or totem is not the content's danger.</summary>
    private void RememberDanger(Unit? unit)
    {
        if (unit is not Creature creature || creature.CharmerOrOwnerGuid.IsPlayer) return;
        _attackerEntries.Add(creature.Entry);
        if (_observedPlayer is { } player) CaptureAttacker(player, creature);
        if (!_attackers.Contains((creature.Guid, creature.Entry)))
            _attackers.Add((creature.Guid, creature.Entry));
    }

    /// <summary>
    /// The bot just died (<see cref="PlayerbotStallWatch.RecordDeath"/>): a creature entry that has now killed it twice is set aside
    /// for the goals, and a death loop takes the spirit healer for this death instead of the body in the same place again, and sets
    /// aside the errand it was last travelling for (its quest and NPC, every trainer for a trainer visit): the route to it led
    /// through the place (replay of the 2026-10-08 rehearsal: without that, Dawnrover walked from the Goldshire graveyard to its
    /// next trainer in Stormwind past the Scourge invasion point and died on the road again). A second death within
    /// <see cref="PlayerbotStallWatch.DeathLoopYards"/> of an earlier one in the window sets the errand aside the same way (its
    /// recovery takes the spirit healer when one can be had, <see cref="JudgeBody"/>).
    /// </summary>
    private void RecordDeath(Player player)
    {
        uint now = _session.World.NowMs;
        if (unchecked(now - _lastQaDeathMs) > PlayerbotStallWatch.DeathLoopWindowMs) _recentDeathAttackerFacts.Clear();
        foreach ((uint entry, QaAttackerFact fact) in _attackerFacts)
            if (_recentDeathAttackerFacts.Count < 16 || _recentDeathAttackerFacts.ContainsKey(entry)) _recentDeathAttackerFacts[entry] = fact;
        _lastQaDeathMs = now;
        _lastQaDeathGoal = _goal.ToString();
        _lastQaDeathQuest = QuestId;
        _lastQaDeathMap = player.MapId;
        _lastQaDeathPosition = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        _lastQaDeathSource = player.LastEnvironmentalDeath?.ToString()
            ?? (player.LastMissingCasterPeriodicDeath ? "PeriodicCasterMissing" : _killingBlowSource);
        _recentDeathSources.RemoveAll(d => unchecked(now - d.AtMs) > PlayerbotStallWatch.DeathLoopWindowMs);
        if (_recentDeathSources.Count >= 16) _recentDeathSources.RemoveAt(0);
        _recentDeathSources.Add((now, player.MapId, _lastQaDeathPosition, _lastQaDeathSource));
        player.LastEnvironmentalDeath = null;
        player.LastMissingCasterPeriodicDeath = false;
        if ((player.Flags & PlayerFlags.Ghost) == 0
            && player.Combat.DeathState != DeathState.Dead)
        {
            player.LastReleasedGraveyard = null;
            player.RepopDestinationResolved = false;
        }
        _killingBlowSource = "Unobserved";
        (bool loop, IReadOnlyList<uint> setAside) = _stall.RecordDeath(now, player.MapId,
            new System.Numerics.Vector3(player.X, player.Y, player.Z), _attackerEntries);
        if (loop && _stall.NewDeathLoop)
            PlayerbotAnomalies.Report(player, "recovery", "death-loop", _stall.Report ?? "the bot keeps dying in one place");
        int killerLevel = 0;
        foreach (uint entry in _attackerEntries)
            if (_attackerFacts.TryGetValue(entry, out QaAttackerFact? fact)) killerLevel = Math.Max(killerLevel, fact.Level);
        _recovery.LastKillerMaxLevel = killerLevel;
        if (loop && _stall.NewDeathLoop && _options.Risk.Enabled)
        {
            (System.Numerics.Vector3 centre, uint[] killers) = _stall.LoopZone(player.MapId,
                new System.Numerics.Vector3(player.X, player.Y, player.Z), now);
            bool repeatedZone = Risk.Hazards.RememberDeathLoop(player.MapId, centre, now,
                _options.Risk.DeathLoopMemorySeconds, killers);
            if (repeatedZone) _suspensions.SuspendQuest(QuestId, now);
        }
        _attackerEntries.Clear();
        Risk.OnDeath(player, _attackers);
        if (_options.Risk.Enabled && _lastQaDeathSource == "Drowning")
        {
            // One drowning is enough (bot QA #271, Sen'jin coast, three deaths in the same cove). The ordinary death mark is 25 yards,
            // which a walk back from the beach never enters until the bot is already under. The wider mark refuses that walk.
            Risk.Hazards.Add(player.MapId, _lastQaDeathPosition, PlayerbotWater.DrowningHazardYards, now,
                _options.Risk.DangerMemorySeconds, "drowning");
        }
        _attackers.Clear();
        foreach (uint entry in setAside) _suspensions.SuspendEntry(entry, now);
        if (loop) StallOutsideHome = false; // the death loop is the report now; the mark was the last living stall's
        // A second death where the bot already died (the repeat death that sends a ghost to the spirit healer, JudgeBody) sets the
        // errand that led there aside as a death loop does, so the revived bot does not walk straight back.
        bool repeat = loop || _stall.DeathsNear(player.MapId, new System.Numerics.Vector3(player.X, player.Y, player.Z), now) >= 2;
        if (!repeat)
        {
            if (_options.Risk.Enabled && _errand.Entry != 0 && unchecked(now - _errand.AtMs) <= DeadlyErrandMs)
            {
                _suspensions.SuspendEntry(_errand.Entry, now);
                if (_errand.Goal == PlayerbotGoalKind.Quest) _suspensions.SuspendQuest(_errand.Quest, now);
                _errand = default;
            }

            return;
        }

        if (loop) _recovery.TakeSpiritHealer();
        if (_errand.Goal == PlayerbotGoalKind.Quest) _suspensions.SuspendQuest(_errand.Quest, now);
        if (_errand.Goal == PlayerbotGoalKind.Train) _suspensions.SuspendTraining(now);
        _suspensions.SuspendEntry(_errand.Entry, now);
        _errand = default;
    }

    /// <summary>
    /// Whether this ghost's body is unsafe to reclaim (<see cref="PlayerbotRecovery.UnsafeBody"/>, <see cref="PlayerbotRecovery.JudgeBody"/>):
    /// the bot already died within <see cref="PlayerbotStallWatch.DeathLoopYards"/> of it inside the death watch's window (this death
    /// and an earlier one, <see cref="PlayerbotStallWatch.DeathsNear"/>: the third would be a death loop, which takes the healer
    /// already); a creature round it is too strong for the bot (<see cref="PlayerbotRisk.TooStrongNear"/>, with the estimate on); or it
    /// lies outside the home while the spirit healer leaves the bot inside the home or at least
    /// <see cref="PlayerbotRecovery.OutsideHomeGainYards"/> nearer its centre
    /// (<see cref="PlayerbotRecovery.SpiritHealerLanding"/>). Live 2026-10-11: Wfbotaz (level 14, homed at Sentinel Hill) reclaimed its
    /// body among Raven Hill's level 20+ skeletons and spiders again and again, two death loops in fifteen minutes (world.log
    /// 05:53:21 and 06:00:42, attackers 202/531/569 and 202/217/569).
    /// <para>
    /// Tightened after the live run of 2026-10-11 (08:04-08:10 UTC, world.log): 80 bodies were given up in six minutes (31 too strong,
    /// 10 died there, 39 outside the home), each costing resurrection sickness and a quarter of the durability. A repeat death counts
    /// only while a creature that would attack the revived bot still stands in reach of the body (a cleared spot is safe whatever
    /// happened there; the third death is a death loop, which asks for the healer anyway). Too strong is a matter of strength, not of a
    /// pull's worth (<see cref="PlayerbotRisk.TooStrongNear"/>). A body outside the home counts only when the home return could not
    /// teleport the revived bot home (<see cref="PlayerbotHomeReturnMode.Walk"/>, or within <see cref="PlayerbotHomeReturn.TeleportCooldownMs"/>
    /// of its last return teleport): otherwise the bot reclaims the body and the Auto return walks or teleports it home for nothing.
    /// </para>
    /// </summary>
    private (PlayerbotUnsafeBody Reason, string? Detail) JudgeBody(Player player, Corpse corpse)
    {
        var body = new System.Numerics.Vector3(corpse.X, corpse.Y, corpse.Z);
        bool bodySubmerged = player.Map is { } map && PlayerbotWater.Submerged(map, body, player.Locomotion.CollisionHeight);
        if (bodySubmerged)
            return (PlayerbotRecovery.JudgeBody(false, false, false, false, bodySubmerged: true),
                "liquid surface above the body's collision height");
        int deaths = _stall.DeathsNear(corpse.MapId, body, _session.World.NowMs);
        bool diedHere = deaths >= 2 && PlayerbotRecovery.Threats(player).Concat(Risk.HazardThreats(player))
            .Any(threat => threat.Reaches(body, PlayerbotRecovery.CampMarginYards));
        PlayerbotEngagement? strong = diedHere || !_options.Risk.Enabled ? null : Risk.TooStrongNear(player, body);
        bool outside = false, nearer = false;
        float bodyFromHome = float.NaN, healerFromHome = float.NaN;
        bool teleportHome = _options.Home.ReturnMode != PlayerbotHomeReturnMode.Walk && _homeReturn.CanTeleportNow(_session.World.NowMs);
        if (!diedHere && strong is null && !teleportHome && _home is { } home && corpse.MapId == home.MapId
            && !home.Contains(corpse.MapId, corpse.X, corpse.Y, corpse.Z))
        {
            outside = true;
            bodyFromHome = home.DistanceTo(corpse.X, corpse.Y);
            if (_recovery.SpiritHealerLanding(player, corpse) is { } landing)
            {
                healerFromHome = home.DistanceTo(landing.X, landing.Y);
                nearer = home.Contains(home.MapId, landing.X, landing.Y, landing.Z)
                    || healerFromHome + PlayerbotRecovery.OutsideHomeGainYards <= bodyFromHome;
            }
        }

        PlayerbotUnsafeBody reason = PlayerbotRecovery.JudgeBody(diedHere, strong is not null, outside, nearer, bodySubmerged);
        string? detail = reason switch
        {
            PlayerbotUnsafeBody.None => null,
            PlayerbotUnsafeBody.DiedHere => string.Create(CultureInfo.InvariantCulture,
                $"{deaths} deaths within {PlayerbotStallWatch.DeathLoopYards:F0} yd in {PlayerbotStallWatch.DeathLoopWindowMs / 1000}s"),
            PlayerbotUnsafeBody.TooStrong => strong.ToString(),
            PlayerbotUnsafeBody.Hazard => "liquid surface above the body's collision height",
            PlayerbotUnsafeBody.OutsideHome => string.Create(CultureInfo.InvariantCulture,
                $"body {bodyFromHome:F0} yd from the home's centre, healer {healerFromHome:F0} yd"),
            _ => null,
        };
        return (reason, detail);
    }

    /// <summary>
    /// The spirit healer just revived the bot among creatures too strong for it (<see cref="PlayerbotRecovery.EscapeLanding"/>), in the
    /// same think, out of combat: a homed bot teleports to its home's centre (<see cref="PlayerbotHomeTeleportReason.UnsafeLanding"/>,
    /// past the cooldown), a bot without a home (or whose home teleport is refused) to its bind point, where its hearthstone leads
    /// (<see cref="TeleportService.TeleportToHomebind"/>, vmangos <c>Player::TeleportToHomebind</c>; the tram's way out uses the same).
    /// The errand that led to the place was set aside at the death (<see cref="RecordDeath"/>). Live 2026-10-11: Wfbotfe (homed) and
    /// Dawnrover (unhomed, death loops at Raven Hill at 08:23:32 and 08:27:47, then a fault at 08:30:25) were revived at the Raven Hill
    /// graveyard among its Skeletal Horrors, attacked before their next think, and died there again and again. True when the bot was moved.
    /// </summary>
    private bool EscapeLanding(Player player)
    {
        uint now = _session.World.NowMs;
        if (!player.IsAlive || player.Combat.IsInCombat) return false;
        if (_home is { } home)
        {
            _homeReturn.Evaluate(player, home, now);
            if (_homeReturn.TryTeleport(_session, player, home, _options, PlayerbotHomeTeleportReason.UnsafeLanding, now, urgent: true))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Home;
                return true;
            }
        }

        // The near teleport pending now is the resurrection's own, into the landing: the escape replaces it (urgent).
        uint revivedIn = player.ZoneId;
        bool toBind = TryTeleportToBindPoint(player, "was revived by the spirit healer among creatures too strong for it", urgent: true);
        if (toBind) RememberEscapedZone(revivedIn);
        return toBind;
    }

    /// <summary>The shortest time between two of the brain's own bind-point teleports that are not urgent (<see cref="TryTeleportToBindPoint"/>).</summary>
    internal const uint BindTeleportCooldownMs = PlayerbotHomeReturn.TeleportCooldownMs;

    private uint _bindTeleportAt;
    private bool _bindTeleported;

    /// <summary>Whether the brain has sent the bot to its bind point (tests).</summary>
    internal bool BindTeleported => _bindTeleported;

    /// <summary>
    /// Why the bot may not teleport to its bind point now, or null: the rules of the return teleport home (PlayerbotHomeReturn.RefusalOf
    /// and TryTeleport; vmangos PartyBotAI.cpp:808) for a teleport the brain makes of its own accord. <c>TeleportService.TeleportToHomebind</c>
    /// checks nothing itself, and a bot can run this brain while in a group (PlayerbotPartyAI.Drives is false while it never engaged and no
    /// real member is online): it must not be pulled away from its group, out of a duel or a battleground, nor in the air. A teleport
    /// under way is waited for, except (<paramref name="urgent"/>) a near one, which the next teleport replaces (the resurrection's own into
    /// an unsafe landing). Not urgent, at most one every <see cref="BindTeleportCooldownMs"/>: a bind point that is itself an island cannot
    /// loop the bot through it at every stall.
    /// </summary>
    private string? BindTeleportRefusal(Player player, TeleportService? teleports, bool urgent, uint now)
    {
        if (teleports is null || !_session.IsManaged) return "no teleport service";
        HomeBind bind = player.Home;
        if (bind == default) return "no bind point";
        if (!player.IsAlive) return "dead";
        if (player.Combat.IsInCombat) return "in combat";
        if (player.Duel is not null) return "in a duel";
        if (_session.Services.GetService<ArcaneCore.World.Social.SocialFeature>()?.Context.Groups.GetGroup(player.Guid) is not null) return "in a group";
        if (player.Map?.Template is { IsBattleground: true }) return "on a battleground";
        if ((player.Movement.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) != 0) return "in the air";
        ArcaneCore.Kernel.WorldData.MapTemplate? template = ArcaneCore.Game.Maps.Templates.WorldMaps.Of(_session.World).Registry.Find(bind.MapId);
        if (template is null || template.IsBattleground) return "bind map unknown";
        if (!PlayerbotMapPolicy.Allows(_options.AllowedMaps, bind.MapId, template, current: false)) return "bind map not allowed";
        if (teleports.IsBeingTeleported(player) && !(urgent && teleports.IsBeingTeleportedNear(player))) return "a teleport under way";
        if (!urgent && _bindTeleported && unchecked(now - _bindTeleportAt) < BindTeleportCooldownMs)
            return "teleported to its bind point less than 5 minutes ago";
        return null;
    }

    /// <summary>The non-urgent bind-point teleport a completed quest's unreachable ender asks for (<see cref="PlayerbotWorldDestinations.TeleportToBind"/>; tests).</summary>
    internal bool TeleportToBindPointForQuest(Player player, string why) => TryTeleportToBindPoint(player, why, urgent: false);

    /// <summary><see cref="TryTeleportToBindPoint(Player, string, bool, bool)"/> with the trip made for the bot's safety exactly when it is urgent.</summary>
    private bool TryTeleportToBindPoint(Player player, string why, bool urgent) => TryTeleportToBindPoint(player, why, urgent, safety: urgent);

    /// <summary>
    /// Send the bot to its bind point when <see cref="BindTeleportRefusal"/> allows it, and log why (<paramref name="why"/>). An urgent escape
    /// (<paramref name="urgent"/>: the bot must leave now, and a ten second cast a fight or a fall interrupts is no escape) teleports directly
    /// (vmangos <c>Player::TeleportToHomebind</c>). Any other reason uses the bot's own hearthstone when it is ready
    /// (<see cref="PlayerbotHearthstone.IsReady"/>: <see cref="PlayerbotTravelOptions.Hearthstone"/> on, the stone carried and off cooldown; the
    /// leg is driven by the next thinks, PlayerbotHearthstone.Drive), as a 1.12 player does; when it is not ready only a trip made for the bot's
    /// safety (<paramref name="safety"/>: stranded on ground no route leaves, or among creatures too strong for it) falls back to the direct teleport, and any other reason sends the bot nowhere. True when the
    /// bot was sent (or its hearthstone cast begun); its intent is dropped, a journey under way with it, and it explores from there.
    /// </summary>
    private bool TryTeleportToBindPoint(Player player, string why, bool urgent, bool safety)
    {
        uint now = _session.World.NowMs;
        TeleportService? teleports = _session.Services.GetService<ArcaneCore.World.Teleport.TeleportFeature>()?.Teleports;
        ILogger? logger = _session.Services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotBrain>();
        var from = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        HomeBind bind = player.Home;
        if (BindTeleportRefusal(player, teleports, urgent, now) is { } refusal)
        {
            logger?.LogDebug("Playerbot {Bot} {Why} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: does not teleport to its bind point ({Refusal})",
                player.Name, why, from.X, from.Y, from.Z, player.MapId, refusal);
            return false;
        }

        if (teleports is null) return false;
        if (!Invariant.Check(float.IsFinite(bind.X) && float.IsFinite(bind.Y) && float.IsFinite(bind.Z),
            "PlayerbotBrain.TryTeleportToBindPoint requires finite bind point coordinates."))
            return false;
        if (!urgent && PlayerbotHearthstone.IsReady(_session, player, _options))
        {
            PlayerbotTravel.Cancel(player);
            if (PlayerbotHearthstone.TryBegin(_session, player, _options, out _))
            {
                _bindTeleported = true;
                _bindTeleportAt = now;
                DropIntent();
                _goal = PlayerbotGoalKind.Explore;
                logger?.LogInformation(
                    "Playerbot {Bot} {Why} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: uses its hearthstone to its bind point ({BX:F1}, {BY:F1}, {BZ:F1}) map {BMap}",
                    player.Name, why, from.X, from.Y, from.Z, player.MapId, bind.X, bind.Y, bind.Z, bind.MapId);
                return true;
            }
        }

        if (!safety)
        {
            logger?.LogDebug("Playerbot {Bot} {Why} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: does not go to its bind point (its hearthstone is not ready)",
                player.Name, why, from.X, from.Y, from.Z, player.MapId);
            return false;
        }

        if (!teleports.TeleportToHomebind(player)) return false;
        PlayerbotTravel.Cancel(player);
        _bindTeleported = true;
        _bindTeleportAt = now;
        DropIntent();
        _goal = PlayerbotGoalKind.Explore;
        logger?.LogInformation(
            "Playerbot {Bot} {Why} at ({X:F1}, {Y:F1}, {Z:F1}) map {Map}: teleports to its bind point ({BX:F1}, {BY:F1}, {BZ:F1}) map {BMap}",
            player.Name, why, from.X, from.Y, from.Z, player.MapId, bind.X, bind.Y, bind.Z, bind.MapId);
        return true;
    }

    private void Explore(Player player, uint elapsedMs)
    {
        _goal = PlayerbotGoalKind.Explore;
        QuestId = 0;
        if (EscapeConfinedExplore(player)) return; // the island check was this think's work
        if (_exploreCandidate is not null && _exploreHasPosition
            && (_exploreLastMap != player.MapId || System.Numerics.Vector3.DistanceSquared(
                _exploreLastPosition, new(player.X, player.Y, player.Z)) > 300f * 300f))
        {
            // A teleport, hearth or outside controller moved the bot away from the site and its old route.
            _exploreCandidate = null;
            AbandonRoute();
        }
        _exploreLastPosition = new(player.X, player.Y, player.Z);
        _exploreLastMap = player.MapId;
        _exploreHasPosition = true;
        if (_route is { Complete: false })
        {
            PlayerbotThinkProbe.Phase("explore advance");
            if (ExploreMovementPaused(player, elapsedMs)) return;
            if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, _session.World.NowMs))
            {
                if (!_route.Complete && _exploreCandidate is { } refused) BlockExploreCell(refused);
                AbandonRoute();
            }
            else ChainWanderLeg(player, elapsedMs);
            return;
        }

        if (player.Map is null) return;
        // The think's optional path budget (PlayerbotThinkProbe.HasOptionalPlanBudget) may already be spent by the planners before this
        // one: then the explore site search and its long plan wait for a think with budget, but the bot still takes its one short wander
        // leg (a search of at most a 48-yard leg's nodes) rather than standing until the stall watch gives it up.
        bool budget = PlayerbotThinkProbe.Active is not { HasOptionalPlanBudget: false };
        if (_home is { Stay: not Population.TownStay.None } town && (player.Map?.MapId ?? player.MapId) == town.MapId)
        {
            // Idle with a town home: back to the inn (or, while a festival runs, to the bot's place around a festival NPC) and wait there
            // rather than roam the wilderness (PlayerbotTownIdle; PlayerbotBrain.Events.cs).
            if (TownIdleThink(player, town, elapsedMs)) return;
        }
        else _townIdle.Interrupted(); // no town idle decision this think (another map, another kind of home)

        if (budget && HasOptionalThinkBudget(15.0))
        {
            PlayerbotThinkProbe.Phase("explore candidates");
            if (TryExploreSpawns(player, elapsedMs)) return;
        }
        // Wander onward: keep roughly the current facing (within +-60 degrees, golden-ratio spread so successive legs
        // differ). Only after a refusal turn anywhere. The old golden-angle-from-zero choice sent each leg
        // ~137 degrees from the last one, so a bot that lost its route every few thinks ran in circles.
        PlayerbotThinkProbe.Phase("explore wander plan");
        System.Numerics.Vector3 destination = WanderLegEnd(player, new(player.X, player.Y, player.Z), player.Orientation);
        ExploreWanderPlans++;
        int strongRefusals = Risk.StrongRefusals;
        if (PlayerbotNavigation.PlanWander(player, destination, _options, ref _tileWait, _session.World.NowMs, out _route) == PlanOutcome.Pending)
        {
            AbandonRoute();
            _roamDirection--; // the same leg again once the tile is in
            return;
        }
        if (_route is { } wanderRoute)
        {
            // A pause (no budget, rooted, falling) is not a refusal: the leg stays and is walked when movement resumes.
            // Zeroing the refusal count before the walk starts did the opposite of a pause. TryAdvance then failed (the end
            // was blacklisted, the first stretch was not walkable, there was no speed), the route was dropped, and the count
            // was already 0. The next think's 48-yard ray was refused again, the shortened ray came back at refusal 1, and
            // that is the same heading: the point the failed walk had just given up was kept until the stall watch
            // (goal Explore, phase explore-wander-plan, dest none).
            PlayerbotThinkProbe.Phase("explore advance");
            if (ExploreMovementPaused(player, elapsedMs))
            {
                _exploreRefusals = 0; // the plan was accepted; the pause is not the wall that refused the last ray
                _exploreRefusalTraced = false;
                return;
            }
            if (!PlayerbotNavigation.TryAdvance(_session, wanderRoute, _options, elapsedMs, _session.World.NowMs))
            {
                AbandonRoute();
                _exploreRefusals++;
                TraceWanderRefusal();
                PlayerbotMovementControl.Stop(_session, player);
            }
            else
            {
                _exploreRefusals = 0;
                _exploreRefusalTraced = false;
                MarkLeg(wanderRoute, townWalk: false);
                ChainWanderLeg(player, elapsedMs); // a leg shorter than the think gap's walk is chained at once
            }
        }
        else
        {
            _exploreRefusals++;
            _exploreRingRefused = Risk.StrongRefusals != strongRefusals;
            TraceWanderRefusal();
            if (_exploreRefusals >= 2 && TryWanderToMeshPoint(player, elapsedMs)) return;
            PlayerbotMovementControl.Stop(_session, player);
        }
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies outside the bot's home on this map: beyond <see cref="PlayerbotHomeArea.ExploreRadius"/>, or, for a
    /// zone-clipped home, outside its zone. The mesh-point fallback keeps to the same limits as the rays (<see cref="WanderLegEnd"/>).
    /// A bot with no home, or on another map than its home, is not limited.
    /// </summary>
    private bool OutsideHome(Player player, System.Numerics.Vector3 point)
        => _home is { } area && area.MapId == (player.Map?.MapId ?? player.MapId)
            && (area.DistanceTo(point.X, point.Y) > area.ExploreRadius
                || (area.ZoneClipped && !area.InZone(point.X, point.Y, point.Z)));

    /// <summary>Records "wander-refused" once per refusal episode. A give-up presets the count (GiveUpStalledGoal), so a count test hid every later refusal.</summary>
    private void TraceWanderRefusal()
    {
        if (_exploreRefusalTraced) return;
        _exploreRefusalTraced = true;
        _trace.Record(_session.World.NowMs, "wander-refused");
    }

    /// <summary>Mesh points sampled per think by the wander fallback.</summary>
    private const int WanderMeshSamples = 4;

    /// <summary>
    /// The way out of ground where straight rays fail (a pocket, a canyon, a slope: the ray end is off the mesh or behind a bend). After the full
    /// and a shortened ray were refused, a point the map's pathfinder says is reachable (<see cref="IMeshPointQuery"/>) is planned exactly;
    /// the shortfall rule of <see cref="PlayerbotNavigation.PlanWander"/> does not apply to a point that is on the mesh. Only a leg that moves
    /// the bot by the stall watch's own progress counts. False (the ray behaviour stays) when the map has no mesh point query or no sample is walkable.
    /// </summary>
    private bool TryWanderToMeshPoint(Player player, uint elapsedMs)
    {
        if (player.Map is not { } map || map.Collision.MeshPoints is not { } mesh) return false;
        _wanderRandom ??= new Random((int)(player.Guid.Low & 0x7fffffff));
        var here = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        float minimum = ExploreLegLength(1);
        for (int sample = 0; sample < WanderMeshSamples; sample++)
        {
            // Each exact plan is a synchronous world-thread search (a leg's node budget is under the path-job threshold): a think that
            // spent its optional budget takes none, the first included (the ray plans just before this already used some). The
            // fallback is tried again at the next think.
            if (!HasOptionalThinkBudget(15.0)) break;
            if (!mesh.TryGetRandomPointAround(map.MapId, here, ExploreLegYards, _wanderRandom, PlayerbotMoveCheck.MeshOptions, out System.Numerics.Vector3 point)
                || !point.IsFinite()
                || System.Numerics.Vector2.Distance(new(point.X, point.Y), new(here.X, here.Y)) < minimum
                || PlayerbotMotion.IsBlacklisted(player, point)
                || OutsideHome(player, point))
            {
                continue;
            }
            ExploreWanderPlans++;
            PlanOutcome outcome = PlayerbotNavigation.PlanExact(player, point, _options, ref _tileWait, _session.World.NowMs, out PlayerbotRoute? route);
            if (outcome == PlanOutcome.Pending)
            {
                AbandonRoute();
                return true; // a tile is still being read: this think ends here, the next one samples again (a new point)
            }
            if (outcome != PlanOutcome.Route || route is not { Points.Count: > 0 } found) continue;
            System.Numerics.Vector3 end = found.Points[^1];
            if (System.Numerics.Vector2.Distance(new(end.X, end.Y), new(here.X, here.Y)) < PlayerbotStallWatch.ProgressYards) continue;
            // A partial route into a wall is refused as PlanWander refuses it: the walk must end at the mesh point it was planned to.
            if (System.Numerics.Vector2.Distance(new(end.X, end.Y), new(point.X, point.Y)) > PlayerbotNavigation.WanderMaxShortfallYards) continue;

            _route = found;
            PlayerbotThinkProbe.Phase("explore advance");
            if (ExploreMovementPaused(player, elapsedMs))
            {
                _exploreRefusals = 0;
                _exploreRefusalTraced = false;
                return true;
            }
            if (!PlayerbotNavigation.TryAdvance(_session, found, _options, elapsedMs, _session.World.NowMs))
            {
                AbandonRoute();
                continue;
            }
            _exploreRefusals = 0;
            _exploreRefusalTraced = false;
            _trace.Record(_session.World.NowMs, "wander-mesh-point");
            MarkLeg(found, townWalk: false);
            ChainWanderLeg(player, elapsedMs);
            return true;
        }
        return false;
    }

    /// <summary>The first wait (world ms) before the grind destinations plan again after a refused plan (<see cref="NoteGrindPlan"/>).</summary>
    internal const uint GrindPlanBackoffMs = 4_000;

    /// <summary>The longest wait of the grind destinations after refused plans in a row (the wait doubles with each).</summary>
    internal const uint GrindPlanBackoffMaxMs = 64_000;

    private uint _grindPlanEntry;
    private int _grindPlanRefusals;
    private uint _grindPlanRetryAt;

    /// <summary>Refused grind-destination plans in a row for the entry hunted now (tests, inspection).</summary>
    internal int GrindPlanRefusals => _grindPlanRefusals;

    /// <summary>Whether the grind destinations may plan for <paramref name="huntEntry"/> this think (<see cref="NoteGrindPlan"/>).</summary>
    private bool GrindPlanDue(uint huntEntry)
        => _grindPlanRefusals == 0 || huntEntry != _grindPlanEntry || unchecked((int)(_grindPlanRetryAt - _session.World.NowMs)) <= 0;

    /// <summary>
    /// The answer of one grind-destination plan. A refused one (<see cref="PlayerbotWorldDestinations.LastPlanRefused"/>: a path search that
    /// found no way, usually one run to its node cap) backs the planner off for <see cref="GrindPlanBackoffMs"/>, doubling while it keeps
    /// being refused (up to <see cref="GrindPlanBackoffMaxMs"/>); a planned walk, or another entry to hunt, starts over. Meanwhile the think
    /// goes to the explore, with the think's path budget (<see cref="PlayerbotThinkProbe.HasOptionalPlanBudget"/>) whole.
    /// <para>
    /// Before, the planner ran first on every think, and its refused search (the next spawn of the camp each think, every one beyond
    /// reach) spent the think's optional path budget, so the explore after it returned without a step: live 2026-10-11..16, bot QA #249,
    /// #281, #845 and 139 other stall issues, 343 of 414 stall diagnostics showed <c>moving:no dest:none</c>, 270 of them in phase explore.
    /// </para>
    /// </summary>
    private void NoteGrindPlan(uint huntEntry, bool planned, bool refused)
    {
        if (planned || huntEntry != _grindPlanEntry)
        {
            _grindPlanEntry = huntEntry;
            _grindPlanRefusals = 0;
        }

        if (planned || !refused) return;
        if (_grindPlanRefusals < 8) _grindPlanRefusals++;
        uint wait = (uint)Math.Min((ulong)GrindPlanBackoffMs << (_grindPlanRefusals - 1), GrindPlanBackoffMaxMs);
        _grindPlanRetryAt = unchecked(_session.World.NowMs + wait);
    }

    /// <summary>
    /// The longest a bot stands in combat with nothing to fight (no victim, no creature attacking it or holding it on a threat list it can
    /// fight) before it walks on: past the server's 5.5-second combat timer and its 1.2-second check (<see cref="CombatConstants"/>), so an
    /// ordinary linger ends by itself first.
    /// </summary>
    internal const uint CombatLingerLimitMs = 10_000;

    private bool _lingering;
    private uint _lingerSinceMs;

    private readonly PlayerbotTownIdle _townIdle = new();
    private uint _bindCheckAt;
    private bool _bindChecked;

    /// <summary>
    /// At the inn of the town it stays in, binds its home to that town through the server's own path (CMSG_BINDER_ACTIVATE to the
    /// innkeeper within interaction distance, <see cref="ArcaneCore.Game.Npc.QuestNpcServices.BinderActivate"/>) when the bind is
    /// elsewhere. Checked every 30 seconds at most; false when nothing was sent.
    /// </summary>
    private bool TryBindAtInn(Player player, PlayerbotHomeArea town)
    {
        uint now = _session.World.NowMs;
        // _bindChecked: the first check runs at once (a zero start would read as a future time on a world up for over 2^31 ms).
        if ((_bindChecked && unchecked((int)(now - _bindCheckAt)) < 0) || !player.IsAlive || player.Combat.IsInCombat) return false;
        (_bindChecked, _bindCheckAt) = (true, unchecked(now + 30_000));
        HomeBind bind = player.Home;
        if (!PlayerbotTownIdle.NeedsBind(bind.MapId, bind.X, bind.Y, town.MapId, town.Centre.X, town.Centre.Y)) return false;
        if (_session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.Services is not { } services) return false;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (services.Deps.Creatures?.Find(player, guid, ArcaneCore.Game.Npc.NpcFlags.Innkeeper) is not { IsAlive: true, IsHostile: false } info
                || (info.NpcFlags & ArcaneCore.Game.Npc.NpcFlags.Innkeeper) == 0
                || services.InteractableNpc(player, guid, ArcaneCore.Game.Npc.NpcFlags.Innkeeper) is null) continue;
            var writer = new PacketWriter(8);
            writer.WriteUInt64(guid.Value);
            return _session.TryManagedAction(WorldOpcode.CmsgBinderActivate, writer.ToArray());
        }

        return false;
    }

    /// <summary>Plans and walks the next leg of an idle bot's way back to the centre of its town home (the wander planner, aimed inward).</summary>
    private void WalkTowardTown(Player player, PlayerbotHomeArea town, uint elapsedMs)
    {
        // One short leg, as the wander of Explore: taken even when the think's optional path budget is spent.
        System.Numerics.Vector3 destination = InwardLeg(town, player, _exploreRefusals);
        PlayerbotThinkProbe.Phase("explore town plan");
        if (PlayerbotNavigation.PlanWander(player, destination, _options, ref _tileWait, _session.World.NowMs, out _route) == PlanOutcome.Pending)
        {
            AbandonRoute();
            return;
        }

        if (_route is { } leg && PlayerbotNavigation.TryAdvance(_session, leg, _options, elapsedMs, _session.World.NowMs))
        {
            _exploreRefusals = 0;
            _exploreRefusalTraced = false;
            MarkLeg(leg, townWalk: true);
            return;
        }

        AbandonRoute();
        _exploreRefusals++;
        PlayerbotMovementControl.Stop(_session, player);
    }

    /// <summary>The wandering leg the bot walks now (<see cref="ChainWanderLeg"/> chains only after such a leg, never after one to a spawn).</summary>
    private PlayerbotRoute? _wanderLeg;

    /// <summary>Records the leg the bot walks: only a real explore leg may be chained onward (a town walk ends at the inn, a chained forward leg would carry the bot past it).</summary>
    internal void MarkLeg(PlayerbotRoute leg, bool townWalk) => _wanderLeg = townWalk ? null : leg;

    /// <summary>Whether <paramref name="route"/> is the explore leg in hand and so may be chained onward (<see cref="ChainWanderLeg"/>).</summary>
    internal bool CanChain(PlayerbotRoute? route) => route is not null && ReferenceEquals(route, _wanderLeg);

    /// <summary>The longest exploring leg.</summary>
    internal const float ExploreLegYards = 48f;

    /// <summary>
    /// The ray used once a full <see cref="ExploreLegYards"/> leg has been refused.
    /// <see cref="PlayerbotNavigation.PlanWander"/> rejects a navigated leg that stops more than
    /// <see cref="PlayerbotNavigation.WanderMaxShortfallYards"/> short of the point it was given, so this length is the stall
    /// watch's own progress (<see cref="PlayerbotStallWatch.ProgressYards"/>) plus that allowance: a refusal still means the
    /// mesh cannot move the bot far enough to count, and an accepted leg does. A 48-yard ray in a street, room or canyon is
    /// refused in every direction (the mesh stops at the wall), the heading changes, and the bot stands with
    /// <c>moving:no dest:none</c> until the stall watch gives Explore up (live 2026-10-17, the wander-refused stalls).
    /// </summary>
    internal static float ExploreLegLength(int refusals)
        => refusals <= 0
            ? ExploreLegYards
            : PlayerbotStallWatch.ProgressYards + PlayerbotNavigation.WanderMaxShortfallYards;

    /// <summary>
    /// The three +/- turn pairs off the forward heading (degrees) <see cref="DeflectZoneBorderLeg"/> tries along a clipped home's zone border,
    /// starting at pair <c>refusals mod 3</c> and flipping each pair when <c>preferPositive</c> is false.
    /// </summary>
    private static readonly float[] BorderDeflectDegrees = [45f, -45f, 90f, -90f, 135f, -135f];

    /// <summary>
    /// The end of the next wandering leg from <paramref name="from"/>, facing <paramref name="orientation"/>: roughly that facing (within
    /// +-60 degrees, golden-ratio spread so successive legs differ). After any refusal turn anywhere around the compass so the bot turns
    /// away from a cliff or canyon wall immediately instead of probing into the same wall again, and shorten the ray
    /// (<see cref="ExploreLegLength"/>): the full 48-yard ray is what <see cref="PlayerbotNavigation.PlanWander"/> keeps refusing.
    /// Beyond 0.8 of the home radius the next leg
    /// heads back in instead of wandering further out; so does a leg that would end out of a clipped home's zone (a wander across the
    /// Westfall-Duskwood border inside the circle). Back in is the centre first, then, while legs are refused, a fan around it (InwardLeg).
    /// Takes the next direction of the rotation (<c>_roamDirection</c>).
    /// </summary>
    private System.Numerics.Vector3 WanderLegEnd(Player player, System.Numerics.Vector3 from, float orientation)
    {
        int attempt = _roamDirection++;
        float spread = (((attempt + (int)(player.Guid.Low % 7)) * 0.618034f) % 1f) - 0.5f;
        float turnSign = (player.Guid.Low & 1) == 0 ? 1f : -1f;
        float angle = _exploreRefusals >= 1
            ? orientation + (turnSign * _exploreRefusals * 2.39996323f)
            : orientation + (spread * MathF.PI * 2f / 3f);
        // A ray refused for a ring of strong creatures stays full length: a short one fits inside the ring, is taken, and the bot paces
        // there instead of being boxed in and escaping through a gap (PlayerbotRisk.Refused; PlayerbotRingEscapeTests, red since d302bdd4).
        float leg = ExploreLegLength(_exploreRingRefused ? 0 : _exploreRefusals);
        var destination = new System.Numerics.Vector3(
            from.X + (MathF.Cos(angle) * leg),
            from.Y + (MathF.Sin(angle) * leg),
            from.Z);
        if (_home is { } area && (player.Map?.MapId ?? player.MapId) == area.MapId)
        {
            if (area.DistanceTo(from.X, from.Y) > area.ExploreRadius)
            {
                destination = InwardLeg(area, from, _exploreRefusals, player.Map);
            }
            else if (area.ZoneClipped && (!HasOptionalThinkBudget(15.0) || !area.InZone(destination.X, destination.Y, destination.Z)))
            {
                destination = HasOptionalThinkBudget(15.0)
                    ? DeflectZoneBorderLeg(area, from, angle, spread >= 0f, _exploreRefusals, player.Map)
                    : InwardLeg(area, from, _exploreRefusals, player.Map);
            }
        }
        return destination;
    }

    /// <summary>
    /// When a wandering bot stands inside <see cref="PlayerbotHomeArea.ExploreRadius"/> and its forward leg crosses a clipped home's
    /// zone boundary (such as the southern Tirisfal hills behind Cold Hearth Manor), try deflecting along the border first so the bot
    /// continues across the zone rather than snapping its heading straight back toward the level-1 start spawn.
    /// </summary>
    internal static System.Numerics.Vector3 DeflectZoneBorderLeg(PlayerbotHomeArea area, System.Numerics.Vector3 from,
        float baseAngle, bool preferPositive, int refusals, Map? map = null)
    {
        for (int i = 0; i < BorderDeflectDegrees.Length; i++)
        {
            int step = (i + (Math.Max(0, refusals) * 2)) % BorderDeflectDegrees.Length;
            int idx = preferPositive ? step : (step ^ 1);
            float candidateAngle = baseAngle + (BorderDeflectDegrees[idx] * (MathF.PI / 180f));
            float leg = ExploreLegLength(refusals);
            var candidate = new System.Numerics.Vector3(
                from.X + (MathF.Cos(candidateAngle) * leg),
                from.Y + (MathF.Sin(candidateAngle) * leg),
                from.Z);
            if (area.DistanceTo(candidate.X, candidate.Y) <= area.ExploreRadius
                && area.InZone(candidate.X, candidate.Y, candidate.Z))
            {
                return candidate;
            }
        }

        return InwardLeg(area, from, refusals, map);
    }

    /// <summary>
    /// A wandering bot that will reach the end of its leg before its next think (the time to walk the rest is under its smoothed think
    /// gap, with a quarter of it spare) is handed the next leg now, planned from the end of this one and joined to what is left of it
    /// (<see cref="PlayerbotNavigation.TryChain"/>): the motion switches routes without a stop. Before, the bot stood at the end of every
    /// leg until its next think, seconds with a thousand bots. The query is the one the next think would make from the end of the leg
    /// (that think then finds a long route in hand and plans nothing), so the path cost per yard walked is unchanged. A leg that cannot be
    /// chained (no usable route, a tile still being read, a hazard) is left to the next think as before.
    /// </summary>
    private void ChainWanderLeg(Player player, uint elapsedMs)
    {
        if (_route is not { Complete: false } route || !CanChain(route) || ExploreMovementPaused(player, elapsedMs)
            || route.Points.Count < 2 || PlayerbotThinkProbe.Active is { HasOptionalPlanBudget: false }) return;
        if (!PlayerbotMotion.TryTimeToEnd(player, route, out uint remainingMs)) return;
        uint gap = PlayerbotMotion.ThinkGapMs(player);
        if (gap == 0) gap = (uint)_options.ThinkIntervalMs;
        if (remainingMs >= gap + gap / 4) return;
        System.Numerics.Vector3 end = route.Points[^1], before = route.Points[^2];
        float orientation = PlayerbotMotion.Normalize(MathF.Atan2(end.Y - before.Y, end.X - before.X));
        int roam = _roamDirection;
        PlayerbotThinkProbe.Phase("explore chain plan");
        if (!PlayerbotNavigation.TryChain(player, route, WanderLegEnd(player, end, orientation), _options, out PlayerbotRoute? chained)
            || chained is null)
        {
            _roamDirection = roam; // the same leg again at the next think
            return;
        }

        _route = chained;
        _wanderLeg = chained;
        // The switch is refused (the motion keeps what it has: keepMotion): walk the leg in hand, plan the next one at the next think.
        if (!PlayerbotNavigation.TryAdvance(_session, chained, _options, elapsedMs, _session.World.NowMs, keepMotion: true))
        {
            _route = route;
            _wanderLeg = route;
        }
    }

    /// <summary>How far (flat yards) the bot must have moved off the confinement's start before EscapeIsland also judges from there.</summary>
    private const float ConfinedFromMinYards = 5f;

    private bool EscapeConfinedExplore(Player player)
    {
        if (player.Map is null || !player.IsAlive || player.Combat.IsInCombat) return false;
        uint now = _session.World.NowMs;
        var at = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        if (!_exploreIslandWatchStarted || _exploreIslandMap != player.MapId
            || System.Numerics.Vector2.DistanceSquared(new(at.X, at.Y),
                new(_exploreIslandAnchor.X, _exploreIslandAnchor.Y)) > PlayerbotNavigation.IslandYards * PlayerbotNavigation.IslandYards)
        {
            _exploreIslandWatchStarted = true;
            _exploreIslandAnchor = at;
            _exploreIslandMap = player.MapId;
            _exploreIslandSince = now;
            _earlyExploreIslandChecked = false;
        }

        // Short valid routes on a small island can move a bot more than the stall watch's 10-yard
        // progress bound while keeping it trapped. Diagnose once when exploration stays confined.
        bool onNullIsland = PlayerbotHomeArea.IsNullIsland(player.MapId, player.X, player.Y);
        if (_earlyExploreIslandChecked || (!onNullIsland && unchecked(now - _exploreIslandSince) < 15_000u)) return false;
        if (player.Map is not { InstanceId: 0 }) return false;
        // The check's path searches (up to IslandDirections, counted as the think's fallbacks) are this think's work: the caller plans
        // no route of its own after it.
        if (!EscapeIsland(player, out bool onIsland, confinedFrom: _exploreIslandAnchor) && onIsland)
        {
            // On an island but not moved (the home teleport is on cooldown, say): ask again in 15 seconds.
            _exploreIslandSince = now;
            return true;
        }

        _earlyExploreIslandChecked = true;
        return true;
    }

    private void BlockExploreCell(PlayerbotExploreAtlas.Candidate candidate)
    {
        // A cell has no visible killable target, or its route was refused. Let respawns and path changes make it eligible again.
        var key = (candidate.Spawn.MapId, candidate.CellX, candidate.CellY);
        if (_exploreBlockedCells.Count >= 64 && !_exploreBlockedCells.ContainsKey(key))
        {
            (uint Map, int X, int Y) oldest = default;
            foreach (var entry in _exploreBlockedCells) { oldest = entry.Key; break; }
            _exploreBlockedCells.Remove(oldest);
        }
        _exploreBlockedCells[key] = unchecked(_session.World.NowMs + 180_000u);
        _exploreCandidate = null;
        _exploreBestDistSq = float.PositiveInfinity;
        TargetEntry = 0;
    }

    private float _exploreBestDistSq = float.PositiveInfinity;

    private bool ExploreMovementPaused(Player player, uint elapsedMs)
        => elapsedMs == 0 || _session.ManagedBudget is { Remaining: <= 0 }
            || (player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0
            || PlayerbotMotion.ControlLost(player); // a pause: no refusal, no abandoned route, no blocked cell

    private bool TryExploreSpawns(Player player, uint elapsedMs)
    {
        // Explore is only entered after _destinations.Update had no unvisited/reachable spawn for PreferredCreatureEntry;
        // use the explore atlas to walk toward nearby level-appropriate spawns while the quest camp waits to respawn,
        // instead of falling into blind 48-yard wander rays against surrounding hills/walls.
        if (player.Map is not { } map) return false;
        uint mapId = map.MapId;
        if (!_options.AllowedMaps.Contains(mapId)) return false;
        var creatureFeature = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>();
        var atlas = creatureFeature?.ExploreAtlas;
        var content = creatureFeature?.Content;
        if (atlas is null || content is null) return false;
        uint now = _session.World.NowMs;
        _expiredExploreCells.Clear();
        foreach (var blocked in _exploreBlockedCells)
            if (unchecked((int)(blocked.Value - now)) <= 0) _expiredExploreCells.Add(blocked.Key);
        foreach (var key in _expiredExploreCells) _exploreBlockedCells.Remove(key);
        _expiredExploreCells.Clear();

        if (_exploreCandidate is { } current)
        {
            float dx = current.Spawn.X - player.X, dy = current.Spawn.Y - player.Y;
            float distSq = (dx * dx) + (dy * dy);
            if (current.Spawn.MapId != mapId || distSq <= 65f * 65f)
            {
                // Within sight but FindTargets found nothing to fight: this patch is depleted, gated, or unavailable.
                BlockExploreCell(current);
            }
            else if (_route is { Complete: true, ToLiftLanding: true })
            {
                // A leg of the walk to a lift's landing ends where one route reaches, which may be farther from the cell (the lift can lie away
                // from it) and is no dead end: each accepted leg closes at least PlayerbotElevator.ApproachProgressYards on the lift.
            }
            else if (_route is { Complete: true })
            {
                // A previous route leg finished outside the 65-yard sight radius: if it failed to reduce flat distance by at least 4 yards,
                // the partial route reached a terrain dead end (e.g., Coldridge ridge above cave #570) and cannot get closer.
                float prevBest = float.IsFinite(_exploreBestDistSq) ? MathF.Sqrt(_exploreBestDistSq) : float.PositiveInfinity;
                float curDist = MathF.Sqrt(distSq);
                if (curDist + 4f >= prevBest)
                {
                    BlockExploreCell(current);
                }
                else
                {
                    _exploreBestDistSq = distSq;
                }
            }
        }

        if (_exploreCandidate is null)
        {
            if (unchecked((int)(_exploreSearchAfter - now)) > 0) return false;
            FactionTemplateCatalog? factions = _session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.FactionTemplates;
            if (factions is not { Count: > 0 }) factions = _session.Services.GetService<FactionTemplateCatalog>();
            if (factions is not { Count: > 0 })
            {
                CombatHooks hooks = CombatHooks.For(_session.World);
                while (hooks is ArcaneCore.Game.Reputation.ReputationCombatHooks reputation) hooks = reputation.Fallback;
                factions = (hooks as FactionCombatHooks)?.Factions;
            }
            FactionTemplateRecord? self = factions?.Find(player.FactionTemplate);
            if (self is null) return false;
            _exploreSearchPlayer = player;
            _exploreSearchSkipIndoor = ExploreSkipsIndoor(player);
            _exploreSearchContent = content;
            _exploreSearchAtlas = atlas;
            _exploreSearchFactions = factions;
            _exploreSearchSelf = self;
            _exploreSearchReactions = _session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.Services.Deps.Reputation
                as ArcaneCore.Game.Reputation.INpcReactionSource;
            _exploreSearchHome = _home is { MapId: var homeMap } && homeMap == mapId ? _home : null;
            _exploreSearchHazards = _options.Risk.Enabled ? Risk.Hazards.Active(mapId, now) : [];
            _exploreSearchMap = mapId;
            _exploreEligibleFilter ??= ExploreEligible;
            _exploreFactionFilter ??= ExploreAttackableFaction;
            _exploreThreatFilter ??= ExploreHostileFaction;
            _exploreBlockedFilter ??= ExploreBlocked;
            _exploreSearchStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _exploreCandidateChecks = 0;
            PlayerbotThinkProbe.Phase("explore atlas find");
            _exploreCandidate = atlas.Find(mapId, player.Level, new(player.X, player.Y, player.Z), _exploreEligibleFilter,
                _exploreBlockedFilter, out _, _exploreFactionFilter);
            if (_exploreCandidate is null)
            {
                _exploreSearchAfter = unchecked(now + 5_000u);
                return false;
            }
            float initDx = _exploreCandidate.Value.Spawn.X - player.X, initDy = _exploreCandidate.Value.Spawn.Y - player.Y;
            _exploreBestDistSq = (initDx * initDx) + (initDy * initDy);
        }

        PlayerbotExploreAtlas.Candidate chosen = _exploreCandidate.Value;
        PlayerbotThinkProbe.Phase("explore plan");
        if (ExploreMovementPaused(player, elapsedMs)) return true;
        var destination = new System.Numerics.Vector3(chosen.Spawn.X, chosen.Spawn.Y, chosen.Spawn.Z);
        PlayerbotRoute? walk = null;
        if (PlayerbotWorldDestinations.TransitAllowed(_home, player.Level, _options.Population)
            && PlayerbotElevator.TryBegin(_session, player, destination, _options, out walk))
        {
            AbandonRoute();
            _goal = PlayerbotGoalKind.Explore;
            TargetEntry = chosen.Spawn.Entry;
            return true;
        }
        PlanOutcome atlasPlan = walk is not null
            ? PlanOutcome.Route
            : PlayerbotNavigation.PlanToward(player, destination, _options, ref _tileWait, now, out walk);
        _route = walk;
        if (atlasPlan == PlanOutcome.Pending) return true;
        float flatDistSq = (destination.X - player.X) * (destination.X - player.X) + (destination.Y - player.Y) * (destination.Y - player.Y);
        if (atlasPlan == PlanOutcome.Refused
            || _route is not { Points.Count: > 0 } plannedRoute
            || (System.Numerics.Vector3.DistanceSquared(new(player.X, player.Y, player.Z), plannedRoute.Points[^1]) <= 4f * 4f
                && flatDistSq > 65f * 65f)
            || (!plannedRoute.ToLiftLanding && plannedRoute.Navigated && flatDistSq <= 175f * 175f
                && (System.Numerics.Vector2.DistanceSquared(new(destination.X, destination.Y), new(plannedRoute.Points[^1].X, plannedRoute.Points[^1].Y)) > 15f * 15f
                    || MathF.Abs(destination.Z - plannedRoute.Points[^1].Z) > 8f)))
        {
            BlockExploreCell(chosen);
            AbandonRoute();
            PlayerbotMovementControl.Stop(_session, player);
            // The refused cell is blocked and the next think tries another, but this think walks a short wander leg (taken even when the
            // optional path budget is spent). Returning true here left the bot standing: one refused cell per think, a think every ~13 s,
            // until the stall watch gave Explore up (live 2026-10-17 on the 20:08 pack: ~140 Explore stalls/h, phase explore-plan, moving:no).
            return false;
        }
        _goal = PlayerbotGoalKind.Explore;
        TargetEntry = chosen.Spawn.Entry;
        PlayerbotThinkProbe.Phase("explore advance");
        if (!PlayerbotNavigation.TryAdvance(_session, plannedRoute, _options, elapsedMs, now))
        {
            if (!plannedRoute.Complete) BlockExploreCell(chosen);
            AbandonRoute();
        }
        return true;
    }

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>), shared by the plans this brain makes.</summary>
    private TileWait _tileWait;

    private bool ExploreBlocked(int x, int y)
        => _exploreBlockedCells.ContainsKey((_exploreSearchMap, x, y));

    private bool ExploreAttackableFaction(uint faction)
    {
        FactionTemplateRecord? npc = _exploreSearchFactions?.Find(faction);
        FactionTemplateRecord? self = _exploreSearchSelf;
        if (npc is null || self is null) return false;
        return ExploreAttackable(_exploreSearchPlayer, npc, self, _exploreSearchReactions);
    }

    /// <summary>
    /// Whether the bot may attack a creature of faction template <paramref name="npc"/>: its reputation reaction
    /// (<see cref="ArcaneCore.Game.Reputation.INpcReactionSource"/>, as <see cref="PlayerbotTrainerDestinations.CanApproachTrainer"/> and
    /// <see cref="PlayerbotQuestGoals.IsKillable"/> judge it) is below Friendly. The template alone says nothing of standing: Timbermaw
    /// Hold's creatures read as a hostile faction to the bot's template, but a bot with Timbermaw reputation is not their enemy (issues
    /// #1478 and #1479). Without a reaction source, or when it cannot resolve this pair, the templates decide: hostility wins over the
    /// friendly bits in each direction (<c>FactionCombatHooks.ReactionTo</c>), and a player may attack a neutral creature, but CanAttack
    /// refuses when either direction's resulting reaction is Friendly.
    /// </summary>
    internal static bool ExploreAttackable(Player? player, FactionTemplateRecord npc, FactionTemplateRecord self,
        ArcaneCore.Game.Reputation.INpcReactionSource? reactions)
    {
        if (reactions is not null && player is not null
            && reactions.TryGetNpcReaction(player, npc, self, out ArcaneCore.Game.Reputation.ReputationRank reaction))
            return reaction < ArcaneCore.Game.Reputation.ReputationRank.Friendly;
        bool friendly = self.IsFriendlyTo(npc) || npc.IsFriendlyTo(self);
        return !friendly || self.IsHostileTo(npc) && npc.IsHostileTo(self);
    }

    /// <summary>
    /// Whether a creature with <paramref name="npcFlags"/> offers a service (gossip, quests or a vendor's wares): not a target for an
    /// exploring bot, which walks to creatures to fight. The Timbermaw Hold vendors and quest givers drew bots into the tunnel
    /// (issues #1478 and #1479).
    /// </summary>
    internal static bool IsExploreServiceNpc(uint npcFlags)
        => (npcFlags & (uint)(ArcaneCore.Game.Npc.NpcFlags.Gossip | ArcaneCore.Game.Npc.NpcFlags.QuestGiver | ArcaneCore.Game.Npc.NpcFlags.Vendor)) != 0;

    private bool ExploreHostileFaction(uint faction)
        => _exploreSearchFactions?.Find(faction) is { } npc && _exploreSearchSelf is { } self && npc.IsHostileTo(self);

    /// <summary>
    /// Whether an explore candidate at <paramref name="point"/> is worth walking to: not in a dead-end pin of this bot or of the world, and not
    /// indoors or underground when the bot stands outdoors (live 2026-10-25: spawns in a sealed Deadwind Pass crypt sent bots on searches that
    /// ended at the node cap). Exploring is optional, so such spawns are simply left out. A dungeon or battleground is all indoors and is not judged.
    /// </summary>
    internal static bool ExploreSpotUsable(Player player, System.Numerics.Vector3 point, bool skipIndoor)
    {
        if (PlayerbotNavigation.IsDeadEndPin(player, point) || PlayerbotNavigation.IsWorldPin(player, point)) return false;
        return !skipIndoor || player.Map is not { } map || PlayerbotNavigation.IsOutdoorsOnSurface(map, point);
    }

    /// <summary>Whether this bot's explore leaves out indoor spots: it stands outdoors on the surface of a map that is not a dungeon or battleground. Judged once per search.</summary>
    internal static bool ExploreSkipsIndoor(Player player)
        => player.Map is { } map && map.Template is not ({ IsDungeon: true } or { IsBattleground: true })
            && PlayerbotNavigation.IsOutdoorsOnSurface(map, PlayerbotMotion.CurrentPosition(player));

    private bool ExploreEligible(PlayerbotExploreAtlas.Candidate candidate)
    {
        if (_exploreSearchPlayer is not { } player
            || _exploreSearchContent is not { } content
            || _exploreSearchAtlas is not { } atlas
            || _exploreThreatFilter is not { } threatFilter)
            return false;
        if (++_exploreCandidateChecks > 24
            && (!HasOptionalThinkBudget(15.0) || System.Diagnostics.Stopwatch.GetElapsedTime(_exploreSearchStartedAt).TotalMilliseconds >= 12.0))
            return false;
        CreatureSpawn spawn = candidate.Spawn;
        uint now = _session.World.NowMs;
        if (_suspensions.IsEntrySuspended(spawn.Entry, now) || _destinations.IsVisited(spawn.Guid, now))
            return false;
        CreatureTemplate? template = content.FindTemplate(spawn.Entry);
        if (template is null || IsExploreServiceNpc(template.NpcFlags) || !PlayerbotWorldDestinations.IsPresent(player, spawn)
            || _exploreSearchHome is { } home && home.DistanceTo(spawn.X, spawn.Y) > home.ExploreRadius)
            return false;
        var point = new System.Numerics.Vector3(spawn.X, spawn.Y, spawn.Z);
        foreach (PlayerbotHazard hazard in _exploreSearchHazards)
            if (hazard.Covers(point)) return false;
        if (!ExploreSpotUsable(player, point, _exploreSearchSkipIndoor))
            return false;
        if (atlas.HasStrongHazard(candidate, player.Level, threatFilter))
            return false;
        if (_exploreSearchHome is { } clippedHome && !clippedHome.Contains(spawn))
            return false;
        return !PlayerbotNavigation.IsUnreachableOnFoot(player, point, out _, out _);
    }

    /// <summary>
    /// The turns from the straight way to the home's centre that an exploring bot heading back in tries, one more per refused leg
    /// (<see cref="InwardLeg"/>). Before, every leg there aimed straight at the centre, the refusals' turn and the stall watch's
    /// give-up included, so one refused leg was refused on every think. Live 2026-10-11 (bot QA issue #154, 200 bots homed at Sentinel
    /// Hill, radius 550): 74 of the run's 204 stalls lay 460 to 480 yards from the centre, just past the 440-yard explore radius;
    /// replayed from the live characters database on the real terrain, the bots stood there with that one leg on the motion's loop
    /// blacklist (<see cref="PlayerbotMotion.LoopBlacklistMs"/>), refused hundreds of times a minute.
    /// </summary>
    internal static readonly float[] InwardFanDegrees = [0f, 25f, -25f, 50f, -50f, 75f, -75f];

    /// <summary>
    /// The end of an exploring bot's leg back in after <paramref name="refusals"/> refused legs: the first turn of
    /// <see cref="InwardFanDegrees"/> from that one on whose leg ends strictly closer to the centre than the bot stands and, for a
    /// clipped home, in its zone. A leg turned θ off the centre is at most the bot's distance × cos θ long (the point of that line
    /// nearest the centre), so it never ends farther out: a full 48-yard leg 75 degrees off from 55 yards out would end 62.9 yards
    /// out, past a 60-yard radius. When no turn qualifies (the bot at the centre, every turn out of the zone), the straight leg
    /// towards the centre. A refusal shortens the ray (<see cref="ExploreLegLength"/>), the same as a wander that the mesh cannot
    /// finish at 48 yards.
    /// </summary>
    internal static System.Numerics.Vector3 InwardLeg(PlayerbotHomeArea home, Player player, int refusals)
    {
        var from = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        return InwardLeg(home, from, refusals, player.Map);
    }

    /// <summary>
    /// <see cref="InwardLeg(PlayerbotHomeArea, Player, int)"/> from <paramref name="from"/> (the end of the leg walked now, for a chained
    /// leg), its end put on the floor nearest <paramref name="from"/>'s height on <paramref name="map"/>
    /// (<see cref="PlayerbotNavigation.GroundNear(Map, System.Numerics.Vector3, System.Numerics.Vector3)"/>) when there is one.
    /// </summary>
    internal static System.Numerics.Vector3 InwardLeg(PlayerbotHomeArea home, System.Numerics.Vector3 from, int refusals, Map? map)
    {
        Func<System.Numerics.Vector3, System.Numerics.Vector3>? ground = null;
        if (map is not null) ground = new PlayerbotNavigation.FloorProbe(map, from).Near;
        return InwardLeg(home, from, refusals, ground);
    }

    /// <summary>
    /// <see cref="InwardLeg(PlayerbotHomeArea, System.Numerics.Vector3, int, Map?)"/> with the grounding given: <paramref name="ground"/>
    /// maps the chosen end (at <paramref name="from"/>'s height) to the point on the floor, or null to leave it at that height. The
    /// legs ended at the bot's own height, which in a cave or tunnel is a point in rock on no region (issues #1478 and #1479).
    /// </summary>
    internal static System.Numerics.Vector3 InwardLeg(PlayerbotHomeArea home, System.Numerics.Vector3 from, int refusals,
        Func<System.Numerics.Vector3, System.Numerics.Vector3>? ground)
    {
        float distance = home.DistanceTo(from.X, from.Y);
        float inward = MathF.Atan2(home.Centre.Y - from.Y, home.Centre.X - from.X);
        float budget = ExploreLegLength(refusals);
        for (int turn = 0; turn < InwardFanDegrees.Length; turn++)
        {
            float degrees = InwardFanDegrees[(int)((uint)(refusals + turn) % (uint)InwardFanDegrees.Length)];
            float length = MathF.Min(budget, distance * MathF.Cos(degrees * MathF.PI / 180f));
            if (length < 1f) continue;
            float angle = inward + (degrees * MathF.PI / 180f);
            var end = new System.Numerics.Vector3(from.X + (MathF.Cos(angle) * length), from.Y + (MathF.Sin(angle) * length), from.Z);
            if (home.DistanceTo(end.X, end.Y) >= distance) continue;
            if (home.ZoneClipped && !home.InZone(end.X, end.Y, end.Z)) continue;
            return ground is null ? end : ground(end);
        }

        float straight = MathF.Min(budget, distance);
        var last = new System.Numerics.Vector3(from.X + (MathF.Cos(inward) * straight), from.Y + (MathF.Sin(inward) * straight), from.Z);
        return ground is null ? last : ground(last);
    }

    /// <summary><see cref="InwardLeg(PlayerbotHomeArea, System.Numerics.Vector3, int, Map?)"/> without a map: the end stays at <paramref name="from"/>'s height.</summary>
    internal static System.Numerics.Vector3 InwardLeg(PlayerbotHomeArea home, System.Numerics.Vector3 from, int refusals)
        => InwardLeg(home, from, refusals, (Func<System.Numerics.Vector3, System.Numerics.Vector3>?)null);

}

/// <summary>Where a bot fights from this decision (<see cref="PlayerbotBrain.DecidePosition"/>).</summary>
internal enum PlayerbotFightPosition : byte
{
    /// <summary>At its range: stand and cast.</summary>
    Hold,

    /// <summary>Beyond its range: close in to it.</summary>
    ChaseToRange,

    /// <summary>A melee fight out of reach: close in to melee.</summary>
    ChaseToMelee,

    /// <summary>In reach of a melee fight: swing.</summary>
    Melee,
}
