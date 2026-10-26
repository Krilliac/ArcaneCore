using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Playerbots;

/// <summary>Configuration policy for server-managed autonomous players.</summary>
public sealed class PlayerbotOptions
{
    public const string SectionName = "World:Playerbots";

    /// <summary>The largest search budget of one bot route query (the pack's <c>PlayerbotNavigation.MaxSearchNodes</c>); the default of <see cref="MaxThinkSearchNodes"/> is three of them.</summary>
    public const int MaxRouteSearchNodes = 32_768;

    /// <summary>Run server-managed playerbots (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Run far playerbot navigation queries on the bounded private worker (off by default during rollout). Restart required: the worker is created when the feature attaches.</summary>
    public bool AsyncNavigationEnabled { get; set; }

    /// <summary>Log the saved managed bots back in when the world starts.</summary>
    public bool RestoreOnStartup { get; set; }

    /// <summary>The highest <see cref="MaxBots"/> accepted (docs/integration/perf-limits-20261008.md has the measurement behind it).</summary>
    public const int MaxBotsCeiling = 5000;

    /// <summary>The highest <see cref="MaxRegisteredBots"/> accepted.</summary>
    public const int MaxRegisteredBotsCeiling = 10_000;

    /// <summary>
    /// The most managed bots running at once (0..5000, <see cref="MaxBotsCeiling"/>). Only running bots take a slot: a stopped,
    /// faulted or quarantined bot does not, and a start beyond the cap is refused with "playerbot-capacity" (a quarantine retry
    /// or startup restore that finds no slot waits for one). Live through <c>.reload config</c>: raising it lets more bots start
    /// at once; lowering it below the running count stops nobody, it only refuses new starts until enough bots are stopped.
    /// </summary>
    public int MaxBots { get; set; } = 8;

    /// <summary>
    /// The most managed bots registered (running or not; 0..10000, <see cref="MaxRegisteredBotsCeiling"/>): <c>.playerbot create</c> is
    /// refused with "playerbot-registry-full" beyond it. Each one is a real account and character. Live through <c>.reload config</c>.
    /// </summary>
    public int MaxRegisteredBots { get; set; } = 1000;

    /// <summary>
    /// The most bots one bulk command (<c>.playerbot summon|send|start|stop ... all</c> or a prefix) moves or starts without
    /// <c>force</c> (1..<see cref="MaxBotsCeiling"/>). A selection larger than this, with no smaller count, is refused. Live through
    /// <c>.reload config</c>.
    /// </summary>
    public int MaxBulkCommand { get; set; } = 50;

    /// <summary>Milliseconds between two decisions of a bot (50..60000).</summary>
    public int ThinkIntervalMs { get; set; } = 500;

    /// <summary>
    /// The most managed client actions all bots may send in one world tick (1..512). The tick's think time is bounded separately, by
    /// <see cref="ThinkBudgetMinMs"/>..<see cref="ThinkBudgetMaxMs"/> (<c>PlayerbotThinkBudget</c>): with about 1000 bots the action budget must not be the first limit a
    /// tick meets, or each bot waits more ticks for its share of actions and its movement lease runs out between thinks.
    /// </summary>
    public int MaxActionsPerTick { get; set; } = 48;

    /// <summary>
    /// The least wall time (ms, 1..200) a world tick spends on the bots' thinks, however loaded the tick is: the guarantee that a busy
    /// server still thinks (<c>PlayerbotThinkBudget</c>). At least one bot always thinks per tick. Equal to
    /// <see cref="ThinkBudgetMaxMs"/> it is a fixed budget. Raised, up to <see cref="ThinkBudgetMaxMs"/>, to what walking every bot within <see cref="ThinkStarvedGapMs"/> takes at the measured cost of a think (<c>PlayerbotThinkBudget.CoverageMs</c>): 8 ms covers about 400 bots (1.3 ms a visit, 50 ms ticks, 3 ms of fights) and 1000 bots need about 16 ms, up to the ceiling: the bots then take more of each tick. The walk is sized from the configured tick interval, not the real one: a real tick longer than the configured one does not raise the budget (that would slow the ticks further), so a full pass of the bots then takes longer than the bound it was sized for (1000 bots at about 10 bots a tick: 8 to 30 s at 80 to 260 ms ticks). The motion lease and the think-starved report follow the measured pass instead (<see cref="ThinkStarvedGapMs"/>).
    /// </summary>
    public double ThinkBudgetMinMs { get; set; } = 8;

