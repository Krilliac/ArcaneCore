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
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A deterministic, world-thread autonomous player. The feature/host owns scheduling and
/// persistence; this controller owns only bounded ordinary gameplay decisions for one session.
/// </summary>
internal sealed class PlayerbotBrain(WorldSession session, PlayerbotOptions options,
    PlayerbotLocalPlanner? planner = null, CancellationToken lifetime = default, PlayerbotSharedSetAsides? shared = null,
    PlayerbotClaims? claims = null)
{
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly PlayerbotContention _contention = new(options, claims);
    private PlayerbotRoute? _route;
    private bool _routeAbandoned;

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
    private CreatureContent? _exploreSearchContent;
    private PlayerbotExploreAtlas? _exploreSearchAtlas;
    private FactionTemplateCatalog? _exploreSearchFactions;
    private FactionTemplateRecord? _exploreSearchSelf;
    private PlayerbotHomeArea? _exploreSearchHome;
    private IReadOnlyList<PlayerbotHazard> _exploreSearchHazards = [];
    private uint _exploreSearchMap;
    private Func<PlayerbotExploreAtlas.Candidate, bool>? _exploreEligibleFilter;
    private Func<uint, bool>? _exploreFactionFilter;
    private Func<uint, bool>? _exploreThreatFilter;
    private Func<int, int, bool>? _exploreBlockedFilter;
    private uint _exploreSearchAfter;
    private PlayerbotGoalKind _goal = PlayerbotGoalKind.Explore;
    private long _lootStartedMs;
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _lootActions = new();
    private bool _lootResponseReceived;
    private readonly PlayerbotQuestGoals _quests = new(session, options);
    private readonly PlayerbotTownGoals _town = new(session, options);
    private readonly PlayerbotEconomy _economy = new(session, options);
    private readonly PlayerbotBudget _budget = new(session, options);
    private readonly PlayerbotRidingTrips _riding = new(session, options);
    private readonly PlayerbotTrainerDestinations _trainers = new(session, options);
    private readonly PlayerbotWorldDestinations _destinations = new(session, options);
    private readonly PlayerbotCombatSpells _combatSpells = new(session);
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
            _quests.Home = value;
            _destinations.Home = value;
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

    /// <summary>The risk line BOTINSPECT and <c>.playerbot status</c> show (<see cref="PlayerbotRisk.Report"/>).</summary>
    internal string RiskReport => _options.Risk.Enabled ? Risk.Report : "decision=off";

    /// <summary>What the bot saves for (<see cref="PlayerbotBudget"/>; inspection and tests).</summary>
    internal PlayerbotBudget Budget => _budget;

    /// <summary>The budget line BOTINSPECT shows (<see cref="PlayerbotBudget.Report"/>).</summary>
    internal string BudgetReport(Player player) => _budget.Report(player);

    /// <summary>The ghost recovery (inspection and tests).</summary>
    internal PlayerbotRecovery Recovery => _recovery;
    private readonly PlayerbotEquipment _equipment = new(session);
    private readonly PlayerbotStallWatch _stall = new();
    private readonly PlayerbotSuspensions _suspensions = new() { Shared = shared };

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
    private readonly CancellationTokenSource _planningStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task<string?>? _planTask;
    private string? _modelChoice;
    private long _nextPlanMs;
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
            if (world.IsWorldThread) _contention.ReleaseClaim();
            else _ = world.InvokeAsync(() => { _contention.ReleaseClaim(); return true; });
        }
        _planningStop.Cancel();
        _planningStop.Dispose();
    }

    internal PlayerbotGoalKind Goal => _goal;
    internal Creature? InspectionTarget => _target;
    internal uint TargetEntry { get; private set; }
    internal uint QuestId { get; private set; }

    /// <summary>The current stall (<see cref="PlayerbotStallWatch"/>), or null while the bot makes progress.</summary>
    internal string? StallReport => _stall.Report;

    /// <summary>The last stall reported, kept after progress resumed.</summary>
    internal string? LastStall => _stall.LastReport;

    /// <summary>Stalls reported so far.</summary>
    internal int StallCount => _stall.Count;

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
        var observation = new QaBotObservation(player.Level, player.Race.ToString(), player.Team.ToString(), player.Class.ToString(),
            death ? _lastQaDeathGoal : GoalFromReport(report) ?? _goal.ToString(),
            death ? _lastQaDeathQuest : QuestFromReport(report) ?? QuestId,
            graveyard?.Id ?? 0, distance, death ? _lastQaDeathSource : "None",
            death ? [.. _recentDeathAttackerFacts.Values.Where(f => entries.Contains(f.Entry)).OrderBy(f => f.Entry)] : [],
            death ? [.. _recentDeathSources.Where(d => d.Map == _lastQaDeathMap
                && System.Numerics.Vector3.Distance(d.Position, _lastQaDeathPosition) <= PlayerbotStallWatch.DeathLoopYards)
                .Select(d => d.Source)] : null);
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
        _stall.Reset();
        _homeReturn.Reset();
        _goal = PlayerbotGoalKind.Explore;
    }

    /// <summary>Whether goals skip creatures of <paramref name="entry"/> for now (a stall or a death loop set it aside; tests).</summary>
    internal bool IsEntrySetAside(uint entry) => _suspensions.IsEntrySuspended(entry, _session.World.NowMs);

    /// <summary>How this bot shares mobs with the others: its claim, the mobs it left alone, the help it gives (inspection and tests).</summary>
    internal PlayerbotContention Contention => _contention;

    /// <summary>The bot's spell helper (tests).</summary>
    internal PlayerbotCombatSpells CombatSpells => _combatSpells;

    /// <summary>The fleet's soft claims on untagged mobs (<see cref="PlayerbotClaims"/>; inspection and tests).</summary>
    internal PlayerbotClaims Claims => _contention.Claims;

    internal void Update(uint elapsedMs)
    {
        if (!_options.Enabled || _stopped != 0 || _session.Player is not { } player)
            return;
        _destinations.SkipQuest ??= _quests.IsRefused;
        _destinations.Untakeable ??= IsUntakeable;
        _destinations.GiverHasCandidate ??= _quests.HasCandidate;
        _destinations.SetQuestAside ??= _quests.SetAside;
        _destinations.TeleportToBind ??= TeleportToBindPointForQuest;
        _quests.Suspensions = _town.Suspensions = _trainers.Suspensions = _destinations.Suspensions = _suspensions;
        _town.Budget = _trainers.Budget = _riding.Budget = _budget; _economy.SpendBudget = _budget;
        _riding.Suspensions = _suspensions;
        _quests.Contention ??= _contention;
        _quests.OnContentionExhausted ??= RaiseContentionExhausted;
        _quests.HeldForGroup ??= (quest, entry) => GroupHeld.Contains((quest, entry)) || GroupHeld.Contains((0, entry));
        if (!player.IsInWorld) return;
        PlayerbotThinkProbe.Phase("brain");
        WatchKills(player);
        SyncHomeFilters(player);
        _recovery.UnsafeBody ??= JudgeBody;
        _recovery.LandingUnsafe ??= (bot, at) => Risk.TooStrongAt(bot, at) is not null;
        _recovery.EscapeLanding ??= EscapeLanding;

        bool dead = !player.IsAlive;
        if (dead)
        {
            _waterRoute = null;
            _waterRetryAt = 0;
            _stall.Reset();
            // The walk home planned from where the bot fell: after the resurrection (maybe far away) it plans afresh.
            _homeReturn.Reset();
            if (!_deathIntentRetired)
            {
                RecordDeath(player);
                RetireDeathIntent();
                // A death ends a tram trip (its stage, car and entrance consent belong to the living bot): the ghost's corpse run may
                // walk the tram map to a body there (PlayerbotTram.AllowsWalkingOn), and a bot revived there takes the nearest exit.
                PlayerbotTram.Cancel(player);
                // A taxi trip that has not left yet is dropped with its walker; a flight in the air is the server's and goes on.
                PlayerbotTaxi.Cancel(player);
                // And a lift ride: the corpse loses the transport. This must run BEFORE the acknowledgements below
                // (PlayerbotMovementControl.Update): the death root's ack then carries the cleared block, relayed as MSG_MOVE_ROOT.
                PlayerbotElevator.Cancel(player);
                _deathIntentRetired = true;
            }
            _goal = PlayerbotGoalKind.Recover;
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
        // bot would follow its underwater route until the breath timer killed it (bot QA #271).
        if (!dead && PlayerbotWater.Submerged(player) && !player.Combat.IsInCombat && PlayerbotWater.BreathRunningOut(player))
        {
            EscapeWater(player, elapsedMs);
            return;
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
                && PlayerbotConsumables.HasActiveFoodDrink(player, restingSpells.System);
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

        _thinkElapsed = checked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs)
            return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;

        // A tram trip (or a bot found on the Deeprun Tram map) owns the bot until it stands on the continent again (PlayerbotTram): nothing
        // else moves it meanwhile, and the stall watch does not judge a bot waiting for its car. A fight on the way to the entrance ends the
        // trip (the goal that began it begins it again afterwards).
        if (player.IsAlive)
        {
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

            if (taxi is TaxiStep.Arrived or TaxiStep.Failed)
            {
                _stall.Reset();
                if (taxi == TaxiStep.Failed)
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
                _stall.ReportExternal(tramFailure ?? "tram: the trip failed (no reason recorded)");
                GiveUpStalledGoal(player, shareable: false);
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
                _stall.ReportExternal(liftFailure ?? "elevator: the ride failed (no reason recorded)");
                GiveUpStalledGoal(player, shareable: false);
                return;
            }
        }

        // A flight master within reach whose node the bot does not know yet teaches it (CMSG_TAXIQUERYAVAILABLENODES, PlayerbotTaxi).
        if (player.IsAlive) PlayerbotTaxi.Discover(_session, player, _options);

        PlayerbotThinkProbe.Phase("stall watch");
        if (player.IsAlive && _stall.Observe(player, _goal, TargetEntry, QuestId, _session.World.NowMs,
                (uint)Math.Clamp(_options.StallSeconds, 10, 86_400) * 1000u, Fingerprint))
            GiveUpStalledGoal(player);

        if (PlayerbotMotion.ConsumeLoop(player))
        {
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
            PlayerbotThinkProbe.Phase("recovery update");
            _recovery.Update(player, interval);
            return;
        }

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

        if (_lootOpened && _target is { } pendingLoot)
        {
            if (!PlayerbotMovementControl.Stop(_session, player)) return;
            if (_lootResponseReceived || _session.World.Uptime.TotalMilliseconds - _lootStartedMs >= 10_000)
            {
                if (!_lootResponseReceived) _session.TryManagedAction(WorldOpcode.CmsgLootRelease,
                    PlayerbotNavigation.GuidPayload(pendingLoot.Guid.Value));
                _lootOpened = false;
                _lootResponseReceived = false;
                _target = null;
                AbandonRoute();
                _attacking = false;
                Rest(player);
            }
            else _lootResponseReceived = LootManagedPackets(pendingLoot.Guid.Value);
            return;
        }

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
            if (completedReturn != 0) TargetEntry = 0;
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
            if (!player.Combat.IsInCombat && _quests.Update(player, interval))
            { QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            _target = null;
            AbandonRoute();
            PlayerbotThinkProbe.Phase("quest destinations");
            // The trip's target is the ender the destinations chose for this quest, or none yet: never the entry of an earlier goal. A
            // failed plan leaves both TargetEntry values as they were, and the stall watch then reported, and GiveUpStalledGoal set aside,
            // a creature this trip has nothing to do with (GitHub #186: quest 1111's return stalled with target 4147, a Saltstone Basilisk
            // the bot had been fighting, and 11259, the ender of quest 5361 it had been walking to before).
            _destinations.Update(player, 0, interval, completedReturn);
            TargetEntry = _destinations.QuestId == completedReturn ? _destinations.TargetEntry : 0u;
            // Wait/retry at the real giver while combat or a normal interaction
            // refusal remains. A completed quest must not reopen opportunistic grind.
            return;
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
            PlayerbotMovementControl.Stop(_session, player);
            // Combat linger after an ordinary attack stop must not reopen an optional
            // grind target. Let the authoritative combat timer/handler clear the state.
            _target = null;
            AbandonRoute();
            _attacking = false;
            _lootOpened = false;
            TargetEntry = 0;
            _goal = PlayerbotGoalKind.Combat;
            return;
        }

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
            PlayerbotThinkProbe.Phase("local plan");
            UpdateLocalPlan(player);
            PlayerbotThinkProbe.Phase("home");
            if (_home is { } home && TryReturnHome(player, home, interval)) return;
            PlayerbotThinkProbe.Phase("town");
            _town.BeginThink();
            // A returned ID is only a proposal; each controller rechecks the live gameplay rules.
            if (_modelChoice == "town" && _town.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("quests");
            if (_quests.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _quests.Goal; QuestId = _quests.QuestId; TargetEntry = _quests.TargetEntry; return; }
            QuestId = _quests.QuestId;
            PlayerbotThinkProbe.Phase("town");
            if (_town.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _town.Goal; TargetEntry = _town.TargetEntry; return; }
            // Under resurrection sickness no new fight: the quest exchanges and town errands above (a repair among them) still run.
            if (StandDownWhileSick(player)) return;
            PlayerbotThinkProbe.Phase("economy");
            if (_economy.Update(player, interval))
            { RetireIdleCombatRoute(); _goal = _economy.Goal; TargetEntry = _economy.TargetEntry; return; }
            PlayerbotThinkProbe.Phase("trainers");
            if (_quests.PreferredCreatureEntry == 0 && _trainers.HasCandidate(player))
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
            if (_quests.PreferredCreatureEntry == 0 && _riding.HasCandidate(player))
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
                && _destinations.HasQuestCandidate(player))
            {
                // Available quest travel precedes optional idle grinding. Actual combat,
                // visible interactions, completed returns, supplies and trainers keep their
                // existing priority; path failure still permits the ordinary fallback.
                if (spellFeature?.System.GetState(player.Guid)?.CurrentCast is
                    { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }) return;
                if (_session.ManagedBudget is { Remaining: <= 0 }) return;
                if (_destinations.Update(player, 0, interval))
                {
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
            if (_modelChoice == "explore") { _target = null; PlayerbotThinkProbe.Phase("explore"); Explore(player, interval); return; }
        }

        // A mob another player tagged while the bot walked to it is no longer the bot's to take: help its tagger or leave it alone, then
        // choose afresh in this same think (docs/areas/playbots.md, Contention).
        if (_target is { IsAlive: true } held && ReferenceEquals(held.Map, player.Map)) DropIfSomeoneElses(player, held);

        if (_target is null || _target.Map != player.Map)
        {
            PlayerbotThinkProbe.Phase("choose target");
            Creature? previousTarget = _target;
            uint preferred = _quests.PreferredCreatureEntry;
            _contention.ReleaseClaim();
            _contention.EndAssist();
            _contention.BlockedSeen = 0;
            _target = _options.Risk.Enabled
                ? Risk.ChooseTarget(player, preferred, IsSkipped, questObjective: preferred != 0, _contention)
                : FindTarget(player, preferred, IsSkipped, _contention);
            if (_target is not null) _contention.Claim(player.Guid.Value, _target, _session.World.NowMs);
            else if (preferred != 0 && _contention.BlockedSeen > 0 && !AnyFreeOfEntry(player, preferred)) _contention.NoteContested(preferred, _session.World.NowMs);
            _rangedIdleMs = 0;
            // Keep an exploration route across decisions; otherwise every thought changes
            // direction after only its first terrain step and the player never travels.
            if (_target is not null || previousTarget is not null) AbandonRoute();
            _attacking = false;
            _lootOpened = false;
            if (_target is null)
            {
                PlayerbotThinkProbe.Phase("grind destinations");
                if (_destinations.Update(player, _quests.PreferredCreatureEntry, interval))
                {
                    AbandonRoute();
                    _goal = _quests.PreferredCreatureEntry != 0 ? PlayerbotGoalKind.Grind : PlayerbotGoalKind.Quest;
                    TargetEntry = _destinations.TargetEntry;
                    if (_destinations.QuestId != 0) QuestId = _destinations.QuestId;
                    return;
                }
                if (_destinations.WaitingForTiles) return; // the route's tiles are being read: keep the goal, plan again next think
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

            if (_combatSpells.Update(player, target, interval))
            {
                _rangedIdleMs = 0;
                _goal = PlayerbotGoalKind.Combat;
                return;
            }

            float distance = Distance(player, target);
            bool fightingBot = player.Combat.ThreatenedBy.Contains(target) || ReferenceEquals(target.Combat.Victim, player);
            PlayerbotFightPosition position = DecidePosition(_combatSpells.PreferredRange(player), distance,
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
                var chaseGoal = new System.Numerics.Vector3(target.X, target.Y, target.Z);
                // A route walked to its end, or whose end the target has left by more than ChaseReplanYards, is planned again in
                // this think (it was dropped here at the next think, and planned at the one after: two think gaps per re-chase).
                if (_route is null || ChaseRouteStale(player, _route, chaseGoal))
                {
                    PlanOutcome chase = PlayerbotNavigation.PlanExact(player, chaseGoal, _options, ref _tileWait,
                        _session.World.NowMs, out PlayerbotRoute? fresh);
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
                        return;
                    }

                    if (_route is not null && !ReferenceEquals(_route, fresh)) AbandonRoute(); // the old route is replaced, not kept
                    _route = fresh;
                    if (fresh is not null)
                    {
                        // The loop watch ignores a chase (PlayerbotMotion.Follow); the goal it was planned to is what "the target moved" is
                        // measured from, and a route cut short of it (MaxRouteYards, MaxPathPoints) is re-planned only once mostly walked.
                        fresh.Chase = true;
                        _chaseGoal = chaseGoal;
                        System.Numerics.Vector3 end = fresh.Points[^1];
                        _chaseTruncated = System.Numerics.Vector2.Distance(new(end.X, end.Y), new(chaseGoal.X, chaseGoal.Y)) > _options.ChaseReplanYards;
                    }
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
            return;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return;
        _goal = PlayerbotGoalKind.Loot;
        _lootOpened = _session.TryManagedAction(WorldOpcode.CmsgLoot,
            PlayerbotNavigation.GuidPayload(target.Guid.Value));
        if (_lootOpened) _lootStartedMs = (long)_session.World.Uptime.TotalMilliseconds;
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
    private bool TryReturnHome(Player player, PlayerbotHomeArea home, uint interval)
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
        }

        if (_quests.Busy || _town.HasCandidate(player)
            || _quests.PreferredCreatureEntry == 0 && (_trainers.HasCandidate(player) || _riding.HasCandidate(player))) return false;
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
        StallReturnQuest = _goal == PlayerbotGoalKind.Quest && QuestId != 0 && QuestId == _quests.ReturnQuest ? QuestId : 0;
        StallOutsideHome = _home is { } home && !home.Contains(player);
        if (shareable && !StallOutsideHome && shared?.Report(_goal, TargetEntry, QuestId, player.Guid.Value, now, player.MapId,
                new System.Numerics.Vector3(player.X, player.Y, player.Z)) is { } everyone)
            SharedSetAside = everyone;
        switch (_goal)
        {
            case PlayerbotGoalKind.Quest:
                _suspensions.SuspendQuest(QuestId, now);
                _suspensions.SuspendEntry(TargetEntry, now);
                break;
            case PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Train or PlayerbotGoalKind.Mount:
                _suspensions.SuspendEntry(TargetEntry, now);
                break;
            case PlayerbotGoalKind.Grind or PlayerbotGoalKind.Combat or PlayerbotGoalKind.Loot:
                if (_target is { } target) MarkUnreachable(target);
                // A grind walk with no creature in sight (the destinations' spawn walk) has no target to set aside: set the entry it
                // was walking to aside, or the next decision plans the same camp again (live 2026-10-12, bot QA #368: 177 stalls
                // at the empty kobold camp of quest 21). The quest's objective is then not hunted until the set-aside ends.
                else if (_goal == PlayerbotGoalKind.Grind && TargetEntry != 0) _suspensions.SuspendEntry(TargetEntry, now);
                break;
            case PlayerbotGoalKind.Home:
                _homeReturn.MarkBlocked(now);
                break;
            case PlayerbotGoalKind.Explore:
                if (_exploreCandidate is { } explore) BlockExploreCell(explore);
                else _exploreRefusals = 3;
                if (_home is null && IsUnderLevelledForArea(player))
                    TryTeleportToBindPoint(player, "stalled exploring where nothing fits its level", urgent: false);
                break;
            default:
                _exploreRefusals = 3;
                break;
        }

        _target = null;
        AbandonRoute();
        _attacking = false;
        _lootOpened = false;
        PlayerbotMovementControl.Stop(_session, player);
        // Sharing describes the stalled goal, not whether this bot can leave its current ground. A failed tram trip can begin
        // on an island too; suppressing the shared set-aside must not suppress local escape.
        EscapeIsland(player, out _);
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
    private bool EscapeIsland(Player player, out bool onIsland)
    {
        onIsland = false;
        if (!player.IsAlive || player.Combat.IsInCombat || player.Map is not { InstanceId: 0 }) return false;
        if (!PlayerbotNavigation.IsOnMeshIsland(player, _options)) return false;
        onIsland = true;
        uint now = _session.World.NowMs;
        // Home first, but only when the teleport home would be made: a refused TryTeleport would set the walk of a bot inside its home
        // aside as blocked (PlayerbotHomeReturn.Refuse), and the bind point is the way out then, as from an unsafe landing.
        if (_home is { } home)
        {
            _homeReturn.Evaluate(player, home, now);
            if (_homeReturn.TeleportRefusal(_session, player, home, _options, now) is null
                && _homeReturn.TryTeleport(_session, player, home, _options, PlayerbotHomeTeleportReason.Island, now))
            {
                DropIntent();
                _goal = PlayerbotGoalKind.Home;
                return true;
            }
        }

        return TryTeleportToBindPoint(player, "stalled on ground no route leaves", urgent: false);
    }

    /// <summary>What counts as progress for the stall watch besides moving.</summary>
    private PlayerbotStallWatch.Fingerprint Fingerprint(Player player)
    {
        PlayerbotThinkProbe.Phase("stall inventory");
        uint items = 0;
        foreach (Item item in player.Inventory.AllItems) items += item.Count;
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
        if (goal is PlayerbotGoalKind.Quest or PlayerbotGoalKind.Train or PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Mount && (entry != 0 || quest != 0))
            _errand = (goal, entry, quest, _session.World.NowMs);
    }

    private void RetireDeathIntent()
    {
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

    private void DropIntent()
    {
        _target = null;
        AbandonRoute();
        _exploreCandidate = null;
        _exploreHasPosition = false;
        _attacking = false;
        _lootOpened = false;
        TargetEntry = 0;
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

    /// <param name="wantedPct">Eat or drink below this percentage (the ordinary 45/35 when 0).</param>
    private void Rest(Player player, uint wantedPct = 0)
    {
        SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
        if (spellFeature is null || player.Combat.IsInCombat
            || spellFeature.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }
            || PlayerbotConsumables.HasActiveFoodDrink(player, spellFeature.System)
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

    private bool LootManagedPackets(ulong target)
    {
        bool opened = false;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Opcode != WorldOpcode.SmsgLootResponse || packet.Payload.Length < 14)
                continue;

            var reader = new PacketReader(packet.Payload);
            if (reader.ReadUInt64() != target || reader.ReadByte() != 1)
                continue;
            opened = true;
            _ = reader.ReadUInt32();
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
                if (slotType == 0)
                    _lootActions.Enqueue((WorldOpcode.CmsgAutostoreLootItem, [slot]));
            }

            _lootActions.Enqueue((WorldOpcode.CmsgLootMoney, []));
            _lootActions.Enqueue((WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(target)));
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
        if (!tap.IsEmpty)
        {
            if (map.FindPlayer(tap) is { } tapper) return tapper;
            if (map.FindObject(tap) is Unit pet && pet.CharmerOrOwnerGuid.IsPlayer && map.FindPlayer(pet.CharmerOrOwnerGuid) is { } owner) return owner;
        }

        if (creature.Combat.Victim is Player victim && IsStranger(player, victim, groups)) return victim;
        if (!creature.Combat.HasThreatList) return null;
        IReadOnlyList<ThreatEntry> entries = creature.Combat.Threat.Entries;
        for (int index = 0; index < entries.Count; index++)
            if (entries[index].Target is Player other && IsStranger(player, other, groups)) return other;
        return null;
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

    private static Creature? FindDefensiveAttacker(Player player)
    {
        if (player.Map is not { } map) return null;
        return player.Combat.Attackers
            .OfType<Creature>()
            .Where(creature => creature.IsInWorld && creature.IsAlive
                && ReferenceEquals(creature.Map, map)
                && map.Combat.Hooks.CanAttack(player, creature))
            .OrderBy(creature => Distance(player, creature))
            .FirstOrDefault();
    }

    private void UpdateLocalPlan(Player player)
    {
        if (planner is null || !_options.AllowLocalLlm || _planningStop.IsCancellationRequested) return;
        if (_planTask?.IsCompleted == true)
        {
            _modelChoice = _planTask.IsCompletedSuccessfully ? _planTask.Result : null;
            _planTask = null;
        }
        long now = (long)_session.World.Uptime.TotalMilliseconds;
        if (_planTask is not null || now < _nextPlanMs) return;
        _nextPlanMs = now + 5000;
        var candidates = new List<PlayerbotPlanCandidate>();
        if (_quests.HasCandidate(player)) candidates.Add(new("quest", PlayerbotGoalKind.Quest, _quests.TargetEntry, _quests.QuestId));
        if (_town.HasCandidate(player)) candidates.Add(new("town", PlayerbotGoalKind.Vendor, _town.TargetEntry, 0));
        if (FindTarget(player, _quests.PreferredCreatureEntry, IsSkipped) is { } target) candidates.Add(new("grind", PlayerbotGoalKind.Grind, target.Entry, QuestId));
        candidates.Add(new("explore", PlayerbotGoalKind.Explore, 0, 0));
        var facts = new PlayerbotPlannerFacts(player.Level, player.Health, player.MaxHealth, player.Combat.IsInCombat,
            player.MapId, (uint)(_session.Services.GetService<SpellFeature>()?.Spellbook.GetSpells(player).Count ?? 0),
            player.Inventory.GetItemCount(117));
        CancellationToken token = _planningStop.Token;
        _planTask = Task.Run(() => planner.SelectAsync(facts, candidates, token), token);
    }

    private void MarkUnreachable(Creature creature)
    {
        uint now = _session.World.NowMs;
        foreach (ObjectGuid expired in _unreachable.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _unreachable.Remove(expired);
        if (_unreachable.Count < 64) _unreachable[creature.Guid] = unchecked(now + UnreachableTargetMs);
    }

    private bool IsUnreachable(Creature creature)
        => _unreachable.TryGetValue(creature.Guid, out uint until) && unchecked((int)(until - _session.World.NowMs)) > 0;

    /// <summary>Whether a live creature in sight is no target for the bot (the grind filter's rules; <see cref="PlayerbotWorldDestinations.Untakeable"/>).</summary>
    internal bool IsUntakeable(Player player, Creature creature)
        => IsSkipped(creature) || _contention.IsSetAside(creature.Guid.Value, _session.World.NowMs)
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
        => IsUnreachable(creature) || _suspensions.IsEntrySuspended(creature.Entry, _session.World.NowMs)
            || _contention.IsEntrySuspended(creature.Entry, _session.World.NowMs)
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
        GroupManager? groups = GroupsOf(player);
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
        return _combatSpells.FirstCastable(player, abilities.DirectHeals, tagger, abilities.HealsAnotherUnit) is { } heal && _combatSpells.CastAt(player, heal, tagger);
    }

    private void RememberAttackers(Player player)
    {
        NoteErrand(_goal, TargetEntry, QuestId);
        _attackerEntries.Clear();
        _attackerFacts.Clear();
        _attackers.Clear();
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
    // never sees that attacker. The map's kill observer carries it to us before RecordDeath runs.
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

    private void RememberKiller(Unit? killer)
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
        // A player's pet or totem is not the content's danger: never set its entry aside.
        if (killer is not Creature creature || creature.CharmerOrOwnerGuid.IsPlayer) return;
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
        player.LastReleasedGraveyard = null;
        player.RepopDestinationResolved = false;
        _killingBlowSource = "Unobserved";
        (bool loop, IReadOnlyList<uint> setAside) = _stall.RecordDeath(now, player.MapId,
            new System.Numerics.Vector3(player.X, player.Y, player.Z), _attackerEntries);
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
        return TryTeleportToBindPoint(player, "was revived by the spirit healer among creatures too strong for it", urgent: true);
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

    /// <summary>
    /// Teleport the bot to its bind point (where its hearthstone leads; vmangos <c>Player::TeleportToHomebind</c>) when
    /// <see cref="BindTeleportRefusal"/> allows it, and log why (<paramref name="why"/>). True when the bot was sent; its intent is dropped
    /// and it explores from there.
    /// </summary>
    private bool TryTeleportToBindPoint(Player player, string why, bool urgent)
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

        if (!teleports!.TeleportToHomebind(player)) return false;
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

        if (player.Map is null)
            return;
        PlayerbotThinkProbe.Phase("explore candidates");
        if (TryExploreSpawns(player, elapsedMs)) return;
        // Wander onward: keep roughly the current facing (within +-60 degrees, golden-ratio spread so successive legs
        // differ). Only after repeated refusals turn anywhere. The old golden-angle-from-zero choice sent each leg
        // ~137 degrees from the last one, so a bot that lost its route every few thinks ran in circles.
        System.Numerics.Vector3 destination = WanderLegEnd(player, new(player.X, player.Y, player.Z), player.Orientation);
        PlayerbotThinkProbe.Phase("explore wander plan");
        if (PlayerbotNavigation.PlanToward(player, destination, _options, ref _tileWait, _session.World.NowMs, out _route) == PlanOutcome.Pending)
        {
            AbandonRoute();
            _roamDirection--; // the same leg again once the tile is in
            return;
        }
        if (_route is not null)
        {
            _exploreRefusals = 0;
            PlayerbotThinkProbe.Phase("explore advance");
            if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, _session.World.NowMs))
                AbandonRoute();
            else
            {
                _wanderLeg = _route;
                ChainWanderLeg(player, elapsedMs); // a leg shorter than the think gap's walk is chained at once
            }
        }
        else
        {
            _exploreRefusals++;
            PlayerbotMovementControl.Stop(_session, player);
        }
    }

    /// <summary>The wandering leg the bot walks now (<see cref="ChainWanderLeg"/> chains only after such a leg, never after one to a spawn).</summary>
    private PlayerbotRoute? _wanderLeg;

    /// <summary>
    /// The end of the next wandering leg from <paramref name="from"/>, facing <paramref name="orientation"/>: roughly that facing (within
    /// +-60 degrees, golden-ratio spread so successive legs differ). Only after repeated refusals turn anywhere. The old
    /// golden-angle-from-zero choice sent each leg ~137 degrees from the last one, so a bot that lost its route every few thinks ran in
    /// circles. Beyond 0.8 of the home radius the next leg heads back in instead of wandering further out; so does a leg that would end
    /// out of a clipped home's zone (a wander across the Westfall-Duskwood border inside the circle). Back in is the centre first,
    /// then, while legs are refused, a fan around it (InwardLeg). Takes the next direction of the rotation (<c>_roamDirection</c>).
    /// </summary>
    private System.Numerics.Vector3 WanderLegEnd(Player player, System.Numerics.Vector3 from, float orientation)
    {
        int attempt = _roamDirection++;
        float spread = (((attempt + (int)(player.Guid.Low % 7)) * 0.618034f) % 1f) - 0.5f;
        float angle = _exploreRefusals >= 3
            ? (attempt + player.Guid.Low) * 2.39996323f
            : orientation + (spread * MathF.PI * 2f / 3f);
        var destination = new System.Numerics.Vector3(
            from.X + (MathF.Cos(angle) * ExploreLegYards),
            from.Y + (MathF.Sin(angle) * ExploreLegYards),
            from.Z);
        if (_home is { } area && (player.Map?.MapId ?? player.MapId) == area.MapId
            && (area.DistanceTo(from.X, from.Y) > area.ExploreRadius
                || area.ZoneClipped && !area.InZone(destination.X, destination.Y, destination.Z)))
            destination = InwardLeg(area, from, _exploreRefusals);
        return destination;
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
        if (_route is not { Complete: false } route || !ReferenceEquals(route, _wanderLeg) || ExploreMovementPaused(player, elapsedMs)
            || route.Points.Count < 2) return;
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
        if (_earlyExploreIslandChecked || unchecked(now - _exploreIslandSince) < 15_000u) return false;
        if (player.Map is not { InstanceId: 0 }) return false;
        // The check's path searches (up to IslandDirections, counted as the think's fallbacks) are this think's work: the caller plans
        // no route of its own after it.
        if (!EscapeIsland(player, out bool onIsland) && onIsland)
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
        _exploreBlockedCells[key] = unchecked(_session.World.NowMs + 60_000u);
        _exploreCandidate = null;
        TargetEntry = 0;
    }

    private bool ExploreMovementPaused(Player player, uint elapsedMs)
        => elapsedMs == 0 || _session.ManagedBudget is { Remaining: <= 0 }
            || (player.Movement.Flags & (MovementFlags.Root | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0
            || PlayerbotMotion.ControlLost(player); // a pause: no refusal, no abandoned route, no blocked cell

    private bool TryExploreSpawns(Player player, uint elapsedMs)
    {
        // A named kill objective still owns travel and targeting. Static grinding is the idle fallback only.
        if (_quests.PreferredCreatureEntry != 0) return false;
        uint mapId = player.Map!.MapId;
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
            if (current.Spawn.MapId != mapId || dx * dx + dy * dy <= 65f * 65f)
            {
                // Within sight but FindTargets found nothing to fight: this patch is depleted, gated, or unavailable.
                BlockExploreCell(current);
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
            _exploreSearchContent = content;
            _exploreSearchAtlas = atlas;
            _exploreSearchFactions = factions;
            _exploreSearchSelf = self;
            _exploreSearchHome = _home is { MapId: var homeMap } && homeMap == mapId ? _home : null;
            _exploreSearchHazards = _options.Risk.Enabled ? Risk.Hazards.Active(mapId, now) : [];
            _exploreSearchMap = mapId;
            _exploreEligibleFilter ??= ExploreEligible;
            _exploreFactionFilter ??= ExploreAttackableFaction;
            _exploreThreatFilter ??= ExploreHostileFaction;
            _exploreBlockedFilter ??= ExploreBlocked;
            PlayerbotThinkProbe.Phase("explore atlas find");
            _exploreCandidate = atlas.Find(mapId, player.Level, new(player.X, player.Y, player.Z), _exploreEligibleFilter,
                _exploreBlockedFilter, out _, _exploreFactionFilter);
            if (_exploreCandidate is null)
            {
                _exploreSearchAfter = unchecked(now + 5_000u);
                return false;
            }
        }

        PlayerbotExploreAtlas.Candidate chosen = _exploreCandidate.Value;
        PlayerbotThinkProbe.Phase("explore plan");
        if (ExploreMovementPaused(player, elapsedMs)) return true;
        var destination = new System.Numerics.Vector3(chosen.Spawn.X, chosen.Spawn.Y, chosen.Spawn.Z);
        PlanOutcome atlasPlan = PlayerbotNavigation.PlanToward(player, destination, _options, ref _tileWait, now, out _route);
        if (atlasPlan == PlanOutcome.Pending) return true;
        if (atlasPlan == PlanOutcome.Refused)
        {
            BlockExploreCell(chosen);
            PlayerbotMovementControl.Stop(_session, player);
            return true; // One path query this think. The next think tries another cell.
        }
        _goal = PlayerbotGoalKind.Explore;
        TargetEntry = chosen.Spawn.Entry;
        PlayerbotThinkProbe.Phase("explore advance");
        if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, now))
        {
            if (!_route!.Complete) BlockExploreCell(chosen);
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
        // FactionCombatHooks.ReactionTo: hostility wins over friendly bits in each direction. A player may attack a
        // neutral creature, but CanAttack refuses when either direction's resulting reaction is Friendly.
        bool friendly = self.IsFriendlyTo(npc) || npc.IsFriendlyTo(self);
        return !friendly || self.IsHostileTo(npc) && npc.IsHostileTo(self);
    }

    private bool ExploreHostileFaction(uint faction)
        => _exploreSearchFactions?.Find(faction) is { } npc && _exploreSearchSelf is { } self && npc.IsHostileTo(self);

    private bool ExploreEligible(PlayerbotExploreAtlas.Candidate candidate)
    {
        CreatureSpawn spawn = candidate.Spawn;
        Player player = _exploreSearchPlayer!;
        CreatureTemplate? template = _exploreSearchContent!.FindTemplate(spawn.Entry);
        if (template is null || !PlayerbotWorldDestinations.IsPresent(player, spawn)
            || _exploreSearchHome is { } home && (!home.Contains(spawn) || home.DistanceTo(spawn.X, spawn.Y) > home.ExploreRadius))
            return false;
        var point = new System.Numerics.Vector3(spawn.X, spawn.Y, spawn.Z);
        foreach (PlayerbotHazard hazard in _exploreSearchHazards)
            if (hazard.Covers(point)) return false;
        return !_exploreSearchAtlas!.HasStrongHazard(candidate, player.Level, _exploreThreatFilter!);
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

    /// <summary>The longest exploring leg.</summary>
    internal const float ExploreLegYards = 48f;

    /// <summary>
    /// The end of an exploring bot's leg back in after <paramref name="refusals"/> refused legs: the first turn of
    /// <see cref="InwardFanDegrees"/> from that one on whose leg ends strictly closer to the centre than the bot stands and, for a
    /// clipped home, in its zone. A leg turned θ off the centre is at most the bot's distance × cos θ long (the point of that line
    /// nearest the centre), so it never ends farther out: a full 48-yard leg 75 degrees off from 55 yards out would end 62.9 yards
    /// out, past a 60-yard radius. When no turn qualifies (the bot at the centre, every turn out of the zone), the straight leg
    /// towards the centre.
    /// </summary>
    internal static System.Numerics.Vector3 InwardLeg(PlayerbotHomeArea home, Player player, int refusals)
        => InwardLeg(home, new System.Numerics.Vector3(player.X, player.Y, player.Z), refusals);

    /// <summary><see cref="InwardLeg(PlayerbotHomeArea, Player, int)"/> from <paramref name="from"/> (the end of the leg walked now, for a chained leg).</summary>
    internal static System.Numerics.Vector3 InwardLeg(PlayerbotHomeArea home, System.Numerics.Vector3 from, int refusals)
    {
        float distance = home.DistanceTo(from.X, from.Y);
        float inward = MathF.Atan2(home.Centre.Y - from.Y, home.Centre.X - from.X);
        for (int turn = 0; turn < InwardFanDegrees.Length; turn++)
        {
            float degrees = InwardFanDegrees[(int)((uint)(refusals + turn) % (uint)InwardFanDegrees.Length)];
            float length = MathF.Min(ExploreLegYards, distance * MathF.Cos(degrees * MathF.PI / 180f));
            if (length < 1f) continue;
            float angle = inward + (degrees * MathF.PI / 180f);
            var end = new System.Numerics.Vector3(from.X + (MathF.Cos(angle) * length), from.Y + (MathF.Sin(angle) * length), from.Z);
            if (home.DistanceTo(end.X, end.Y) >= distance) continue;
            if (home.ZoneClipped && !home.InZone(end.X, end.Y, end.Z)) continue;
            return end;
        }

        float straight = MathF.Min(ExploreLegYards, distance);
        return new System.Numerics.Vector3(from.X + (MathF.Cos(inward) * straight), from.Y + (MathF.Sin(inward) * straight), from.Z);
    }

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));
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
