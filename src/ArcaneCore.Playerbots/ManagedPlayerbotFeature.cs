using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Diagnostics;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Teleport;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Microsoft.Extensions.Options;
using ArcaneCore.World.Bots;
using BotChat = ArcaneCore.World.Playerbots.Chat;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Owns persistent autonomous P0 players, their ordinary sessions and save barriers.
/// <para>
/// Faults: an exception out of a bot's update (brain, scripted controller or motion) is an action fault. It is logged with the
/// whole exception and the bot is quarantined, not disabled: its session closes, its character is saved, the record turns
/// Faulted with the fault as its error code but keeps DesiredEnabled, and after <see cref="PlayerbotOptions.FaultBackoffSeconds"/>
/// (doubled for each further fault) it logs in again, autonomous. Only <see cref="PlayerbotOptions.MaxFaults"/> faults within
/// <see cref="PlayerbotOptions.FaultWindowSeconds"/> turn DesiredEnabled off. One fault used to disable the bot for good
/// (Dawnrover and Ironwander, 2026-10-07). The fault history lives in this process; a world restart restores every desired bot.
/// </para>
/// </summary>
public sealed partial class ManagedPlayerbotFeature(IServiceProvider services, ILogger<ManagedPlayerbotFeature> logger) : IDetachableWorldFeature, IPlayerbotService
{
    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private IServiceScopeFactory scopes => services.GetRequiredService<IServiceScopeFactory>();
    private SessionRegistry registry => services.GetRequiredService<SessionRegistry>();
    private OpcodeTable opcodes => services.GetService<OpcodeTable>() ?? WorldServiceCollectionExtensions.BuildOpcodeTable();
    private IOptions<WorldSessionOptions> sessionOptions => services.GetService<IOptions<WorldSessionOptions>>() ?? Options.Create(new WorldSessionOptions());
    private CharacterSaveQueue saves => services.GetRequiredService<CharacterSaveQueue>();

    /// <summary>Bot-led groups for group content (<see cref="PlayerbotGroupCoordinator"/>; absent in a minimal host).</summary>
    private PlayerbotGroupCoordinator? coordinator
    {
        get
        {
            if (!_coordinatorResolved)
            {
                _coordinator = services.GetService<PlayerbotGroupCoordinator>();
                _coordinatorResolved = true;
            }

            return _coordinator;
        }
    }
    private PlayerbotGroupCoordinator? _coordinator;
    private bool _coordinatorResolved;

    /// <summary>Bots in battlegrounds (<see cref="PlayerbotBattlegroundCoordinator"/>; absent in a minimal host).</summary>
    private PlayerbotBattlegroundCoordinator? battlegrounds
    {
        get
        {
            if (!_battlegroundsResolved)
            {
                _battlegrounds = services.GetService<PlayerbotBattlegroundCoordinator>();
                _battlegroundsResolved = true;
            }

            return _battlegrounds;
        }
    }
    private PlayerbotBattlegroundCoordinator? _battlegrounds;
    private bool _battlegroundsResolved;
    private static readonly Comparison<ActiveBot> CompareByBotId = static (a, b) => a.Record.BotId.CompareTo(b.Record.BotId);
    private static readonly Func<ActiveBot, Guid> BotIdOf = static bot => bot.Record.BotId;
    private static readonly ManagedActionBudget EmptyActionBudget = new(0);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveBot> _active = new();
    private PlayerbotStatus[] _snapshot = [];
    private readonly object _snapshotGate = new();
    // Allocation per tick (docs/integration/perf-limits-20261008.md): the running bots in BotId order are re-sorted only when the
    // set changes (_activeVersion), and the snapshot is rebuilt only when a running bot's status changed or PublishStopped
    // touched it (_snapshotEpoch). Before, both were rebuilt every tick: ~100 bytes per bot per tick in PlayerbotStatus alone.
    private int _activeVersion;
    private int _orderedVersion = -1;
    private ActiveBot[] _ordered = [];
    private readonly List<ActiveBot> _orderedScratch = [];
    private int _snapshotEpoch;
    private int _builtEpoch = -1;
    private int _builtCount = -1;
    private int _builtActiveVersion = -1;
    private uint _snapshotElapsedMs;
    private readonly List<PlayerbotGroupCoordinator.PlayerbotGroupBot> _groupBotsScratch = [];
    private readonly List<PlayerbotBattlegroundCoordinator.PlayerbotBattlegroundBot> _bgBotsScratch = [];
    private readonly HashSet<Guid> _republishedScratch = [];
    private readonly List<PlayerbotStatus> _freshSnapshotScratch = [];
    private readonly List<PlayerbotStatus> _nextSnapshotScratch = [];
    private WorldRuntime? _world;
    private bool _stopping;
    private readonly PlayerbotThinkRotation _rotation = new();
    private readonly PlayerbotCadenceStats _cadence = new();
    /// <summary>The main lane's measured full-pass time (<see cref="PlayerbotMainPass"/>): sizes the motion lease cap and the think-starved bound.</summary>
    private readonly PlayerbotMainPass _mainPass = new();

    /// <summary>The main lane's measured full-pass time in ms, 0 before the first main-lane gap (tests).</summary>
    internal uint MainPassMs => _mainPass.Ms;

    /// <summary>Test seam: sets the main-lane pass estimate; with <paramref name="pin"/> it is then held there (the samples change nothing).</summary>
    internal void SeedMainPassForTests(uint ms, bool pin = false)
    {
        _mainPass.Seed(ms);
        _mainPass.Pinned = pin;
    }
    private readonly PlayerbotThinkBudget _thinkBudget = new();
    private double _lastOwnMs;
    private double _lastBudgetMs = PlayerbotThinkRotation.TickBudgetMs;

    /// <summary>The longest silence (ms of world time) of any running bot after the last tick: the first think lane yields time to the main lane as it nears the starvation bound (<see cref="PlayerbotThinkStarvation.PriorityScale"/>).</summary>
    private uint _oldestSilentMs;
    // Per-think attribution (PlayerbotThinkProbe, PlayerbotSlowThinkLog): which bot, goal and phase a slow think spent its time in.
    private readonly PlayerbotThinkProbe _probe = new();
    private PlayerbotPathJobs? _pathJobs;
    private uint _lastPathJobSweepMs;

    /// <summary>The bots' far path searches as pool jobs (<see cref="PlayerbotPathJobs"/>); created on first use, world thread.</summary>
    private PlayerbotPathJobs PathJobs => _pathJobs ??= new PlayerbotPathJobs(_options,
        PathJobRunnerForTests ?? new NavJobSystemRunner(() => NavJobSystem.SharedIfStarted ?? NavJobSystem.Shared(PathJobThreads(), logger)),
        () => unchecked((uint)ClockMs), logger);

    /// <summary>Test seam: the runner the path-job service submits to instead of the shared pool. Set before the first think creates the service.</summary>
    internal IPlayerbotPathJobRunner? PathJobRunnerForTests { get; set; }

    /// <summary>
    /// The pool's size when this feature is the first to start it: the creatures' <c>Creatures:Movement:PathJobThreads</c> from the
    /// configuration (their default when unset), so a bot-first start does not size the pool the creatures would have. A pool already
    /// running keeps its own size (<see cref="NavJobSystem.Shared"/> warns once when the counts differ).
    /// </summary>
    private int PathJobThreads()
    {
        var creatures = new ArcaneCore.Game.Creatures.CreatureOptions();
        if (services.GetService<Microsoft.Extensions.Configuration.IConfiguration>() is { } configuration)
        {
            Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(
                configuration.GetSection(ArcaneCore.Game.Creatures.CreatureOptions.SectionName), creatures);
        }

        return creatures.Movement.PathJobThreads;
    }

    /// <summary>The bots' path-job service; null until a think or a test created it.</summary>
    internal PlayerbotPathJobs? PathJobsForTests => _pathJobs;
    private PlayerbotSlowThinkLog? _slowThinks;

    /// <summary>
    /// Called after every think with the bot and the think's probe (world thread; benchmarks and tests). The probe is valid only
    /// during the call.
    /// </summary>
    internal Action<Guid, PlayerbotThinkProbe>? ThinkObserver { get; set; }

    /// <summary>
    /// A fixed wall time (ms) a tick spends on the bots' thinks, instead of the adaptive budget of <see cref="PlayerbotThinkBudget"/> (null in
    /// production). A test world on a manual clock sets it to infinity: with a real-time budget the bots visited per simulated tick depend
    /// on how loaded the machine is.
    /// </summary>
    internal double? ThinkBudgetMs { get; set; }

    /// <summary>The action budgets the last tick gave the combat lane and the main lane (tests).</summary>
    internal (int Combat, int Main) LastLaneActions { get; private set; }

    /// <summary>The scale the last tick gave the first think lane's time share (tests).</summary>
    internal double LastPriorityScale => _rotation.LastPriorityScale;

    /// <summary>The inputs of the last tick's think budget: tick interval, smoothed non-bot tick time and the time the bots already used (tests).</summary>
    internal (double TickIntervalMs, double NonBotMs, double SpentMs) LastThinkBudgetInputs { get; private set; }

    /// <summary>The think budget (ms) the last tick used (<c>.playerbot cadence</c>).</summary>
    internal double LastThinkBudgetMs => _lastBudgetMs;

    /// <summary>The smoothed wall time (ms) of one main-lane visit that the budget's coverage floor is built on (tests, metrics).</summary>
    internal double ThinkVisitMs => _thinkBudget.VisitMs;

    /// <summary>The cadence of the last finished 60 s window (null before the first one).</summary>
    internal PlayerbotCadenceReport? LastCadence => _cadence.Last;

    /// <summary>The slow-think log (null before the first think).</summary>
    internal PlayerbotSlowThinkLog? SlowThinks => _slowThinks;
    private uint _checkpointMs;
    private Task _checkpoint = Task.CompletedTask;

    /// <summary>
    /// The last checkpoint started (world thread: read it there, after the tick that started it). Tests step the manual world clock
    /// one tick at a time and let each checkpoint finish before game time moves on.
    /// </summary>
    internal Task LastCheckpoint => _checkpoint;
    private readonly ConcurrentDictionary<Guid, Quarantine> _quarantine = new();

    /// <summary>
    /// A refused revision-checked write means another writer owns the bot's row (a second world process on the same characters database), not that the bot is faulty:
    /// the bot leaves the active set without a fault. More than this many such refusals in one checkpoint is a split brain.
    /// </summary>
    internal const int SplitBrainConflictThreshold = 3;

    /// <summary>
    /// Set when a checkpoint saw more than <see cref="SplitBrainConflictThreshold"/> conflicts: no further playerbot writes are issued by this process until an operator
    /// clears it (<see cref="ClearSplitBrainAsync"/>) or the process restarts. Kept on the core's <see cref="ManagedBotRegistry"/>, so a detach does not forget it.
    /// </summary>
    private bool SplitBrainLatched
    {
        get => BotRegistry.SplitBrain;
        set => BotRegistry.SplitBrain = value;
    }

    /// <summary>The core-owned bots and seams (docs/design/hot-reload-tiers.md, section 4.1); a private one in a host without the world features.</summary>
    private ManagedBotRegistry BotRegistry => _botRegistry ??= services.GetService<ManagedBotRegistry>() ?? new ManagedBotRegistry(services);
    private ManagedBotRegistry? _botRegistry;
    private readonly ConcurrentDictionary<Guid, byte> _conflictLogged = new();

    /// <summary>Whether this process stopped writing playerbot rows because another world process owns them (see <see cref="SplitBrainConflictThreshold"/>).</summary>
    internal bool SplitBrain => SplitBrainLatched;

    /// <summary>Whether <see cref="ShutdownBeforeWorldStopAsync"/> has begun: bots stopping now are stopped by the shutdown itself, not by an operator or a fault. Any thread.</summary>
    internal bool IsStopping => Volatile.Read(ref _stopping);

    /// <summary>
    /// Thrown by a home change refused because split brain is latched. <see cref="Done"/> is how many bots it had already stored or cleared before the latch was seen
    /// (those are real and stay). The message is the refusal code.
    /// </summary>
    internal sealed class PlayerbotSplitBrainException(int done) : InvalidOperationException("playerbot-split-brain")
    {
        public int Done { get; } = done;
    }

    /// <summary>Bots currently in fault quarantine (tests).</summary>
    internal int QuarantineCount => _quarantine.Count;

    private long _clockMs;
    private Chat.PlayerbotChat? _chat;
    private BotSocial.PlayerbotSocialRecorder? _social;
    private BotSocial.PlayerbotPvpKillFeed? _pvpKills;

    /// <summary>The feed of this world's player deaths for the social memory (null before the attach; tests).</summary>
    internal BotSocial.PlayerbotPvpKillFeed? PvpKills => _pvpKills;

    /// <summary>
    /// The bots' social memory and the one seam events are recorded through (docs/areas/playbots-social.md, "Social memory"). Created on first
    /// use from the live <c>World:Playerbots:Social:Memory</c> options and the feature's clock; any thread.
    /// </summary>
    internal BotSocial.PlayerbotSocialRecorder Social => _social ?? CreateSocial();

    // Two threads may race to the first use: the loser's empty recorder is dropped and both see the winner's.
    private BotSocial.PlayerbotSocialRecorder CreateSocial()
    {
        var created = new BotSocial.PlayerbotSocialRecorder(
            new BotSocial.PlayerbotSocialMemory(_options.Social.Memory, () => _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds(), _options.Social.WarMode, logger), logger);
        return Interlocked.CompareExchange(ref _social, created, null) ?? created;
    }

    // The persisted home areas (managed_playerbot_home), by bot id: loaded at startup, written by .playerbot home.
    private readonly ConcurrentDictionary<Guid, PlayerbotHome> _homes = new();
    private readonly ConcurrentDictionary<Guid, PlayerbotSetAside[]> _loadedSetAsides = new();
    private readonly TimeProvider _setAsideTime = services.GetService<TimeProvider>() ?? TimeProvider.System;
    private int _savedSharedSetAsideVersion;
    private bool _homeStore;
    private Chat.HttpBotChatClient? _chatClient;
    private Chat.Inference.InProcessBotChatClient? _chatInProcess;
    private PlayerbotNavigationExecutor? _navigationExecutor;
    private Chaos.PlayerbotChaosEngine? _chaos;
    private Policy.PlayerbotPolicyEngine? _policy;
    private uint _policyReportMs;
    private bool _policyReportArmed;

    /// <summary>World time this feature has seen, in ms (the fault backoff and window run on it).</summary>
    private long ClockMs => Interlocked.Read(ref _clockMs);

    /// <summary>The bots' chat service (null before <see cref="Attach"/> and after shutdown; <c>.playerbot chat status</c>).</summary>
    internal Chat.PlayerbotChat? Chat => _chat;

    /// <summary>The in-process chat model provider (null before <see cref="Attach"/> and after shutdown; its <c>.playerbot chat status</c> line).</summary>
    internal Chat.Inference.InProcessBotChatClient? ChatInProcess => _chatInProcess;

    /// <summary>The live playerbot configuration options.</summary>
    internal PlayerbotOptions BotOptions => _options;

    /// <summary>The number of currently active bot sessions.</summary>
    internal int RunningCount => _active.Count;

    /// <summary>Adversarial server-breaker chaos engine (<c>.playerbot chaos</c>).</summary>
    internal Chaos.PlayerbotChaosEngine Chaos => _chaos ??= new Chaos.PlayerbotChaosEngine(_options, opcodes, logger, _setAsideTime);

    /// <summary>The goal policy engine (null when <c>World:Playerbots:Policy:Enabled</c> is false; docs/areas/playbots.md, Goal policy).</summary>
    internal Policy.PlayerbotPolicyEngine? PolicyEngine => _policy;

    internal bool TryResolveBotId(string idOrName, out Guid botId, out string botName)
    {
        if (Guid.TryParse(idOrName, out Guid parsed))
        {
            if (_active.TryGetValue(parsed, out ActiveBot? active))
            {
                botId = parsed;
                botName = active.Name;
                return true;
            }
            PlayerbotStatus? snap = Volatile.Read(ref _snapshot).FirstOrDefault(s => s.BotId == parsed);
            if (snap is not null)
            {
                botId = snap.BotId;
                botName = snap.Name;
                return true;
            }
        }
        else
        {
            ActiveBot? active = _active.Values.FirstOrDefault(b => b.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (active is not null)
            {
                botId = active.Record.BotId;
                botName = active.Name;
                return true;
            }
            PlayerbotStatus? snap = Volatile.Read(ref _snapshot).FirstOrDefault(s => s.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (snap is not null)
            {
                botId = snap.BotId;
                botName = snap.Name;
                return true;
            }
        }

        botId = Guid.Empty;
        botName = string.Empty;
        return false;
    }

    internal (int Bots, int Probes, long FaultsBefore, long FaultsAfter) RunChaosBurst(int probesPerBot, string? idOrName = null)
    {
        long before = Chaos.FaultsCaught;
        ActiveBot[] bots = _active.Values.OrderBy(b => b.Record.BotId).ToArray();
        int botCount = 0;
        int executed = 0;
        int maxBots = Math.Max(1, _options.Chaos.ActiveChaosBots);
        for (int i = 0; i < bots.Length; i++)
        {
            ActiveBot active = bots[i];
            if (active.Paused || active.Session.State != SessionState.InWorld || active.Session.Player is not { IsInWorld: true } player)
                continue;

            if (idOrName is not null)
            {
                bool match = Guid.TryParse(idOrName, out Guid id)
                    ? active.Record.BotId == id
                    : active.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
            }
            else if (!Chaos.IsAssigned(active.Record.BotId) && botCount >= maxBots)
            {
                continue;
            }

            executed += Chaos.RunBurst(active.Record.BotId, active.Session, player, probesPerBot);
            botCount++;
            if (idOrName is not null) break;
        }

        return (botCount, executed, before, Chaos.FaultsCaught);
    }

    // --- lifecycle (docs/design/hot-reload-tiers.md, sections 4.2 and 4.4 2a) -------------------------------------------------

    /// <summary>The refusal code of an operation asked for while the feature is being detached (a quarantine retry reschedules on it).</summary>
    internal const string PlayerbotsDetaching = "playerbots-detaching";

    private const string HandoverSetAsides = "playerbots.setAsides";
    private const string HandoverControllers = "playerbots.controllers";
    private const string HandoverSharedSetAsides = "playerbots.sharedSetAsides";
    private const string HandoverLoadedSetAsides = "playerbots.loadedSetAsides";
    private const string HandoverRelations = "playerbots.socialRelations";
    private const string HandoverHomes = "playerbots.homes";
    private const string HandoverHomeStore = "playerbots.homeStore";
    private const string HandoverClock = "playerbots.clockMs";
    private const string HandoverQuarantine = "playerbots.quarantine";
    private const string HandoverConflicts = "playerbots.conflictLogged";
    private const string HandoverSnapshot = "playerbots.snapshot";
    private const string HandoverMainPass = "playerbots.mainPassMs";

    private FeatureSubscriptions? _subscriptions;
    private volatile bool _detaching;

    /// <summary>
    /// Handover. The bots stay logged in across a detach: their rows, scopes and sessions are the core's (<see cref="ManagedBotRegistry"/>),
    /// and the next attach builds new brains and party AIs around them. What a brain remembers that matters (its set-asides) and the
    /// feature's own state (homes, the shared set-asides, the fault quarantine, the clock, the status lines, scripted controllers) are
    /// handed over; the policy engine, the navigation executor and the chat services are stopped and started again.
    /// </summary>
    public DetachKind DetachKind => DetachKind.Handover;

    /// <summary>Whether the feature is being detached, or is detached: no update runs and every operation refuses (<see cref="PlayerbotsDetaching"/>). Any thread.</summary>
    internal bool IsDetaching => _detaching;

    // World thread (the spell system raises it). A positive aura on a managed bot from another player of its team who is not in its group is
    // help the bot thanks; the recorder counts a pair once per fight, so a buff that comes with a rescue is not thanked twice.
    private void OnAuraAdded(WorldRuntime world, SpellAuraHolder holder)
    {
        if (!holder.CasterGuid.IsPlayer || holder.IsRemoved || holder.Spell.IsPassive || !holder.Spell.IsPositive) return;
        if (holder.Target is not Player target || target.Guid == holder.CasterGuid || !Social.IsManaged(target.Guid)) return;
        if (world.FindOnlinePlayer(holder.CasterGuid) is not { } caster || caster.Team != target.Team) return;
        if (services.GetService<ArcaneCore.World.Social.SocialFeature>()?.Context.Groups.AreInSameGroup(target.Guid, caster.Guid) == true) return;
        Social.NoteHelp(caster, target, BotSocial.PlayerbotThanksKind.Buff);
    }

    public void Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(nameof(ManagedPlayerbotFeature)));

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(subscriptions);
        _ = PlayerbotAnomalyHistory.Shared; // the bot anomaly history hears the anomaly ledger from now on (the core no longer touches it)
        _options.Validate();
        // A service still running is kept, not replaced: a quiesce that failed before it stopped them is undone by a detach and
        // re-attach in one tick (FeatureHost), and a second policy engine, navigation worker or chat service would leak beside the first.
        if (_options.Policy.Enabled && _policy is null) StartPolicy();
        if (_chat is null)
        {
            // A chat service whose stop was interrupted part way leaves its provider behind: it goes, and the chain is built afresh.
            _chatInProcess?.Dispose();
            _chatInProcess = null;
            _chatClient?.Dispose();
            _chatClient = null;
            // The in-process model provider (Chat:InProcess) sits in front of the transport; every other provider kind passes through.
            _chatInProcess = new Chat.Inference.InProcessBotChatClient(
                services.GetService<Chat.IBotChatClient>() ?? (_chatClient = new Chat.HttpBotChatClient()), _options,
                services.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory, logger);
            _chat = new Chat.PlayerbotChat(_options, _chatInProcess,
                services.GetService<Chat.IBotChatEnvironment>() ?? new Chat.ProcessBotChatEnvironment(),
                services.GetService<TimeProvider>() ?? TimeProvider.System, logger);
            if (_options.Enabled) _chat.LogConfiguration();
            _chat.Social = Social;
            BotSocial.PlayerbotSocialRecorder recorder = Social;
            // A line addressed to a bot is an exchange with its sender; the recorder never throws into the chat service.
            _chat.LineHeard += line =>
            {
                if (line.AddressesBot && line.SenderGuid != line.BotGuid) recorder.NoteChatLine(line.BotId, line.BotGuid, line.SenderGuid, line.Text);
                // A say within earshot from the player a bot has just opened with is its answer, named or not (BotSocial.PlayerbotInitiativeLane).
                else if (line.Type == ChatType.Say && !line.SenderIsBot && line.SenderGuid != line.BotGuid) recorder.NoteHeardSay(line.BotId, line.SenderGuid, line.Text);
                // A yell or a say that calls for help is a call every bot in earshot may answer (BotSocial.PlayerbotHelpLane decides who).
                if (line.SenderGuid != line.BotGuid && line.Type is ChatType.Yell or ChatType.Say && BotSocial.PlayerbotHelpRules.LooksLikeHelpCall(line.Text))
                    recorder.NoteHelpCall(line.BotGuid, line.SenderGuid);
            };
        }

        if (coordinator is { } groupCoordinator)
        {
            groupCoordinator.Social = Social;
            groupCoordinator.ChatSafety = _chat?.Safety;
        }

        _world = world;
        _sharedSetAsides.World = world;
        // A player killed by another player (or its pet or totem) is rivalry for the managed bot on either side. The feed hears every map of this
        // world, so a kill reaches only this world's memory, exactly once, whether or not a bot has thought since it changed maps; the detach takes it off.
        BotSocial.PlayerbotSocialRecorder pvpRecorder = Social;
        _pvpKills = new BotSocial.PlayerbotPvpKillFeed(world, pvpRecorder.NotePvpKill);
        subscriptions.Add(_pvpKills);
        // A buff on a managed bot from a same-faction player outside its group is help it will thank (BotSocial.PlayerbotGratitudeLane).
        if (services.GetService<SpellFeature>() is { } spellFeature)
            subscriptions.Subscribe<Action<SpellAuraHolder>>(
                holder => OnAuraAdded(world, holder),
                h => spellFeature.System.HolderAdded += h, h => spellFeature.System.HolderAdded -= h);
        if (_navigationExecutor is null && _options.AsyncNavigationEnabled && WorldCollision.Of(world).Pathfinder is NavMeshPathfinder)
        {
            ILogger ownerLogger = logger;
            _navigationExecutor = new PlayerbotNavigationExecutor(
                dataset => NavMeshPathfinder.CreateIndependent(dataset, ownerLogger));
        }
        if (handover is not null) RestoreHandover(handover, world);
        subscriptions.OnUpdated(world, Update);
        subscriptions.Add(BotRegistry.UseSessionPolicy(new SessionPolicy(_options)));
        subscriptions.Add(BotRegistry.UseRouteSource(new PlayerbotRouteSource())); // the map feed reads the route a bot walks (docs/areas/map-feed.md)
        if (services.GetService<IWorldLifecycleHooks>() is { } hooks)
        {
            // The world host runs these (WorldHost): the restore once the world is started, the shutdown before it stops.
            subscriptions.Add(hooks.Register(WorldLifecycleStage.AfterWorldStart, "playerbots.restore", StartupAsync));
            subscriptions.Add(hooks.Register(WorldLifecycleStage.BeforeWorldStop, "playerbots.shutdown", _ => ShutdownBeforeWorldStopAsync()));
        }

        // Bots still logged in from before a detach: new brains and party AIs around the same sessions.
        Dictionary<Guid, PlayerbotSetAside[]>? setAsides = null;
        Dictionary<Guid, IPlayerbotController>? controllers = null;
        handover?.TryGet(HandoverSetAsides, out setAsides);
        handover?.TryGet(HandoverControllers, out controllers);
        long unixNow = _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (ManagedBot entry in BotRegistry.Bots)
        {
            if (_active.ContainsKey(entry.BotId)) continue;
            PlayerbotBrain brain = NewBrain(entry.Session, entry.BotId, restoreLoaded: false);
            if (setAsides?.TryGetValue(entry.BotId, out PlayerbotSetAside[]? rows) == true)
                brain.Suspensions.Restore(rows, world.NowMs, unixNow);
            ActiveBot active = NewActiveBot(entry, brain);
            active.SavedSetAsideVersion = -1; // written again at the next checkpoint
            active.SavedRelationVersion = -1;
            if (controllers?.TryGetValue(entry.BotId, out IPlayerbotController? controller) == true) active.Controller = controller;
            if (_active.TryAdd(entry.BotId, active)) Interlocked.Increment(ref _activeVersion);
        }

        _subscriptions = subscriptions;
        _detaching = false;
    }