    /// <summary>The most wall time (ms, at least <see cref="ThinkBudgetMinMs"/>, at most 200) a world tick spends on the bots' thinks, however idle the rest of the tick is.</summary>
    public double ThinkBudgetMaxMs { get; set; } = 30;

    /// <summary>
    /// The share (0.05..0.95) of a tick's slack (tick interval minus the time the world spent on everything but the bots) the bots' work
    /// may take; the think budget is this share of the slack less what the bots already used this tick, between the min and the max.
    /// </summary>
    public double ThinkBudgetTickShare { get; set; } = 0.6;

    /// <summary>
    /// Milliseconds (50..60000) after which a bot that is in combat, dead or a ghost is due for the think lane that comes first
    /// (<c>PlayerbotThinkRotation.TickLanes</c>). Under load such a bot thinks about this often; with room for everyone every bot
    /// thinks every tick as before.
    /// </summary>
    public int CombatThinkIntervalMs { get; set; } = 300;

    /// <summary>The share (0.1..0.9) of the think time budget and of <see cref="MaxActionsPerTick"/> the combat lane may use. The lane gets floor(share × <see cref="MaxActionsPerTick"/>) actions of its own (none at <see cref="MaxActionsPerTick"/> 1); the rest, at least one, is the other bots' and no fight's think can touch it, so they are never starved by a crowd of fights. What the combat lane leaves goes to the other bots too.</summary>
    public double CombatLaneShare { get; set; } = 0.7;

    /// <summary>
    /// A bot that goes longer than this many milliseconds (1000..3600000) of world time without a think is reported once per episode as a
    /// "cadence think-starved" anomaly (<c>PlayerbotAnomalies</c>); 0 turns the report off. The default is the ghost walk's stuck
    /// bound (<c>PlayerbotRecovery.StuckMs</c>). The report fires only for a bot that falls behind the rotation: its bound is
    /// the larger of this value and 1.5 times the measured time of a full pass of the main think lane (three times this value
    /// until a pass is measured), so a rotation that is slow for every bot alike raises nothing. The think budget is still sized
    /// from this value unraised.
    /// </summary>
    public int ThinkStarvedGapMs { get; set; } = 10_000;

    /// <summary>
    /// How far (flat yards, 0.5..100) the target of a chase may move from the place the chase route was planned to before the route is
    /// planned again, in the same think that sees it. A route cut short of its target by the route bounds (<c>MaxRouteYards</c>,
    /// <c>MaxPathPoints</c>) is planned again only once the bot has walked 75% of it. A chase route walked to its end is planned again
    /// at once, whatever this says.
    /// </summary>
    public float ChaseReplanYards { get; set; } = 5f;

    /// <summary>
    /// A single bot think longer than this many milliseconds is logged with its bot, goal, phases and path searches
    /// (<c>PlayerbotSlowThinkLog</c>; 0 = off, 0..60000). Live: <c>.reload config</c> applies a change at once.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 60000 ('.' is the decimal point); 0 turns the log off")]
    public double SlowThinkLogMs { get; set; } = 50;

    /// <summary>
    /// The most navigation-mesh search nodes the fallback path queries of one bot think may create together (the steep escape, the
    /// starts beside the bot, the hazard detours; <c>PlayerbotThinkProbe.NodeBudget</c>): past it a fallback runs with what is
    /// left, or not at all, and finds nothing (its plan may then fail, as when no such way exists). A plan's own query is neither held
    /// to it nor counted. 0 = no bound, else 2048..10000000; three of
    /// the largest single searches (<c>PlayerbotNavigation.MaxSearchNodes</c>) by default. Live: <c>.reload config</c>.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("0 (no bound), or a whole number from 2048 to 10000000")]
    public int MaxThinkSearchNodes { get; set; } = 3 * MaxRouteSearchNodes;

    /// <summary>
    /// Whether a bot plan's far navigation-mesh search (more than <see cref="PathJobSyncNodeThreshold"/> nodes) runs on the shared
    /// path-job pool instead of the world thread: the bot answers "pending" for the think and the finished answer is applied on a later
    /// one, as it waits for a navigation tile still being read. A finished answer within 15% of the distance (10 to 40 yards) of a destination
    /// that keeps moving is applied as a route cut short, spliced from where the bot stands, and a refresh job for the new destination is queued
    /// in the same think. Off by default (rollout). Separately from this switch, and on whatever it says: a follower keeps walking its current
    /// route while a re-plan is pending (it also covers a navigation tile still being read). Live: <c>.reload config</c> (jobs in flight finish
    /// and are dropped or applied by the same checks).
    /// </summary>
    public bool PathJobs { get; set; }

    /// <summary>The most path jobs one bot may have in flight (1..8).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 8")]
    public int PathJobsMaxPerBot { get; set; } = 1;

    /// <summary>The most bot path jobs in flight together (1..1024); a bot past it waits in line, oldest first, and answers "pending" until a slot frees; one held that long still runs a single search on the world thread after <see cref="PathJobWaitMaxMs"/>, held to <c>SyncPathQueryMaxMs</c>.</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 1024")]
    public int PathJobsMaxInFlight { get; set; } = 64;

    /// <summary>The most search nodes (the sum of the jobs' <c>MaxSearchNodes</c>) the bot path jobs in flight may hold together: it bounds pool time, not only the job count (4096..100000000).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 4096 to 100000000")]
    public long PathJobsMaxInFlightNodes { get; set; } = 8L * MaxRouteSearchNodes;

    /// <summary>A path query of at most this many nodes stays on the world thread even with <see cref="PathJobs"/> (2048, vmangos' own budget, is a goal within about 100 yards; 0..32768).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 0 to 32768")]
    public int PathJobSyncNodeThreshold { get; set; } = ArcaneCore.Game.Maps.Collision.PathOptions.DefaultMaxSearchNodes;

    /// <summary>The longest wall time in milliseconds a path job may hold a pool thread (a search past it ends as one that ran out of nodes; 10..60000).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 10 to 60000")]
    public int PathJobWorkerMaxMs { get; set; } = 1000;

    /// <summary>How long one query may wait for its path job (wall ms) before it runs once on the world thread, held to <see cref="SyncPathQueryMaxMs"/> (1000..600000).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1000 to 600000")]
    public int PathJobWaitMaxMs { get; set; } = 30_000;

    /// <summary>
    /// The longest wall time in milliseconds one navigation-mesh search may take on the world thread (0 = no bound, else 10..60000):
    /// a search past it stops as one that ran out of nodes does (capped). Live 2026-10-24: one bot think spent 2 168 ms in a single
    /// 32 768-node search. Live: <c>.reload config</c>.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("0 (no bound), or a whole number from 10 to 60000")]
    public int SyncPathQueryMaxMs { get; set; } = 0;

    /// <summary>The most points of one bot route (1..4096).</summary>
    public int MaxPathPoints { get; set; } = 128;

    /// <summary>The longest bot route in yards (above 0, at most 100000).</summary>
    public float MaxRouteYards { get; set; } = 2000f;

    /// <summary>Maximum same-map straight-line trip from a quest giver to its nearest present ender for an unhomed bot (0 disables the distance limit; 0..100000).</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 100000 ('.' is the decimal point); 0 disables the distance limit")]
    public float MaxDeliveryYards { get; set; } = 1200f;

    /// <summary>
    /// Abandon an incomplete quest that has turned grey for the bot (quest level at or below its vmangos grey level) and hunt no objective of it
    /// (on by default). A completed grey quest is still turned in. Off: grey quests are kept until a full log sheds one. Live: <c>.reload config</c>.
    /// </summary>
    public bool DropGreyQuests { get; set; } = true;

    /// <summary>The bots' movement speed in yards per second, never above the player's run speed (above 0, at most 100).</summary>
    public float MoveSpeed { get; set; } = 7f;

    /// <summary>
    /// Seconds of world time a living bot may go without any progress (it moved less than 10 yards and its level, experience,
    /// money, bags, quest log and spells did not change) before it reports a stall in <c>.playerbot list</c> and gives up its
    /// goal (<c>PlayerbotStallWatch</c>; 10..86400).
    /// </summary>
    public int StallSeconds { get; set; } = 120;