    /// <summary>
    /// Stop updating (the flag), wait for the last checkpoint and for the operation in flight (a start, a stop, a home change), then stop the
    /// policy engine, the navigation executor and the chat services. The bots stay logged in.
    /// </summary>
    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        _detaching = true;
        if (_world is { } world && !world.IsStopped)
        {
            // Read on the world thread, after which no update of ours starts another checkpoint (it sees the flag).
            Task checkpoint = world.IsWorldThread ? _checkpoint
                : await world.InvokeAsync(() => _checkpoint, cancellationToken).ConfigureAwait(false);
            await checkpoint.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The operation in flight finishes; every later one refuses (playerbots-detaching).
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        _operations.Release();
        await StopServicesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// World thread (the feature host): stop every brain, hand the state over and forget it. The sessions stay open.
    /// <para>
    /// Throws, having changed nothing, while an operation (a start, stop, create, home change, fleet reset or checkpoint) still holds
    /// the operation lock: it works on <see cref="_active"/>, the homes and the registry off the world thread, and clearing them under it
    /// would race it. That is the case of a quiesce that timed out waiting for the lock; the host then rolls the detach back (re-attach
    /// from an empty handover, which keeps every bot and service as it is) and reports the detach failed.
    /// </para>
    /// </summary>
    public void Detach(FeatureHandover handover)
    {
        ArgumentNullException.ThrowIfNull(handover);
        if (!_operations.Wait(0))
        {
            throw new InvalidOperationException("a playerbot operation is still running; nothing was detached");
        }

        try
        {
            DetachHeld(handover);
        }
        finally
        {
            _operations.Release();
        }
    }

    /// <summary>The body of <see cref="Detach"/>, with the operation lock held.</summary>
    private void DetachHeld(FeatureHandover handover)
    {
        _pathJobs?.ForgetAll();
        long unixNow = _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds();
        var setAsides = new Dictionary<Guid, PlayerbotSetAside[]>();
        var controllers = new Dictionary<Guid, IPlayerbotController>();
        foreach (ActiveBot active in _active.Values)
        {
            lock (active.SetAsideGate)
            {
                setAsides[active.Entry.BotId] = active.Brain.Suspensions.Export(active.Record.BotId, active.Record.CharacterId,
                    active.Session.World.NowMs, unixNow);
            }

            // A hot reload ends the posts (memory only): the PvP flag a post turned on goes off before the brain is dropped.
            if (_world is { IsStopped: false } held)
            {
                if (held.IsWorldThread) ReleaseDefendFlag(active);
                else _ = held.InvokeAsync(() => { ReleaseDefendFlag(active); return true; });
            }

            active.Brain.Stop();
            if (Interlocked.Exchange(ref active.Controller, null) is { } controller) controllers[active.Entry.BotId] = controller;
        }

        uint nowMs = _world?.NowMs ?? 0;
        handover.Set(HandoverSetAsides, setAsides);
        handover.Set(HandoverControllers, controllers);
        handover.Set(HandoverSharedSetAsides, _sharedSetAsides.Export(nowMs, unixNow));
        handover.Set(HandoverLoadedSetAsides, new Dictionary<Guid, PlayerbotSetAside[]>(_loadedSetAsides));
        handover.Set(HandoverRelations, Social.Memory.SnapshotAll());
        handover.Set(HandoverHomes, _homes.Values.ToArray());
        handover.Set(HandoverHomeStore, _homeStore);
        handover.Set(HandoverClock, ClockMs);
        handover.Set(HandoverQuarantine, new Dictionary<Guid, Quarantine>(_quarantine));
        handover.Set(HandoverConflicts, _conflictLogged.Keys.ToArray());
        handover.Set(HandoverSnapshot, Volatile.Read(ref _snapshot));
        handover.Set(HandoverMainPass, _mainPass.Ms);

        _active.Clear();
        Interlocked.Increment(ref _activeVersion);
        _ordered = [];
        _homes.Clear();
        _loadedSetAsides.Clear();
        _quarantine.Clear();
        _conflictLogged.Clear();
        lock (_snapshotGate)
        {
            Volatile.Write(ref _snapshot, []);
            _snapshotEpoch++;
        }

        _subscriptions = null;
        _world = null;
    }

    /// <summary>Takes the main-lane pass estimate over from the previous feature (it is a property of the population, not of the code).</summary>
    private void RestoreMainPass(FeatureHandover handover)
    {
        if (handover.TryGet(HandoverMainPass, out uint mainPassMs)) _mainPass.Seed(mainPassMs);
    }

    /// <summary>Test seam: <see cref="RestoreMainPass"/> alone.</summary>
    internal void RestoreMainPassForTests(FeatureHandover handover) => RestoreMainPass(handover);

    /// <summary>Put back what <see cref="Detach"/> handed over (before the bots are rebuilt).</summary>
    private void RestoreHandover(FeatureHandover handover, WorldRuntime world)
    {
        long unixNow = _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds();
        if (handover.TryGet(HandoverClock, out long clock)) Interlocked.Exchange(ref _clockMs, clock);
        RestoreMainPass(handover);
        if (handover.TryGet<PlayerbotRelation[]>(HandoverRelations, out var relations)) Social.Memory.Load(relations);
        if (handover.TryGet<PlayerbotHome[]>(HandoverHomes, out var homes))
            foreach (PlayerbotHome home in homes) _homes[home.BotId] = home;
        if (handover.TryGet(HandoverHomeStore, out bool homeStore)) _homeStore = homeStore;
        if (handover.TryGet<Dictionary<Guid, PlayerbotSetAside[]>>(HandoverLoadedSetAsides, out var loaded))
            foreach ((Guid botId, PlayerbotSetAside[] rows) in loaded) _loadedSetAsides[botId] = rows;
        if (handover.TryGet<PlayerbotSetAside[]>(HandoverSharedSetAsides, out var shared))
            _sharedSetAsides.Restore(shared, world.NowMs, unixNow);
        Volatile.Write(ref _savedSharedSetAsideVersion, -1); // written again at the next checkpoint
        if (handover.TryGet<Dictionary<Guid, Quarantine>>(HandoverQuarantine, out var quarantine))
            foreach ((Guid botId, Quarantine entry) in quarantine) _quarantine[botId] = entry;
        if (handover.TryGet<Guid[]>(HandoverConflicts, out var conflicts))
            foreach (Guid botId in conflicts) _conflictLogged[botId] = 0;
        if (handover.TryGet<PlayerbotStatus[]>(HandoverSnapshot, out var snapshot))
        {
            lock (_snapshotGate)
            {
                Volatile.Write(ref _snapshot, snapshot);
                _snapshotEpoch++;
            }
        }
    }

    /// <summary>Stop the policy engine, the navigation executor and the chat services (a detach; the next attach starts them again).</summary>
    private async Task StopServicesAsync()
    {
        // Each field is taken before its service is stopped, so an attach running meanwhile (a quiesce the host gave up on while this
        // ran) never ends up with two of a service. It can end up with none of one (it kept a service this then stopped) until the
        // next attach; the bot code treats a missing chat, policy or navigation worker as off.
        if (Interlocked.Exchange(ref _policy, null) is { } policy) await policy.DisposeAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _navigationExecutor, null) is { } navigationExecutor) await navigationExecutor.DisposeAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _chat, null) is { } chat) await chat.DisposeAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _chatInProcess, null)?.Dispose();
        Interlocked.Exchange(ref _chatClient, null)?.Dispose();
    }

    /// <summary>Test seam: hold the operation lock (as a long fleet reset, start or checkpoint does) until the result is disposed.</summary>
    internal async Task<IDisposable> HoldOperationForTestsAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        return new OperationHold(_operations);
    }

    private sealed class OperationHold(SemaphoreSlim operations) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) operations.Release();
        }
    }

    /// <summary>
    /// <see cref="PlayerbotInvitePolicy.Affinity"/>: the bot's memory of <paramref name="inviter"/> (rival; affinity at least
    /// <c>AcceptInviteAffinity</c>, through <c>EffectiveAffinity</c>) and whether they share a quest or dungeon goal. World thread.
    /// </summary>
    private PlayerbotGroupInvites.Standing InviteStandingOf(Guid botId, Player bot, Player inviter)
    {
        bool rival = false, wellKnown = false;
        if (Social.Memory.TryGet(botId, inviter.Guid.Value, out PlayerbotRelation relation))
        {
            rival = Social.Memory.IsRival(relation);
            wellKnown = Social.Memory.EffectiveAffinity(relation) >= _options.Party.AcceptInviteAffinity;
        }

        return new PlayerbotGroupInvites.Standing(rival, wellKnown, !rival && coordinator?.SharesGoal(botId, inviter) == true);
    }

    /// <summary>The feature's driver of one bot around its core entry: the party AI wired to the coordinator, the brain given.</summary>
    private ActiveBot NewActiveBot(ManagedBot entry, PlayerbotBrain brain)
    {
        var party = new PlayerbotPartyAI(entry.Session, _options, _chat, entry.BotId)
        {
            // A bot-led group's invitations and loot rolls (PlayerbotGroupCoordinator); everything else is the party AI's own rules.
            AcceptsBotGroup = (member, inviter) => coordinator?.AcceptsInvite(member, inviter) == true,
            GroupLootVote = (member, item) => coordinator?.LootVote(member, item),
            // A group whose lead its master handed to a bot (PlayerbotGroupCoordinator.Handoff.cs) owns the bot until an order takes the lead back.
            GroupOwns = (member, master, command) => coordinator?.OwnsAgainst(member, master, command) == true,
            // What the bot's memory and goal say about an inviter (PlayerbotInvitePolicy.Affinity).
            InviteStanding = (member, inviter) => InviteStandingOf(entry.BotId, member, inviter),
        };
        party.Social = Social;
        Social.Track(entry.BotId, entry.Record.CharacterId);
        var active = new ActiveBot(entry, brain, party) { SavedRelationVersion = Social.Memory.Version(entry.BotId) };
        active.Fight = new BotSocial.PlayerbotFightSocial(entry.BotId, _options.Social.Memory)
        {
            Recorder = Social,
            CallForHelp = (bot, nearest) => SayCallForHelp(active, bot, nearest),
        };
        // The chat persona's goal: the bot-led group's while the coordinator drives the bot, otherwise the brain's (GoalOf).
        active.Party.BrainGoal = () => { var goal = GoalOf(active); return (goal.Goal, goal.QuestId); };
        active.Party.BrainDiagnostics = p => FormatBotDiagnostics(active, p);
        return active;
    }

    /// <summary>
    /// A bot losing a fight calls for help: one line through the chat service (its limits, safety screen and disclosure rules apply) and the help
    /// text emote at the nearest asked unit. True when either was taken. World thread.
    /// </summary>
    private bool SayCallForHelp(ActiveBot active, Player bot, Player nearest)
    {
        coordinator?.NoteHelpCall(active.Record.BotId, active.Brain.QuestId);
        if (_chat is not { IsActive: true } chat) return false;
        Guid botId = active.Record.BotId;
        var speech = new BotChat.BotSpeech(BotChat.BotSpeechChannel.Yell, string.Empty, "call-for-help", BotChat.BotSpeechPriority.High);
        var topic = new BotChat.BotChatTopic("callhelp", "You are losing a fight and a healthy player is nearby. Call out for help in one short line.",
            new Dictionary<string, string>(), string.Empty);
        var ask = new BotChat.BotChatAsk(botId, bot.Guid, active.Party.PersonaFor(bot), bot.Guid, bot.Name, ChatType.Yell,
            "You are losing a fight and call out for help.", FromMaster: false, InviteAllowed: false, SenderAccountId: 0, Topic: topic, Speech: speech);
        bool said = chat.TryAnnounce(ask) == BotChat.BotChatAdmission.Queued;
        bool waved = chat.TrySpeak(botId, bot.Guid, BotChat.BotSpeech.EmoteAt(BotSocial.PlayerbotEmoteMoods.HelpMe, nearest.Guid, "call-for-help")) == BotChat.BotSpeechAdmission.Queued;
        return said || waved;
    }

    /// <summary>The core's view of a bot session while this feature is attached (<see cref="IManagedBotSessionPolicy"/>).</summary>
    private sealed class SessionPolicy(PlayerbotOptions options) : IManagedBotSessionPolicy
    {
        public bool Lean => options.ClientFidelity == PlayerbotClientFidelity.Lean;

        public bool KeepsPacket(WorldOpcode opcode) => PlayerbotPacketIntake.Contains(opcode);

        public void BeforeAction(IPlayerSession session, Player player, WorldOpcode opcode)
        {
            if (session is WorldSession world) PlayerbotMount.DismountForAction(world, player, opcode);
        }
    }


    public async Task StartupAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await ReconcileProvisioningAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ManagedPlayerbot> registered = await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
            .LoadAllAsync(cancellationToken).ConfigureAwait(false);
        if (scope.ServiceProvider.GetService<IManagedPlayerbotSetAsideStore>() is { } setAsides && _world is { } world)
        {
            IReadOnlyList<PlayerbotSetAside> rows = await setAsides.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (IGrouping<Guid, PlayerbotSetAside> group in rows.Where(row => row.BotId != Guid.Empty).GroupBy(row => row.BotId))
                _loadedSetAsides[group.Key] = [.. group];
            await world.InvokeAsync(() =>
            {
                _sharedSetAsides.Restore(rows.Where(row => row.BotId == Guid.Empty), world.NowMs,
                    _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
                return true;
            }, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _savedSharedSetAsideVersion, _sharedSetAsides.Version);
        }
        if (scope.ServiceProvider.GetService<IPlayerbotRelationStore>() is { } relationStore)
        {
            try { Social.Memory.Load(await relationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
                logger.LogWarning(ex, "Playerbot social memory could not be loaded ({Type}); the bots start with none", ex.GetType().Name);
            }
        }
        if (scope.ServiceProvider.GetService<IManagedPlayerbotHomeStore>() is { } homeStore)
        {
            _homeStore = true;

            foreach (PlayerbotHome home in await homeStore.LoadAllAsync(cancellationToken).ConfigureAwait(false))
                _homes[home.BotId] = home;
        }

        var stopped = new List<PlayerbotStatus>();
        var orphans = new HashSet<Guid>();
        foreach (ManagedPlayerbot bot in registered)
        {
            CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
                .GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false);
            if (character is null)
            {
                // A registry row whose character is gone owns nothing; leaving it would hold a MaxRegisteredBots slot for good.
                await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
                    .DeleteAsync(bot.BotId, bot.AccountId, bot.CharacterId, cancellationToken).ConfigureAwait(false);
                _homes.TryRemove(bot.BotId, out _);
                await ForgetRelationsAsync(scope.ServiceProvider, bot.BotId, bot.CharacterId).ConfigureAwait(false);
                orphans.Add(bot.BotId);
                continue;
            }
            stopped.Add(new(bot.BotId, character.Name, ManagedPlayerbotState.Stopped, bot.DesiredEnabled,
                bot.Goal, bot.TargetEntry, bot.QuestId, character.MapId, 0, bot.ErrorCode, Home: StoppedHome(bot.BotId)));
        }
        lock (_snapshotGate)
        {
            Volatile.Write(ref _snapshot, stopped.ToArray());
            _snapshotEpoch++;
        }
        // A restore that cannot log a bot in is a fault, not an operator decision: the bot stays desired and is retried (quarantine).
        if (_options.RestoreOnStartup)
            foreach (ManagedPlayerbot[] chunk in registered.Where(b => b.DesiredEnabled && !orphans.Contains(b.BotId)).Take(_options.MaxBots).Chunk(32))
                await Task.WhenAll(chunk.Select(bot => StartCoreAsync(bot.BotId.ToString(), null, cancellationToken, StartKind.Restore))).ConfigureAwait(false);
    }

    /// <summary>
    /// Every registered bot's status line. Off the world thread it is the copy the last tick published. On the world thread it is the
    /// live state: a command handler (<c>.bot raid</c>, <c>.bot party fill</c>, <c>.playerbot summon</c>) reads it to decide which bots are
    /// running, and a start completes off the world thread, so a bot can enter the active set after the last tick's publish and before
    /// the command runs (a command or world-thread action can run before this feature's next <see cref="Update"/>). The published copy then
    /// still holds that bot's stopped row from its creation and the command skipped a bot that was in the world (CI: ".bot raid 3"
    /// answered "invited 2 of 3" or "no eligible online bots"). So a world-thread read republishes first whenever the active set changed
    /// since the last publish, from the active set as it is now.
    /// </summary>
    public IReadOnlyList<PlayerbotStatus> Snapshot()
    {
        if (_world?.IsWorldThread == true)
        {
            // Two marks, as a start can land between a tick's capture of the active set (the version it captured with it) and its
            // publish, which then records the newer version (_builtActiveVersion) without writing that bot's row (PublishSnapshot).
            int version = Volatile.Read(ref _activeVersion);
            if (_snapshotElapsedMs > 0 || version != _orderedVersion || version != _builtActiveVersion)
            {
                (ActiveBot[] ordered, int orderedVersion) = RefreshOrdered();
                PublishSnapshot(ordered, orderedVersion, 0, force: true);
            }
        }

        return Volatile.Read(ref _snapshot).ToArray();
    }

    /// <summary>The active set in bot-id order, rebuilt when it changed since the last build, with the version it was built from (world thread).</summary>
    private (ActiveBot[] Bots, int Version) RefreshOrdered()
    {
        int version = Volatile.Read(ref _activeVersion);
        if (version != _orderedVersion)
        {
            _orderedScratch.Clear();
            foreach (KeyValuePair<Guid, ActiveBot> pair in _active)
                _orderedScratch.Add(pair.Value);
            _orderedScratch.Sort(CompareByBotId);
            _ordered = _orderedScratch.ToArray();
            _orderedVersion = version;
        }

        return (_ordered, _orderedVersion);
    }

    internal void GatherSocialBots(PlayerbotGroupCoordinator? groups, List<BotSocial.PlayerbotSocialBot> destination)
    {
        destination.Clear();
        ActiveBot[] bots = RefreshOrdered().Bots;
        destination.EnsureCapacity(bots.Length);
        foreach (ActiveBot active in bots)
        {
            if (active.Paused || active.Controller is not null || active.Record.State != ManagedPlayerbotState.Running) continue;
            if (active.Session is not { State: SessionState.InWorld, Player: { IsInWorld: true, Map: not null } player } session) continue;
            destination.Add(new BotSocial.PlayerbotSocialBot(active.Record.BotId, session, player, active.Party, active.PartyDriven,
                groups?.Drives(active.Record.BotId) == true, active.Brain));
        }
    }

    public Task<PlayerbotInspection?> InspectAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        WorldRuntime? world = _world;
        if (world is null) return Task.FromResult<PlayerbotInspection?>(null);
        cancellationToken.ThrowIfCancellationRequested();
        Guid? id = Guid.TryParse(idOrName, out Guid parsed) ? parsed : null;
        return world.InvokeAsync(() =>
        {
            ActiveBot? bot = id is { } key ? _active.GetValueOrDefault(key)
                : _active.Values.FirstOrDefault(value => value.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (bot is null) return null;
            PlayerbotInspection? inspection = PlayerbotInspector.Capture(bot.Session, bot.Brain, bot.PartyDriven ? bot.Party : null);
            return inspection is null ? null : inspection with { Group = coordinator?.Describe(bot.Record.BotId) ?? battlegrounds?.Describe(bot.Record.BotId) };
        }).WaitAsync(cancellationToken);
    }

    public async Task<PlayerbotOperationResult> CreateAsync(string name, byte race, byte characterClass,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled");
        if (ResetInProgress) return new(false, "fleet-reset-running");
        if (SplitBrainLatched) return new(false, "playerbot-split-brain");
        if (_detaching) return new(false, PlayerbotsDetaching);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        Account? spare = null;
        try
        {
            if (_stopping) return new(false, "playerbots-stopping");
            if (ResetInProgress) return new(false, "fleet-reset-running");
            if (SplitBrainLatched) return new(false, "playerbot-split-brain"); // a checkpoint that held the lock may have latched it while this call queued
            if (_detaching) return new(false, PlayerbotsDetaching);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await ReconcileProvisioningAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            IManagedPlayerbotStore store = scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>();
            // Registration is bounded by MaxRegisteredBots; MaxBots bounds only the bots running at once (StartCoreAsync), so a
            // stopped bot never takes a running slot (the live stress test of 2026-10-08 found 5 stopped bots blocking creation).
            int knownRegistered = Volatile.Read(ref _snapshot).Length;
            IReadOnlyList<ManagedPlayerbot>? registered = null;
            if (knownRegistered == 0 || knownRegistered + 8 >= _options.MaxRegisteredBots || !_spareAccounts.IsEmpty)
            {
                registered = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                if (registered.Count >= _options.MaxRegisteredBots)
                    return new(false, "playerbot-registry-full");
            }
            ICharacterStore characters = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            if (await characters.IsNameTakenAsync(name, cancellationToken).ConfigureAwait(false)) return new(false, "name-in-use");
            Guid botId = Guid.NewGuid();
            // The empty account of a bot a fleet reset deleted is reused (the provisioning journal is for NEW accounts only).
            spare = registered is not null ? await ClaimSpareAccountAsync(scope.ServiceProvider, registered, cancellationToken).ConfigureAwait(false) : null;
            Account owner;
            if (spare is not null) owner = spare;
            else
            {
                // Credentials are random and discarded; only salt/verifier persist. These accounts cannot be adopted by name.
                string username = "PB" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                byte[] salt = RandomNumberGenerator.GetBytes(32);
                string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                owner = await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotProvisionStore>().CreateAsync(botId, new Account
                {
                    Username = username, Salt = salt,
                    Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, username, password), 32),
                    Security = AccountSecurity.Player,
                }, cancellationToken).ConfigureAwait(false);
            }

            WorldSession session = await NewSessionAsync(owner, null, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                var packet = new PacketWriter();
                packet.WriteCString(name); packet.WriteByte(race); packet.WriteByte(characterClass);
                for (int i = 0; i < 8; i++) packet.WriteByte(0);
                // The character handler answers with SMSG_CHAR_CREATE: keep its result so a refusal can say why (name rule, limit, ...).
                int createResult = -1;
                session.ManagedPacketObserver = (opcode, payload) =>
                {
                    if (opcode == WorldOpcode.SmsgCharCreate && payload.Length > 0) Volatile.Write(ref createResult, payload[0]);
                };
                await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, packet.ToArray()).ConfigureAwait(false);
                session.ManagedPacketObserver = null;
                CharacterRecord? character = (await characters.GetByAccountAsync(owner.Id, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
                if (character is null) throw new PlayerbotCreateRefusedException(DescribeCreateRefusal(Volatile.Read(ref createResult)));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var bot = new ManagedPlayerbot(botId, owner.Id, character.Id, owner.Username, false,
                    ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, now, now);
                await store.CreateAsync(bot, cancellationToken).ConfigureAwait(false);
                if (spare is null && !await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotProvisionStore>()
                    .CompleteAsync(botId, owner.Id, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("provision-completion-refused");
                spare = null; // in use now
                PublishStopped(bot, character.Name, character.MapId);
                logger.LogInformation("Created managed playerbot {BotId}, character {CharacterId}", bot.BotId, bot.CharacterId);
                return new(true, "created", bot.BotId, character.Name);
            }
            finally
            {
                session.Kick("playerbot character created");
                await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not PlayerbotCreateRefusedException and not OperationCanceledException)
                FaultLedger.Shared.Record("playerbot.feature", ex);
            // A refusal of the character handler names its reason; any other failure names the exception and its message (the reason
            // used to be the exception type alone, so "InvalidOperationException" was all an operator saw of a name with a digit).
            string reason = ex is PlayerbotCreateRefusedException refused ? refused.Reason : $"{ex.GetType().Name}: {ex.Message}";
            // A refusal names its own reason and a cancellation is a shutdown: both expected (Warning). Anything else is a server bug (Error).
            logger.Log(ex is PlayerbotCreateRefusedException or OperationCanceledException ? LogLevel.Warning : LogLevel.Error, ex,
                "Managed playerbot creation of '{Name}' failed: {Reason}", name, reason);
            // A reused account has no provisioning journal, so what this attempt put on it is removed here (cancellation included).
            if (spare is not null) await RetireOrRequeueSpareAsync(spare).ConfigureAwait(false);
            await using AsyncServiceScope repair = scopes.CreateAsyncScope();
            try { await ReconcileProvisioningAsync(repair.ServiceProvider, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.feature", failure);
                logger.LogError(failure, "Playerbot provisioning retained for reconciliation ({Type}: {Message})", failure.GetType().Name, failure.Message);
            }
            if (ex is OperationCanceledException) throw;
            return new(false, ex is PlayerbotCreateRefusedException ? $"character-create-refused: {reason}" : $"create-failed: {reason}", null, name);
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// A creation on a reused account failed. The account was empty when it was claimed, so any character on it came from this attempt
    /// and no registry row names it (unless one was stored after all, then nothing is touched): delete it like a player's delete
    /// would, then make the account spare again. If a character cannot be deleted the account is retired (not offered again) and the
    /// character is logged. Runs with no cancellation token on purpose: a shutdown mid-creation is exactly when this matters.
    /// </summary>
    private async Task RetireOrRequeueSpareAsync(Account spare)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IServiceProvider provider = scope.ServiceProvider;
            IReadOnlyList<CharacterRecord> held = await provider.GetRequiredService<ICharacterStore>().GetByAccountAsync(spare.Id, CancellationToken.None).ConfigureAwait(false);
            if (held.Count != 0)
            {
                IReadOnlyList<ManagedPlayerbot> registered = await provider.GetRequiredService<IManagedPlayerbotStore>().LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
                Account? owner = await provider.GetRequiredService<IAccountStore>().FindByUsernameAsync(spare.Username, CancellationToken.None).ConfigureAwait(false);
                if (registered.Any(b => b.AccountId == spare.Id) || owner is null || owner.Id != spare.Id
                    || owner.Status != AccountStatus.Active || owner.Security != AccountSecurity.Player || owner.SessionKey is not null) return; // in use, or not ours: leave it alone
                WorldSession session = await NewSessionAsync(owner, null, provider, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    foreach (CharacterRecord character in held)
                        if (!await CharacterDeletion.TryDeleteAsync(session, (ulong)(uint)character.Id).ConfigureAwait(false))
                            logger.LogError("Playerbot creation on reused account '{Account}' failed and character {CharacterId} could not be deleted", spare.Username, character.Id);
                }
                finally
                {
                    session.Kick("playerbot spare account cleanup");
                    try { await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException timeout)
                    {
                        FaultLedger.Shared.Record("playerbot.lifecycle", timeout);
                        AnomalyLedger.Shared.Report("bot-lifecycle", "session-close-timeout",
                            string.Create(CultureInfo.InvariantCulture, $"bot {held[0].Name} (guid {(uint)held[0].Id:X})"),
                            string.Create(CultureInfo.InvariantCulture, $"spare account '{spare.Username}' cleanup session did not close within 10 s"),
                            AnomalySeverity.Warning, _world?.Identity);
                        logger.LogWarning(timeout, "Playerbot cleanup session of '{Account}' did not close within 10 s", spare.Username);
                    }
                }

                if ((await provider.GetRequiredService<ICharacterStore>().GetByAccountAsync(spare.Id, CancellationToken.None).ConfigureAwait(false)).Count != 0)
                {
                    logger.LogError("Playerbot cleanup of reused account '{Account}' left a character behind; the account is retired", spare.Username);
                    return;
                }
            }

            _spareAccounts.Enqueue(new SpareAccount(spare.Id, spare.Username));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.feature", ex);
            logger.LogError(ex, "Playerbot cleanup of reused account '{Account}' failed; the account is retired ({Type}: {Message})", spare.Username, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// The next account a fleet reset left empty that is still safe to reuse, or null. Every candidate is read again: the account must
    /// be the same plain, active player account without a login key, hold no character, and be named by no registry row. Operation lock held.
    /// </summary>
    private async Task<Account?> ClaimSpareAccountAsync(IServiceProvider provider, IReadOnlyList<ManagedPlayerbot> registered, CancellationToken cancellationToken)
    {
        if (_spareAccounts.IsEmpty) return null;
        IAccountStore accounts = provider.GetRequiredService<IAccountStore>();
        ICharacterStore characters = provider.GetRequiredService<ICharacterStore>();
        while (_spareAccounts.TryDequeue(out SpareAccount? candidate))
        {
            Account? account = await accounts.FindByUsernameAsync(candidate.Name, cancellationToken).ConfigureAwait(false);
            if (account is null || account.Id != candidate.AccountId || account.Status != AccountStatus.Active
                || account.Security != AccountSecurity.Player || account.SessionKey is not null) continue;
            if (registered.Any(b => b.AccountId == account.Id)) continue;
            if ((await characters.GetByAccountAsync(account.Id, cancellationToken).ConfigureAwait(false)).Count != 0) continue;
            return account;
        }

        return null;
    }

    /// <summary>The character handler refused the bot's character; <see cref="Reason"/> says why.</summary>
    private sealed class PlayerbotCreateRefusedException(string reason) : InvalidOperationException(reason)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>Why <c>CMSG_CHAR_CREATE</c> was refused, from its <c>SMSG_CHAR_CREATE</c> result (-1: no answer came).</summary>
    internal static string DescribeCreateRefusal(int result)
    {
        if (result < 0) return "no SMSG_CHAR_CREATE answer";
        var code = (CharResult)result;
        string text = code switch
        {
            CharResult.CharNameNoName => "no name, or a name longer than the limit",
            CharResult.CharNameTooShort => "the name is too short",
            CharResult.CharNameTooLong => "the name is too long",
            CharResult.CharNameInvalidCharacter => "the name is not well-formed text",
            CharResult.CharNameMixedLanguages => "the name may contain only letters of the realm's alphabet (no digits, symbols or mixed alphabets)",
            CharResult.CharNameProfane => "the name is profane",
            CharResult.CharNameReserved => "the name is reserved",
            CharResult.CharNameInvalidApostrophe => "the name has an apostrophe where none is allowed",
            CharResult.CharNameMultipleApostrophes => "the name has more than one apostrophe",
            CharResult.CharNameThreeConsecutive => "the name has three identical letters in a row",
            CharResult.CharNameInvalidSpace => "the name has a space",
            CharResult.CharNameFailure => "the name is not allowed",
            CharResult.CharCreateNameInUse => "the name is in use",
            CharResult.CharCreateDisabled => "character creation is disabled",
            CharResult.CharCreatePvpTeamsViolation => "the race's faction is not allowed (PvP realm team rule)",
            CharResult.CharCreateServerLimit => "the server's character limit is reached",
            CharResult.CharCreateAccountLimit => "the account's character limit is reached",
            CharResult.CharCreateSuccess => "the handler reported success but stored no character",
            _ => "character creation failed",
        };
        return Enum.IsDefined(code) ? $"{text} ({code})" : $"{text} (result 0x{result:X2})";
    }

    /// <summary>Start a bot; an operator start also clears the bot's quarantine and fault history.</summary>
    public Task<PlayerbotOperationResult> StartAsync(string idOrName, CancellationToken cancellationToken = default)
        => StartCoreAsync(idOrName, null, cancellationToken, StartKind.Operator);

    /// <summary>
    /// Start a bot in scripted mode: <paramref name="controller"/> replaces <see cref="PlayerbotBrain"/> from the first
    /// tick (the brain never runs). A bot that is already running is refused with "already-running"; switch it with
    /// <see cref="SetControllerAsync"/> instead.
    /// </summary>
    public Task<PlayerbotOperationResult> StartScriptedAsync(string idOrName, IPlayerbotController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return StartCoreAsync(idOrName, controller, cancellationToken, StartKind.Operator);
    }

    /// <summary>
    /// Attach (scripted mode) or detach (null: back to autonomous mode) the controller of a running bot. Runs on the
    /// world thread; false when the bot is not running.
    /// </summary>
    public Task<bool> SetControllerAsync(Guid botId, IPlayerbotController? controller)
    {
        if (_world is not { } world) return Task.FromResult(false);
        return world.InvokeAsync(() =>
        {
            if (!_active.TryGetValue(botId, out ActiveBot? active) || active.Paused) return false;
            IPlayerbotController? previous = active.Controller;
            active.Controller = controller;
            // A controller attaching or detaching changes who walks the bot: the route of the one before is stopped, not left to its lease.
            if (!ReferenceEquals(previous, controller) && active.Session.Player is { } walker) ReleaseMotion(active.Session, walker);
            if (controller is not null && active.GroupDriven)
            {
                // A controller takes over a member of a bot-led group: it leaves the group (and the brain resumes when it detaches).
                coordinator?.Forget(botId); _pathJobs?.Forget(botId);
                active.GroupDriven = false;
                active.Brain.ResumeAfterGroup();
            }
            if (controller is not null)
            {
                // A controller takes over a bot with battleground business: the coordinator lets it go where it stands (the server keeps
                // its queue or match; the controller decides), and the brain resumes when the controller detaches.
                battlegrounds?.Forget(botId);
                if (active.BattlegroundDriven)
                {
                    active.BattlegroundDriven = false;
                    active.Brain.ResumeAfterGroup();
                }
            }
            if (controller is not null && active.Session.Player is { } taken)
            {
                PlayerbotTaxi.Cancel(taken);
                // The journey and its legs end (a ship leg aboard rides on to the next port, PlayerbotShip.Cancel, and the brain steps the
                // bot off there once the controller detaches; the controller is the administrator's explicit choice meanwhile).
                PlayerbotTravel.CancelLegs(active.Session, taken);
            }
            active.Session.ManagedBudget = null;
            if (controller is not null && active.Session.Player is { } player && (active.PartyDriven || active.Party.IsEngaged))
            {
                // The controller drives now: the party AI lets go, so no party goal, master or mode is reported while it does. When
                // the controller detaches, a bot still grouped is engaged afresh; an ungrouped one gets a fresh brain.
                active.Party.Disengage(player);
                if (active.PartyDriven) ReplaceBrain(active);
                active.PartyDriven = false;
            }
            if (previous is not null && !ReferenceEquals(previous, controller)) previous.Detached(botId);
            return true;
        });
    }

    /// <summary>Whether a running bot is driven by a controller instead of the brain.</summary>
    public bool IsScripted(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) && active.Controller is not null;

    /// <summary>Whether a running autonomous bot is driven by its party AI (it is in a real player's group) instead of the brain.</summary>
    public bool IsPartyDriven(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) && active.PartyDriven;

    /// <summary>
    /// Why the brain does not drive a running bot now (null when it does, or the bot is not running): a controller holds it, the
    /// battlegrounds coordinator or a bot-led group drives it, or its party AI does (it is in a real player's group). A journey begun for
    /// such a bot would be cancelled by that driver at once (<see cref="PlayerbotTravel.CancelLegs"/>). World thread.
    /// </summary>
    internal string? HeldFromBrain(Guid botId)
    {
        if (!_active.TryGetValue(botId, out ActiveBot? active)) return null;
        if (active.Controller is not null) return "a controller holds the bot";
        if (active.BattlegroundDriven || battlegrounds?.Drives(botId) == true) return "the battleground coordinator drives the bot";
        if (active.GroupDriven || coordinator?.Drives(botId) == true) return "a bot-led group drives the bot";
        if (active.PartyDriven) return "the bot follows its party (a real player's group)";
        return null;
    }

    /// <summary>Whether the bot is in the active set (test seam; <see cref="FindBrain"/> also hides a paused one).</summary>
    internal bool IsActive(Guid botId) => _active.ContainsKey(botId);

    /// <summary>The bot's fight tracker (null for an unknown bot; tests and inspection).</summary>
    internal BotSocial.PlayerbotFightSocial? FightOf(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) ? active.Fight : null;

    /// <summary>The brain of a running bot (inspection and the scenario harness; null when not running).</summary>
    internal PlayerbotBrain? FindBrain(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Brain : null;

    /// <summary>The party AI of a running bot while it drives the bot (inspection and the scenario harness), otherwise null.</summary>
    internal PlayerbotPartyAI? FindParty(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused && active.PartyDriven ? active.Party : null;

    /// <summary>
    /// <c>.playerbot invite</c> (the vmangos <c>.partybot add</c> analogue; world thread): <paramref name="inviter"/> invites the
    /// running autonomous bot <paramref name="idOrName"/> through the ordinary group invite, and the bot accepts at once whatever its
    /// <see cref="PlayerbotPartyOptions.InvitePolicy"/> says. The invite keeps every ordinary rule (faction, full group, leader or
    /// assistant only); a refused one is answered to the inviter by the group system and reported as "invite-refused".
    /// </summary>
    internal PlayerbotOperationResult InviteToGroup(Player inviter, string idOrName)
    {
        ArgumentNullException.ThrowIfNull(inviter);
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-invite-thread");
        Guid? id = Guid.TryParse(idOrName, out Guid parsed) ? parsed : null;
        ActiveBot? bot = id is { } key ? _active.GetValueOrDefault(key)
            : _active.Values.FirstOrDefault(value => value.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        if (bot is null || bot.Paused || bot.Session.State != SessionState.InWorld || bot.Session.Player is not { } player)
            return new(false, "bot-not-running");
        if (bot.Controller is not null) return new(false, "bot-scripted", bot.Record.BotId, player.Name);
        if (ReferenceEquals(player, inviter)) return new(false, "invite-refused", bot.Record.BotId, player.Name);
        if (services.GetService<Social.SocialFeature>()?.Context.Groups is not { } groups) return new(false, "groups-unavailable");
        if (groups.AreInSameGroup(inviter.Guid, player.Guid)) return new(true, "already-in-your-group", bot.Record.BotId, player.Name);
        if (groups.GetGroup(player.Guid) is not null) return new(false, "bot-already-grouped", bot.Record.BotId, player.Name);
        groups.Invite(inviter, player.Name);
        if (groups.GetInvite(player.Guid) is null) return new(false, "invite-refused", bot.Record.BotId, player.Name);
        bot.Session.ManagedBudget = null;
        bot.Session.TryManagedAction(WorldOpcode.CmsgGroupAccept, []);
        return groups.AreInSameGroup(inviter.Guid, player.Guid)
            ? new(true, "invited", bot.Record.BotId, player.Name)
            : new(false, "accept-refused", bot.Record.BotId, player.Name);
    }

    /// <summary>Whether homes can be set: the characters database provides <see cref="IManagedPlayerbotHomeStore"/>.</summary>
    internal bool HomesAvailable => _homeStore;

    /// <summary>The persisted home of a bot (tests and inspection), null when it has none.</summary>
    internal PlayerbotHome? HomeOf(Guid botId) => _homes.GetValueOrDefault(botId);

    /// <summary>Whether a home may be set on <paramref name="mapId"/>: the maps bots may enter (<see cref="PlayerbotOptions.AllowedMaps"/>).</summary>
    internal bool AllowsHomeMap(uint mapId) => PlayerbotMapPolicy.Allows(_options.AllowedMaps, mapId, null, current: false);

    /// <summary>
    /// The bots a <c>.playerbot home</c> selector names (<see cref="PlayerbotHomeSelector"/>): one bot by id or name, or every bot
    /// whose name starts with the prefix, running or not. False for a selector that is none of those.
    /// </summary>
    internal bool TryMatchBots(string selector, out IReadOnlyList<PlayerbotStatus> matches)
    {
        matches = [];
        if (!PlayerbotHomeSelector.TryParse(selector, out PlayerbotHomeSelector parsed)) return false;
        matches = [.. Snapshot().Where(parsed.Matches).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        return true;
    }

    /// <summary>Where a running bot stands (world thread; false when it is not running or has no map).</summary>
    internal bool TryGetPosition(Guid botId, out uint mapId, out System.Numerics.Vector3 position)
    {
        mapId = 0;
        position = default;
        if (!_active.TryGetValue(botId, out ActiveBot? active) || active.Paused
            || active.Session.Player is not { Map: { } map } player) return false;
        mapId = map.MapId;
        position = new(player.X, player.Y, player.Z);
        return true;
    }

    /// <summary>One bot's home centre for <see cref="SetHomesAsync"/>.</summary>
    internal readonly record struct HomeTarget(Guid BotId, uint MapId, System.Numerics.Vector3 Centre);

    /// <summary>What <see cref="SetHomesAsync"/> did: homes stored and applied, and bots it could not store a home for.</summary>
    internal readonly record struct HomeOutcome(int Set, int Failed);

    /// <summary>
    /// The zone lookup of a map for the homes' zone clip (<see cref="PlayerbotZoneLookup"/>); null reads the world's terrain
    /// (<see cref="PlayerbotZoneLookup.FromTerrain"/>). Tests without terrain data set a fixture. World thread.
    /// </summary>
    internal Func<uint, PlayerbotZoneLookup>? ZoneLookups { get; set; }

    // One lookup per map, shared by the bots homed there; built on any thread, read on the world thread.
    private readonly ConcurrentDictionary<uint, PlayerbotZoneLookup> _zoneLookups = new();

    /// <summary>
    /// The AreaTable zone at a home centre (<see cref="PlayerbotZoneLookup"/>, the terrain lookup <c>Map.GetZoneAndAreaId</c> uses);
    /// 0 when the terrain does not know it. World thread: <c>.playerbot home</c> refuses a zone-clipped home whose centre has no
    /// known zone (it would contain nothing).
    /// </summary>
    internal uint ZoneAt(uint mapId, System.Numerics.Vector3 position) => ZoneLookupOf(mapId)?.ZoneAt(position.X, position.Y, position.Z) ?? 0;

    private PlayerbotZoneLookup? ZoneLookupOf(uint mapId)
    {
        if (_zoneLookups.TryGetValue(mapId, out PlayerbotZoneLookup? known)) return known;
        PlayerbotZoneLookup? lookup = ZoneLookups is { } fixture ? fixture(mapId)
            : _world is { } world ? PlayerbotZoneLookup.FromTerrain(ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world).Terrain, mapId) : null;
        return lookup is null ? null : _zoneLookups.GetOrAdd(mapId, lookup);
    }

    /// <summary>
    /// The area-level lookup of a map for the danger check of an Auto walk home (<see cref="PlayerbotAreaLevelLookup"/>); null reads
    /// the world's terrain (<see cref="PlayerbotAreaLevelLookup.FromTerrain"/>). Tests without terrain data set a fixture. World thread.
    /// </summary>
    internal Func<uint, PlayerbotAreaLevelLookup>? AreaLevelLookups { get; set; }

    // One lookup per map, shared by the bots homed there; built on any thread, read on the world thread.
    private readonly ConcurrentDictionary<uint, PlayerbotAreaLevelLookup> _levelLookups = new();

    private PlayerbotAreaLevelLookup? AreaLevelLookupOf(uint mapId)
    {
        if (_levelLookups.TryGetValue(mapId, out PlayerbotAreaLevelLookup? known)) return known;
        PlayerbotAreaLevelLookup? lookup = AreaLevelLookups is { } fixture ? fixture(mapId)
            : _world is { } world ? PlayerbotAreaLevelLookup.FromTerrain(ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world).Terrain, mapId) : null;
        return lookup is null ? null : _levelLookups.GetOrAdd(mapId, lookup);
    }

    /// <summary>
    /// Store the homes (characters schema 56; the zone clip, schema 58) and apply them to the running brains on the world thread. A
    /// home that cannot be stored is not applied (fail closed) and counted as failed. Throws when no home store is available or the
    /// radius is out of bounds. <paramref name="zoneClipped"/> clips each home to the zone of its centre (the command's default).
    /// </summary>
    internal async Task<HomeOutcome> SetHomesAsync(IReadOnlyList<HomeTarget> targets, float radius, string? place,
        CancellationToken cancellationToken = default, bool zoneClipped = true, bool logFailures = true)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (SplitBrainLatched) throw new PlayerbotSplitBrainException(0);
        if (_world is not { } world) throw new InvalidOperationException("playerbot-home-unavailable");
        if (!float.IsFinite(radius) || radius < PlayerbotHomeBounds.MinRadius || radius > PlayerbotHomeBounds.MaxRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        string? cleanPlace = place is null ? null : place.Length > PlayerbotHomeBounds.MaxPlaceLength ? place[..PlayerbotHomeBounds.MaxPlaceLength] : place;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IManagedPlayerbotHomeStore store = scope.ServiceProvider.GetService<IManagedPlayerbotHomeStore>()
            ?? throw new InvalidOperationException("playerbot-home-unavailable");
        IManagedPlayerbotStore bots = scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>();
        var saved = new List<PlayerbotHome>(targets.Count);
        int failed = 0;
        bool refused = false;
        foreach (HomeTarget target in targets)
        {
            if (SplitBrainLatched) { refused = true; break; } // latched while this call ran: store nothing more (the homes already stored below are still applied)
            try
            {
                ManagedPlayerbot? bot = await bots.FindAsync(target.BotId, cancellationToken).ConfigureAwait(false);
                if (bot is null) { failed++; continue; }
                var home = new PlayerbotHome(target.BotId, bot.CharacterId, target.MapId, target.Centre.X, target.Centre.Y, target.Centre.Z,
                    radius, cleanPlace, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), zoneClipped);
                await store.SetAsync(home, cancellationToken).ConfigureAwait(false);
                saved.Add(home);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                failed++;
                FaultLedger.Shared.Record("playerbot.feature", ex);
                if (logFailures) logger.LogWarning(ex, "Playerbot home of {BotId} was not stored ({Type}: {Message})", target.BotId, ex.GetType().Name, ex.Message);
            }
        }

        bool anyActive = false;
        foreach (PlayerbotHome home in saved)
        {
            _homes[home.BotId] = home;
            if (_active.ContainsKey(home.BotId)) anyActive = true;
        }
        if (saved.Count != 0)
        {
            if (anyActive)
            {
                await world.InvokeAsync(() =>
                {
                    foreach (PlayerbotHome home in saved)
                        if (_active.TryGetValue(home.BotId, out ActiveBot? active) && !active.Paused) active.Brain.Home = AreaOf(home.BotId);
                    lock (_snapshotGate) _snapshotEpoch++;
                    return true;
                }).ConfigureAwait(false);
            }
            else
            {
                lock (_snapshotGate)
                {
                    PlayerbotStatus[] current = Volatile.Read(ref _snapshot);
                    PlayerbotStatus[]? updated = null;
                    for (int i = 0; i < current.Length; i++)
                    {
                        PlayerbotStatus s = current[i];
                        if (s.State == ManagedPlayerbotState.Running || s.Home == "stopped") continue;
                        for (int j = 0; j < saved.Count; j++)
                        {
                            if (saved[j].BotId == s.BotId)
                            {
                                updated ??= (PlayerbotStatus[])current.Clone();
                                updated[i] = s with { Home = "stopped" };
                                break;
                            }
                        }
                    }
                    if (updated is not null)
                        Volatile.Write(ref _snapshot, updated);
                }
            }
        }

        if (refused) throw new PlayerbotSplitBrainException(saved.Count);
        return new(saved.Count, failed);
    }

    /// <summary>
    /// Raised before an operator's <see cref="ClearHomesAsync"/> removes homes, with the bots whose home is a population start home
    /// (<see cref="Population.PopulationStartAreas"/>): the population must not give those bots one again.
    /// </summary>
    internal event Action<IReadOnlyList<Guid>>? HomesClearedByOperator;

    /// <summary>Remove the homes of these bots (store and running brains); returns how many bots had one. <paramref name="byOperator"/>: the command, as opposed to the population's own graduation.</summary>
    internal async Task<int> ClearHomesAsync(IReadOnlyList<Guid> botIds, CancellationToken cancellationToken = default, bool byOperator = true)
    {
        ArgumentNullException.ThrowIfNull(botIds);
        if (SplitBrainLatched) throw new PlayerbotSplitBrainException(0);
        if (byOperator && HomesClearedByOperator is { } handler)
        {
            Guid[] start = [.. botIds.Where(id => _homes.TryGetValue(id, out PlayerbotHome? h) && Population.PopulationStartAreas.TryParse(h.Place, out _, out _))];
            if (start.Length != 0) handler(start);
        }

        if (_world is not { } world) throw new InvalidOperationException("playerbot-home-unavailable");
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IManagedPlayerbotHomeStore store = scope.ServiceProvider.GetService<IManagedPlayerbotHomeStore>()
            ?? throw new InvalidOperationException("playerbot-home-unavailable");
        int cleared = 0;
        List<Guid> done = new(botIds.Count);
        try
        {
            foreach (Guid botId in botIds)
            {
                if (SplitBrainLatched) throw new PlayerbotSplitBrainException(cleared); // latched while this call ran: clear nothing more
                bool stored = await store.ClearAsync(botId, cancellationToken).ConfigureAwait(false);
                done.Add(botId);
                if (_homes.TryRemove(botId, out _) || stored) cleared++;
            }
        }
        finally
        {
            // A store failure partway must not leave the bots already cleared with a live brain home (they would keep walking home
            // while status shows no home=): the world-thread step always runs for the ids done so far; the failure then propagates.
            if (done.Count != 0)
                await world.InvokeAsync(() =>
                {
                    foreach (Guid botId in done)
                        if (_active.TryGetValue(botId, out ActiveBot? active)) active.Brain.Home = null;
                    lock (_snapshotGate) _snapshotEpoch++;
                    return true;
                }).ConfigureAwait(false);
        }

        return cleared;
    }

    /// <summary>
    /// The party AI of a running bot whether or not it drives the bot (null when not running): the persona and the chat delivery of the
    /// bot's own announcements (<see cref="BotSocial.PlayerbotSocialFeature"/>).
    /// </summary>
    internal PlayerbotPartyAI? FindPartyAI(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Party : null;

    /// <summary>The ordinary session of a running bot (scenario harness; null when not running).</summary>
    internal WorldSession? FindSession(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Session : null;

    /// <summary>
    /// <c>.playerbot follow|stay</c> (world thread): leash the running autonomous bot to <paramref name="master"/>, a real player, with
    /// no group (<see cref="PlayerbotPartyAI.Leash"/>). A member of a bot-led group leaves it first, exactly as when a controller takes
    /// the bot over (<see cref="SetControllerAsync"/>). The bot is marked party-driven at once, so the bot-led group matching of the
    /// same tick cannot take it back before its next think; a bot with battleground business is no longer free, so the battlegrounds
    /// coordinator releases it at its next update (leaving its queue or match), before the think that would follow it.
    /// </summary>
    internal PlayerbotOperationResult Leash(Guid botId, Player master, bool stay)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-leash-thread");
        if (!_active.TryGetValue(botId, out ActiveBot? bot) || bot.Paused || bot.Session.State != SessionState.InWorld
            || bot.Session.Player is not { } player)
            return new(false, "bot-not-running", botId);
        if (bot.Controller is not null) return new(false, "bot-scripted", botId, player.Name);
        if (ReferenceEquals(player, master)) return new(false, "leash-refused", botId, player.Name);
        // The coordinator may already hold the bot without a group-driven think yet (matched in this very tick): it lets go either way.
        if (bot.GroupDriven || coordinator?.Drives(botId) == true)
        {
            coordinator?.Forget(botId); _pathJobs?.Forget(botId);
            if (bot.GroupDriven)
            {
                bot.GroupDriven = false;
                bot.Brain.ResumeAfterGroup();
            }
        }

        bot.Session.ManagedBudget = null;
        ReleaseMotion(bot.Session, player); // the brain's route is not the party AI's
        bot.Party.Leash(player, master, stay);
        bot.PartyDriven = true;
        return new(true, stay ? "staying" : "following", botId, player.Name);
    }

    /// <summary>
    /// <c>.playerbot dismiss</c> (world thread): end a leash. The party AI lets go now; the bot's next think finds it neither
    /// grouped nor leashed and gives it a fresh brain (<see cref="UpdateAutonomous"/>).
    /// </summary>
    internal PlayerbotOperationResult Unleash(Guid botId)
    {
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-leash-thread");
        if (!_active.TryGetValue(botId, out ActiveBot? bot) || bot.Paused || bot.Session.State != SessionState.InWorld
            || bot.Session.Player is not { } player)
            return new(false, "bot-not-running", botId);
        if (!bot.Party.IsLeashed) return new(false, "not-leashed", botId, player.Name);
        bot.Party.Unleash(player);
        return new(true, "dismissed", botId, player.Name);
    }

    /// <summary>
    /// <c>.playerbot defend</c> (world thread): give the running autonomous bots <paramref name="botIds"/> a defend post, or end theirs with
    /// null. Memory only (<see cref="PlayerbotBrain.Defend"/>), so a restart ends it. A bot that is scripted, grouped or leashed, in a
    /// battleground, or not running is refused with its reason (the brain does not drive it).
    /// </summary>
    internal IReadOnlyList<PlayerbotOperationResult> SetDefend(IReadOnlyList<Guid> botIds, PlayerbotDefendPost? post)
    {
        ArgumentNullException.ThrowIfNull(botIds);
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-defend-thread");
        var results = new List<PlayerbotOperationResult>(botIds.Count);
        foreach (Guid botId in botIds)
        {
            if (!_active.TryGetValue(botId, out ActiveBot? bot) || bot.Paused || bot.Session.State != SessionState.InWorld
                || bot.Session.Player is not { } player)
            {
                results.Add(new(false, "bot-not-running", botId));
                continue;
            }

            if (bot.Controller is not null) results.Add(new(false, "bot-scripted", botId, player.Name));
            else if (post is not null && (bot.PartyDriven || bot.GroupDriven || bot.BattlegroundDriven)) results.Add(new(false, "bot-busy", botId, player.Name));
            else
            {
                bot.Brain.Defend = post;
                if (post is null) PlayerbotDefend.ReleaseFlag(bot.Session, player);
                results.Add(new(true, post is null ? "defend-cleared" : "defending", botId, player.Name));
            }
        }

        return results;
    }

    /// <summary>
    /// Give the running autonomous bots <paramref name="botIds"/> a scenario stay (<see cref="PlayerbotStay"/>), renew the one they hold, or
    /// end theirs with null (world thread). Refusals as <see cref="SetDefend"/>: not running, scripted, and (only when setting) driven by a
    /// party, a bot-led group or a battleground. A stay with a new id cancels a taxi ride and the journey legs the bot was on. Memory only.
    /// </summary>
    internal IReadOnlyList<PlayerbotOperationResult> SetStay(IReadOnlyList<Guid> botIds, PlayerbotStay? stay)
    {
        ArgumentNullException.ThrowIfNull(botIds);
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-stay-thread");
        var results = new List<PlayerbotOperationResult>(botIds.Count);
        foreach (Guid botId in botIds)
        {
            if (!_active.TryGetValue(botId, out ActiveBot? bot) || bot.Paused || bot.Session.State != SessionState.InWorld
                || bot.Session.Player is not { } player)
            {
                results.Add(new(false, "bot-not-running", botId));
                continue;
            }

            if (bot.Controller is not null) results.Add(new(false, "bot-scripted", botId, player.Name));
            else if (stay is not null && (bot.PartyDriven || bot.GroupDriven || bot.BattlegroundDriven)) results.Add(new(false, "bot-busy", botId, player.Name));
            else
            {
                if (stay is not null && bot.Brain.Stay?.StayId != stay.StayId)
                {
                    PlayerbotTaxi.Cancel(player);
                    PlayerbotTravel.CancelLegs(bot.Session, player);
                }

                bot.Brain.Stay = stay;
                results.Add(new(true, stay is null ? "stay-cleared" : "staying", botId, player.Name));
            }
        }

        return results;
    }

    /// <summary>The id of the scenario stay a running bot holds (a lease already run out still counts until the brain drops it), or null.</summary>
    internal int? StayIdOf(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) ? active.Brain.Stay?.StayId : null;

    /// <summary>
    /// Why the group, run and battleground coordinators will not match the bot (null when they may): it is queued for a battleground. A
    /// coordinator that already drives it is reported by <see cref="HeldFromBrain"/>.
    /// </summary>
    internal string? HeldFromCoordinators(Guid botId)
        => battlegrounds?.Holds(botId) == true ? "is queued for a battleground" : null;

    /// <summary>Who starts a bot: an operator (clears the quarantine), the startup restore, or a quarantine retry.</summary>
    private enum StartKind { Operator, Restore, QuarantineRetry }

    /// <param name="retrying">For <see cref="StartKind.QuarantineRetry"/>: the quarantine entry the retry was scheduled from.</param>
    private async Task<PlayerbotOperationResult> StartCoreAsync(string idOrName, IPlayerbotController? controller,
        CancellationToken cancellationToken, StartKind kind, Quarantine? retrying = null)
    {
        bool quarantineRetry = kind != StartKind.Operator;
        if (!_options.Enabled || _stopping || _world is not { } world) return new(false, "playerbots-disabled");
        if (ResetInProgress) return new(false, "fleet-reset-running");
        if (SplitBrainLatched) return new(false, "playerbot-split-brain");
        if (_detaching) return new(false, PlayerbotsDetaching);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        AsyncServiceScope scope = scopes.CreateAsyncScope();
        WorldSession? session = null;
        bool retained = false;
        ManagedPlayerbot? bot = null;
        try
        {
            if (_stopping) return new(false, "playerbots-stopping");
            if (ResetInProgress) return new(false, "fleet-reset-running");
            if (SplitBrainLatched) return new(false, "playerbot-split-brain"); // a checkpoint that held the lock may have latched it while this call queued
            if (_detaching) return new(false, PlayerbotsDetaching);
            bot = await ResolveAsync(scope.ServiceProvider, idOrName, cancellationToken).ConfigureAwait(false);
            if (bot is null) return new(false, "bot-not-found");
            if (kind == StartKind.Operator) _quarantine.TryRemove(bot.BotId, out _);
            // The retry was scheduled outside this lock; an operator stop (or start) that took the lock in between cleared the
            // quarantine and decided DesiredEnabled. Re-check under the lock so the retry never undoes it.
            if (kind == StartKind.QuarantineRetry && (!bot.DesiredEnabled
                || !_quarantine.TryGetValue(bot.BotId, out Quarantine? current) || !ReferenceEquals(current, retrying)))
                return new(false, "quarantine-cleared", bot.BotId);
            if (_active.TryGetValue(bot.BotId, out ActiveBot? existing))
                return new(!existing.Paused, existing.Paused ? "stop-incomplete" : "already-running", bot.BotId);
            // Running bots only (World:Playerbots:MaxBots, live): stopped and quarantined bots hold no slot.
            if (_active.Count >= _options.MaxBots) return new(false, "playerbot-capacity");
            Account? owner = await scope.ServiceProvider.GetRequiredService<IAccountStore>()
                .FindByUsernameAsync(bot.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner?.Id != bot.AccountId || owner.Status != AccountStatus.Active
                || owner.Security != AccountSecurity.Player || owner.SessionKey is not null)
                return new(false, "owner-refused", bot.BotId);
            session = await NewSessionAsync(owner, bot.CharacterId, scope.ServiceProvider, cancellationToken, bot.BotId).ConfigureAwait(false);
            bot = await PersistAsync(scope.ServiceProvider, bot with { DesiredEnabled = true, State = ManagedPlayerbotState.Starting, ErrorCode = null }, cancellationToken).ConfigureAwait(false);
            var login = new PacketWriter(); login.WriteUInt64((ulong)(uint)bot.CharacterId);
            await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray()).ConfigureAwait(false);
            // A bot saved over a void (no floor near its height) would log in there again and wander on a stale height: put it on a floor
            // before its brain starts (a near teleport, so like any vmangos near teleport it also applies Honorless Target). Bots only; a real player keeps the saved position (the vmangos login).
            bool entered = await world.InvokeAsync(() =>
                {
                    if (session.State != SessionState.InWorld || session.Player is not { Map: { } botMap } botPlayer
                        || !PlayerbotMapPolicy.MayStayOnMap(botPlayer, _options))
                        return false;
                    try
                    {
                        TeleportService? teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
                        PlayerbotLoginFloor.Result settled = PlayerbotLoginFloor.Settle(botPlayer, botMap, world.NowMs, teleports);
                        if (settled.Outcome is PlayerbotLoginFloor.Outcome.MovedToMesh or PlayerbotLoginFloor.Outcome.MovedToHome)
                            logger.LogInformation("Playerbot {BotId} logged in over a void; placed {Outcome} at {Position}", bot.BotId, settled.Outcome, settled.Position);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException and not TimeoutException)
                    {
                        // A stalled world thread still fails the login as before; the placement itself is a courtesy: a bug in it must not fault or quarantine a bot that did log in.
                        FaultLedger.Shared.Record("playerbot.login-floor", ex);
                        logger.LogWarning(ex, "Playerbot {BotId} login placement failed; the bot keeps its saved position", bot.BotId);
                    }
                    return true;
                })
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("login-refused");
            bot = await PersistAsync(scope.ServiceProvider, bot with { State = ManagedPlayerbotState.Running }, cancellationToken).ConfigureAwait(false);
            // The row, scope and session are the core's (ManagedBotRegistry): they outlive a detach of this feature.
            var entry = new ManagedBot(bot, scope, session);
            ActiveBot active = NewActiveBot(entry, NewBrain(session, bot.BotId));
            active.Controller = controller;
            if (!BotRegistry.TryAdd(entry)) throw new InvalidOperationException("duplicate-bot");
            if (!_active.TryAdd(bot.BotId, active))
            {
                BotRegistry.TryRemove(entry);
                throw new InvalidOperationException("duplicate-bot");
            }

            _loadedSetAsides.TryRemove(bot.BotId, out _);
            Interlocked.Increment(ref _activeVersion);
            retained = true;
            logger.LogInformation("Started managed playerbot {BotId}, character {CharacterId}", bot.BotId, bot.CharacterId);
            return new(true, "started", bot.BotId, session.Player?.Name ?? active.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is ManagedRevisionConflictException)
            {
                // Another writer owns the row: no fault, no quarantine and no write of ours on top of theirs. The finally kicks the half-started session.
                WarnConflictOnce(bot?.BotId ?? Guid.Empty, "start");
                return new(false, "managed-revision-conflict", bot?.BotId);
            }

            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                // The caller gave up (the World is stopping while it restores its bots): not the bot's fault. Recording a fault would persist
                // State=Faulted (and, from an operator start, DesiredEnabled=false), so every bot still starting at a shutdown would stay out
                // after the restart. Leave the record as it is; the finally kicks the half-started session and the next start restores it.
                logger.LogInformation("Managed playerbot {Bot} start interrupted by a stop request; its record is kept for the next start", bot?.BotId.ToString() ?? idOrName);
                throw;
            }

            if (ex is not OperationCanceledException)
            {
                FaultLedger.Shared.Record("playerbot.feature", ex);
                FaultLedger.Shared.Record("playerbot.lifecycle", ex);
            }
            logger.LogWarning(ex, "Managed playerbot start failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
            CharacterRecord? character = null;
            if (bot is not null)
            {
                // An operator start that fails is reported and not retried; a quarantine retry that fails is one more fault.
                (bool desired, string code) = quarantineRetry ? RecordFault(bot.BotId, "start-failed: " + ex.Message, session?.Player) : (false, "start-failed");
                bot = await PersistAsync(scope.ServiceProvider, bot with { DesiredEnabled = desired, State = ManagedPlayerbotState.Faulted, ErrorCode = code }, CancellationToken.None).ConfigureAwait(false);
                character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(bot.CharacterId, CancellationToken.None).ConfigureAwait(false);
                PublishStopped(bot, character?.Name ?? idOrName, character?.MapId ?? 0);
            }
            if (ex is not OperationCanceledException)
            {
                string subject = session?.Player is { } player ? PlayerbotAnomalies.SubjectOf(player)
                    : bot is not null ? string.Create(CultureInfo.InvariantCulture, $"bot {character?.Name ?? idOrName} (guid {(uint)bot.CharacterId:X})")
                    : $"bot {idOrName}";
                AnomalyLedger.Shared.Report("bot-lifecycle", "operation-fault", subject,
                    string.Create(CultureInfo.InvariantCulture, $"bot {bot?.BotId.ToString("D") ?? idOrName} start failed ({ex.GetType().Name}: {ex.Message})"),
                    AnomalySeverity.Warning, _world?.Identity);
            }
            if (ex is OperationCanceledException) throw;
            return new(false, "start-failed", bot?.BotId);
        }
        finally
        {
            try
            {
                if (!retained)
                {
                    if (session is not null)
                    {
                        session.Kick("playerbot start aborted");
                        await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                        if (bot is not null) await FlushBoundedAsync(bot.CharacterId, CancellationToken.None, session.Player?.Name ?? idOrName).ConfigureAwait(false);
                    }
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { _operations.Release(); }
        }
    }

    /// <summary>
    /// Wait for a bot character's save barrier, but not forever: the caller holds <c>_operations</c>, and while another writer holds the database the
    /// save queue retains the snapshot and retries it, which would otherwise stall every bot operation. The snapshot is not lost on a timeout.
    /// </summary>
    private async Task FlushBoundedAsync(int characterId, CancellationToken cancellationToken, string? botName = null)
    {
        try
        {
            await saves.FlushCharacterAsync(characterId, cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            FaultLedger.Shared.Record("playerbot.lifecycle", timeout);
            AnomalyLedger.Shared.Report("bot-lifecycle", "session-close-timeout",
                string.Create(CultureInfo.InvariantCulture, $"bot {botName ?? characterId.ToString(CultureInfo.InvariantCulture)} (guid {(uint)characterId:X})"),
                string.Create(CultureInfo.InvariantCulture, $"character {characterId} save barrier did not complete within 15 s"),
                AnomalySeverity.Warning, _world?.Identity);
            logger.LogWarning(timeout, "Managed playerbot character {CharacterId}: the save barrier did not complete in 15 s (database busy?); continuing, the snapshot stays queued", characterId);
        }
    }

    public async Task<PlayerbotOperationResult> StopAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        if (SplitBrainLatched) return new(false, "playerbot-split-brain");
        if (_detaching) return new(false, PlayerbotsDetaching);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (SplitBrainLatched) return new(false, "playerbot-split-brain"); // a checkpoint that held the lock may have latched it while this call queued
            if (_detaching) return new(false, PlayerbotsDetaching);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ManagedPlayerbot? bot = await ResolveAsync(scope.ServiceProvider, idOrName, cancellationToken).ConfigureAwait(false);
            if (bot is null) return new(false, "bot-not-found");
            _quarantine.TryRemove(bot.BotId, out _);
            await StopCoreAsync(bot, false, cancellationToken).ConfigureAwait(false);
            return new(true, "stopped", bot.BotId);
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// Delete a stopped bot: its character through the ordinary character-deletion path (which also clears the registry, member and
    /// home rows in the same transaction in production), then whatever is left of the registry row, then the in-memory records. The
    /// character goes first, never the row: a row without a character is recoverable, a character without a row is not owned by
    /// anything. The owner account stays. Refused while the bot runs.
    /// </summary>
    public async Task<PlayerbotOperationResult> DeleteAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled");
        if (SplitBrainLatched) return new(false, "playerbot-split-brain");
        if (_detaching) return new(false, PlayerbotsDetaching);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return new(false, "playerbots-disabled");
            if (SplitBrainLatched) return new(false, "playerbot-split-brain");
            if (_detaching) return new(false, PlayerbotsDetaching);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ManagedPlayerbot? bot = await ResolveAsync(scope.ServiceProvider, idOrName, cancellationToken).ConfigureAwait(false);
            if (bot is null) return new(false, "bot-not-found");
            if (_active.ContainsKey(bot.BotId)) return new(false, "bot-running", bot.BotId);
            Account? owner = await scope.ServiceProvider.GetRequiredService<IAccountStore>()
                .FindByUsernameAsync(bot.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner?.Id != bot.AccountId || owner.Status != AccountStatus.Active
                || owner.Security != AccountSecurity.Player || owner.SessionKey is not null)
                return new(false, "owner-refused", bot.BotId);

            if (await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
                .GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false) is { } character)
            {
                WorldSession session = await NewSessionAsync(owner, null, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!await CharacterDeletion.TryDeleteAsync(session, (ulong)(uint)bot.CharacterId).ConfigureAwait(false))
                        return new(false, "character-delete-refused", bot.BotId);
                }
                finally
                {
                    session.Kick("playerbot character deleted");
                    // A close that is slow after a committed deletion must not skip the cleanup below.
                    try { await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException timeout)
                    {
                        FaultLedger.Shared.Record("playerbot.lifecycle", timeout);
                        AnomalyLedger.Shared.Report("bot-lifecycle", "session-close-timeout",
                            string.Create(CultureInfo.InvariantCulture, $"bot {character.Name} (guid {(uint)bot.CharacterId:X})"),
                            string.Create(CultureInfo.InvariantCulture, $"bot {bot.BotId:D} deletion session did not close within 10 s"),
                            AnomalySeverity.Warning, _world?.Identity);
                        logger.LogWarning(timeout, "Playerbot {BotId}: the deletion session did not close within 10 s", bot.BotId);
                    }
                }
            }

            await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
                .DeleteAsync(bot.BotId, bot.AccountId, bot.CharacterId, cancellationToken).ConfigureAwait(false);
            _quarantine.TryRemove(bot.BotId, out _);
            _homes.TryRemove(bot.BotId, out _);
            await ForgetRelationsAsync(scope.ServiceProvider, bot.BotId, bot.CharacterId).ConfigureAwait(false);
            lock (_snapshotGate)
            {
                Volatile.Write(ref _snapshot, Volatile.Read(ref _snapshot).Where(s => s.BotId != bot.BotId).ToArray());
                _snapshotEpoch++;
            }

            logger.LogInformation("Deleted managed playerbot {BotId}, character {CharacterId}", bot.BotId, bot.CharacterId);
            return new(true, "deleted", bot.BotId);
        }
        finally { _operations.Release(); }
    }

    public async Task ShutdownBeforeWorldStopAsync()
    {
        Volatile.Write(ref _stopping, true);
        _pathJobs?.ForgetAll();
        coordinator?.BeginShutdown();
        await _checkpoint.ConfigureAwait(false);
        try { await _warWipeTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); } // the war layer wipe (no new one starts once stopping)
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { logger.LogWarning("The playerbot war layer wipe did not finish before shutdown ({Type}); the next start finds the rows as they were", ex.GetType().Name); }
        await _operations.WaitAsync().ConfigureAwait(false);
        List<Exception> failures = [];
        try
        {
            // Each stop waits for its own world-thread removal and character flush. Start a bounded
            // group together so those waits overlap, while the operation lock keeps other lifecycle
            // work out until all members of the group have finished.
            foreach (ActiveBot[] batch in _active.Values.ToArray().Chunk(8))
            {
                Task[] stops = [.. batch.Select(active => StopCoreAsync(active.Record, true, CancellationToken.None))];
                try { await Task.WhenAll(stops).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    foreach (Task stop in stops.Where(stop => stop.IsFaulted))
                        if (stop.Exception is { } stopEx)
                            failures.AddRange(stopEx.InnerExceptions);
                    foreach (Task stop in stops.Where(stop => stop.IsCanceled))
                        failures.Add(new TaskCanceledException(stop));
                }
            }
            try { if (!SplitBrainLatched) await SaveSharedSetAsidesAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.feature", ex);
                logger.LogWarning(ex, "Managed playerbot shared set-aside shutdown save failed ({Type})", ex.GetType().Name);
            }
        }
        finally { _operations.Release(); }
        if (_policy is { } policy) { _policy = null; await policy.DisposeAsync().ConfigureAwait(false); }
        if (_navigationExecutor is { } navigationExecutor) { _navigationExecutor = null; await navigationExecutor.DisposeAsync().ConfigureAwait(false); }
        if (_chat is { } chat) { _chat = null; await chat.DisposeAsync().ConfigureAwait(false); }
        _chatInProcess?.Dispose();
        _chatInProcess = null;
        _chatClient?.Dispose();
        _chatClient = null;
        if (failures.Count > 0) throw new AggregateException("Managed playerbot shutdown failed", failures);
    }

    public Task StopAsync() => ShutdownBeforeWorldStopAsync();

    /// <summary>
    /// Start the goal policy engine (<see cref="Attach"/>). Never throws for a bad model (one anomaly, then the rule ladder scores); a
    /// telemetry folder that cannot be made is a fault and a warning, and the policy runs without telemetry.
    /// </summary>
    private void StartPolicy()
    {
        string contentRoot = services.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
        var policy = new Policy.PlayerbotPolicyEngine(_options.Policy, logger, contentRoot);
        if (_options.Policy.Telemetry.Enabled)
        {
            try
            {
                policy.Telemetry = new Policy.PlayerbotPolicyTelemetry(_options.Policy.Telemetry,
                    Path.Combine(contentRoot, _options.Policy.Telemetry.Directory), _setAsideTime, logger);
                if (services.GetService<ArcaneCore.Kernel.Ops.Watchdog.TickMonitor>() is { Enabled: true } monitor)
                    policy.Telemetry.TickHealth = () => monitor.BudgetMicros < long.MaxValue ? new Policy.PlayerbotTickHealth(monitor.Overruns, monitor.BudgetMicros) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The host's disk or folder: an environment site (PlayerbotQaFaultFeed.EnvironmentSites).
                FaultLedger.Shared.Record("playerbot.policy-telemetry-io", ex);
                logger.LogWarning(ex, "Playerbot goal policy telemetry could not start ({Type}: {Message}); the policy runs without it", ex.GetType().Name, ex.Message);
            }
            catch (ArgumentException ex)
            {
                // A bad folder name, or a limit the writer refuses: possibly a defect, so not the environment site.
                FaultLedger.Shared.Record("playerbot.policy-telemetry", ex);
                logger.LogWarning(ex, "Playerbot goal policy telemetry could not start ({Type}: {Message}); the policy runs without it", ex.GetType().Name, ex.Message);
            }
        }
        logger.LogInformation(
            "Playerbot goal policy on: {Policy}, a snapshot every {IntervalMs} ms, stale after {StaleMs} ms, {BudgetMs} ms per map batch, exploration {Rate}, policy arm share {Share}, telemetry {Telemetry}",
            policy.Policy.Name, _options.Policy.IntervalMs, _options.Policy.StaleAfterMs, _options.Policy.TickBudgetMs, _options.Policy.ExplorationRate,
            _options.Policy.AbPolicyShare,
            policy.Telemetry is null ? "off" : "on");
        _policy = policy;
    }

    /// <summary>
    /// End of tick (world thread): hand each map's policy snapshots to the worker, sweep the telemetry, and every five minutes log what the
    /// policy did (one Info line, no bot named).
    /// </summary>
    private void UpdatePolicy(Policy.PlayerbotPolicyEngine policy)
    {
        try
        {
            policy.Dispatch();
            if (_world is not { } world) return;
            policy.Telemetry?.Sweep(world.NowMs);
            bool first = !_policyReportArmed;
            if (!first && unchecked((int)(world.NowMs - _policyReportMs)) < 0) return;
            _policyReportArmed = true;
            _policyReportMs = unchecked(world.NowMs + 300_000);
            if (first) return;
            Policy.PlayerbotPolicyStats stats = policy.Stats;
            (long Queued, long Dropped, long Unfinished, long Written) telemetryCounts = policy.Telemetry?.Counts ?? default;
            logger.LogInformation(
                "Playerbot goal policy {Policy}: {Submitted} snapshots, {Scored} scored, {Dropped} dropped, {Unscored} unscored over {Overruns} budget overruns, {Errors} errors; "
                + "thinks {Applied} applied, {Agreed} agreed, {Floor} floor, {Explored} explored, {Missing} missing, {Stale} stale, {NonFinite} non-finite, {Refused} refused; telemetry {Written} written, {TelemetryDropped} dropped, {Unfinished} unfinished, {TelemetryRefused} refused",
                stats.Policy, stats.Submitted, stats.Scored, stats.Dropped, stats.Unscored, stats.Overruns, stats.Errors,
                stats.Applied, stats.Agreed, stats.Floor, stats.Explored, stats.Missing, stats.Stale, stats.NonFinite, stats.Refused,
                telemetryCounts.Written, telemetryCounts.Dropped, telemetryCounts.Unfinished, policy.Telemetry?.Refused ?? 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.policy", ex);
            logger.LogWarning(ex, "Playerbot goal policy tick failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>A brain for the bot. <paramref name="restoreLoaded"/>: put back the set-asides read at startup (a start); a brain that replaces a running one gets the running one's state instead (<see cref="ReplaceBrain"/>), which the startup rows are older than.</summary>
    private PlayerbotBrain NewBrain(WorldSession session, Guid botId, bool restoreLoaded = true)
    {
        // Every brain records through the feature's recorder (kill steals, help), a replacement too (ReplaceBrain), and has it before it is
        // registered anywhere below.
        var brain = new PlayerbotBrain(session, _options, _sharedSetAsides, _claims, _navigationExecutor) { Home = AreaOf(botId), PolicyEngine = _policy, Social = Social };
        // The combat telemetry hears the risk model from the brain's first think (its first pull).
        if (_policy?.Telemetry is { } telemetry)
        {
            brain.Risk.CombatSink = telemetry.Combat;
            brain.Opportunities.Sink = telemetry.Combat;
        }

        if (session.Player is { IsInWorld: true } player) brain.ObserveKillingBlows(player);
        if (restoreLoaded && _loadedSetAsides.TryGetValue(botId, out PlayerbotSetAside[]? rows))
            brain.Suspensions.Restore(rows, session.World.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
        return brain;
    }

    /// <summary>
    /// The bot's home as a brain's area, or null when it has none. A zone-clipped home takes the zone of its centre from the terrain on
    /// first use (world thread); a centre whose zone is not known gives an area that contains nothing (fail closed), logged then.
    /// </summary>
    private PlayerbotHomeArea? AreaOf(Guid botId)
        => _homes.TryGetValue(botId, out PlayerbotHome? home)
            ? new PlayerbotHomeArea(home.MapId, new System.Numerics.Vector3(home.X, home.Y, home.Z), home.Radius, home.Place,
                home.ZoneClipped, home.ZoneClipped ? ZoneLookupOf(home.MapId) : null,
                area => logger.LogWarning("Playerbot home of {BotId} is zone-clipped but the terrain knows no zone at its centre "
                    + "(map {Map} {X:F1},{Y:F1}): the home contains nothing until it is set again, or set with nozone",
                    botId, area.MapId, area.Centre.X, area.Centre.Y))
            {
                AreaLevels = AreaLevelLookupOf(home.MapId),
                // Leave-home quests (the next band's town) only from a zone home and the band town, never from a capital.
                AllowsLeaveQuests = Population.PopulationStartAreas.StageOfPlace(home.Place) == Population.StartHomeStage.Zone
                    || Population.PopulationTownProgression.StayOf(home.Place) == Population.TownStay.Secondary,
                Stay = Population.PopulationTownProgression.StayOf(home.Place),
                WalkRangeYards = Population.PopulationTownProgression.StayOf(home.Place) == Population.TownStay.None ? 0f
                    : _options.Population.TownProgressionTeleport ? _options.Population.TownWalkYards : 20000f,
                TownTeleports = Population.PopulationTownProgression.StayOf(home.Place) != Population.TownStay.None && _options.Population.TownProgressionTeleport,
                ValleyRadius = Population.PopulationStartAreas.ValleyRadiusOfPlace(home.Place),
                LeavePolicy = _options.Population,
            }
            : null;

    // What every bot sets aside after several of them stalled on it (PlayerbotSharedSetAsides).
    private readonly PlayerbotSharedSetAsides _sharedSetAsides = new();

    // The soft claims of the fleet on untagged mobs (PlayerbotClaims): one table for every bot, touched on the world thread only.
    private readonly PlayerbotClaims _claims = new();

    /// <summary>The goals set aside for every bot (inspection and tests).</summary>
    internal PlayerbotSharedSetAsides SharedSetAsides => _sharedSetAsides;

    /// <summary>The goal the bot reports and persists: its party AI's while that drives it, otherwise the brain's.</summary>
    private (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId) GoalOf(ActiveBot active)
    {
        GoalReadProbe?.Invoke();
        return GoalCore(active);
    }

    /// <summary>Test seam: runs on whichever thread reads a bot's goal, so a test can assert that thread is the world thread.</summary>
    internal Action? GoalReadProbe { get; set; }

    private (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId) GoalCore(ActiveBot active)
        => active.BattlegroundDriven && battlegrounds?.GoalOf(active.Record.BotId) is { } bg ? (bg.Goal, bg.TargetEntry, active.Brain.QuestId)
            : active.GroupDriven && coordinator?.GoalOf(active.Record.BotId) is { } group ? (group.Goal, group.TargetEntry, active.Brain.QuestId)
            : active.PartyDriven ? (active.Party.Goal, active.Party.TargetEntry, active.Brain.QuestId)
            : (active.Brain.Goal, active.Brain.TargetEntry, active.Brain.QuestId);

    /// <summary>
    /// <see cref="GoalOf(ActiveBot)"/> read where the coordinators' collections are owned. A stopped world has no tick left to mutate them,
    /// so it is read in place then (as the set-aside captures do).
    /// </summary>
    private async Task<(PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)> GoalOnWorldThreadAsync(ActiveBot active)
    {
        if (_world is not { } world || world.IsStopped || world.IsWorldThread) return GoalOf(active);
        try { return await world.InvokeAsync(() => GoalOf(active)).ConfigureAwait(false); }
        catch (TaskCanceledException) when (world.IsStopped) { return GoalOf(active); }
    }

    /// <summary>
    /// The goals of <paramref name="bots"/> (same order), read in one world-thread hop: the checkpoint runs on a pool thread, and the
    /// coordinators' member maps are mutated by the world thread. A stopped world is read in place (see <see cref="GoalOnWorldThreadAsync"/>).
    /// </summary>
    private async Task<(PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[]> GoalsOnWorldThreadAsync(ActiveBot[] bots)
    {
        (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[] ReadAll()
        {
            var goals = new (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[bots.Length];
            for (int i = 0; i < bots.Length; i++) goals[i] = GoalOf(bots[i]);
            return goals;
        }

        if (bots.Length == 0 || _world is not { } world || world.IsStopped || world.IsWorldThread) return ReadAll();
        try { return await world.InvokeAsync(ReadAll).ConfigureAwait(false); }
        catch (TaskCanceledException) when (world.IsStopped) { return ReadAll(); }
    }

    private Task<WorldSession> NewSessionAsync(Account owner, int? characterId, IServiceProvider services, CancellationToken cancellationToken, Guid? botId = null)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("playerbot-world-not-attached");
        return WorldSession.CreateManagedAsync(owner, characterId, services, opcodes, world, registry, sessionOptions.Value, logger, cancellationToken, botId);
    }

    private async Task StopCoreAsync(ManagedPlayerbot bot, bool preserveDesired, CancellationToken cancellationToken, string? faultCode = null)
    {
        if (_active.TryGetValue(bot.BotId, out ActiveBot? active))
        {
            active.Paused = true;
            // Defence only: entering split brain drops every active bot and every start is refused while it is set, so no bot is active here when it is latched
            // (a race that latched it between the lookup above and this check is the one way in). Another process owns the rows, so this stop leaves the active
            // set without a single managed-row or set-aside write. The character's own session-close save still runs (there is no clean way to discard it); wait, bounded, for the session to close.
            if (SplitBrainLatched)
            {
                await DropFromActiveAsync(active, active.Record, writeSetAsides: false, announce: false).ConfigureAwait(false);
                try { await active.Session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException timeout)
                {
                    FaultLedger.Shared.Record("playerbot.lifecycle", timeout);
                    AnomalyLedger.Shared.Report("bot-lifecycle", "session-close-timeout",
                        string.Create(CultureInfo.InvariantCulture, $"bot {active.Name} (guid {(uint)bot.CharacterId:X})"),
                        string.Create(CultureInfo.InvariantCulture, $"bot {bot.BotId:D} session did not close within 10 s of the split-brain stop"),
                        AnomalySeverity.Warning, _world?.Identity);
                    logger.LogWarning("Playerbot {BotId} session did not close within 10 s of the split-brain stop", bot.BotId);
                }
                await FlushBoundedAsync(bot.CharacterId, CancellationToken.None, active.Name).ConfigureAwait(false);
                return;
            }

            await ReleaseDefendFlagAsync(active).ConfigureAwait(false);
            void Quiesce()
            {
                if (_world is { } stopping) _ = stopping.InvokeAsync(() => { coordinator?.Forget(bot.BotId); _pathJobs?.Forget(bot.BotId); battlegrounds?.Forget(bot.BotId); return true; });
                active.Brain.Stop();
                if (Interlocked.Exchange(ref active.Controller, null) is { } controller) controller.Detached(bot.BotId);
                _chat?.Abandon(bot.BotId); // replies still being written for it are no longer wanted
                Social.Untrack(bot.CharacterId);
            }

            bool stoppingWritten = false;
            try
            {
                // The revision-checked write comes first: on a conflict another writer owns the row (and its set-aside rows), and nothing of ours is written.
                active.Record = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                { State = ManagedPlayerbotState.Stopping, DesiredEnabled = preserveDesired && active.Record.DesiredEnabled }, cancellationToken).ConfigureAwait(false);
                stoppingWritten = true;
                try { await SaveBotSetAsidesAsync(active, force: true).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.feature", ex);
                    logger.LogWarning(ex, "Managed playerbot {BotId} set-aside stop save failed ({Type})", bot.BotId, ex.GetType().Name);
                }

                await SaveBotRelationsAsync(active).ConfigureAwait(false);

                Quiesce();
                // A reason, so the stop is no session/kick-no-reason (live: ~300 per restart and a steady trickle from the rotation).
                active.Session.Kick(faultCode is null ? "playerbot stopped" : "playerbot faulted: " + faultCode);
                await active.Session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                await FlushBoundedAsync(bot.CharacterId, cancellationToken, active.Name).ConfigureAwait(false);
                // Read on the world thread, queued behind this bot's own Forget: other bots' Forgets mutate the coordinators' collections.
                (PlayerbotGoalKind goal, uint targetEntry, uint questId) = await GoalOnWorldThreadAsync(active).ConfigureAwait(false);
                bot = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                {
                    DesiredEnabled = preserveDesired && active.Record.DesiredEnabled,
                    State = faultCode is null ? ManagedPlayerbotState.Stopped : ManagedPlayerbotState.Faulted,
                    ErrorCode = faultCode ?? active.Record.ErrorCode,
                    Goal = goal, TargetEntry = targetEntry, QuestId = questId,
                }, cancellationToken).ConfigureAwait(false);
                active.Record = bot;
                if (_active.TryRemove(new KeyValuePair<Guid, ActiveBot>(bot.BotId, active))) Interlocked.Increment(ref _activeVersion);
                BotRegistry.TryRemove(active.Entry);
                PublishStopped(bot, active.Name, active.MapId);
                await active.Scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (ManagedRevisionConflictException)
            {
                // Another writer moved the row on. Their values win: adopt the stored row and leave the active set, with no write of ours on top of it.
                ManagedPlayerbot? stored = null;
                try { stored = await FindStoredAsync(active.Scope.ServiceProvider, bot.BotId).ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.feature", failure);
                    logger.LogWarning(failure, "Playerbot {BotId} conflicting row could not be read during its stop ({Type})", bot.BotId, failure.GetType().Name);
                }

                await DropFromActiveAsync(active, stored, writeSetAsides: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (!stoppingWritten) Quiesce();
                if (ex is not OperationCanceledException)
                {
                    FaultLedger.Shared.Record("playerbot.feature", ex);
                    FaultLedger.Shared.Record("playerbot.lifecycle", ex);
                    AnomalyLedger.Shared.Report("bot-lifecycle",
                        ex is TimeoutException ? "session-close-timeout" : "operation-fault",
                        string.Create(CultureInfo.InvariantCulture, $"bot {active.Name} (guid {(uint)bot.CharacterId:X})"),
                        string.Create(CultureInfo.InvariantCulture, $"bot {bot.BotId:D} stop failed ({ex.GetType().Name}: {ex.Message})"),
                        AnomalySeverity.Warning, _world?.Identity);
                }
                try
                {
                    active.Record = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                    { State = ManagedPlayerbotState.Faulted, DesiredEnabled = preserveDesired && active.Record.DesiredEnabled,
                        ErrorCode = "stop-incomplete" }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.feature", failure);
                    logger.LogError(failure, "Playerbot {BotId} retains an incomplete stop ({Type})", bot.BotId, failure.GetType().Name);
                }
                PublishStopped(active.Record, active.Name, active.MapId);
                throw;
            }
        }
        else
        {
            if (SplitBrainLatched) return; // no managed-row write while another process owns the rows
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            try
            {
                bot = await PersistAsync(scope.ServiceProvider, bot with { DesiredEnabled = preserveDesired && bot.DesiredEnabled, State = ManagedPlayerbotState.Stopped }, cancellationToken).ConfigureAwait(false);
            }
            catch (ManagedRevisionConflictException)
            {
                WarnConflictOnce(bot.BotId, "stop"); // not running here; the other writer's row stays as it is
                return;
            }

            CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false);
            PublishStopped(bot, character?.Name ?? bot.BotId.ToString(), character?.MapId ?? 0);
        }
    }

    private static ActiveBot? FindOrderedBot(ActiveBot[] bots, Guid botId)
    {
        int lo = 0;
        int hi = bots.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            ActiveBot candidate = bots[mid];
            int cmp = candidate.Record.BotId.CompareTo(botId);
            if (cmp == 0) return candidate;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }

        return null;
    }

    private struct TickLaneDriver(
        ManagedPlayerbotFeature feature,
        ActiveBot[] bots,
        uint now,
        uint combatIntervalMs,
        int reserve,
        ManagedActionBudget combatBudget,
        long thinkStarted) : PlayerbotThinkRotation.IThinkLaneDriver
    {
        public ManagedActionBudget? MainBudget;
        public int MainActions = -1;
        public int Thinks;
        public bool ActionLimited;
        public double CombatMs;

        public PlayerbotThinkRotation.ThinkLane Classify(int index)
        {
            ActiveBot bot = bots[index];
            return IsCombatLane(bot) && unchecked(now - bot.LastUpdateMs) >= combatIntervalMs
                ? PlayerbotThinkRotation.ThinkLane.Priority
                : PlayerbotThinkRotation.ThinkLane.Normal;
        }

        public bool Visit(int index, PlayerbotThinkRotation.ThinkLane lane)
        {
            ManagedActionBudget budget = lane == PlayerbotThinkRotation.ThinkLane.Priority
                ? combatBudget
                : MainBudget ??= new ManagedActionBudget(MainActions = reserve + combatBudget.Remaining);
            if (budget.Remaining <= 0)
            {
                if (lane == PlayerbotThinkRotation.ThinkLane.Normal) ActionLimited = true;
                return false;
            }

            // The main lane's cost per visit feeds the budget's coverage floor (PlayerbotThinkBudget.CoverageMs).
            long visitStarted = Stopwatch.GetTimestamp();
            feature.Think(bots[index], budget, index);
            double visitMs = Stopwatch.GetElapsedTime(visitStarted).TotalMilliseconds;
            if (lane == PlayerbotThinkRotation.ThinkLane.Normal) feature._thinkBudget.ObserveVisit(visitMs);
            else CombatMs += visitMs;
            Thinks++;
            return true;
        }

        public double ElapsedMs() => Stopwatch.GetElapsedTime(thinkStarted).TotalMilliseconds;
    }

    private void Update(uint elapsedMs)
    {
        if (_stopping || _detaching || !_options.Enabled || _world is not { } world) return;
        long updateStarted = Stopwatch.GetTimestamp();
        // What the world spent on everything but the bots in the last tick, smoothed: the think budget is a share of what is left.
        TickPhases lastTick = world.LastTickPhases;
        _thinkBudget.Observe((lastTick.CommandsMicros + lastTick.MapsMicros + lastTick.FeaturesMicros) / 1000.0, _lastOwnMs);
        _thinkBudget.ObserveTickInterval(elapsedMs);
        Interlocked.Add(ref _clockMs, elapsedMs);
        (ActiveBot[] bots, int botsVersion) = RefreshOrdered();
        uint now = world.NowMs;
        if (_pathJobs is { } pathJobs && unchecked(now - _lastPathJobSweepMs) >= 1_000)
        {
            _lastPathJobSweepMs = now;
            pathJobs.Sweep();
        }
        // Movement first, every tick, for every bot: the motion is a client's own reporting and must not wait for a
        // think, the shared action budget or the per-tick time cap below (PlayerbotMotion).
        foreach (ActiveBot active in bots)
        {
            if (active.Paused) continue;
            // A session that is not in the world, or has no player, is skipped here: no motion pump, no party intake and no
            // acknowledgement this tick. It is reported once as lifecycle/session-lost (report only; nothing here acts on it).
            if (active.Session.State != SessionState.InWorld || active.Session.Player is not { } mover)
            {
                ReportSessionLostOnce(active, "tick");
                continue;
            }
            try { PlayerbotMotion.Pump(active.Session, mover, now); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fault(active, "movement", ex); continue; }
            // Party intake, every tick for every autonomous bot (a scripted controller drains its own queue): invitations,
            // chat, loot rolls and resurrection offers are answered as they come, not when the bot's turn in the budget comes.
            if (active.Controller is not null || active.Session.State != SessionState.InWorld) continue;
            try { active.Party.Intake(mover); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fault(active, "party", ex); }
        }
        DeliverChatReplies();
        PlayerbotGroupCoordinator? groups = coordinator;
        PlayerbotBattlegroundCoordinator? fields = battlegrounds;
        // Bot-led groups (PlayerbotGroupCoordinator): their states, the needs and the matching, once per tick before any think.
        if (groups is not null)
        {
            if (bots.Length > 64 && !groups.NeedsBotSnapshot(elapsedMs))
            {
                groups.AdvanceIdle(elapsedMs);
            }
            else
            {
                _groupBotsScratch.Clear();
                _groupBotsScratch.EnsureCapacity(bots.Length);
                foreach (ActiveBot active in bots)
                    if (!active.Paused && active.Session.State == SessionState.InWorld && active.Session.Player is not null)
                        _groupBotsScratch.Add(new(active.Record.BotId, active.Session, active.Brain,
                            // A bot with a defend post holds it: it is neither matched into a quest group nor enlisted in a siege. A scenario stay is
                            // not a hold: the bot stays free, the coordinators' own pick sites leave it out of group, run and battleground matching, and
                            // a siege enlists it (and clears the stay).
                            active.Controller is null && !active.PartyDriven && fields?.Holds(active.Record.BotId) != true && active.Brain.Defend is null,
                            Scripted: active.Controller is not null));
                try { groups.Update(_groupBotsScratch, elapsedMs); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.feature", ex);
                    logger.LogWarning(ex, "Playerbot group coordinator failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
                }
            }
            // A group that stalled (PlayerbotGroupCoordinator.WatchStall) is reported like a brain's stall, on its leader: the brains do not
            // think while a group drives them, so this is the only stall line such a group gets (bot QA parses it).
            foreach ((Guid botId, string name, string report) in groups.TakeStalls())
            {
                ActiveBot? leader = FindOrderedBot(bots, botId);
                string? context = leader?.Session.Player is { } player ? leader.Brain.QaReportContext(player, report) : null;
                string? diag = leader?.Session.Player is { } leaderPlayer ? leader.Brain.FormatDiagnosticSnapshot(leaderPlayer) : null;
                LogStall(logger, botId, name, stallCount: 1, stallsLogged: 0, report, outsideHome: false, everyone: null, context, diag: diag);
            }
        }
        // Battlegrounds (PlayerbotBattlegroundCoordinator): the queue machines, the demand and the free bots' choices, after the groups
        // so a bot a group took this tick is not free here.
        if (fields is not null)
        {
            if (bots.Length > 64 && !fields.NeedsBotSnapshot(elapsedMs))
            {
                fields.AdvanceIdle(elapsedMs);
            }
            else
            {
                _bgBotsScratch.Clear();
                _bgBotsScratch.EnsureCapacity(bots.Length);
                foreach (ActiveBot active in bots)
                    if (!active.Paused && active.Session.State == SessionState.InWorld && active.Session.Player is not null)
                        _bgBotsScratch.Add(new(active.Record.BotId, active.Session, active.Brain,
                            active.Controller is null && !active.PartyDriven && groups?.Involves(active.Record.BotId) != true));
                try { fields.Update(_bgBotsScratch, elapsedMs); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.feature", ex);
                    FaultLedger.Shared.Record("playerbot.bg-coordinator", ex);
                    logger.LogWarning(ex, "Playerbot battleground coordinator failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
                }
            }
        }

        // Hard per-lane action budgets (PlayerbotThinkRotation.PriorityActions / ActionReserve): the combat lane draws from its own; the main
        // lane's is the reserve plus what the combat lane left, created at its first visit, so no fight's think can empty it.
        int reserve = PlayerbotThinkRotation.ActionReserve(_options.MaxActionsPerTick, _options.CombatLaneShare);
        int combatActions = PlayerbotThinkRotation.PriorityActions(_options.MaxActionsPerTick, _options.CombatLaneShare);
        ManagedActionBudget combatBudget = combatActions > 0 ? new ManagedActionBudget(combatActions) : EmptyActionBudget;
        // A fair rotation in two lanes (PlayerbotThinkRotation.TickLanes): bots in combat, dead or ghosts that are due are thought for first, then
        // every bot from the main cursor, until the think time budget (PlayerbotThinkBudget; one think itself is bounded by MaxThinkSearchNodes).
        long thinkStarted = Stopwatch.GetTimestamp();
        double spentMs = Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds;
        LastThinkBudgetInputs = (world.Options.TickIntervalMs, _thinkBudget.NonBotMs, spentMs);
        uint starvedBoundMs = PlayerbotThinkStarvation.EffectiveBoundMs((uint)_options.ThinkStarvedGapMs, bots.Length);
        double budgetMs = ThinkBudgetMs ?? _thinkBudget.Budget(world.Options.TickIntervalMs, spentMs,
            _options.ThinkBudgetMinMs, _options.ThinkBudgetMaxMs, _options.ThinkBudgetTickShare, bots.Length, starvedBoundMs);
        _lastBudgetMs = budgetMs;
        uint combatIntervalMs = (uint)_options.CombatThinkIntervalMs;
        _rotation.Track(bots, BotIdOf);
        var laneDriver = new TickLaneDriver(this, bots, now, combatIntervalMs, reserve, combatBudget, thinkStarted);
        _rotation.TickLanes(bots.Length, budgetMs, _options.CombatLaneShare, ref laneDriver,
            PlayerbotThinkStarvation.PriorityScale(_oldestSilentMs, starvedBoundMs));
        _thinkBudget.ObservePriorityTick(laneDriver.CombatMs);
        LastLaneActions = (combatActions, laneDriver.MainActions >= 0 ? laneDriver.MainActions : reserve + combatBudget.Remaining);
        // The cadence the bots get (PlayerbotCadenceStats): one Info line per minute, no bot named.
        _cadence.EndTick(laneDriver.Thinks, _rotation.LastTickTimeLimited, laneDriver.ActionLimited, budgetMs);
        ReportStarvedBots(bots, now, starvedBoundMs, PlayerbotThinkStarvation.ReportBoundMs(starvedBoundMs, _mainPass.Ms));
        if (_policy is { } policy) UpdatePolicy(policy);
        if (_cadence.TryReport(elapsedMs, bots.Length, out PlayerbotCadenceReport cadence) && cadence.Bots > 0)
            logger.LogInformation(
                "Playerbot cadence over the last minute: {Bots} bots, think gap p50 {GapP50Ms} ms, p95 {GapP95Ms} ms, p99 {GapP99Ms} ms, max {GapMaxMs} ms "
                + "(combat/dead lane: {CombatThinks} thinks, p50 {CombatP50Ms} ms, p95 {CombatP95Ms} ms); "
                + "{ThinksPerTick:F1} thinks per tick over {Ticks} ticks, budget {AvgBudgetMs:F1} ms; ticks ended on the time budget {TimeLimitedPct:F1}%, on the action budget {ActionLimitedPct:F1}%",
                cadence.Bots, cadence.GapP50Ms, cadence.GapP95Ms, cadence.GapP99Ms, cadence.GapMaxMs, cadence.CombatThinks, cadence.CombatGapP50Ms, cadence.CombatGapP95Ms,
                cadence.ThinksPerTick, cadence.Ticks, cadence.AvgBudgetMs, cadence.TimeLimitedPct, cadence.ActionLimitedPct);
        PublishSnapshot(bots, botsVersion, elapsedMs);
        _lastOwnMs = Stopwatch.GetElapsedTime(updateStarted).TotalMilliseconds;
        _checkpointMs += elapsedMs;
        if (_checkpointMs >= 5000 && _checkpoint.IsCompleted)
        {
            if (_checkpoint.Exception is { } checkpointEx)
                FaultLedger.Shared.Record("playerbot.checkpoint", checkpointEx.GetBaseException());
            _checkpointMs = 0;
            _checkpoint = Task.Run(CheckpointAsync);
        }
    }

    /// <summary>Whether the bot is in the first think lane: alive and fighting, or dead or a ghost (world thread).</summary>
    private static bool IsCombatLane(ActiveBot active)
        => !active.Paused && active.Session.State == SessionState.InWorld
           && active.Session.Player is { } player && (!player.IsAlive || player.Combat.IsInCombat);

    /// <summary>
    /// Reports each bot that has gone longer than <paramref name="reportBound"/> without a think (<see cref="PlayerbotThinkStarvation.ReportBoundMs"/>:
    /// <see cref="PlayerbotOptions.ThinkStarvedGapMs"/> raised to 1.5 x the measured full pass, so a bot behind the rotation is reported and a
    /// uniformly slow rotation is not), once per episode (<see cref="PlayerbotThinkStarvation"/>; the episode ends at its next think). The oldest
    /// silence it records for the first lane's yield is judged against the unraised <paramref name="bound"/>. One compare per bot per tick.
    /// </summary>
    private void ReportStarvedBots(ActiveBot[] bots, uint now, uint bound, uint reportBound)
    {
        _oldestSilentMs = 0;
        if (bound == 0) return;
        uint oldest = 0;
        foreach (ActiveBot active in bots)
        {
            if (active.Paused || !active.GapPrimed || active.Session.State != SessionState.InWorld) continue;
            uint silentMs = unchecked(now - active.LastUpdateMs);
            if (silentMs > oldest && silentMs <= int.MaxValue) oldest = silentMs;
            if (!PlayerbotThinkStarvation.ShouldReport(now, active.LastUpdateMs, reportBound, ref active.StarvedReported)) continue;
            if (active.Session.Player is not { } player || active.Session.IsInChaosProbe) continue;
            PlayerbotAnomalies.Report(player, "cadence", "think-starved",
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"no think for {unchecked(now - active.LastUpdateMs)} ms (bound {reportBound} ms, configured {bound} ms, full pass {_mainPass.Ms} ms; {bots.Length} bots, think budget {_lastBudgetMs:F1} ms per tick)"));
        }

        _oldestSilentMs = oldest;
    }

    /// <summary>
    /// One bot's think (world thread), measured (<see cref="PlayerbotThinkProbe"/>) and with its fallback path searches held to
    /// <see cref="PlayerbotOptions.MaxThinkSearchNodes"/>: a think over <see cref="PlayerbotOptions.SlowThinkLogMs"/> is logged with its
    /// bot, driver, goal and phases (<see cref="PlayerbotSlowThinkLog"/>). Both are read from the shared options at each think, so
    /// <c>.reload config</c> changes them at once (WorldConfigKeys).
    /// </summary>
    private void Think(ActiveBot active, ManagedActionBudget budget, int botOrdinal = 0)
    {
        _probe.NodeBudget = _options.MaxThinkSearchNodes;
        _probe.SyncQueryMaxMs = _options.SyncPathQueryMaxMs;
        _probe.Begin();
        string driver;
        // The think's far path searches are the pool's (PlayerbotPathJobs): bound to this bot for the think.
        PlayerbotPathJobs.BindScope bound = active.Session.Player is { } thinker0 ? PathJobs.Bind(active.Record.BotId, thinker0) : default;
        try { driver = ThinkCore(active, budget, botOrdinal); }
        finally
        {
            bound.Dispose();
            _probe.End();
        }
        // The decision telemetry, outside the think's probe: what it costs is not the brain's.
        if (driver == "brain" && _policy?.Telemetry is { } telemetry && active.Session.Player is { } thinker && _world is { } thinkWorld)
        {
            try { telemetry.AfterThink(active.Brain, thinker, thinkWorld.NowMs); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { FaultLedger.Shared.Record("playerbot.policy-telemetry", ex); }
        }
        double slowMs = _options.SlowThinkLogMs;
        bool slow = slowMs > 0 && _probe.ElapsedMs >= slowMs;
        if (!slow) _slowThinks?.EndEpisode(active.Record.BotId);
        if (!slow && ThinkObserver is null) return; // the common case: nothing to describe, nothing allocated
        string goal = GoalOf(active, driver);
        if (slow) (_slowThinks ??= new PlayerbotSlowThinkLog(logger)).Observe(_probe, slowMs, ClockMs, active.Record.BotId, active.Name, driver, goal, active.Session.Player);
        if (ThinkObserver is { } observer)
        {
            _probe.Subject = (driver, goal);
            observer(active.Record.BotId, _probe);
        }
    }

    /// <summary>
    /// The goal a think was about, for the slow-think line: the brain's goal, target entry and quest when the brain drove it; "n/a" for
    /// another driver (party AI, bot-led group, battleground, controller), which holds the brain suspended with the goal it had before.
    /// </summary>
    private static string GoalOf(ActiveBot active, string driver)
        => driver == "brain" ? $"{active.Brain.Goal} entry {active.Brain.TargetEntry} quest {active.Brain.QuestId}" : "n/a";

    /// <summary>One bot's think (world thread): its scripted controller, its bot-led group, its party AI or its brain. Returns which drove it.</summary>
    private string ThinkCore(ActiveBot active, ManagedActionBudget budget, int botOrdinal = 0)
    {
        string driver = "idle";
        if (active.Paused || active.Session.State != SessionState.InWorld) active.GapPrimed = false;
        if (!active.Paused && active.Session.State == SessionState.InWorld && _world is { } world)
        {
            uint sinceLast = unchecked(world.NowMs - active.LastUpdateMs);
            active.LastUpdateMs = world.NowMs;
            // The motion lease follows the scheduler's pass, also for a bot with no gap of its own yet (its first think skips the gap below).
            Player? measured = active.Session.Player;
            if (measured is not null) PlayerbotMotion.NoteSchedulerPass(measured, _mainPass.Ms);
            // The first think after login or unpause measures the time since the record was made (or the pause), not a think gap.
            if (active.GapPrimed)
            {
                bool combatLane = IsCombatLane(active);
                _cadence.RecordGap(sinceLast, combatLane);
                if (!combatLane) _mainPass.Note(sinceLast);
                if (measured is not null) PlayerbotMotion.NoteThinkGap(measured, sinceLast, combatLane);
            }

            active.GapPrimed = true;
            active.StarvedReported = false;
            try
            {
                if (active.Session.Player is { IsInWorld: true } observed)
                {
                    active.Brain.ObserveKillingBlows(observed);
                    active.Fight?.Observe(observed, world, world.NowMs);
                }
                if (active.Controller is { } controller)
                {
                    // Scripted mode: the brain is suppressed and the shared action budget does not apply.
                    active.Session.ManagedBudget = null;
                    driver = "controller";
                    PlayerbotThinkProbe.Phase("controller");
                    controller.Tick(active.ControllerContext, sinceLast);
                }
                else if (active.Session.Player is { } driven)
                {
                    active.Session.ManagedBudget = budget;
                    // A bot the battlegrounds coordinator drives (to a battlemaster, into, through and out of a match) follows it, even
                    // inside a battleground raid with a real player in it (the party AI never takes it there); the brain resumes after.
                    if (battlegrounds?.Drives(active.Record.BotId) == true)
                    {
                        if (active.PartyDriven)
                        {
                            active.Party.Disengage(driven);
                            active.PartyDriven = false;
                        }

                        if (!active.BattlegroundDriven)
                        {
                            ReleaseBrainRoute(driven);
                            PlayerbotTaxi.Cancel(driven);
                            PlayerbotTravel.CancelLegs(active.Session, driven);
                        }

                        active.BattlegroundDriven = true;
                        if (HoldAboard(active, driven, sinceLast))
                        {
                            driver = "ship";
                        }
                        else
                        {
                            driver = "battleground";
                            PlayerbotThinkProbe.Phase("battleground");
                            uint renewals = PlayerbotMotion.RenewalsOf(driven);
                            battlegrounds.UpdateMember(active.Record.BotId, driven, sinceLast);
                            PlayerbotMotion.SettleAfterDriver(active.Session, driven, renewals, sinceLast);
                        }
                    }
                    // A member of a bot-led group follows its group AI; when the group lets it go the brain resumes, with its
                    // quests and memories (its routes and targets belong to another place).
                    else if (coordinator?.Drives(active.Record.BotId) == true)
                    {
                        if (active.PartyDriven)
                        {
                            active.Party.Disengage(driven);
                            active.PartyDriven = false;
                        }

                        if (!active.GroupDriven)
                        {
                            ReleaseBrainRoute(driven);
                            PlayerbotTaxi.Cancel(driven);
                            PlayerbotTravel.CancelLegs(active.Session, driven);
                        }

                        active.GroupDriven = true;
                        if (HoldAboard(active, driven, sinceLast))
                        {
                            driver = "ship";
                        }
                        else
                        {
                            driver = "group";
                            PlayerbotThinkProbe.Phase("group");
                            uint renewals = PlayerbotMotion.RenewalsOf(driven);
                            coordinator.UpdateMember(active.Record.BotId, driven, sinceLast);
                            PlayerbotMotion.SettleAfterDriver(active.Session, driven, renewals, sinceLast);
                        }
                    }
                    else
                    {
                        if (active.GroupDriven)
                        {
                            active.GroupDriven = false;
                            active.Brain.ResumeAfterGroup();
                        }

                        if (active.BattlegroundDriven)
                        {
                            active.BattlegroundDriven = false;
                            active.Brain.ResumeAfterGroup();
                        }

                        driver = UpdateAutonomous(active, driven, sinceLast);
                    }

                    if (_options.Chaos.Enabled && active.Session.Player is { IsInWorld: true } chaosPlayer)
                    {
                        PlayerbotThinkProbe.Phase("chaos");
                        Chaos.StepBot(active.Record.BotId, botOrdinal, active.Session, chaosPlayer);
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fault(active, "action", ex); }
            if (measured is not null) PlayerbotMotion.NoteThink(measured, driver, world.NowMs);
        }
        if (active.Session.Player is { } player) { active.Name = player.Name; active.MapId = player.Map?.MapId ?? player.MapId; }
        if (driver != "brain") active.Brain.CancelNavigationPlanning();
        return driver;
    }

    /// <summary>
    /// The party AI takes the bot or gives it back, or a controller attaches or detaches: the route of the one before is not the next
    /// one's, and the motion would walk it for its lease, seconds long (<see cref="PlayerbotMotion"/>). Stops the bot if it moves along
    /// one. Party AI and controllers only: a bot-led group and the battleground coordinator do not stop the bot at takeover (stopping
    /// there broke group formation); <see cref="ReleaseBrainRoute"/> marks the brain's route released instead, and the driver's first
    /// settle (<see cref="PlayerbotMotion.SettleAfterDriver"/>) stops the bot unless that think followed a route of its own. The drivers
    /// also stop the bot themselves when they have nothing to walk: the group leader's Hold states (PlayerbotGroupAI.Hold), a member
    /// whose leader is missing, Release, and the battleground coordinator's idle phases.
    /// </summary>
    private static void ReleaseMotion(WorldSession session, Player player)
    {
        if (PlayerbotMotion.IsActive(player)) PlayerbotMovementControl.Stop(session, player);
    }

    /// <summary>
    /// The battleground coordinator or a bot-led group takes the bot from its brain: the brain's route is marked released
    /// (<see cref="PlayerbotRoute.Released"/>) without stopping the bot (see <see cref="ReleaseMotion"/>). The driver's settle
    /// (<see cref="PlayerbotMotion.SettleAfterDriver"/>) stops a walk the driver does not take over (its own Follow clears the mark).
    /// </summary>
    private static void ReleaseBrainRoute(Player player)
    {
        if (PlayerbotMotion.RouteOf(player) is { } route) route.Released = true;
    }

    /// <summary>
    /// After a brain's think: a route the brain gave up (<see cref="PlayerbotBrain.TakeRouteAbandoned"/>), or a goal it changed, with no
    /// route kept or started by the think, stops the bot. The motion walks a route for its lease, as long as twice the bot's think gap
    /// (<see cref="PlayerbotMotion.NoteThinkGap"/>), and left to it a bot would run a stale route for seconds. A think that kept or started
    /// a route (a chase that waits for a tile keeps and renews the route it has) is not stopped.
    /// </summary>
    internal static void SettleMotion(WorldSession session, PlayerbotBrain brain, Player player, PlayerbotGoalKind goalBefore, uint renewals, uint sinceLast)
    {
        bool abandoned = brain.TakeRouteAbandoned();
        PlayerbotMotion.SettleAfterThink(session, player, abandoned || brain.Goal != goalBefore, renewals, sinceLast);
    }

    /// <summary>
    /// A bot aboard a ship whose leg was cancelled (a party, a battleground or a bot-led group took it over) is never put off at sea: its leg
    /// rides on to the next port and steps off there (<see cref="PlayerbotShip.Cancel"/>), driven here until it ends (movement orders
    /// acknowledged first, as the brain does). True while the ship leg still holds the bot; the new driver takes it from the dock. A leg
    /// that failed with the bot still aboard stays active the same way (<see cref="ShipTrip.Settling"/>), so no bot is left on a ship
    /// without a driver.
    /// </summary>
    private bool HoldAboard(ActiveBot active, Player driven, uint sinceLast) => HoldAboard(active.Session, driven, _options, sinceLast);

    /// <inheritdoc cref="HoldAboard(ActiveBot, Player, uint)"/>
    internal static bool HoldAboard(WorldSession session, Player driven, PlayerbotOptions options, uint sinceLast)
    {
        if (!PlayerbotShip.IsActive(driven)) return false;
        if (!PlayerbotMovementControl.Update(session, driven)) PlayerbotShip.Drive(session, driven, options, sinceLast, out _);
        return PlayerbotShip.IsActive(driven);
    }

    /// <summary>One tick of an autonomous bot outside a bot-led group: its party AI in a real player's group, its brain otherwise.</summary>
    private string UpdateAutonomous(ActiveBot active, Player driven, uint sinceLast)
    {
        // A bot in a real player's group follows its party AI; out of it (or after its master's timeout) the
        // brain takes over again, a fresh one: the old one's routes and targets belong to another place.
        bool party = active.Party.Drives(driven);
        if (active.PartyDriven && !party)
        {
            ReleaseMotion(active.Session, driven);
            ReplaceBrain(active);
        }
        // The party AI follows its master: a tram trip of the brain ends (its entrance consent with it); off the tram map again under the
        // brain, a bot left there takes the nearest exit (PlayerbotTram.Drive).
        if (party && !active.PartyDriven)
        {
            ReleaseMotion(active.Session, driven);
            PlayerbotTram.Cancel(driven);
            PlayerbotTaxi.Cancel(driven);
            // A bot aboard a lift is never moved off the car mid-shaft: its ride ends at the next stop (PlayerbotElevator.Cancel), and the
            // party AI takes it only once it stands on the landing. Meanwhile the ride is driven here (PartyDriven stays false).
            PlayerbotElevator.Cancel(driven);
            if (PlayerbotElevator.HoldsForLanding(driven))
            {
                if (!PlayerbotMovementControl.Update(active.Session, driven))
                    PlayerbotElevator.Drive(active.Session, driven, _options, sinceLast, out _);
                return "elevator";
            }

            // The journey and its legs end the same way; a bot aboard a ship rides on to the next port and steps off there (never at sea),
            // driven here meanwhile, and the party AI takes it on the dock.
            PlayerbotTravel.CancelLegs(active.Session, driven);
            if (HoldAboard(active, driven, sinceLast)) return "ship";
        }
        active.PartyDriven = party;
        if (party)
        {
            PlayerbotThinkProbe.Phase("party");
            uint partyRenewals = PlayerbotMotion.RenewalsOf(driven);
            active.Party.Update(driven, sinceLast);
            PlayerbotMotion.SettleAfterDriver(active.Session, driven, partyRenewals, sinceLast);
            return "party AI";
        }
        else
        {
            PlayerbotGoalKind goalBefore = active.Brain.Goal;
            uint renewals = PlayerbotMotion.RenewalsOf(driven);
            active.Brain.Update(sinceLast);
            SettleMotion(active.Session, active.Brain, driven, goalBefore, renewals, sinceLast);
            string? report = active.Brain.StallReport;
            bool awaitRelease = report?.StartsWith("death loop:", StringComparison.Ordinal) == true && !driven.IsAlive
                && !driven.RepopDestinationResolved;
            bool willLogStall = report is not null && !awaitRelease && active.Brain.StallCount != active.StallsLogged;
            (active.StallsLogged, bool sharedLogged) = LogStall(logger, active.Record.BotId, active.Name, active.Brain.StallCount,
                active.StallsLogged, report, active.Brain.StallOutsideHome, active.Brain.SharedSetAside,
                willLogStall && report is not null ? active.Brain.QaReportContext(driven, report) : null,
                awaitRelease,
                willLogStall ? active.Brain.FormatDiagnosticSnapshot(driven) : null);
            if (sharedLogged) active.Brain.SharedSetAside = null;
            IReadOnlyList<string> unreachable = active.Brain.TakeUnreachableReports();
            if (unreachable.Count > 0)
            {
                string diag = active.Brain.FormatDiagnosticSnapshot(driven);
                foreach (string skipped in unreachable)
                    LogUnreachable(logger, active.Record.BotId, active.Name, skipped, active.Brain.QaReportContext(driven, skipped), diag);
            }
            return "brain";
        }
    }

    /// <summary>
    /// The warning of a goal a bot skipped for lying where no walk reaches (PlayerbotWorldDestinations.SkipUnreachable; bot QA parses it as
    /// <c>QaSignalKind.UnreachableGoal</c>). The destinations report each entry once per window and queue every report of a think, so
    /// each skip is logged exactly once, none lost to a later one.
    /// </summary>
    internal static void LogUnreachable(ILogger logger, Guid botId, string name, string report, string? qaContext, string? diag = null)
        => logger.LogWarning("Playerbot {BotId} ({Name}) {Report}", botId, name,
            string.IsNullOrEmpty(diag) ? report + qaContext : report + qaContext + "; diag: " + diag);

    /// <summary>
    /// The warnings of a bot's stall (bot QA parses them, PlayerbotQaSignalParser): a new stall once (<paramref name="stallCount"/> moved
    /// past <paramref name="stallsLogged"/>), marked when the bot stood outside its home (<paramref name="outsideHome"/>,
    /// PlayerbotBrain.StallOutsideHome), and a goal it set aside for every bot (<paramref name="everyone"/>) as soon as there is one, with
    /// where the bots behind it stalled. Before, the set-aside was logged only beside a new stall line: one completed by a further give-up
    /// of a stall already logged (PlayerbotStallWatch.GiveUps) waited for the bot's next stall, and was logged beside a stall on another
    /// goal (live 2026-10-11 04:23:57: quest 2160 beside an Explore stall). Returns the stalls now logged and whether
    /// <paramref name="everyone"/> was logged (the caller clears it then). A death loop's report is never marked outside the home.
    /// </summary>
    internal static (int StallsLogged, bool SharedLogged) LogStall(ILogger logger, Guid botId, string name, int stallCount, int stallsLogged,
        string? stall, bool outsideHome, PlayerbotSharedSetAside? everyone, string? qaContext = null, bool awaitRelease = false, string? diag = null)
    {
        if (stallCount != stallsLogged && stall is not null && !awaitRelease)
        {
            stallsLogged = stallCount;
            // The mark belongs to a living stall: a death loop counts as a stall too and its report comes first, while the flag still
            // holds the last give-up (PlayerbotStallWatch.Report).
            if (outsideHome && stall.StartsWith("stalled ", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(diag))
                    logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal outside its home", botId, name, stall + qaContext);
                else
                    logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal outside its home; diag: {Diag}", botId, name, stall + qaContext, diag);
            }
            else
            {
                if (string.IsNullOrEmpty(diag))
                    logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal", botId, name, stall + qaContext);
                else
                    logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal; diag: {Diag}", botId, name, stall + qaContext, diag);
            }
        }

        if (everyone is not { } shared) return (stallsLogged, false);
        logger.LogWarning("Playerbots: {Count} bots stalled on {Goal}; every bot sets it aside (stalls at {Stalls})",
            PlayerbotSharedSetAsides.BotsToShare, shared.Goal, shared.Stalls);
        return (stallsLogged, true);
    }

    /// <summary>
    /// The running bots' status lines (StatusOf), the snapshot rebuilt only when one of them changed, the running set changed or
    /// PublishStopped touched it (_snapshotEpoch). <paramref name="bots"/> is the active set captured at the start of the tick, with
    /// <paramref name="botsVersion"/>, the version it was built from. A start or a drop can change <see cref="_active"/> after that
    /// capture and before this rebuild: a start's login check is a command at the start of a tick and the rest of it runs off the
    /// world thread, so the active-set add can land while this tick is already inside <see cref="Update"/>. Such a change is detected
    /// against <paramref name="botsVersion"/>, not the shared cache mark (_orderedVersion): a world-thread <see cref="Snapshot"/>
    /// inside the tick can rebuild the cache and republish before this publish, which would make the stale set look current. A bot
    /// that joined after the capture is not in <paramref name="bots"/>, so this publish writes no row for it and keeps whatever row
    /// is already published: its stopped row from its creation, or its running row if a world-thread Snapshot republished it. The
    /// next tick writes it.
    /// </summary>
    private void PublishSnapshot(ActiveBot[] bots, int botsVersion, uint elapsedMs, bool force = false)
    {
        _snapshotElapsedMs = unchecked(_snapshotElapsedMs + elapsedMs);
        int currentVersion = Volatile.Read(ref _activeVersion);
        bool sameActiveSet = currentVersion == botsVersion;
        if (!force
            && bots.Length > 64
            && sameActiveSet
            && currentVersion == _builtActiveVersion
            && Volatile.Read(ref _snapshotEpoch) == _builtEpoch
            && _snapshotElapsedMs < 1000)
        {
            return;
        }
        _snapshotElapsedMs = 0;

        bool changed = false;
        int running = 0;
        foreach (ActiveBot b in bots)
        {
            if (!sameActiveSet && !_active.ContainsKey(b.Record.BotId)) continue;
            running++;
            PlayerbotStatus status = StatusOf(b);
            if (!ReferenceEquals(status, b.Status)) { b.Status = status; changed = true; }
        }

        lock (_snapshotGate)
        {
            if (changed || running != _builtCount || _snapshotEpoch != _builtEpoch || currentVersion != _builtActiveVersion)
            {
                PlayerbotStatus[] current = Volatile.Read(ref _snapshot);
                if (sameActiveSet
                    && currentVersion == _builtActiveVersion
                    && _snapshotEpoch == _builtEpoch
                    && running == _builtCount
                    && running == bots.Length
                    && current.Length >= running)
                {
                    int stoppedCount = current.Length - running;
                    var next = new PlayerbotStatus[current.Length];
                    if (stoppedCount > 0)
                        Array.Copy(current, 0, next, 0, stoppedCount);
                    for (int i = 0; i < bots.Length; i++)
                        next[stoppedCount + i] = bots[i].Status ?? StatusOf(bots[i]);
                    Volatile.Write(ref _snapshot, next);
                }
                else
                {
                    // Rows this tick republishes are replaced. Every other row stays, including a bot that became active after
                    // `bots` was captured: its existing row remains (the stopped row from its creation, or the running one a world-thread
                    // Snapshot() published) until the next tick writes it.
                    // Membership is re-checked here, under the gate, not taken from `sameActiveSet`: a drop off the world thread
                    // (checkpoint conflict, delete, stop) removes the bot from _active and then, under this gate, its row. When that
                    // drop lands between the version read above and this lock, `sameActiveSet` is stale and the dropped bot's
                    // StatusOf (Faulted, session-closed: the drop kicked it) was re-appended after the drop had filtered it out,
                    // a ghost row nothing removed again (CI run 39573816775). A bot still in _active here is either kept, or its
                    // drop takes this gate after us and filters the row itself.
                    _republishedScratch.Clear();
                    _republishedScratch.EnsureCapacity(running);
                    _freshSnapshotScratch.Clear();
                    _freshSnapshotScratch.EnsureCapacity(running);
                    foreach (ActiveBot b in bots)
                    {
                        if (b.Status is null || !_active.TryGetValue(b.Record.BotId, out ActiveBot? live) || !ReferenceEquals(live, b)) continue;
                        _republishedScratch.Add(b.Record.BotId);
                        _freshSnapshotScratch.Add(b.Status);
                    }

                    _nextSnapshotScratch.Clear();
                    _nextSnapshotScratch.EnsureCapacity(current.Length + _freshSnapshotScratch.Count);
                    foreach (PlayerbotStatus s in current)
                    {
                        if (!_republishedScratch.Contains(s.BotId))
                            _nextSnapshotScratch.Add(s);
                    }
                    _nextSnapshotScratch.AddRange(_freshSnapshotScratch);
                    Volatile.Write(ref _snapshot, _nextSnapshotScratch.ToArray());
                }

                _builtCount = running;
                _builtEpoch = _snapshotEpoch;
                _builtActiveVersion = currentVersion;
            }
        }
    }

    /// <summary>
    /// World thread: say the chat replies the workers finished, each through its bot's own CMSG_MESSAGECHAT (a client's answer: no
    /// action budget). A reply for a bot that stopped, is scripted or left the world is dropped.
    /// </summary>
    private void DeliverChatReplies()
    {
        if (_chat is not { } chat) return;
        foreach (Chat.BotChatMute mute in chat.DrainMutes()) ApplyChatMute(mute);
        foreach (Chat.BotChatReply reply in chat.DrainReplies())
        {
            if (!_active.TryGetValue(reply.BotId, out ActiveBot? active) || active.Paused || active.Controller is not null
                || active.Session.State != SessionState.InWorld || active.Session.Player is not { } speaker)
            {
                chat.Lost(reply.Speech); // the bot is gone: the line will never be said
                continue;
            }

            try { active.Party.DeliverChat(speaker, reply); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.feature", ex);
                // A chat reply is never worth a bot: it is dropped and the bot carries on.
                logger.LogWarning(ex, "Playerbot {BotId} ({Name}) could not say a chat reply", active.Record.BotId, active.Name);
            }
        }
    }

    /// <summary>
    /// World thread: the bot chat safety's automatic mute (World:Playerbots:Chat:Safety:AutoMute), through the account mute of
    /// <c>.mute</c> (persisted, lifted by <c>.unmute</c>). A longer mute already in force is kept.
    /// </summary>
    private void ApplyChatMute(Chat.BotChatMute mute)
    {
        if (services.GetService<Gm.Audit.GmAuditFeature>() is not { } audit)
        {
            logger.LogWarning("Bot chat safety could not mute {Player}: the GM audit feature (account mutes) is not running", mute.PlayerName);
            return;
        }

        long seconds = mute.Minutes * 60L;
        if (audit.MuteOf(mute.AccountId) is { } existing && existing.MutedUntil >= audit.NowUnixSeconds + seconds) return;
        const string reason = "Bot chat safety: repeated flagged messages to bots";
        audit.Mute(mute.AccountId, seconds, "Bot chat safety", Kernel.Accounts.AccountSecurity.GameMaster, reason);
        if (_world?.FindOnlinePlayer(new Game.ObjectGuid(mute.PlayerGuid)) is { } player)
            Gm.Core.GmReplies.SendSystemMessage(player, Gm.Audit.GmAuditStrings.YourChatDisabled(Gm.Audit.GmAuditStrings.Span(seconds), "Bot chat safety", reason));
    }

    private async Task CheckpointAsync()
    {
        if (SplitBrainLatched) return; // another world process owns the rows: this one issues no playerbot writes
        KickWarLayerWipe(); // with no bot running there is no bot save to kick it
        if (!await _operations.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            ActiveBot[] bots = _active.Values.ToArray();
            // One world-thread hop for every bot's goal (the coordinators' collections are world-thread owned).
            (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[] goals = await GoalsOnWorldThreadAsync(bots).ConfigureAwait(false);
            // Only a bot whose persisted goal fields changed is written (UpdatedUnix alone is never a reason), and all of them
            // together: one transaction per chunk instead of one autocommit (one fsync) per bot per checkpoint.
            var live = new List<(ActiveBot Active, ManagedPlayerbot Next)>(bots.Length);
            for (int i = 0; i < bots.Length; i++)
            {
                ActiveBot active = bots[i];
                if (active.Record.State == ManagedPlayerbotState.Faulted) continue;
                if (active.Session.State == SessionState.Closed)
                {
                    // One bot's failed stop must not keep the other bots from being checkpointed.
                    try
                    {
                        if (Volatile.Read(ref active.FaultCode) is { } fault)
                        {
                            (bool desired, string code) = RecordFault(active.Record.BotId, fault, active.Session.Player, active.AnomalySubject);
                            await StopCoreAsync(active.Record, desired, CancellationToken.None, code).ConfigureAwait(false);
                        }
                        else
                        {
                            ReportSessionLostOnce(active, "checkpoint");
                            await StopCoreAsync(active.Record, false, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FaultLedger.Shared.Record("playerbot.checkpoint", ex);
                        logger.LogWarning(ex, "Playerbot {BotId} checkpoint stop failed ({Type}: {Message})", active.Record.BotId, ex.GetType().Name, ex.Message);
                    }
                    continue;
                }
                live.Add((active, active.Record with { Goal = goals[i].Goal, TargetEntry = goals[i].TargetEntry, QuestId = goals[i].QuestId }));
            }

            var conflicted = new HashSet<ActiveBot>();
            int foreignRows = 0;
            ManagedPlayerbot? lastForeign = null;
            var changed = live.Where(item => GoalFieldsDiffer(item.Active.Record, item.Next)).ToList();
            if (changed.Count > 0)
            {
                // A failed write (not a refused row) leaves these bots' records as they were, so the next checkpoint retries them,
                // and it must not keep the set-asides below from being saved.
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    IManagedPlayerbotStore store = scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    for (int start = 0; start < changed.Count; start += ManagedPlayerbotUpdate.MaxBatch)
                    {
                        int count = Math.Min(ManagedPlayerbotUpdate.MaxBatch, changed.Count - start);
                        var updates = new ManagedPlayerbotUpdate[count];
                        for (int j = 0; j < count; j++)
                        {
                            ManagedPlayerbot next = changed[start + j].Next;
                            updates[j] = new ManagedPlayerbotUpdate(next with { Revision = next.Revision + 1, UpdatedUnix = now }, next.Revision);
                        }

                        IReadOnlyList<bool> applied = await store.UpdateManyAsync(updates, CancellationToken.None).ConfigureAwait(false);
                        if (applied.Count != count)
                            throw new InvalidOperationException($"managed-update-many-mismatch: {applied.Count} results for {count} updates");
                        // The committed rows first: nothing below may leave a written bot with a stale revision.
                        var refused = new List<ActiveBot>();
                        for (int j = 0; j < count; j++)
                        {
                            ActiveBot active = changed[start + j].Active;
                            if (applied[j]) active.Record = updates[j].Bot;
                            else refused.Add(active);
                        }

                        foreach (ActiveBot active in refused)
                        {
                            // Someone else changed or removed the row (in-process paths run under _operations and never do). This is not the bot's fault.
                            conflicted.Add(active);
                            ManagedPlayerbot? stored;
                            try { stored = await store.FindAsync(active.Record.BotId, CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                FaultLedger.Shared.Record("playerbot.checkpoint", ex);
                                logger.LogWarning(ex, "Playerbot {BotId} conflicting row could not be read ({Type})", active.Record.BotId, ex.GetType().Name);
                                continue; // not faulted: the next checkpoint finds the same conflict and looks again
                            }

                            // Another writer owns the row (a second world process on this characters database, or an operator): the bot leaves the active set
                            // without a fault, a quarantine or a write, so its row keeps what the owner wrote (DesiredEnabled included) and the owner keeps running it.
                            // A row that differs from ours only in DesiredEnabled is an operator edit (a SQL UPDATE), not a second process: it drops the bot but
                            // does not count toward a split brain. Any other difference (state, goal, error code) or none at all counts.
                            if (stored is not null && stored.State != ManagedPlayerbotState.Faulted && !IsOperatorEdit(active.Record, stored))
                            {
                                foreignRows++;
                                lastForeign = stored;
                            }

                            await DropFromActiveAsync(active, stored, writeSetAsides: stored is null || stored.State == ManagedPlayerbotState.Faulted).ConfigureAwait(false);
                        }
                    }

                    if (foreignRows > SplitBrainConflictThreshold) await EnterSplitBrainAsync(foreignRows, lastForeign).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.checkpoint", ex);
                    logger.LogWarning(ex, "Managed playerbot checkpoint write failed ({Type})", ex.GetType().Name);
                }
            }

            if (SplitBrainLatched) return;
            foreach ((ActiveBot active, _) in live)
            {
                if (conflicted.Contains(active)) continue;
                await SaveBotSetAsidesAsync(active).ConfigureAwait(false);
                await SaveBotRelationsAsync(active).ConfigureAwait(false);
            }
            await SaveSharedSetAsidesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.checkpoint", ex);
            logger.LogWarning(ex, "Managed playerbot checkpoint failed ({Type})", ex.GetType().Name);
        }
        finally { _operations.Release(); }
        try
        {
            await RetryQuarantinedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.checkpoint", ex);
            logger.LogWarning(ex, "Managed playerbot quarantine retry loop failed ({Type})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// More than <see cref="SplitBrainConflictThreshold"/> bots had their rows moved on by another writer in one checkpoint: a second world process is running this fleet.
    /// Like the claim watch's terminal path it latches until restart or an operator's <see cref="ClearSplitBrainAsync"/>: checkpoints, starts and stops stop issuing managed-row and set-aside writes,
    /// and every bot still active is stopped without one and no longer driven, so it makes no further progress. Their characters still save (autosave and the session close); only
    /// <c>Cluster:Fencing</c> bounds those on a server database. Runs under <c>_operations</c> (the checkpoint holds it).
    /// </summary>
    private async Task EnterSplitBrainAsync(int conflicts, ManagedPlayerbot? lastRow)
    {
        SplitBrainLatched = true;
        _quarantine.Clear(); // no retry timer fires for a quarantined bot once the latch clears; its row stays Faulted, which only an operator start (or a new fault count) leaves
        string row = lastRow is null ? "no row read" : string.Create(CultureInfo.InvariantCulture,
            $"last conflicting row bot {lastRow.BotId:D}: revision {lastRow.Revision}, state {lastRow.State}, updated {DateTimeOffset.FromUnixTimeSeconds(lastRow.UpdatedUnix):u}");
        logger.LogCritical("Split brain: {Conflicts} managed playerbot rows were changed by another writer in one checkpoint (threshold {Threshold}); another world process is writing this characters database ({Row}). This process, the one whose rows were taken over, stops issuing playerbot writes. Stop the other world, then clear it with .playerbot splitbrain clear", conflicts, SplitBrainConflictThreshold, row);
        // Every bot still active leaves without a managed-row or set-aside write and stops being driven, so none makes further progress on rows that are not ours.
        foreach (ActiveBot active in _active.Values.ToArray())
        {
            try { await DropFromActiveAsync(active, active.Record, writeSetAsides: false, announce: false).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.checkpoint", ex);
                logger.LogWarning(ex, "Playerbot {BotId} split-brain stop failed ({Type})", active.Record.BotId, ex.GetType().Name);
            }
        }
    }

    /// <summary>Drops one active bot as an out-of-band row conflict would, without the row writes (tests only; completes when the drop has).</summary>
    internal Task DropFromActiveForTestsAsync(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active)
            ? DropFromActiveAsync(active, active.Record, writeSetAsides: false, announce: false)
            : Task.CompletedTask;

    /// <summary>Latches the split brain as a checkpoint would (tests only).</summary>
    internal async Task ForceSplitBrainForTests()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try { await EnterSplitBrainAsync(0, null).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    /// <summary>Whether <paramref name="stored"/> differs from <paramref name="ours"/> only in <see cref="ManagedPlayerbot.DesiredEnabled"/> (and the revision stamps): an operator's edit.</summary>
    private static bool IsOperatorEdit(ManagedPlayerbot ours, ManagedPlayerbot stored)
        => stored.DesiredEnabled != ours.DesiredEnabled
            && stored with { DesiredEnabled = ours.DesiredEnabled, Revision = ours.Revision, UpdatedUnix = ours.UpdatedUnix } == ours;

    /// <summary>What <see cref="ClearSplitBrainAsync"/> found: whether the latch was set, and the stored rows it re-read (all bots, those wanted running, those faulted).</summary>
    public readonly record struct SplitBrainClear(bool WasSet, int Rows, int Enabled, int Faulted);

    /// <summary>
    /// Clears the split-brain latch (<c>.playerbot splitbrain clear</c>); the operator must have stopped the other world first. It re-reads the stored registry rows and reports them, then
    /// un-latches. No bot is active at that point (entering the latch stopped them all and every start is refused while it is set), so there is no in-memory row to reconcile:
    /// each start reads the stored row afresh. If the read fails the latch stays set and the exception propagates.
    /// </summary>
    public async Task<SplitBrainClear> ClearSplitBrainAsync()
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!SplitBrainLatched) return new(false, 0, 0, 0);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IReadOnlyList<ManagedPlayerbot> stored = await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
                .LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
            var result = new SplitBrainClear(true, stored.Count, stored.Count(r => r.DesiredEnabled), stored.Count(r => r.State == ManagedPlayerbotState.Faulted));
            _conflictLogged.Clear();
            SplitBrainLatched = false;
            logger.LogWarning("Playerbot split-brain latch cleared by an operator; playerbot writes resume ({Rows} stored rows re-read, {Enabled} wanted running, {Faulted} faulted)",
                result.Rows, result.Enabled, result.Faulted);
            return result;
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// A running bot whose registry row no longer exists (<paramref name="row"/> null, deleted out of band) or was faulted out of band
    /// (the checkpoint skips Faulted records, so it could never stop): it leaves <see cref="_active"/> without a revision-checked
    /// write instead of failing or lingering every checkpoint. Logged once. The faulted row is published as stopped.
    /// </summary>
    private async Task DropFromActiveAsync(ActiveBot active, ManagedPlayerbot? row, bool writeSetAsides = true, bool announce = true)
    {
        Guid botId = active.Record.BotId;
        if (announce)
        {
            if (row is null) logger.LogWarning("Playerbot {BotId} has no registry row any more; dropping it from the active set", botId);
            else if (row.State == ManagedPlayerbotState.Faulted) logger.LogWarning("Playerbot {BotId} was faulted out of band ({Code}); dropping it from the active set", botId, row.ErrorCode);
            else WarnConflictOnce(botId, "write (dropped from the active set)");
        }
        active.Paused = true;
        // A row that exists keeps the bot's suspensions across a restart, as a stop does.
        // (not when another writer owns the row: then the set-aside rows are theirs too, and this process writes none).
        if (row is not null && writeSetAsides)
        {
            try { await SaveBotSetAsidesAsync(active, force: true).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.checkpoint", ex);
                logger.LogWarning(ex, "Playerbot {BotId} set-aside save failed ({Type})", botId, ex.GetType().Name);
            }

            await SaveBotRelationsAsync(active).ConfigureAwait(false);
        }

        await ReleaseDefendFlagAsync(active).ConfigureAwait(false);
        if (_world is { } world) _ = world.InvokeAsync(() => { coordinator?.Forget(botId); _pathJobs?.Forget(botId); battlegrounds?.Forget(botId); return true; });
        active.Brain.Stop();
        _chat?.Abandon(botId); // replies still being written for it are no longer wanted
        Social.Untrack(active.Record.CharacterId);
        try { if (Interlocked.Exchange(ref active.Controller, null) is { } controller) controller.Detached(botId); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.checkpoint", ex);
            logger.LogWarning(ex, "Playerbot {BotId} controller detach failed ({Type})", botId, ex.GetType().Name);
        }
        active.Session.Kick("playerbot dropped from the active set");
        if (_active.TryRemove(new KeyValuePair<Guid, ActiveBot>(botId, active))) Interlocked.Increment(ref _activeVersion);
        BotRegistry.TryRemove(active.Entry);
        if (row is not null)
        {
            active.Record = row;
            // Not running here, whatever the row says: published as stopped (a faulted row stays faulted).
            PublishStopped(row.State == ManagedPlayerbotState.Faulted ? row : row with { State = ManagedPlayerbotState.Stopped }, active.Name, active.MapId);
        }
        else
        {
            // The same cleanup as an in-process delete: no status line, quarantine or home is kept for a bot with no row.
            _quarantine.TryRemove(botId, out _);
            _homes.TryRemove(botId, out _);
            lock (_snapshotGate)
            {
                Volatile.Write(ref _snapshot, Volatile.Read(ref _snapshot).Where(s => s.BotId != botId).ToArray());
                _snapshotEpoch++;
            }
        }

        try { await active.Scope.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.checkpoint", ex);
            logger.LogWarning(ex, "Playerbot {BotId} scope disposal failed ({Type})", botId, ex.GetType().Name);
        }
    }

    /// <summary>Whether the checkpoint's persisted fields (the goal, its target and its quest) differ between two records.</summary>
    private static bool GoalFieldsDiffer(ManagedPlayerbot stored, ManagedPlayerbot next)
        => stored.Goal != next.Goal || stored.TargetEntry != next.TargetEntry || stored.QuestId != next.QuestId;

    /// <summary>A running bot's status line: the one it had when nothing in it changed (no allocation), otherwise a new one.</summary>
    private PlayerbotStatus StatusOf(ActiveBot b)
    {
        bool closed = b.Session.State == SessionState.Closed;
        ManagedPlayerbotState state = closed ? ManagedPlayerbotState.Faulted : b.Record.State;
        (PlayerbotGoalKind goal, uint target, uint quest) = GoalOf(b);
        uint health = b.Session.Player?.Health ?? 0;
        string? error = closed ? "session-closed" : b.Record.ErrorCode ?? StallOf(b);
        string? risk = RiskOf(b);
        string? group = coordinator?.Describe(b.Record.BotId) ?? battlegrounds?.Describe(b.Record.BotId);
        string? home = HomeLineOf(b);
        if (b.Status is { } last && last.BotId == b.Record.BotId && last.State == state && last.DesiredEnabled == b.Record.DesiredEnabled
            && last.Goal == goal && last.TargetEntry == target && last.QuestId == quest && last.MapId == b.MapId && last.Health == health
            && string.Equals(last.Name, b.Name, StringComparison.Ordinal) && string.Equals(last.ErrorCode, error, StringComparison.Ordinal)
            && string.Equals(last.Risk, risk, StringComparison.Ordinal) && string.Equals(last.Group, group, StringComparison.Ordinal)
            && string.Equals(last.Home, home, StringComparison.Ordinal))
            return last;
        return new PlayerbotStatus(b.Record.BotId, b.Name, state, b.Record.DesiredEnabled, goal, target, quest, b.MapId, health, error, risk,
            group, home);
    }

    /// <summary>
    /// The home line of a running bot: none without a home, <c>suspended</c> while a scripted controller, a party AI, a bot-led group or
    /// the battlegrounds coordinator drives it (they ignore the home), otherwise the brain's report (<see cref="PlayerbotHomeReturn.Report"/>).
    /// </summary>
    private string? HomeLineOf(ActiveBot active) => _homes.IsEmpty || !_homes.ContainsKey(active.Record.BotId) ? null
        : active.Controller is not null || active.PartyDriven || active.GroupDriven || active.BattlegroundDriven ? "suspended" : active.Brain.HomeReport;

    /// <summary>The home line of a bot that is not running.</summary>
    private string? StoppedHome(Guid botId) => !_homes.IsEmpty && _homes.ContainsKey(botId) ? "stopped" : null;

    /// <summary>The risk line of a running bot (its party AI's while that drives it); none for a scripted bot.</summary>
    private string? RiskOf(ActiveBot active) => active.Controller is not null ? null
        : active.BattlegroundDriven ? "decision=battleground"
        : active.GroupDriven ? coordinator?.FindAI(active.Record.BotId) is { Retreat.Active: true } ai
            ? $"retreat reason={ai.Retreat.Reason}" : "decision=group"
        : active.PartyDriven ? active.Party.RiskReport : active.Brain.RiskReport;

    /// <summary>A running bot's current stall (<see cref="PlayerbotStallWatch"/>), shown where a fault would be.</summary>
    private static string? StallOf(ActiveBot active) => active.PartyDriven || active.GroupDriven || active.BattlegroundDriven || active.Controller is not null ? null
        : active.Brain.StallReport is { } stall ? active.StallCode(stall) : null;

    private string FormatBotDiagnostics(ActiveBot active, Player player)
    {
        (PlayerbotGoalKind goal, uint target, uint quest) = GoalOf(active);
        string driver = active.Controller is not null ? "controller"
            : active.PartyDriven ? "party"
            : active.GroupDriven ? "group"
            : active.BattlegroundDriven ? "bg"
            : "brain";
        string stall = StallOf(active) ?? "none";
        string home = HomeLineOf(active) ?? "none";
        string risk = RiskOf(active) ?? "none";
        string snap = active.Brain.FormatDiagnosticSnapshot(player);
        string grp = active.GroupDriven && coordinator is { } coord ? $" grpStep={coord.FindAI(active.Record.BotId)?.Step ?? "-"}" : string.Empty;
        return $"bot={active.Record.BotId.ToString("N")[..8]} drv={driver}{grp} goal={goal}/{target}/q{quest} stall={stall} home={home} risk={risk} {snap}";
    }

    /// <summary>World thread: the PvP flag a defend post turned on goes off (<see cref="PlayerbotDefend.ReleaseFlag"/>), so the saved character keeps none.</summary>
    private static void ReleaseDefendFlag(ActiveBot active)
    {
        if (active.Session.Player is { IsInWorld: true } player) PlayerbotDefend.ReleaseFlag(active.Session, player);
    }

    /// <summary>As <see cref="ReleaseDefendFlag"/> from any thread, done before the bot's session closes and the character is saved.</summary>
    private async Task ReleaseDefendFlagAsync(ActiveBot active)
    {
        if (_world is not { IsStopped: false } world) return;
        if (world.IsWorldThread)
        {
            ReleaseDefendFlag(active);
            return;
        }

        try { await world.InvokeAsync(() => { ReleaseDefendFlag(active); return true; }).ConfigureAwait(false); }
        catch (TaskCanceledException) when (world.IsStopped) { }
    }

    private void ReplaceBrain(ActiveBot active)
    {
        lock (active.SetAsideGate)
        {
            PlayerbotSetAside[] current = active.Brain.Suspensions.Export(active.Record.BotId, active.Record.CharacterId,
                active.Session.World.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
            active.Brain.Stop();
            PlayerbotDefendPost? defend = active.Brain.Defend;
            active.Brain = NewBrain(active.Session, active.Record.BotId, restoreLoaded: false);
            active.Brain.Defend = defend;
            active.Brain.Suspensions.Restore(current, active.Session.World.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
            Volatile.Write(ref active.SavedSetAsideVersion, -1);
        }
    }

    private async Task SaveBotSetAsidesAsync(ActiveBot active, bool force = false)
    {
        if (_world is not { } world) return;
        if (!force && Volatile.Read(ref active.SavedSetAsideVersion) == active.Brain.Suspensions.Version) return;
        (PlayerbotSetAside[] rows, int version, PlayerbotBrain brain) Capture()
        {
            PlayerbotBrain brain = active.Brain;
            PlayerbotSuspensions state = brain.Suspensions;
            return (state.Export(active.Record.BotId, active.Record.CharacterId, active.Session.World.NowMs,
                _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds()), state.Version, brain);
        }
        // WorldTestHost and the production host may stop the world before stopping features. No tick can mutate the brain then.
        (PlayerbotSetAside[] rows, int version, PlayerbotBrain brain) snapshot;
        try { snapshot = world.IsStopped || world.IsWorldThread ? Capture() : await world.InvokeAsync(Capture).ConfigureAwait(false); }
        catch (TaskCanceledException) when (world.IsStopped) { snapshot = Capture(); }
        (PlayerbotSetAside[] rows, int version, PlayerbotBrain brain) = snapshot;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IManagedPlayerbotSetAsideStore>() is { } store)
            await RetryTransientBusyAsync(() => store.ReplaceAsync(active.Record.BotId, rows), CancellationToken.None).ConfigureAwait(false);
        if (force) _loadedSetAsides[active.Record.BotId] = rows;
        lock (active.SetAsideGate)
            if (ReferenceEquals(active.Brain, brain)) Volatile.Write(ref active.SavedSetAsideVersion, version);
    }

    private int _warLayerLive = 1;
    private int _warWipeRequested;
    private int _warWipeRunning;
    private Task _warWipeTask = Task.CompletedTask;

    // The bots whose war layer was wiped in memory and whose rows are not yet written: a bot leaves the set only after its write succeeded, so a failed write is
    // tried again at the next kick.
    private readonly ConcurrentDictionary<Guid, byte> _warWipePending = new();

    // Deleted bots whose rows are gone from the store and whose gate was retired: nothing is written for them again (a write would only make a gate to put [] in).
    private readonly ConcurrentDictionary<Guid, byte> _retiredBots = new();

    // War Mode on marks the war layer live; once it is off and ClearOnDisable is set, the layer is wiped (once) by a background task of its own, started here
    // and guarded so that only one runs at a time. A bot's save (a checkpoint, a stop, a pause) only kicks it and never waits for it or for another bot's write.
    // The wipe is memory-wide, so it also reaches the bots that are not running; the checkpoint kicks it on every tick, so it does not depend on a running bot.
    private void KickWarLayerWipe()
    {
        BotSocial.PlayerbotWarModeOptions war = _options.Social.WarMode;
        if (war.Enabled) { Volatile.Write(ref _warLayerLive, 1); return; }
        if (!war.ClearOnDisable || _stopping) return;
        if (Interlocked.Exchange(ref _warLayerLive, 0) == 1) Volatile.Write(ref _warWipeRequested, 1);
        if (Volatile.Read(ref _warWipeRequested) == 0 && _warWipePending.IsEmpty) return;
        if (Interlocked.CompareExchange(ref _warWipeRunning, 1, 0) != 0) return;
        _warWipeTask = Task.Run(RunWarLayerWipeAsync);
    }

    private async Task RunWarLayerWipeAsync()
    {
        try
        {
            BotSocial.PlayerbotSocialMemory memory = Social.Memory;
            do
            {
                if (Interlocked.Exchange(ref _warWipeRequested, 0) == 1)
                {
                    List<Guid> changed = [];
                    memory.ClearWarLayers(changed);
                    foreach (Guid botId in changed) _warWipePending[botId] = 0;
                }

                if (_warWipePending.IsEmpty) continue;
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IPlayerbotRelationStore>() is not { } store)
                {
                    _warWipePending.Clear();
                    continue;
                }

                foreach (Guid botId in _warWipePending.Keys)
                {
                    if (_retiredBots.ContainsKey(botId)) { _warWipePending.TryRemove(botId, out _); continue; }
                    try
                    {
                        await WriteBotRelationsAsync(store, memory, botId).ConfigureAwait(false);
                        _warWipePending.TryRemove(botId, out _); // only a written bot is done; a failed one stays for the next kick
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
                        logger.LogWarning(ex, "Playerbot {BotId} social memory could not be written after the war layer was cleared ({Type}); it is tried again", botId, ex.GetType().Name);
                    }
                }
            }
            while (Volatile.Read(ref _warWipeRequested) != 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot war layer wipe failed ({Type})", ex.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _warWipeRunning, 0);
        }

        if (Volatile.Read(ref _warWipeRequested) != 0) KickWarLayerWipe(); // a switch-off that arrived as the task ended
    }

    /// <summary>The bots whose cleared war layer still waits for a successful write (tests).</summary>
    internal int WarWipePendingCount => _warWipePending.Count;

    /// <summary>Completes when the war layer wipe task that is running now (if any) has ended (tests).</summary>
    internal Task WarWipeIdle => _warWipeTask;

    // One bot's rows written through its gate; a running bot's saved version is recorded with the write (taken before the snapshot, so it never runs ahead of the rows).
    private Task WriteBotRelationsAsync(IPlayerbotRelationStore store, BotSocial.PlayerbotSocialMemory memory, Guid botId)
    {
        long version = 0;
        return WriteRelationsAsync(store, botId, () =>
        {
            version = memory.Version(botId);
            return memory.Snapshot(botId);
        }, () =>
        {
            if (_active.TryGetValue(botId, out ActiveBot? active)) Volatile.Write(ref active.SavedRelationVersion, version);
        });
    }

    /// <summary>The saved relation version of a running bot, or -2 when it is not running (tests).</summary>
    internal long SavedRelationVersionOf(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) ? Volatile.Read(ref active.SavedRelationVersion) : -2;

    /// <summary>Whether the bot has a write gate (tests).</summary>
    internal bool HasRelationGate(Guid botId) => _relationGates.ContainsKey(botId);

    // One writer per bot at a time: a forget and a checkpoint (or two forgets) never replace the same bot's rows concurrently, and each takes its
    // snapshot inside the gate, so the last writer holds the newest rows.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _relationGates = new();

    // A deleted bot's gate is retired by its last write (retire: true): taken out of the map while held, so no later write finds it. A write that was already
    // waiting on it gets it after the release, sees it is no longer the bot's gate and starts over on a fresh one. The retired semaphore is never disposed:
    // a queued waiter would then be released onto a disposed object, and a SemaphoreSlim whose wait handle was never asked for holds nothing to free.
    internal async Task WriteRelationsAsync(IPlayerbotRelationStore store, Guid botId, Func<IReadOnlyList<PlayerbotRelation>> rows, Action? written = null, bool retire = false)
    {
        while (true)
        {
            SemaphoreSlim gate = _relationGates.GetOrAdd(botId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            bool attempted = false;
            try
            {
                if (!_relationGates.TryGetValue(botId, out SemaphoreSlim? current) || !ReferenceEquals(current, gate)) continue; // retired while this write waited
                attempted = true;
                IReadOnlyList<PlayerbotRelation> snapshot = rows();
                await RetryTransientBusyAsync(() => store.ReplaceAsync(botId, snapshot), CancellationToken.None).ConfigureAwait(false);
                written?.Invoke();
                return;
            }
            finally
            {
                // Retired whether or not the write succeeded: the bot is deleted either way, and a failed write must not leave its gate in the map.
                if (retire && attempted) _relationGates.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(botId, gate));
                gate.Release();
            }
        }
    }

    /// <summary>
    /// A bot was deleted (or its character was already gone): forget its relations in memory and in the store, and the other bots' relations
    /// toward its character, which are written again without it. Never throws: a failure is recorded at <c>playerbot.social-memory</c>.
    /// </summary>
    internal Task ForgetRelationsAsync(IServiceProvider provider, Guid botId, int characterId)
        => ForgetRelationsAsync(provider, [(botId, characterId > 0 ? new[] { characterId } : [])]);

    /// <summary>
    /// Several bots were deleted (a fleet reset): every one is forgotten in memory first (the bot, then everyone's relation toward its characters),
    /// then each removed bot's rows are deleted once and each surviving bot that knew one of them is written once, without all of them.
    /// Never throws: a failure is recorded at <c>playerbot.social-memory</c>.
    /// </summary>
    private async Task ForgetRelationsAsync(IServiceProvider provider, IReadOnlyList<(Guid BotId, IReadOnlyList<int> CharacterIds)> removed)
    {
        try
        {
            BotSocial.PlayerbotSocialMemory memory = Social.Memory;
            foreach ((Guid botId, _) in removed) memory.Forget(botId);
            HashSet<Guid> survivors = [];
            foreach ((_, IReadOnlyList<int> characterIds) in removed)
                foreach (int characterId in characterIds)
                    if (characterId > 0) survivors.UnionWith(memory.ForgetOthers(ArcaneCore.Game.ObjectGuid.Player((uint)characterId).Value));
            if (provider.GetService<IPlayerbotRelationStore>() is not { } store) return;
            foreach ((Guid botId, _) in removed)
            {
                if (_retiredBots.ContainsKey(botId)) continue; // already deleted from the store and its gate retired: nothing to write, no gate to make
                await WriteRelationsAsync(store, botId, static () => [], retire: true).ConfigureAwait(false);
                _retiredBots[botId] = 0; // only after the write went through: a failed delete is tried again by the next forget
            }

            foreach (Guid other in survivors)
                await WriteBotRelationsAsync(store, memory, other).ConfigureAwait(false); // and its saved version moves, so the next checkpoint does not write it again
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot social memory of {Count} deleted bots could not be removed ({Type})", removed.Count, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Write the bot's social memory when it changed since the last write. Off the world thread: the memory is thread-safe and the rows are
    /// a copy. A failure is recorded at <c>playerbot.social-memory</c> and logged; it never blocks a stop or a checkpoint, and the next
    /// call tries again (the saved version is only recorded after the write).
    /// </summary>
    private async Task SaveBotRelationsAsync(ActiveBot active)
    {
        Guid botId = active.Record.BotId;
        try
        {
            BotSocial.PlayerbotSocialMemory memory = Social.Memory;
            KickWarLayerWipe();
            if (Volatile.Read(ref active.SavedRelationVersion) == memory.Version(botId)) return;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IPlayerbotRelationStore>() is { } store)
            {
                long version = 0;
                await WriteRelationsAsync(store, botId, () =>
                {
                    version = memory.Version(botId); // before the snapshot: the saved version never runs ahead of the rows
                    return memory.Snapshot(botId);
                }, () => Volatile.Write(ref active.SavedRelationVersion, version)).ConfigureAwait(false);
            }
            else Volatile.Write(ref active.SavedRelationVersion, memory.Version(botId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, ex);
            logger.LogWarning(ex, "Playerbot {BotId} social memory save failed ({Type})", botId, ex.GetType().Name);
        }
    }

    private async Task SaveSharedSetAsidesAsync()
    {
        if (_world is not { } world) return;
        if (Volatile.Read(ref _savedSharedSetAsideVersion) == _sharedSetAsides.Version) return;
        (PlayerbotSetAside[] rows, int version) Capture()
        {
            return (_sharedSetAsides.Export(world.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds()),
                _sharedSetAsides.Version);
        }
        (PlayerbotSetAside[] rows, int version) snapshot;
        try { snapshot = world.IsStopped || world.IsWorldThread ? Capture() : await world.InvokeAsync(Capture).ConfigureAwait(false); }
        catch (TaskCanceledException) when (world.IsStopped) { snapshot = Capture(); }
        (PlayerbotSetAside[] rows, int version) = snapshot;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IManagedPlayerbotSetAsideStore>() is { } store)
            await RetryTransientBusyAsync(() => store.ReplaceAsync(Guid.Empty, rows), CancellationToken.None).ConfigureAwait(false);
        Volatile.Write(ref _savedSharedSetAsideVersion, version);
    }

    private void ReportSessionLostOnce(ActiveBot active, string where)
    {
        if (active.Paused || _stopping || ResetInProgress || Volatile.Read(ref active.FaultCode) is not null)
            return;
        if (Interlocked.Exchange(ref active.SessionLostReported, 1) != 0)
            return;

        string detail = $"bot {active.Record.BotId:D} session lost while registered active ({where}, sessionState={active.Session.State}, hasPlayer={active.Session.Player is not null}, inWorld={active.Session.Player?.IsInWorld == true})";
        if (active.Session.Player is { } player)
        {
            active.AnomalySubject = PlayerbotAnomalies.SubjectOf(player);
            PlayerbotAnomalies.Report(player, "lifecycle", "session-lost", detail, AnomalySeverity.Warning);
        }
        else
        {
            AnomalyLedger.Shared.Report(
                PlayerbotAnomalies.Subsystem("lifecycle"),
                "session-lost",
                active.AnomalySubject,
                $"{detail} | map {active.MapId}",
                AnomalySeverity.Warning,
                origin: _world?.Identity);
        }
    }

    /// <summary>
    /// An exception out of a bot's update (world thread): log all of it, remember the fault for the checkpoint, close the session.
    /// The next checkpoint quarantines the bot (<see cref="RecordFault"/>).
    /// </summary>
    private void Fault(ActiveBot active, string what, Exception ex)
    {
        // Counted in the fault ledger (.server faults). Every occurrence is logged, because the QA log tap reads these lines as action faults,
        // but only the first FaultLogLimit of one signature carry the exception (Error, with the stack trace): a storm of one fault is then a
        // short Warning line per bot instead of a stack trace per bot, so it cannot flood the console.
        FaultRecording recording = FaultLedger.Shared.Record("playerbot", ex);
        string diag = PlayerbotTraceLog.FormatExceptionDiag(ex, active.Session.Player, active.Brain);
        bool hasDiag = !string.IsNullOrEmpty(diag);
        // Error, not Warning: an action fault is an exception the server did not expect, and Error is what the console colours red. The
        // line text ("Playerbot {BotId} {What} failed ({Type}: {Message})") is what PlayerbotQaSignalParser matches; the level is not part of it.
        if (recording.ShouldLog && recording.Suppressed > 0)
        {
            // like FaultLedger.Log: a repeat after held-back occurrences says how many it stands for (the QA parser accepts the suffix)
            if (hasDiag)
            {
                logger.LogError(ex, "Playerbot {BotId} {What} failed ({Type}: {Message}) ({Suppressed} more since); diag: {Diag}", active.Record.BotId, what, ex.GetType().Name, ex.Message, recording.Suppressed, diag);
            }
            else
            {
                logger.LogError(ex, "Playerbot {BotId} {What} failed ({Type}: {Message}) ({Suppressed} more since)", active.Record.BotId, what, ex.GetType().Name, ex.Message, recording.Suppressed);
            }
        }
        else if (recording.ShouldLog)
        {
            if (hasDiag)
            {
                logger.LogError(ex, "Playerbot {BotId} {What} failed ({Type}: {Message}); diag: {Diag}", active.Record.BotId, what, ex.GetType().Name, ex.Message, diag);
            }
            else
            {
                logger.LogError(ex, "Playerbot {BotId} {What} failed ({Type}: {Message})", active.Record.BotId, what, ex.GetType().Name, ex.Message);
            }
        }
        else
        {
            if (hasDiag)
            {
                logger.LogWarning("Playerbot {BotId} {What} failed ({Type}: {Message}); diag: {Diag}", active.Record.BotId, what, ex.GetType().Name, ex.Message, diag);
            }
            else
            {
                logger.LogWarning("Playerbot {BotId} {What} failed ({Type}: {Message})", active.Record.BotId, what, ex.GetType().Name, ex.Message);
            }
        }

        string thinkDetail = $"bot {active.Record.BotId:D} {what} failed ({ex.GetType().Name}: {ex.Message})";
        if (active.Session.Player is { } faultPlayer)
        {
            active.AnomalySubject = PlayerbotAnomalies.SubjectOf(faultPlayer);
            PlayerbotAnomalies.Report(faultPlayer, "lifecycle", "think-fault", thinkDetail, AnomalySeverity.Warning);
        }
        else
        {
            AnomalyLedger.Shared.Report(
                PlayerbotAnomalies.Subsystem("lifecycle"),
                "think-fault",
                active.AnomalySubject,
                $"{thinkDetail} | map {active.MapId}",
                AnomalySeverity.Warning,
                origin: _world?.Identity);
        }

        Quarantine quarantine = _quarantine.GetOrAdd(active.Record.BotId, static _ => new Quarantine());
        lock (quarantine)
        {
            quarantine.Subject = active.AnomalySubject;
            quarantine.MapId = active.MapId;
        }

        Interlocked.CompareExchange(ref active.FaultCode, $"{what}: {ex.Message}", null);
        active.Session.Kick("playerbot fault: " + what);
    }

    /// <summary>Log and count an exception a fire-and-forget operator command caught (<c>.playerbot</c> subcommands answer the GM with the type only).</summary>
    internal void ReportCommandFault(string command, Exception error)
        => logger.LogFault("playerbot.command", error, "Playerbot command {Command} failed", command);

    /// <summary>
    /// Count one fault of a bot and schedule its retry: whether the bot stays desired, and the error code to store. The
    /// <see cref="PlayerbotOptions.MaxFaults"/>-th fault within the window disables it (no retry).
    /// </summary>
    private (bool Desired, string ErrorCode) RecordFault(Guid botId, string fault, Player? player = null, string? fallbackSubject = null)
    {
        long now = ClockMs;
        Quarantine quarantine = _quarantine.GetOrAdd(botId, static _ => new Quarantine());
        lock (quarantine)
        {
            if (player is not null)
            {
                quarantine.Subject = PlayerbotAnomalies.SubjectOf(player);
                quarantine.MapId = player.Map?.MapId ?? player.MapId;
            }
            else if (!string.IsNullOrEmpty(fallbackSubject))
            {
                quarantine.Subject = fallbackSubject;
            }

            string subject = quarantine.Subject ?? $"bot {botId:D}";
            quarantine.Faults.RemoveAll(at => now - at >= _options.FaultWindowSeconds * 1000L);
            quarantine.Faults.Add(now);
            int count = quarantine.Faults.Count;
            if (count >= _options.MaxFaults)
            {
                _quarantine.TryRemove(botId, out _);
                logger.LogWarning("Playerbot {BotId} disabled after {Count} faults within {Window} s: {Fault}",
                    botId, count, _options.FaultWindowSeconds, fault);
                string disabledDetail = $"bot {botId:D} disabled after {count} faults within {_options.FaultWindowSeconds} s: {fault}";
                if (player is not null)
                {
                    PlayerbotAnomalies.Report(player, "lifecycle", "disabled", disabledDetail, AnomalySeverity.Error);
                }
                else
                {
                    AnomalyLedger.Shared.Report(
                        PlayerbotAnomalies.Subsystem("lifecycle"),
                        "disabled",
                        subject,
                        $"{disabledDetail} | map {quarantine.MapId}",
                        AnomalySeverity.Error,
                        origin: _world?.Identity);
                }
                return (false, Code($"disabled after {count} faults: {fault}"));
            }

            long backoffMs = Math.Min(3_600_000L, (_options.FaultBackoffSeconds * 1000L) << Math.Min(count - 1, 20));
            quarantine.RetryAtMs = now + backoffMs;
            logger.LogWarning("Playerbot {BotId} quarantined for {Seconds} s (fault {Count} of {Max}): {Fault}",
                botId, backoffMs / 1000, count, _options.MaxFaults, fault);
            string quarantineDetail = $"bot {botId:D} quarantined for {backoffMs / 1000} s (fault {count} of {_options.MaxFaults}): {fault}";
            if (player is not null)
            {
                PlayerbotAnomalies.Report(player, "lifecycle", "quarantined", quarantineDetail, AnomalySeverity.Warning);
            }
            else
            {
                AnomalyLedger.Shared.Report(
                    PlayerbotAnomalies.Subsystem("lifecycle"),
                    "quarantined",
                    subject,
                    $"{quarantineDetail} | map {quarantine.MapId}",
                    AnomalySeverity.Warning,
                    origin: _world?.Identity);
            }
            return (true, Code($"quarantined (fault {count}/{_options.MaxFaults}): {fault}"));
        }

    }

    /// <summary>
    /// The stored error code (at most 128 UTF-16 units, the column's width), echoed to chat by '.playerbot' status: control
    /// characters of an exception message (newlines, tabs) become spaces, and the cut never splits a surrogate pair.
    /// </summary>
    internal static string Code(string text)
    {
        const int Max = 128;
        string clean = string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++) span[i] = char.IsControl(source[i]) ? ' ' : source[i];
        });
        if (clean.Length <= Max) return clean;
        int cut = char.IsHighSurrogate(clean[Max - 1]) ? Max - 1 : Max;
        return clean[..cut];
    }

    /// <summary>Log the quarantined bots whose backoff ran out back in (checkpoint thread, outside the operation lock).</summary>
    private async Task RetryQuarantinedAsync()
    {
        long now = ClockMs;
        foreach ((Guid botId, Quarantine quarantine) in _quarantine.ToArray())
        {
            if (_stopping) return;
            lock (quarantine)
            {
                if (quarantine.RetryAtMs > now || _active.ContainsKey(botId)) continue;
                quarantine.RetryAtMs = long.MaxValue; // one retry at a time; a failed start schedules the next
            }

            // Scheduled: from here on an operator stop or start that takes the operation lock first decides (StartCoreAsync re-checks).
            logger.LogInformation("Playerbot {BotId} quarantine retry starting", botId);
            PlayerbotOperationResult result;
            try { result = await StartCoreAsync(botId.ToString(), null, CancellationToken.None, StartKind.QuarantineRetry, quarantine).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.feature", ex);
                logger.LogWarning(ex, "Playerbot {BotId} quarantine retry failed", botId);
                result = new(false, "start-failed", botId);
            }

            if (result.Success)
            {
                logger.LogInformation("Playerbot {BotId} left quarantine", botId);
            }
            else if (result.Code is "playerbot-capacity" or "playerbots-stopping" or "playerbots-disabled" or PlayerbotsDetaching)
            {
                lock (quarantine) quarantine.RetryAtMs = ClockMs + (_options.FaultBackoffSeconds * 1000L);
            }
            else if (result.Code != "start-failed")
            {
                // Gone, refused, cleared by an operator or already running: nothing left to retry. Only this entry is removed;
                // a newer one (a fault after an operator start) keeps its own schedule.
                _quarantine.TryRemove(new KeyValuePair<Guid, Quarantine>(botId, quarantine));
                logger.LogWarning("Playerbot {BotId} quarantine retry ended ({Code})", botId, result.Code);
            }
        }
    }

    private sealed class Quarantine
    {
        public readonly List<long> Faults = [];
        public long RetryAtMs = long.MaxValue;
        public string? Subject;
        public uint MapId;
    }

    private async Task ReconcileProvisioningAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        IManagedPlayerbotProvisionStore provisions = provider.GetRequiredService<IManagedPlayerbotProvisionStore>();
        IReadOnlyList<ManagedPlayerbotProvision> pendingList = await provisions.LoadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pendingList.Count == 0) return;
        ICharacterStore characters = provider.GetRequiredService<ICharacterStore>();
        IReadOnlyList<ManagedPlayerbot> registered = await provider.GetRequiredService<IManagedPlayerbotStore>().LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (ManagedPlayerbotProvision pending in pendingList)
        {
            ManagedPlayerbot? completed = registered.SingleOrDefault(b => b.BotId == pending.BotId && b.AccountId == pending.AccountId && b.AccountName == pending.AccountName);
            if (completed is not null)
            {
                CharacterRecord? character = await characters.GetByIdAsync(completed.CharacterId, cancellationToken).ConfigureAwait(false);
                if (character?.AccountId != pending.AccountId || !await provisions.CompleteAsync(pending.BotId, pending.AccountId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("provision-reconcile-refused");
                continue;
            }
            Account? owner = await provider.GetRequiredService<IAccountStore>().FindByUsernameAsync(pending.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner?.Id != pending.AccountId) throw new InvalidOperationException("provision-owner-refused");
            WorldSession session = await NewSessionAsync(owner, null, provider, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (CharacterRecord character in await characters.GetByAccountAsync(pending.AccountId, cancellationToken).ConfigureAwait(false))
                    if (!await CharacterDeletion.TryDeleteAsync(session, (ulong)(uint)character.Id).ConfigureAwait(false))
                        throw new InvalidOperationException("provision-character-cleanup-refused");
            }
            finally
            {
                session.Kick("playerbot provisioning cleanup");
                await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            if ((await characters.GetByAccountAsync(pending.AccountId, cancellationToken).ConfigureAwait(false)).Count != 0
                || !await provisions.RollbackEmptyOwnerAsync(pending.BotId, pending.AccountId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("provision-rollback-refused");
        }
    }

    private static async Task<ManagedPlayerbot?> ResolveAsync(IServiceProvider services, string idOrName, CancellationToken cancellationToken)
    {
        IManagedPlayerbotStore store = services.GetRequiredService<IManagedPlayerbotStore>();
        if (Guid.TryParse(idOrName, out Guid id)) return await store.FindAsync(id, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ManagedPlayerbot> bots = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        ICharacterStore characters = services.GetRequiredService<ICharacterStore>();
        foreach (ManagedPlayerbot bot in bots)
            if ((await characters.GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false))?.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase) == true) return bot;
        return null;
    }

    private static async Task<ManagedPlayerbot> PersistAsync(IServiceProvider services, ManagedPlayerbot next, CancellationToken cancellationToken)
    {
        ManagedPlayerbot updated = next with { Revision = next.Revision + 1, UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        IManagedPlayerbotStore store = services.GetRequiredService<IManagedPlayerbotStore>();
        if (!await RetryTransientBusyAsync(() => store.UpdateAsync(updated, next.Revision, cancellationToken), cancellationToken).ConfigureAwait(false))
            throw new ManagedRevisionConflictException();
        return updated;
    }

    private static Task RetryTransientBusyAsync(Func<Task> action, CancellationToken cancellationToken)
        => RetryTransientBusyAsync(async () => { await action().ConfigureAwait(false); return true; }, cancellationToken);

    private static async Task<T> RetryTransientBusyAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested && ArcaneCore.Data.Resilience.DatabaseTransience.IsBusy(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (1 << (attempt - 1))), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The store refused a revision-checked write: someone else changed or removed the row since this process read it.</summary>
    private sealed class ManagedRevisionConflictException() : InvalidOperationException("managed-revision-conflict");

    private static Task<ManagedPlayerbot?> FindStoredAsync(IServiceProvider services, Guid botId)
        => services.GetRequiredService<IManagedPlayerbotStore>().FindAsync(botId, CancellationToken.None);

    /// <summary>Logs, once per bot for the life of the process, that another writer owns the bot's row.</summary>
    private void WarnConflictOnce(Guid botId, string during)
    {
        if (_conflictLogged.TryAdd(botId, 0))
            logger.LogWarning("Playerbot {BotId} {During} refused: managed-revision-conflict (another world process owns its registry row?)", botId, during);
    }

    private void PublishStopped(ManagedPlayerbot bot, string name, uint mapId)
    {
        PlayerbotStatus next = new(bot.BotId, name, bot.State, bot.DesiredEnabled, bot.Goal, bot.TargetEntry, bot.QuestId, mapId, 0, bot.ErrorCode,
            Home: StoppedHome(bot.BotId));
        lock (_snapshotGate)
        {
            PlayerbotStatus[] current = Volatile.Read(ref _snapshot);
            Volatile.Write(ref _snapshot, current.Where(s => s.BotId != bot.BotId).Append(next).ToArray());
            _snapshotEpoch++;
        }
    }

    /// <summary>
    /// A running bot as this feature drives it: the core-owned <see cref="ManagedBot"/> (row, scope, session; it survives a detach) and
    /// the brain, party AI and bookkeeping this feature builds around it (rebuilt when the feature attaches again).
    /// </summary>
    private sealed class ActiveBot(ManagedBot entry, PlayerbotBrain brain, PlayerbotPartyAI party)
    {
        public ManagedBot Entry { get; } = entry;

        public ManagedPlayerbot Record
        {
            get => Entry.Record;
            set => Entry.Record = value;
        }

        public AsyncServiceScope Scope => Entry.Scope;
        public WorldSession Session => Entry.Session;
        public PlayerbotBrain Brain = brain;
        public int SavedSetAsideVersion;
        public long SavedRelationVersion;

        /// <summary>The bot's social view of its fights (helpers, the low-health window, the call for help); survives a brain replacement.</summary>
        public BotSocial.PlayerbotFightSocial? Fight;
        public readonly object SetAsideGate = new();
        public PlayerbotPartyAI Party { get; } = party;
        public bool PartyDriven;
        public bool GroupDriven;
        public bool BattlegroundDriven;
        public string Name = entry.Session.Player?.Name ?? string.Empty;
        public string AnomalySubject = entry.Session.Player is { } p ? PlayerbotAnomalies.SubjectOf(p) : $"bot {entry.BotId:D}";
        public uint MapId = entry.Session.Player?.Map?.MapId ?? entry.Session.Player?.MapId ?? 0;
        public uint LastUpdateMs = entry.Session.World.NowMs;
        /// <summary>Whether a think of this bot has run since it logged in or was last unpaused (its gap is a think gap from then on).</summary>
        public bool GapPrimed;
        /// <summary>Whether the current silence of this bot was reported as a starved think gap (cleared by its next think).</summary>
        public bool StarvedReported;
        public volatile bool Paused;
        public IPlayerbotController? Controller;
        public string? FaultCode;
        public int SessionLostReported;
        public int StallsLogged;
        public PlayerbotStatus? Status; // world thread: the last status line (StatusOf)
        private string? _stallSource;
        private string? _stallCode;

        /// <summary>The status code of a stall report (<see cref="Code"/>), computed once per report instead of every tick.</summary>
        public string StallCode(string report)
        {
            if (!ReferenceEquals(report, _stallSource) || _stallCode is null)
            {
                _stallSource = report;
                _stallCode = Code(report);
            }

            return _stallCode;
        }
        public PlayerbotControllerContext ControllerContext { get; } = new(entry.BotId, entry.Session);
    }
}