    /// <summary>
    /// How a bot's movement reaches the world (on by default). On: the bot reports its motion with the MSG_MOVE_* packets a 1.12
    /// client sends, dispatched through the ordinary movement handlers like any client's. Off: the server relocates the bot itself
    /// (the same locomotion observers) and sends its observers the same MSG_MOVE_* relay, skipping the opcode dispatch, the
    /// packet encode/decode and the handler, which is cheaper with many bots. Live: <c>.reload config</c> applies a change at once.
    /// </summary>
    public bool MovementPackets { get; set; } = true;

    /// <summary>
    /// How much real-client packet work a bot's session does (<see cref="PlayerbotClientFidelity.Lean"/> by default). Lean: a bot has no
    /// client, so the map builds no SMSG_UPDATE_OBJECT blocks for it (its visible set is still maintained) and its session keeps only
    /// the received packets a bot reads back (<c>PlayerbotPacketIntake</c>). Full: bots are treated like real clients, every
    /// packet is built, compressed and copied into the bot's receive ring, which is the stress-test mode for load testing. A session
    /// with an observer attached (scenario, packet capture) always receives everything. Read at start; a restart applies a change.
    /// </summary>
    public PlayerbotClientFidelity ClientFidelity { get; set; } = PlayerbotClientFidelity.Lean;

    /// <summary>The maps bots may act and travel on (non-empty, no duplicates; a configured list replaces the default continents 0 and 1).</summary>
    public uint[] AllowedMaps { get; set; } = [0, 1];

    /// <summary>
    /// Seconds a bot waits in quarantine after an action fault before it is logged in again (1..3600); each further fault in
    /// <see cref="FaultWindowSeconds"/> doubles the wait, up to an hour. The bot stays desired meanwhile.
    /// </summary>
    public int FaultBackoffSeconds { get; set; } = 30;

    /// <summary>The faults within <see cref="FaultWindowSeconds"/> that disable a bot for good (DesiredEnabled off; 1..100).</summary>
    public int MaxFaults { get; set; } = 3;

    /// <summary>How far back faults count towards <see cref="MaxFaults"/>, in seconds of world time (1..604800).</summary>
    public int FaultWindowSeconds { get; set; } = 3600;

    /// <summary>The scenario harness on a live world (<c>World:Playerbots:Scenarios</c>; off by default).</summary>
    public PlayerbotScenarioOptions Scenarios { get; set; } = new();

    /// <summary>How a bot behaves in a real player's group (<c>World:Playerbots:Party</c>; <c>Party.PlayerbotPartyAI</c>).</summary>
    public PlayerbotPartyOptions Party { get; set; } = new();

    /// <summary>Risk against reward before a pull, and retreat from a lost fight (<c>World:Playerbots:Risk</c>; live).</summary>
    public PlayerbotRiskOptions Risk { get; set; } = new();

    /// <summary>Chests, gathering nodes and rare creatures a bot weighs against their guards and goes for when worth it (<c>World:Playerbots:Opportunities</c>; live; docs/areas/playbots-opportunities.md).</summary>
    public PlayerbotOpportunityOptions Opportunities { get; set; } = new();

    /// <summary>
    /// Bots answer players who talk to them (<c>World:Playerbots:Chat</c>; live): built-in template replies by default, with optional
    /// model providers (Anthropic, OpenAI-compatible, local servers) in front of them (docs/areas/playbots.md, Bot chat).
    /// </summary>
    public Chat.PlayerbotChatOptions Chat { get; set; } = new();

    /// <summary>Bots group up for content they cannot do alone (<c>World:Playerbots:Groups</c>; live).</summary>
    public PlayerbotGroupOptions Groups { get; set; } = new();

    /// <summary>How a following bot keeps up with its leader and catches up when it cannot walk (<c>World:Playerbots:Follow</c>; live).</summary>
    public PlayerbotFollowOptions Follow { get; set; } = new();

    /// <summary>How a homed bot returns to its home area: walk, teleport, or teleport when the walk is long or unsafe (<c>World:Playerbots:Home</c>; live).</summary>
    public PlayerbotHomeOptions Home { get; set; } = new();

    /// <summary>How bots share the mobs of one place: soft claims, helping a tagger, leaving someone else's mob alone (<c>World:Playerbots:Contention</c>; live).</summary>
    public PlayerbotContentionOptions Contention { get; set; } = new();

    /// <summary>Bots discover flight masters and fly between known nodes on long trips (<c>World:Playerbots:Taxi</c>; live).</summary>
    public PlayerbotTaxiOptions Taxi { get; set; } = new();

    /// <summary>Bots plan multi-leg trips to far places by boat, zeppelin, flight path, hearthstone and mage portal (<c>World:Playerbots:Travel</c>; live).</summary>
    public PlayerbotTravelOptions Travel { get; set; } = new();

    /// <summary>A target number of bots kept online by level band and the world's tick time (<c>World:Playerbots:Population</c>; live; off by default).</summary>
    public Population.PlayerbotPopulationOptions Population { get; set; } = new();

    /// <summary>Bot guilds, guild chat and greetings (<c>World:Playerbots:Social</c>; live; docs/areas/playbots-social.md).</summary>
    public BotSocial.PlayerbotSocialOptions Social { get; set; } = new();

    /// <summary>Bots use the faction auction house and mailbox (<c>World:Playerbots:Economy</c>; live; docs/areas/playbots-economy.md).</summary>
    public PlayerbotEconomyOptions Economy { get; set; } = new();

    /// <summary>How bots save money for what they will need: class spells, riding, a mount (<c>World:Playerbots:Budget</c>; live; docs/areas/playbots-economy.md).</summary>
    public PlayerbotBudgetOptions Budget { get; set; } = new();

    /// <summary>Bots save for riding training and a mount, train, buy and ride it (<c>World:Playerbots:Mounts</c>; live; docs/areas/playbots-economy.md).</summary>
    public PlayerbotMountOptions Mounts { get; set; } = new();
    /// <summary>Bots queue for, play and leave battlegrounds (<c>World:Playerbots:Battlegrounds</c>; live).</summary>
    public Battlegrounds.PlayerbotBattlegroundOptions Battlegrounds { get; set; } = new();

    /// <summary>Adversarial server-breaker chaos bots that fuzz handlers, state machines and invariants (<c>World:Playerbots:Chaos</c>; live; off by default).</summary>
    public Chaos.PlayerbotChaosOptions Chaos { get; set; } = new();

    /// <summary>The goal policy engine and its decision telemetry (<c>World:Playerbots:Policy</c>; off by default; docs/areas/playbots.md, Goal policy).</summary>
    public Policy.PlayerbotPolicyOptions Policy { get; set; } = new();

    public static PlayerbotOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new PlayerbotOptions();
        ApplyConfiguration(options, configuration);
        return options;
    }

    internal static void ApplyConfiguration(PlayerbotOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        // ConfigurationBinder appends array elements to a non-empty initialized array.
        // Explicit maps replace the defaults; missing maps retain the default continents.
        if (section.GetChildren().Any(child => string.Equals(child.Key, nameof(AllowedMaps), StringComparison.OrdinalIgnoreCase))) options.AllowedMaps = [];
        ArcaneCore.World.Playerbots.Population.PlayerbotPopulationOptions.PrepareBinding(options.Population, section);
        section.Bind(options);
    }

    public void Validate()
    {
        if (MaxBots is < 0 or > MaxBotsCeiling) throw new InvalidOperationException($"{SectionName}: MaxBots must be 0..{MaxBotsCeiling}.");
        if (MaxRegisteredBots is < 0 or > MaxRegisteredBotsCeiling)
            throw new InvalidOperationException($"{SectionName}: MaxRegisteredBots must be 0..{MaxRegisteredBotsCeiling}.");
        if (MaxBulkCommand is < 1 or > MaxBotsCeiling) throw new InvalidOperationException($"{SectionName}: MaxBulkCommand must be 1..{MaxBotsCeiling}.");
        if (ThinkIntervalMs is < 50 or > 60_000) throw new InvalidOperationException($"{SectionName}: ThinkIntervalMs must be 50..60000.");
        if (MaxActionsPerTick is < 1 or > 512) throw new InvalidOperationException($"{SectionName}: MaxActionsPerTick must be 1..512.");
        if (!double.IsFinite(ThinkBudgetMinMs) || ThinkBudgetMinMs is < 1 or > 200)
            throw new InvalidOperationException($"{SectionName}: ThinkBudgetMinMs must be 1..200.");
        if (!double.IsFinite(ThinkBudgetMaxMs) || ThinkBudgetMaxMs < ThinkBudgetMinMs || ThinkBudgetMaxMs > 200)
            throw new InvalidOperationException($"{SectionName}: ThinkBudgetMaxMs must be ThinkBudgetMinMs..200.");
        if (!double.IsFinite(ThinkBudgetTickShare) || ThinkBudgetTickShare is < 0.05 or > 0.95)
            throw new InvalidOperationException($"{SectionName}: ThinkBudgetTickShare must be 0.05..0.95.");
        if (CombatThinkIntervalMs is < 50 or > 60_000) throw new InvalidOperationException($"{SectionName}: CombatThinkIntervalMs must be 50..60000.");
        if (!double.IsFinite(CombatLaneShare) || CombatLaneShare is < 0.1 or > 0.9)
            throw new InvalidOperationException($"{SectionName}: CombatLaneShare must be 0.1..0.9.");
        if (ThinkStarvedGapMs != 0 && ThinkStarvedGapMs is < 1000 or > 3_600_000)
            throw new InvalidOperationException($"{SectionName}: ThinkStarvedGapMs must be 0 (off) or 1000..3600000.");
        if (!float.IsFinite(ChaseReplanYards) || ChaseReplanYards is < 0.5f or > 100f) throw new InvalidOperationException($"{SectionName}: ChaseReplanYards must be 0.5..100.");
        if (!double.IsFinite(SlowThinkLogMs) || SlowThinkLogMs is < 0 or > 60_000) throw new InvalidOperationException($"{SectionName}: SlowThinkLogMs must be 0..60000.");
        if (MaxThinkSearchNodes != 0 && MaxThinkSearchNodes is < ArcaneCore.Game.Maps.Collision.PathOptions.DefaultMaxSearchNodes or > 10_000_000)
            throw new InvalidOperationException($"{SectionName}: MaxThinkSearchNodes must be 0 or {ArcaneCore.Game.Maps.Collision.PathOptions.DefaultMaxSearchNodes}..10000000.");
        if (PathJobsMaxPerBot is < 1 or > 8) throw new InvalidOperationException($"{SectionName}: PathJobsMaxPerBot must be 1..8.");
        if (PathJobsMaxInFlight is < 1 or > 1024) throw new InvalidOperationException($"{SectionName}: PathJobsMaxInFlight must be 1..1024.");
        if (PathJobsMaxInFlightNodes is < 4096 or > 100_000_000) throw new InvalidOperationException($"{SectionName}: PathJobsMaxInFlightNodes must be 4096..100000000.");
        if (PathJobSyncNodeThreshold is < 0 or > 32_768) throw new InvalidOperationException($"{SectionName}: PathJobSyncNodeThreshold must be 0..32768.");
        if (PathJobWorkerMaxMs is < 10 or > 60_000) throw new InvalidOperationException($"{SectionName}: PathJobWorkerMaxMs must be 10..60000.");
        if (PathJobWaitMaxMs is < 1000 or > 600_000) throw new InvalidOperationException($"{SectionName}: PathJobWaitMaxMs must be 1000..600000.");
        if (SyncPathQueryMaxMs != 0 && (SyncPathQueryMaxMs is < 10 or > 60_000))
            throw new InvalidOperationException($"{SectionName}: SyncPathQueryMaxMs must be 0 or 10..60000.");
        if (MaxPathPoints is < 1 or > 4096) throw new InvalidOperationException($"{SectionName}: MaxPathPoints must be 1..4096.");
        if (!float.IsFinite(MaxRouteYards) || MaxRouteYards <= 0 || MaxRouteYards > 100_000)
            throw new InvalidOperationException($"{SectionName}: MaxRouteYards must be finite and > 0..100000.");
        if (!float.IsFinite(MaxDeliveryYards) || MaxDeliveryYards < 0 || MaxDeliveryYards > 100_000)
            throw new InvalidOperationException($"{SectionName}: MaxDeliveryYards must be finite and 0..100000.");
        if (!float.IsFinite(MoveSpeed) || MoveSpeed <= 0 || MoveSpeed > 100)
            throw new InvalidOperationException($"{SectionName}: MoveSpeed must be finite and > 0..100.");
        if (StallSeconds is < 10 or > 86_400) throw new InvalidOperationException($"{SectionName}: StallSeconds must be 10..86400.");
        if (!Enum.IsDefined(ClientFidelity)) throw new InvalidOperationException($"{SectionName}: ClientFidelity must be Lean or Full.");
        if (AllowedMaps is null || AllowedMaps.Length == 0 || AllowedMaps.Distinct().Count() != AllowedMaps.Length)
            throw new InvalidOperationException($"{SectionName}: AllowedMaps must be non-empty and contain no duplicates.");
        if (FaultBackoffSeconds is < 1 or > 3600 || MaxFaults is < 1 or > 100 || FaultWindowSeconds is < 1 or > 604_800)
            throw new InvalidOperationException($"{SectionName}: FaultBackoffSeconds must be 1..3600, MaxFaults 1..100 and FaultWindowSeconds 1..604800.");
        if (Scenarios is null || Scenarios.MaxDurationSeconds is < 5 or > 600 || Scenarios.StepTimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException($"{SectionName}:Scenarios: MaxDurationSeconds must be 5..600 and StepTimeoutSeconds 1..300.");
        if (Party is null) throw new InvalidOperationException($"{SectionName}:Party is missing.");
        Party.Validate();
        if (Risk is null) throw new InvalidOperationException($"{SectionName}:Risk is missing.");
        Risk.Validate();
        if (Opportunities is null) throw new InvalidOperationException($"{SectionName}:Opportunities is missing.");
        Opportunities.Validate();
        if (Chat is null) throw new InvalidOperationException($"{SectionName}:Chat is missing.");
        Chat.Validate();
        if (Groups is null) throw new InvalidOperationException($"{SectionName}:Groups is missing.");
        Groups.Validate();
        if (Follow is null) throw new InvalidOperationException($"{SectionName}:Follow is missing.");
        Follow.Validate();
        if (Home is null) throw new InvalidOperationException($"{SectionName}:Home is missing.");
        Home.Validate();
        if (Contention is null) throw new InvalidOperationException($"{SectionName}:Contention is missing.");
        Contention.Validate();
        if (Taxi is null) throw new InvalidOperationException($"{SectionName}:Taxi is missing.");
        Taxi.Validate();
        if (Travel is null) throw new InvalidOperationException($"{SectionName}:Travel is missing.");
        Travel.Validate();
        if (Population is null) throw new InvalidOperationException($"{SectionName}:Population is missing.");
        Population.Validate();
        if (Social is null) throw new InvalidOperationException($"{SectionName}:Social is missing.");
        Social.Validate();
        if (Economy is null) throw new InvalidOperationException($"{SectionName}:Economy is missing.");
        Economy.Validate();
        if (Budget is null) throw new InvalidOperationException($"{SectionName}:Budget is missing.");
        Budget.Validate();
        if (Mounts is null) throw new InvalidOperationException($"{SectionName}:Mounts is missing.");
        Mounts.Validate();
        if (Battlegrounds is null) throw new InvalidOperationException($"{SectionName}:Battlegrounds is missing.");
        Battlegrounds.Validate();
        if (Chaos is null) throw new InvalidOperationException($"{SectionName}:Chaos is missing.");
        Chaos.Validate();
        if (Policy is null) throw new InvalidOperationException($"{SectionName}:Policy is missing.");
        Policy.Validate();
    }
}

/// <summary>How much real-client packet work a bot session does (<see cref="PlayerbotOptions.ClientFidelity"/>).</summary>
public enum PlayerbotClientFidelity
{
    /// <summary>No update-object blocks are built for bots and only the packets bots read back are kept.</summary>
    Lean,

    /// <summary>Bots are treated like real clients: every packet is built, compressed and copied into the bot's receive ring (load-test mode).</summary>
    Full,
}

/// <summary>Who may invite a managed bot into a group (<see cref="PlayerbotPartyOptions.InvitePolicy"/>).</summary>
public enum PlayerbotInvitePolicy
{
    /// <summary>Only the names in <see cref="PlayerbotPartyOptions.Allowlist"/>.</summary>
    None,

    /// <summary>The allowlist, the bot's guild mates and players on the bot's own friend list (a player's own list does not count).</summary>
    GuildOrFriends,

    /// <summary>Every player.</summary>
    Anyone,

    /// <summary>
    /// The allowlist, and a player the bot knows well enough: in the bot's guild or on its friend list, with affinity (the bot's memory of
    /// the player; War Mode's layer while it is on) of at least <see cref="PlayerbotPartyOptions.AcceptInviteAffinity"/>, or on the same
    /// quest or dungeon goal as the bot. A rival is always declined.
    /// </summary>
    Affinity,
}

/// <summary>What a grouped bot answers to a group loot roll (<see cref="PlayerbotPartyOptions.LootRoll"/>).</summary>
public enum PlayerbotLootRoll
{
    Pass,
    Greed,
    Need,
}

/// <summary>
/// <c>World:Playerbots:Party</c>: a bot in a real player's group (vmangos PartyBotAI; mangoszero playerbot actions). The bot
/// accepts or declines invitations by <see cref="InvitePolicy"/>, follows and assists its master, takes the master's whispered or
/// party-chat commands, answers loot rolls at once and goes where the master goes (docs/areas/playbots.md, Party bots).
/// </summary>
public sealed class PlayerbotPartyOptions
{
    /// <summary>The most names <see cref="Allowlist"/> may hold.</summary>
    public const int MaxAllowlist = 64;

    /// <summary>Who may invite a bot (<see cref="PlayerbotInvitePolicy.Affinity"/> by default).</summary>
    public PlayerbotInvitePolicy InvitePolicy { get; set; } = PlayerbotInvitePolicy.Affinity;

    /// <summary>The affinity toward a stranger that makes a bot accept its group invitation under <see cref="PlayerbotInvitePolicy.Affinity"/> (-100..100).</summary>
    public int AcceptInviteAffinity { get; set; } = 10;

    /// <summary>Character names whose invitations a bot always accepts, whatever <see cref="InvitePolicy"/> says (case-insensitive).</summary>
    public string[] Allowlist { get; set; } = [];

    /// <summary>
    /// Teleport to the master when the follower cannot keep up by walking (vmangos PartyBotAI .goname; on by default). Off: the
    /// bot never teleports to its master, whatever <see cref="PlayerbotFollowOptions.CatchUpMode"/> says.
    /// </summary>
    public bool TeleportToLeader { get; set; } = true;

    /// <summary>Revive a dead bot in place when vmangos <c>PartyBotAI::ShouldAutoRevive</c> says so (on by default); otherwise it runs back.</summary>
    public bool AutoRevive { get; set; } = true;

    /// <summary>The vote on every group loot roll (<see cref="PlayerbotLootRoll.Need"/> by default: Need on upgrades, Greed otherwise).</summary>
    public PlayerbotLootRoll LootRoll { get; set; } = PlayerbotLootRoll.Need;

    /// <summary>Seconds a bot waits for an offline or departed master before it leaves the group and goes back to its own goals (1..3600).</summary>
    public int MasterTimeoutSeconds { get; set; } = 60;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Party";
        if (!Enum.IsDefined(InvitePolicy)) throw new InvalidOperationException($"{section}: InvitePolicy must be None, GuildOrFriends, Anyone or Affinity.");
        if (AcceptInviteAffinity is < -100 or > 100) throw new InvalidOperationException($"{section}: AcceptInviteAffinity must be -100..100.");
        if (!Enum.IsDefined(LootRoll)) throw new InvalidOperationException($"{section}: LootRoll must be Pass, Greed or Need.");
        if (MasterTimeoutSeconds is < 1 or > 3600) throw new InvalidOperationException($"{section}: MasterTimeoutSeconds must be 1..3600.");
        if (Allowlist is null || Allowlist.Length > MaxAllowlist
            || Allowlist.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 12 || !name.All(char.IsLetter))
            || Allowlist.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Allowlist.Length)
            throw new InvalidOperationException($"{section}: Allowlist must hold at most {MaxAllowlist} distinct character names (letters only, at most 12).");
    }
}

/// <summary>
/// <c>World:Playerbots:Scenarios</c>: whether an Administrator may run registered bot scenarios against the live world
/// (<c>.playerbot scenario run</c>). Scenarios create/start their own managed bots in scripted mode and use world-thread
/// setup helpers (placement, items, money, spells) that are never reachable by ordinary players.
/// </summary>
public sealed class PlayerbotScenarioOptions
{
    /// <summary>Let an Administrator run registered bot scenarios against the live world (off by default).</summary>
    public bool Enabled { get; set; }

    /// <summary>Wall-clock bound of one scenario run in seconds (5..600).</summary>
    public int MaxDurationSeconds { get; set; } = 120;

    /// <summary>Default bound of one WaitUntil step in seconds (1..300).</summary>
    public int StepTimeoutSeconds { get; set; } = 20;
}
