using ArcaneCore.Kernel.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// One level band in <see cref="PopulationStatus"/>: <see cref="Want"/> is its weighted share of the population's own target (the
/// effective target less the operator bots running), <see cref="Have"/> the population's own bots running in it. An operator's bot
/// never counts in a band.
/// </summary>
public sealed record PopulationBandStatus(int MinLevel, int MaxLevel, int Weight, int Want, int Have, int? Capacity = null);

/// <summary>What the population manager last saw and did (immutable; replaced after every pass).</summary>
public sealed record PopulationStatus(
    bool Enabled,
    string State,
    string Reason,
    double P95Ms,
    int Samples,
    int BudgetMs,
    int Target,
    int EffectiveTarget,
    int RunningAll,
    int OwnedRunning,
    int OwnedParked,
    int CreatesLastMinute,
    string Boost,
    int AllianceZones,
    int HordeZones,
    IReadOnlyList<PopulationBandStatus> Bands,
    string? LastError,
    long Passes,
    int OperatorRunning = 0,
    int OwnedTarget = 0,
    bool FreshLaunch = false)
{
    public static PopulationStatus Idle(bool enabled, string state, string reason) => new(enabled, state, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        "unknown", 0, 0, [], null, 0);
}

/// <summary>
/// The population manager (<c>World:Playerbots:Population</c>; docs/areas/playbots-population.md): keeps <c>TargetOnline</c> managed bots
/// running, spread over the level bands and over the zones that fit their levels, as long as the world's tick p95 allows.
/// <para>
/// Every <c>IntervalSeconds</c> of world time the world thread captures the facts of every running bot and the tick p95 (a pass), and one
/// task decides and acts: it never waits on the world thread, and the world thread never waits on it. Starts, creations, parks and
/// moves go through <see cref="IPlayerbotService"/> and the world thread; the tick window is reset after any of them, so the next decision
/// sees only ticks measured after the change. Bots it created are recorded in <c>playerbot_population_member</c>; only those are ever
/// parked or relocated, and only those count in a band. A bot an operator created counts towards the total target (it is taken off
/// the top of the population's own share, <see cref="PopulationCounts"/>) and is never stopped here.
/// </para>
/// <para>Thread affinity: <c>Attach</c>, the event handlers and the capture run on the world thread; the pass runs on the thread pool, one at a time.</para>
/// </summary>
public sealed partial class PlayerbotPopulationFeature(IServiceProvider services, ILogger<PlayerbotPopulationFeature> logger) : IDetachableWorldFeature
{
    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private readonly WorldTickWindow _window = new();
    private readonly PopulationProvisioner _provisioner = new(services);
    private readonly Random _random = new();
    private readonly PopulationNames _names = PopulationNames.Create();
    private readonly CancellationTokenSource _stop = new();

    /// <summary>The provisioner the passes use (tests replace its area-level source).</summary>
    internal PopulationProvisioner Provisioner => _provisioner;
    private readonly Queue<long> _createTimes = new();
    private readonly Dictionary<Guid, long> _startedAt = [];
    private readonly Dictionary<Guid, long> _parkedAt = [];
    private readonly HashSet<Guid> _reportedParkedUnexpected = [];
    private readonly PopulationBudgetWindows _windows = new();
    private readonly PopulationActionLog _actionLog = new();
    private WorldRuntime? _world;
    private long _clockMs;
    private long _sinceMs;
    private long _passes;
    private long _passesStarted;

    /// <summary>How many passes were started (every call that reached the start of a pass, even one that then could not capture the world), whether or not they finished (tests).</summary>
    internal long PassesStarted => Interlocked.Read(ref _passesStarted);
    // Pass-thread-only (one pass at a time): placements refused in a row after a bot was created, and the pass the pause began at.
    private int _placementRefusals;
    private long _pausedAtPass;
    // Pass-thread-only: the clock of the last start-home sweep, and (written by a pass, read by the world thread) the clock before which
    // a rush that found every start area full starts no new pass.
    private long _lastHomeSweepMs = long.MinValue / 2;
    private long _rushDeferredUntilMs;
    private readonly Dictionary<Guid, int> _pendingFailures = [];
    private readonly Dictionary<Guid, long> _pendingRetryAt = [];
    // A member row whose Band is this value is pending deletion: its bot was created and could not be placed. The row is written
    // before the bot is stopped and deleted and removed after, so a failed or interrupted deletion is retried by a later pass (on a
    // growing interval). The pass takes the member out of every count and never restarts it. Until the stop completes the bot is
    // still desired-enabled: a restart in that window restores it, and the first pass then stops and deletes it without counting
    // it in any band (a real band index is below the configured band count).
    internal const byte PendingDeleteBand = byte.MaxValue;
    private const int MaxPlacementRefusals = 3;
    private const int PauseLiftsAfterPasses = 10;
    internal const long BandDeficitThresholdMs = 60_000L;
    internal const int BandDeficitMinPasses = 3;
    private readonly Dictionary<int, (long SinceMs, int Passes, int LastHave, bool Reported)> _bandDeficits = [];
    private bool _stopping;
    private Task _pass = Task.CompletedTask;
    private PopulationStatus _status = PopulationStatus.Idle(false, "disabled", "the population manager is off");
    private PopulationRaceClassCache? _pairs;
    private string _pairsKey = string.Empty;
    private PopulationZoneAtlas _atlas = PopulationZoneAtlas.Empty;
    private object? _atlasAreas;
    private object? _atlasGraveyards;
    private object? _atlasCreatures;
    private string _atlasKey = string.Empty;
    private bool _atlasLocated;
    private PopulationLocated? _located;
    private (PopulationZoneAtlas Atlas, PopulationBand[] Bands, PopulationLevelRule Rule, PopulationFactions Factions)? _coverageChecked;
    private object? _locating;
    private bool _locateRunning;
    private int _locateHeldPasses;
    private CancellationTokenSource? _locateCts;
    private PopulationSpawnCapacity? _capacity;
    private string _capacityKey = string.Empty;
    private object? _capacityToken;
    private object? _capacityAreas;
    private CancellationTokenSource? _capacityCts;
    private Task _capacityTask = Task.CompletedTask;
    private readonly PopulationZoneVetoes _vetoes = new();

    /// <summary>The most passes creation and relocation wait for a running graveyard terrain lookup before going ahead on the fallback atlas.</summary>
    internal const int MaxTerrainHoldPasses = 6;
    private string? _lastError;

    private ManagedPlayerbotFeature? Managed => services.GetService<ManagedPlayerbotFeature>();

    private readonly PopulationRush _rush = new();

    /// <summary>The real-time clock of the rush brake (a test replaces it).</summary>
    internal TimeProvider RushClock { get; set; } = TimeProvider.System;

    /// <summary>The rush itself (tests).</summary>
    internal PopulationRush Rush => _rush;

    /// <summary>The rush (<c>.playerbot population rush</c>): how it stands, readable from any thread.</summary>
    public PopulationRushStatus RushStatus => _rush.Snapshot(ClockMs);

    /// <summary>
    /// Start a rush of <paramref name="count"/> new bots at <paramref name="perSecond"/>. Null on success, otherwise why it was refused
    /// (one rush at a time; not during a fleet reset; playerbots on). Any thread.
    /// </summary>
    public string? StartRush(int count, int perSecond)
    {
        if (!_options.Enabled) return "playerbots are off (World:Playerbots:Enabled)";
        if (Managed is null) return "the managed playerbot feature is not available";
        if (Managed.ResetInProgress) return "a fleet reset is running";
        if (Managed.SplitBrain) return "another world process is writing the playerbot rows (split brain; see .playerbot splitbrain)";
        if (_rush.Active) return "a rush is already running; stop it first";
        _rush.Begin(count, perSecond, ClockMs);
        logger.LogInformation("Playerbot population rush started: {Count} new bots at {PerSecond} per second (fresh launch {Fresh}, brake {Brake} ms in {Windows} windows of {Seconds} s)",
            count, perSecond, _options.Population.FreshLaunch, _options.Population.RushCeilingMs(), _options.Population.RushBrakeWindows,
            _options.Population.RushBrakeWindowSeconds);
        return null;
    }

    /// <summary>Stop the rush; the bots it made keep running. False when none was running.</summary>
    public bool StopRush(string reason = "stopped by an operator")
    {
        if (!_rush.Active) return false;
        _rush.RequestStop(reason, ClockMs);
        PopulationRushStatus status = _rush.Snapshot(ClockMs);
        logger.LogInformation("Playerbot population rush stopped ({Reason}): {Created} of {Requested} created", reason, status.Created, status.Requested);
        return true;
    }

    /// <summary>The tick source the control loop reads (a test supplies a fake; null reads the world's tick window).</summary>
    internal IPopulationTickSource? TickSource { get; set; }

    private IPopulationTickSource Source => TickSource ?? _window;

    /// <summary>
    /// Where a graveyard stands (zone, area), called on a background thread for each graveyard of the atlas; a test supplies a fake.
    /// Null reads the terrain files (<see cref="TerrainManager.ReadZoneAndAreaIdDetached"/>) when terrain data is configured.
    /// Set it before the first pass.
    /// </summary>
    internal Func<WorldSafeLoc, (uint ZoneId, uint AreaId)>? TerrainLocator { get; set; }

    /// <summary>
    /// The creature survey level of a graveyard for a team, called with the terrain lookup on a background thread; a test supplies a
    /// fake. Null surveys the creature spawns (<see cref="PopulationCreatureSurvey"/>) when creature content and faction templates are
    /// loaded and <c>CreatureSurveyRadius</c> is above 0. Set it before the first pass.
    /// </summary>
    internal Func<WorldSafeLoc, Team, int>? CreatureSurveyor { get; set; }

    /// <summary>What the background lookup found: where each graveyard stands, and its creature survey level by team.</summary>
    private sealed record PopulationLocated(IReadOnlyDictionary<uint, (uint ZoneId, uint AreaId)> Where,
        IReadOnlyDictionary<(uint LocationId, Team Team), int> Creatures);

    /// <summary>The atlas the last capture used (world thread; tests).</summary>
    internal PopulationZoneAtlas Atlas => _atlas;

    /// <summary>World time this feature has seen, in ms (the creation rate limit runs on it); tests move it forward.</summary>
    internal long ClockMs
    {
        get => Volatile.Read(ref _clockMs);
        set => Volatile.Write(ref _clockMs, value);
    }

    /// <summary>The status the last pass published (any thread).</summary>
    public PopulationStatus Status => Volatile.Read(ref _status);

    /// <summary>The live options (the same object <c>.reload config</c> mutates).</summary>
    internal PlayerbotPopulationOptions Options => _options.Population;

    // --- lifecycle (docs/design/hot-reload-tiers.md, section 4.2) -------------------------------------------------------

    /// <summary>
    /// Handover: the creation rate limit (the creations of the last minute), when each bot was started or parked (the minimum run time
    /// and the parking order) and the feature's clock carry over; a rush in progress is stopped (an operator starts it again), and the
    /// atlas and the retry schedules are rebuilt.
    /// </summary>
    public DetachKind DetachKind => DetachKind.Handover;

    private const string CreateTimesKey = "population.createTimes";
    private const string StartedAtKey = "population.startedAt";
    private const string ParkedAtKey = "population.parkedAt";
    private const string ClockKey = "population.clockMs";
    private FeatureSubscriptions? _subscriptions;
    private volatile bool _quiesced;
    private Task _locateTask = Task.CompletedTask;

    public void Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(nameof(PlayerbotPopulationFeature)));

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (handover is not null)
        {
            if (handover.TryGet<long[]>(CreateTimesKey, out var created))
                foreach (long at in created) _createTimes.Enqueue(at);
            if (handover.TryGet<Dictionary<Guid, long>>(StartedAtKey, out var started))
                foreach ((Guid bot, long at) in started) _startedAt[bot] = at;
            if (handover.TryGet<Dictionary<Guid, long>>(ParkedAtKey, out var parked))
                foreach ((Guid bot, long at) in parked) _parkedAt[bot] = at;
            if (handover.TryGet(ClockKey, out long clock)) ClockMs = clock;
        }

        _world = world;
        _subscriptions = subscriptions;
        subscriptions.OnUpdated(world, OnUpdated);
        subscriptions.Subscribe<Action<long, long, TickPhases>>(OnTickCompleted, h => world.TickCompleted += h, h => world.TickCompleted -= h);
        if (Managed is { } managed)
            subscriptions.Subscribe<Action<IReadOnlyList<Guid>>>(OnHomesClearedByOperator, h => managed.HomesClearedByOperator += h, h => managed.HomesClearedByOperator -= h);
        _quiesced = false;
    }

    /// <summary>Start no further pass, then wait for the pass and the terrain lookup still running (the pass is not cut short).</summary>
    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        _quiesced = true;
        if (_world is not { } world) return;
        // Read on the world thread, which writes them: after this hop no update starts another pass (it sees the flag).
        (Task pass, Task locate) = world.IsStopped || world.IsWorldThread
            ? (_pass, CancelLocating())
            : await world.InvokeAsync(() => (_pass, CancelLocating())).WaitAsync(cancellationToken).ConfigureAwait(false);
        await pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await locate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* the lookup was cancelled */ }
    }

    public void Detach(FeatureHandover handover)
    {
        StopRush("the population feature was detached");
        handover.Set(CreateTimesKey, _createTimes.ToArray());
        handover.Set(StartedAtKey, new Dictionary<Guid, long>(_startedAt));
        handover.Set(ParkedAtKey, new Dictionary<Guid, long>(_parkedAt));
        handover.Set(ClockKey, ClockMs);
        _createTimes.Clear();
        _startedAt.Clear();
        _parkedAt.Clear();
        _pendingFailures.Clear();
        _pendingRetryAt.Clear();
        _bandDeficits.Clear();
        _reportedParkedUnexpected.Clear();
        _ = CancelLocating();
        _atlas = PopulationZoneAtlas.Empty;
        _atlasKey = string.Empty;
        _atlasAreas = _atlasGraveyards = _atlasCreatures = null;
        _atlasLocated = false;
        _located = null;
        _capacity = null;
        _capacityKey = string.Empty;
        _zoneValue = null;
        _zoneValueAreas = null;
        _coverageChecked = null;
        _pairs = null;
        _pairsKey = string.Empty;
        _pass = Task.CompletedTask;
        _subscriptions = null;
        _world = null;
    }

    /// <summary>World thread: stop the terrain lookup and the placement data build in flight (their late results are ignored); the task to wait for.</summary>
    private Task CancelLocating()
    {
        _locating = null;
        _locateRunning = false;
        _locateCts?.Cancel();
        _locateCts?.Dispose();
        _locateCts = null;
        _capacityToken = null;
        _capacityKey = string.Empty; // a cancelled survey restarts on the next capture
        _capacityCts?.Cancel();
        _capacityCts?.Dispose();
        _capacityCts = null;
        CancelZoneValue();
        return Task.WhenAll(_locateTask, _capacityTask, _zoneValueTask, _provisioner.CancelWarm());
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _subscriptions?.Dispose(); // the feature is done: its handlers come off the world (the host never reuses the bag)
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _pass.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { /* a pass cut short by the shutdown */ }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.population", ex);
            AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"shutdown pass error: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
            logger.LogWarning(ex, "Playerbot population pass ended with an error at shutdown");
        }
    }

    // World thread, once per tick: one ring write (WorldTickWindow.Add), no allocation.
    private void OnTickCompleted(long durationMicros, long bytes, TickPhases phases) => _window.Add(durationMicros);

    private void OnUpdated(uint elapsedMs)
    {
        _clockMs += elapsedMs;
        _sinceMs += elapsedMs;
        if (_stopping || _quiesced) return;
        // A fleet reset (.playerbot fleet reset) deletes every bot: the population stands still until it is done.
        if (Managed is { ResetInProgress: true })
        {
            StopRush("a fleet reset started");
            return;
        }

        // Split brain: another world owns the playerbot rows and this world writes none, so the population starts, parks and stores nothing until an operator clears it.
        if (Managed is { SplitBrain: true })
        {
            StopRush("split brain: another world process is writing the playerbot rows");
            return;
        }

        PlayerbotPopulationOptions pop = _options.Population;
        if (_rush.Active && !_options.Enabled) StopRush("playerbots were turned off");
        if (_rush.Active)
        {
            // While a rush runs (or is paused by its brake) the interval pass still runs, but only to shed: it parks owned bots when the
            // tick p95 is over the budget and never adds, parks for the target, or moves anyone. It goes first, so a rush that is busy
            // every update cannot starve it. The brake in RushUpdate cannot stop it either.
            if (_sinceMs >= pop.IntervalSeconds * 1000L && _pass.IsCompleted)
            {
                _sinceMs = 0;
                StartPass(shedOnly: true);
            }

            RushUpdate(elapsedMs, pop);
            return;
        }

        if (!_options.Enabled || !pop.Enabled)
        {
            if (_status.State != "disabled")
                Volatile.Write(ref _status, PopulationStatus.Idle(false, "disabled", _options.Enabled ? "the population manager is off" : "playerbots are off"));
            return;
        }

        if (_sinceMs < pop.IntervalSeconds * 1000L || !_pass.IsCompleted) return;
        _sinceMs = 0;
        StartPass();
    }

    /// <summary>
    /// World thread, every update while a rush is active: read the brake once per fresh window, then (running, previous pass done) turn
    /// the elapsed time into a batch of bots and start a rush pass for them. Nothing is allocated unless a pass starts.
    /// </summary>
    private void RushUpdate(uint elapsedMs, PlayerbotPopulationOptions pop)
    {
        IPopulationTickSource source = Source;
        PopulationRushBrake brake = _rush.Brake;
        int ceiling = pop.RushCeilingMs();
        _rush.SetBrake(ceiling, pop.RushBrakeWindows);
        // Real time, not ticks: a world ticking slowly must trip the brake as soon as a fast one would.
        TimeProvider clock = RushClock;
        long wallMs = (long)(clock.GetTimestamp() * 1000.0 / clock.TimestampFrequency);
        long totalTicks = source.TotalSamples;
        if (brake.WindowDue(wallMs, totalTicks, pop.RushBrakeWindowSeconds * 1000) && source.Samples > 0)
        {
            double p95 = source.P95Ms((int)Math.Min(brake.TicksInWindow(totalTicks), WorldTickWindow.Capacity));
            switch (brake.Observe(wallMs, totalTicks, p95, ceiling, pop))
            {
                case PopulationRushBrakeChange.Paused:
                    _rush.Pause(string.Create(CultureInfo.InvariantCulture,
                        $"paused by the brake: tick p95 {p95:0.0} ms over {ceiling} ms in {pop.RushBrakeWindows} windows of {pop.RushBrakeWindowSeconds} s"));
                    PopulationRushStatus paused = _rush.Snapshot(ClockMs);
                    logger.LogWarning("Playerbot population rush PAUSED: tick p95 {P95:0.0} ms is over the {Ceiling} ms ceiling in {Windows} consecutive {Seconds} s windows; {Created} of {Requested} created",
                        p95, ceiling, pop.RushBrakeWindows, pop.RushBrakeWindowSeconds, paused.Created, paused.Requested);
                    break;
                case PopulationRushBrakeChange.Resumed:
                    _rush.Resume(string.Create(CultureInfo.InvariantCulture, $"resumed: tick p95 {p95:0.0} ms is back under the resume line"));
                    logger.LogInformation("Playerbot population rush RESUMED: tick p95 {P95:0.0} ms", p95);
                    break;
            }
        }

        if (_rush.Paused || _rush.StopRequested) return;
        // Every start area is at its cap: nothing to create until the sweep graduates a bot, which the interval passes do. Wait for
        // one interval instead of loading the registry on every batch.
        if (ClockMs < Volatile.Read(ref _rushDeferredUntilMs)) return;
        _rush.Pacer.Add(elapsedMs, _rush.PerSecond);
        if (!_pass.IsCompleted) return;
        int batch = _rush.Pacer.Take(Math.Min(_rush.Remaining, PopulationRush.MaxPerPass));
        if (batch > 0) StartPass(batch);
    }

    /// <summary>
    /// Run one pass now and wait for it (tests; the world's own timer starts passes the same way). A pass already running is awaited instead.
    /// Unlike the timer's passes it first waits for the placement safety data of the atlas in force to be in (so a test sees the behaviour
    /// of a warm world), unless the test holds the build open through <see cref="PopulationProvisioner.WarmGate"/> to watch the deferral.
    /// </summary>
    internal async Task RunPassNowAsync(bool shedOnly = false)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the population feature is not attached");
        if (_provisioner.WarmGate is null) await WarmNowAsync().ConfigureAwait(false);

        Task pass = await world.InvokeAsync(() => _pass.IsCompleted ? StartPass(shedOnly: shedOnly) : _pass).ConfigureAwait(false);
        await pass.ConfigureAwait(false);
    }

    /// <summary>Tests: build the placement safety data for the atlas in force now and wait until the world thread has it (a pass never waits for it).</summary>
    internal async Task WarmNowAsync()
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the population feature is not attached");
        Task build = await world.InvokeAsync(() =>
        {
            if (_options.Population.FreshLaunch) return Task.CompletedTask;
            _ = _provisioner.Warm(world, AtlasFor(world, _options.Population), _stop.Token);
            return _provisioner.WarmTask;
        }).ConfigureAwait(false);
        await build.ConfigureAwait(false);
        await world.InvokeAsync(() => true).ConfigureAwait(false); // the finished build is posted to the world thread; this runs after it (Post and InvokeAsync share one FIFO queue)
    }

    /// <summary>World thread: capture the facts and start the pass task.</summary>
    private Task StartPass(int rushCount = 0, bool shedOnly = false)
    {
        // The host is stopping its bots: a pass would only see them stop and try to start or create more.
        if (Managed is { IsStopping: true }) return _pass = Task.CompletedTask;
        // Quiescing for a detach, or the managed feature is (its operations would refuse): no new pass.
        if (_quiesced || Managed is { IsDetaching: true }) return _pass = Task.CompletedTask;
        Interlocked.Increment(ref _passesStarted);
        PassInput? input;
        try { input = Capture(rushCount, shedOnly); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.population", ex);
            AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"capture failed: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
            logger.LogWarning(ex, "Playerbot population could not capture the world ({Type}: {Message})", ex.GetType().Name, ex.Message);
            _lastError = "capture: " + ex.Message;
            Publish(_status with { Enabled = true, State = "error", Reason = "capture failed: " + ex.Message, LastError = _lastError });
            return _pass = Task.CompletedTask;
        }

        if (input is null) return _pass = Task.CompletedTask;
        return _pass = Task.Run(async () =>
        {
            try
            {
                await RunAsync(input).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested || _stopping || _world?.IsStopped == true)
            {
                // Shutdown cancellation is expected.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"pass faulted: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
                logger.LogFault("playerbot.population", ex, "Playerbot population pass faulted ({Type}: {Message})", ex.GetType().Name, ex.Message);
            }
        });
    }

    /// <summary>Fresh launch counts the whole population as one pool: one band over every level, so levelling up never opens a gap to refill.</summary>
    private static readonly PopulationBand[] FreshBands = [new() { MinLevel = 1, MaxLevel = PlayerbotPopulationOptions.MaxLevel, Weight = 1 }];

    private void Publish(PopulationStatus status) => Volatile.Write(ref _status, status);

    private sealed record PassInput(
        PlayerbotPopulationOptions Pop,
        PopulationBand[] Bands,
        int Samples,
        long TotalSamples,
        double P95Ms,
        int RunningAll,
        IReadOnlyList<PopulationBotFacts> Running,
        IReadOnlyDictionary<Guid, ManagedPlayerbotState> States,
        PopulationZoneAtlas Atlas,
        bool BoostAvailable,
        string BoostMissing,
        long ClockMs,
        ManagedPlayerbotFeature Managed,
        string? TerrainHold, // why no bot is created or moved this pass: the graveyard lookup or the placement safety data is not in
        bool TeleportAvailable,
        int RushCount = 0,
        bool ShedOnly = false,
        PopulationPlacementFacts? Facts = null, // non-null whenever TerrainHold is null and the launch is not fresh: one snapshot for the whole pass
        PopulationZoneValue? ZoneValue = null); // non-null when ValueRouting is on and the zone content value is built

    private PassInput? Capture(int rushCount = 0, bool shedOnly = false)
    {
        if (_world is not { } world) return null;
        ManagedPlayerbotFeature? managed = Managed;
        if (managed is null)
        {
            Publish(PopulationStatus.Idle(true, "unavailable", "the managed playerbot feature is not available"));
            return null;
        }

        PlayerbotPopulationOptions pop = _options.Population;
        // Fresh launch places nobody (the character-create handler puts a bot at its race start): no atlas, no terrain lookup, no hold.
        PopulationZoneAtlas atlas = pop.FreshLaunch ? _atlas : AtlasFor(world, pop);
        string? terrainHold = pop.FreshLaunch ? null : TerrainHold();
        // The placement safety data is built in the background for the atlas in force. While the unlocated fallback atlas is held back the
        // terrain hold covers it; once the hold gives up after MaxTerrainHoldPasses the facts are built for the fallback too, and rebuilt when
        // the located atlas arrives. Until they are ready the same hold applies, with no pass limit: going ahead without them would build
        // them on the world thread.
        PopulationPlacementFacts? facts = null;
        if (!pop.FreshLaunch && terrainHold is null)
        {
            terrainHold = _provisioner.Warm(world, atlas, _stop.Token);
            if (terrainHold is null) facts = _provisioner.Facts;
        }

        var realZones = new HashSet<uint>();
        foreach (Player online in world.OnlinePlayers)
            if (online.Session is not WorldSession { IsManaged: true }) realZones.Add(online.ZoneId);

        PlayerbotGroupCoordinator? coordinator = services.GetService<PlayerbotGroupCoordinator>();
        TeleportService? teleports = services.GetService<TeleportFeature>()?.Teleports;
        IPopulationPlacementVeto[] vetoes = [.. services.GetServices<IPopulationPlacementVeto>()];
        SpellSystem? spells = services.GetService<SpellFeature>()?.System;
        var states = new Dictionary<Guid, ManagedPlayerbotState>();
        var running = new List<PopulationBotFacts>();
        int runningAll = 0;
        foreach (PlayerbotStatus status in managed.Snapshot())
        {
            states[status.BotId] = status.State;
            if (status.State != ManagedPlayerbotState.Running) continue;
            runningAll++;
            if (managed.FindSession(status.BotId)?.Player is not { } player) continue;
            bool autonomous = !managed.IsPartyDriven(status.BotId) && !managed.IsScripted(status.BotId) && coordinator?.Drives(status.BotId) != true;
            // Busy: cannot be stopped now. A dead bot or a ghost is not busy: it can log out, and it earns nothing until it is back.
            bool busy = !player.IsInWorld || player.Combat.IsInCombat || teleports?.IsBeingTeleported(player) == true;
            bool sick = spells?.GetAuras(player).Any(h => !h.IsRemoved && h.Spell.Id == SpiritHealerResurrection.ResurrectionSicknessSpell) == true;
            PlayerbotBrain? brain = managed.FindBrain(status.BotId);
            bool stalled = brain?.StallReport is not null;
            if (brain?.TakeEscapedZones() is { Length: > 0 } escaped)
                foreach (uint escapedZone in escaped) _vetoes.RecordEscape(status.BotId, escapedZone, NowUnix());
            running.Add(new PopulationBotFacts(status.BotId, player.Level, player.Team, player.ZoneId, autonomous, busy,
                realZones.Contains(player.ZoneId), vetoes.Any(v => v.OwnsPlacement(status.BotId)), !player.IsAlive, sick, stalled,
                (byte)player.Race, (byte)player.Class, player.MapId, player.X, player.Y));
        }

        long nowUnix = NowUnix();
        foreach (PopulationBotFacts trouble in running)
            if (trouble.Stalled || trouble.Dead) _trouble.Record(trouble.ZoneId, trouble.BotId, nowUnix);
        _trouble.Prune(nowUnix);
        IPopulationTickSource source = Source;
        int samples = source.Samples;
        bool boost = _provisioner.BoostAvailable(out string missing);
        return new PassInput(pop, pop.FreshLaunch ? FreshBands : pop.Bands, samples, source.TotalSamples, samples == 0 ? 0 : source.P95Ms(pop.WindowTicks), runningAll, running, states, atlas, boost, missing,
            ClockMs, managed, terrainHold, teleports is not null, rushCount, shedOnly, facts, pop.ValueRouting ? _zoneValue : null);
    }

    /// <summary>
    /// The atlas for the area table and graveyards in force, rebuilt when either is replaced (a content reload), the maps/exclusions
    /// change, or the terrain lookup of the graveyards (<see cref="StartLocating"/>) has come in.
    /// <para>
    /// Where each graveyard really stands comes from the terrain, and the world thread never reads it: a <c>TerrainInfo</c> tile not in
    /// memory is read from disk synchronously, and the clean-up frees unreferenced tiles after a minute, so about 90 graveyard lookups
    /// would be about 90 file reads inside a tick on every build. Until the background lookup for the current key is in, the atlas is
    /// built without it (every link kept, the zone's lowest sub-area as the floor: the behaviour without terrain data).
    /// </para>
    /// </summary>
    private PopulationZoneAtlas AtlasFor(WorldRuntime world, PlayerbotPopulationOptions pop)
    {
        AreaTable areas = WorldMaps.Of(world).Areas;
        GraveyardCatalog graveyards = WorldGraveyards.Of(world).Catalog;
        CreatureContent? creatures = services.GetService<CreatureWorldFeature>()?.Content;
        // The creature definitions version: `.reload creature_template` swaps templates (levels, factions) in the same content.
        string key = string.Join(',', _options.AllowedMaps) + "|" + string.Join(',', pop.ExcludedZones) + "|" + pop.CreatureSurveyRadius
            + "|" + creatures?.DefinitionsVersion;
        bool sourcesChanged = !ReferenceEquals(areas, _atlasAreas) || !ReferenceEquals(graveyards, _atlasGraveyards)
            || !ReferenceEquals(creatures, _atlasCreatures) || key != _atlasKey;
        if (sourcesChanged)
        {
            _atlasAreas = areas;
            _atlasGraveyards = graveyards;
            _atlasCreatures = creatures;
            _atlasKey = key;
            _located = null;
            // A lookup still running for the old sources is cancelled, and ignored if it posts anyway (its token is no longer current),
            // so repeated reloads never stack lookups.
            _locating = null;
            _locateRunning = false;
            _locateCts?.Cancel();
            _locateCts?.Dispose();
            _locateCts = null;
        }

        _atlas.SpawnsPerBot = pop.SpawnsPerBot; // read live: not part of the atlas key, so a change restarts no terrain lookup
        if (!sourcesChanged && (_located is null || _atlasLocated))
        {
            EnsureCapacity(world, areas, pop);
            EnsureZoneValue(world, areas, pop);
            LogCoverage(pop);
            return _atlas;
        }

        PopulationLocated? located = _located;
        _atlas = PopulationZoneAtlas.Build(areas.All, graveyards, _options.AllowedMaps, new HashSet<uint>(pop.ExcludedZones),
            located is null ? null : l => located.Where.GetValueOrDefault(l.Id),
            located is null || located.Creatures.Count == 0 ? null : (l, t) => located.Creatures.GetValueOrDefault((l.Id, t)),
            pop.SpawnsPerBot, _capacity);
        _atlasLocated = located is not null;
        logger.LogInformation(
            "Playerbot population atlas{Pending}: {Zones} zones, {Locations} locations ({Alliance} zones open to the Alliance, {Horde} to the Horde); {Surveyed} graveyard/faction pairs with a creature survey level; {Dropped} graveyard links left out as standing in another zone{List}",
            located is null ? " (graveyard terrain lookup not in yet)" : string.Empty,
            _atlas.Zones.Count, _atlas.LocationCount, _atlas.CountFor(Team.Alliance), _atlas.CountFor(Team.Horde),
            _atlas.Zones.SelectMany(z => z.CreatureLevels.Keys).Distinct().Count(), _atlas.Dropped.Count,
            _atlas.Dropped.Count == 0 ? string.Empty
                : ": " + string.Join(", ", _atlas.Dropped.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.LocationId} (zone {d.ZoneId}, stands in {d.TerrainZoneId})"))));
        if (located is null && _locating is null) StartLocating(world, areas, pop);
        EnsureCapacity(world, areas, pop);
        EnsureZoneValue(world, areas, pop);
        LogCoverage(pop);
        return _atlas;
    }

    /// <summary>
    /// World thread: warn once per (atlas, bands, rule, factions) when a band with a weight has no zone for a faction the population
    /// creates bots for under the level rule in force (<see cref="PopulationZoneAtlas.UncoveredBands"/>): its deficit can never be filled
    /// by a new bot. Quiet while the atlas is the unlocated fallback (it is replaced within a few passes).
    /// </summary>
    private void LogCoverage(PlayerbotPopulationOptions pop)
    {
        if (!_atlasLocated) return;
        PopulationLevelRule rule = PopulationLevelRule.From(pop);
        // Nothing to build when nothing changed (every pass): the atlas and band list are replaced, never mutated; the rule is a record.
        if (_coverageChecked is { } last && ReferenceEquals(last.Atlas, _atlas) && ReferenceEquals(last.Bands, pop.Bands) && last.Rule == rule
            && last.Factions == pop.Factions) return;
        _coverageChecked = (_atlas, pop.Bands, rule, pop.Factions);
        Team[] teams = pop.Factions switch
        {
            PopulationFactions.Alliance => [Team.Alliance],
            PopulationFactions.Horde => [Team.Horde],
            _ => [Team.Alliance, Team.Horde],
        };
        IReadOnlyList<(PopulationBand Band, Team Team)> uncovered = _atlas.UncoveredBands(pop.Bands, teams, rule);
        if (uncovered.Count == 0) return;
        logger.LogWarning(
            "Playerbot population: no zone takes a bot of {Bands} under the level rule (ZoneLevelSlack {Slack}, ZoneLevelSlackMaxFloor {MaxFloor}, CreatureLevelMargin {Margin}); those bands get no new bots",
            string.Join(", ", uncovered.Select(u => string.Create(CultureInfo.InvariantCulture, $"levels {u.Band.MinLevel}-{u.Band.MaxLevel} for the {u.Team}"))),
            pop.ZoneLevelSlack, pop.ZoneLevelSlackMaxFloor, pop.CreatureLevelMargin);
    }

    /// <summary>
    /// World thread: start the background lookup of where the graveyards of the current (unlocated) atlas stand, through
    /// <see cref="TerrainLocator"/> or, by default, <see cref="TerrainManager.ReadZoneAndAreaIdDetached"/> (its own file reads, never
    /// the world thread's tile cache). The result is posted back to the world thread (<see cref="WorldRuntime.Post"/>) and taken by
    /// the next capture; a result for sources that have changed since is dropped. Without a locator and without terrain data nothing
    /// starts and the atlas stays unlocated. A lookup that fails keeps the unlocated atlas until the sources change again (it is not
    /// retried). Each lookup has its own token source, linked to the feature's stop, cancelled when the sources change.
    /// </summary>
    private void StartLocating(WorldRuntime world, AreaTable areas, PlayerbotPopulationOptions pop)
    {
        TerrainManager terrain = WorldMaps.Of(world).Terrain;
        Func<WorldSafeLoc, (uint ZoneId, uint AreaId)>? locate = TerrainLocator
            ?? (terrain.Enabled ? l => terrain.ReadZoneAndAreaIdDetached(l.MapId, l.X, l.Y, areas) : null);
        Func<WorldSafeLoc, Team, int>? survey = CreatureSurveyor ?? Surveyor(world, pop.CreatureSurveyRadius);
        if (locate is null && survey is null) return;
        WorldSafeLoc[] locations = [.. _atlas.Zones.SelectMany(z => z.AllianceLocations.Concat(z.HordeLocations)).DistinctBy(l => l.Id)];
        var token = new object();
        _locating = token;
        _locateRunning = true;
        _locateHeldPasses = 0;
        _locateCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancellationToken ct = _locateCts.Token;
        _locateTask = Task.Run(() =>
        {
            Dictionary<uint, (uint ZoneId, uint AreaId)> where = [];
            Dictionary<(uint LocationId, Team Team), int> creatures = [];
            PopulationLocated? result = null;
            try
            {
                foreach (WorldSafeLoc location in locations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (locate is not null) where[location.Id] = locate(location);
                    if (survey is null) continue;
                    foreach (Team team in SurveyedTeams)
                        if (survey(location, team) is var level and > 0) creatures[(location.Id, team)] = level;
                }

                result = new PopulationLocated(where, creatures);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; } // superseded or stopping: nothing to deliver
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"graveyard terrain lookup or creature survey failed: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
                logger.LogFault("playerbot.population", ex, "Playerbot population: the graveyard terrain lookup or creature survey failed; the atlas keeps every graveyard link until the area table, graveyards, creatures or maps change");
                result = null;
            }

            // Every outcome ends the run on the world thread, so a failed lookup never holds the population back.
            world.Post(() =>
            {
                if (!ReferenceEquals(_locating, token)) return;
                _locateRunning = false;
                if (result is not null) _located = result;
            });
        }, CancellationToken.None);
    }

    private static readonly Team[] SurveyedTeams = [Team.Alliance, Team.Horde];

    /// <summary>Test seam: replaces the production survey (zones, terrain and creature content are then not needed). Set before the first pass.</summary>
    internal Func<CancellationToken, PopulationSpawnCapacity?>? CapacitySurveyor { get; set; }

    /// <summary>Test seam: part of the survey key while <see cref="CapacitySurveyor"/> is set; a new value is a source change.</summary>
    internal int CapacitySurveyorVersion { get; set; }

    /// <summary>The latest capacity survey task (tests).</summary>
    internal Task CapacityTask => _capacityTask;

    /// <summary>World thread: stop the terrain lookup, the capacity survey and the placement data build in flight (tests call it to cancel).</summary>
    internal Task CancelBackgroundWork() => CancelLocating();

    /// <summary>
    /// World thread: start the background survey of the zones' surface creature spawns (<see cref="SurveySurface"/>) when
    /// <c>SpawnsPerBot</c> is above 0 and terrain data, creature content and faction templates are there, and none is running or
    /// done for the current sources (maps, creature definitions, area table). <c>SpawnsPerBot</c> itself is not a source: the divisor is
    /// read live from the atlas. The old capacity stays in force until the new one arrives, unless the area table changed. The result is
    /// posted back and attached to the atlas in force; it never holds creation back. The log gives the survey's duration, hop count and
    /// the vmap model cache before and after.
    /// </summary>
    private void EnsureCapacity(WorldRuntime world, AreaTable areas, PlayerbotPopulationOptions pop)
    {
        CreatureContent? creatures = _atlasCreatures as CreatureContent;
        TerrainManager terrain = WorldMaps.Of(world).Terrain;
        Func<CancellationToken, PopulationSpawnCapacity?>? fake = CapacitySurveyor;
        FactionTemplateCatalog? factions = fake is null ? FactionTemplatesOf(world) : null;
        if (pop.SpawnsPerBot <= 0) return;
        if (fake is null && (creatures is null || !terrain.Enabled || factions is not { Count: > 0 })) return;
        // The area table changed (zone ids may mean something else): the old capacity no longer applies.
        if (!ReferenceEquals(_capacityAreas, areas))
        {
            _capacity = null;
            _atlas.Capacity = null;
            _capacityAreas = areas;
        }

        string key = fake is not null
            ? "fake|" + CapacitySurveyorVersion + "|" + RuntimeHelpers.GetHashCode(areas)
            : string.Join(',', _options.AllowedMaps) + "|" + creatures!.DefinitionsVersion + "|" + RuntimeHelpers.GetHashCode(areas)
                + "|" + RuntimeHelpers.GetHashCode(creatures);
        if (key == _capacityKey) return;
        _capacityKey = key;
        _capacityCts?.Cancel();
        _capacityCts?.Dispose();
        var token = new object();
        _capacityToken = token;
        _capacityCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancellationToken ct = _capacityCts.Token;
        HashSet<uint> gated = [];
        if (services.GetService<GameEventFeature>()?.Service?.Rows is { } rows)
            foreach ((int signedEvent, IReadOnlyList<uint> guids) in rows.Creatures)
                if (signedEvent > 0) gated.UnionWith(guids);
        uint[] maps = [.. _options.AllowedMaps];
        _capacityTask = Task.Factory.StartNew(() =>
        {
            PopulationSpawnCapacity? result = null;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            SurfaceSurveyProbe probe = new();
            try
            {
                if (fake is not null) result = fake(ct);
                else
                {
                    IEnumerable<CreatureSpawn> ordered = maps.SelectMany(m => creatures!.GetSpawns(m))
                        .Select(s => (Spawn: s, Tile: TerrainTile.TileOf(s.X, s.Y) ?? (-1, -1)))
                        .OrderBy(p => p.Spawn.MapId).ThenBy(p => p.Tile.X).ThenBy(p => p.Tile.Y)
                        .Select(p => p.Spawn);
                    result = SurveySurface(world, ordered, terrain, areas, creatures!.FindTemplate, factions!, gated, probe, ct);
                }

                if (result is { Total: 0 }) result = null; // nothing located (no tile data): no capacity rather than a zero cap everywhere
            }
            catch (OperationCanceledException) { return; } // superseded or stopping: nothing to deliver
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"zone capacity survey failed: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
                logger.LogFault("playerbot.population", ex, "Playerbot population: the zone capacity survey failed; no zone is capped until the sources change");
                result = null;
            }

            TimeSpan took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            world.Post(() =>
            {
                if (!ReferenceEquals(_capacityToken, token)) return;
                if (result is not null)
                {
                    _capacity = result;
                    _atlas.Capacity = result;
                    logger.LogInformation(
                        "Playerbot population: zone capacity survey in: {Spawns} surface creature spawn/team pairs counted in {Seconds:0.0} s over {Hops} world-thread hops (vmap models cached {Before} before, {After} after, {Trimmed} dropped); {Caps}",
                        result.Total, took.TotalSeconds, probe.Hops, probe.ModelsBefore, probe.ModelsAfter, probe.Trimmed,
                        string.Join("; ", _atlas.CapacityLines(_options.Population)));
                }
            });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>What the world-thread hops of a survey saw: their count and the vmap model cache around the survey.</summary>
    internal sealed class SurfaceSurveyProbe
    {
        public int Hops { get; set; }

        public int ModelsBefore { get; set; } = -1;

        public int ModelsAfter { get; set; }

        public int Trimmed { get; set; }

        public uint LastMapId { get; set; }
    }

    /// <summary>Hops between two trims of the vmap model cache: a model shared by neighbouring tiles is parsed once per window.</summary>
    internal const int TrimEveryHops = 16;

    /// <summary>
    /// The zone of the spawn a bot can walk to, read from the spawn's terrain tile (any thread): 0 when the spawn stands more than
    /// <see cref="PopulationCreatureSurvey.UndergroundYards"/> under the ground, or the tile's area flag is 0 or has no area row.
    /// </summary>
    internal static uint SurfaceZoneOf(CreatureSpawn spawn, TerrainTile tile, AreaTable areas)
    {
        float ground = tile.GetHeight(spawn.X, spawn.Y);
        if (PlayerbotMotion.ValidHeight(ground) && spawn.Z < ground - PopulationCreatureSurvey.UndergroundYards) return 0;
        ushort flag = tile.GetAreaFlag(spawn.X, spawn.Y);
        return flag == 0 ? 0 : areas.GetZoneAndAreaId(flag, spawn.MapId).ZoneId;
    }

    /// <summary>
    /// The survey proper, on the calling (background) thread: zones and ground heights from the detached tile reads, spawns ordered by
    /// tile so one tile is held at a time; only the indoor test hops to the world thread (<see cref="IndoorSurface(WorldRuntime, IReadOnlyList{CreatureSpawn}, bool, SurfaceSurveyProbe)"/>),
    /// once per batch lying in one tile. The vmap tiles and models the survey brings in are released again (see there).
    /// </summary>
    internal static PopulationSpawnCapacity SurveySurface(WorldRuntime world, IEnumerable<CreatureSpawn> ordered, TerrainManager terrain, AreaTable areas,
        Func<uint, CreatureTemplate?> templateOf, FactionTemplateCatalog factions, IReadOnlySet<uint> gated, SurfaceSurveyProbe probe, CancellationToken ct)
    {
        (uint Map, int X, int Y) loaded = (uint.MaxValue, 0, 0);
        TerrainTile tile = TerrainTile.Empty;
        try
        {
            return PopulationCreatureSurvey.CountSurface(ordered,
                spawn =>
                {
                    if (TerrainTile.TileOf(spawn.X, spawn.Y) is not { } at) return 0;
                    if (loaded != (spawn.MapId, at.X, at.Y))
                    {
                        tile = terrain.ReadTileDetached(spawn.MapId, at.X, at.Y);
                        loaded = (spawn.MapId, at.X, at.Y);
                    }

                    return SurfaceZoneOf(spawn, tile, areas);
                },
                batch =>
                {
                    int hop = ++probe.Hops;
                    return world.InvokeAsync(() => IndoorSurface(world, batch, hop % TrimEveryHops == 0, probe), ct).GetAwaiter().GetResult();
                },
                templateOf, factions, gated, ct);
        }
        finally
        {
            // The last window's models go too, also when the survey was cancelled or failed (bounded wait: the world may be stopping).
            if (probe.Hops > 0)
            {
                try { world.InvokeAsync(() => IndoorSurface(world, [], true, probe)).Wait(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is AggregateException or OperationCanceledException) { }
            }
        }
    }

    /// <summary>
    /// World thread: <see cref="IndoorSurface(IReadOnlyList{CreatureSpawn}, Action{float, float}, Func{float, float, float, bool})"/> on the batch's map. The
    /// tile's vmap is loaded on demand (the line-of-sight service only; never the navigation mesh tile, which would evict the mesh cache
    /// the bots' routes use). A tile nobody held before is released again after the batch, and every <see cref="TrimEveryHops"/>th
    /// hop (<paramref name="trim"/>) the cached models no loaded tile uses are dropped (<see cref="VMapManager.TrimUnusedModels"/>),
    /// so the survey does not leave its models behind: the model cache otherwise only grows. The trim drops every unused model server-wide, so it may drop more than the survey brought in, and a grid tile that reloads later parses such a model again on the world thread. An empty batch only trims.
    /// </summary>
    internal static bool[] IndoorSurface(WorldRuntime world, IReadOnlyList<CreatureSpawn> batch, bool trim, SurfaceSurveyProbe probe)
    {
        uint mapId = batch.Count > 0 ? batch[0].MapId : probe.LastMapId;
        Map map = world.FindMap(mapId) ?? world.GetMap(mapId);
        probe.LastMapId = mapId;
        VMapManager? vmaps = map.Collision.LineOfSight as VMapManager;
        if (vmaps is not null && probe.ModelsBefore < 0) probe.ModelsBefore = vmaps.CachedModelCount;
        bool[] answers = [];
        if (batch.Count > 0)
        {
            bool held = vmaps?.HoldsTile(mapId, batch[0].X, batch[0].Y) ?? true;
            try
            {
                answers = IndoorSurface(batch, (x, y) => (map.Collision.LineOfSight as ICollisionTileDemand)?.LoadTileOnDemand(mapId, x, y),
                    (x, y, z) => map.Collision.IsOutdoors(x, y, z));
            }
            finally
            {
                if (!held) vmaps!.ReleaseOnDemandTile(mapId, batch[0].X, batch[0].Y);
            }
        }

        if (vmaps is not null)
        {
            if (trim) probe.Trimmed += vmaps.TrimUnusedModels();
            probe.ModelsAfter = vmaps.CachedModelCount;
        }

        return answers;
    }

    /// <summary>
    /// For each spawn of a batch lying in one tile, whether it is outdoors (not inside an indoor WMO group): the tile is loaded first
    /// (<paramref name="loadTile"/>, once), so an unloaded tile does not read as outdoors, then each spawn is asked (<paramref name="isOutdoors"/>
    /// with x, y and z + 1). The under-terrain test was made off the world thread.
    /// </summary>
    internal static bool[] IndoorSurface(IReadOnlyList<CreatureSpawn> batch, Action<float, float> loadTile, Func<float, float, float, bool> isOutdoors)
    {
        var answers = new bool[batch.Count];
        if (batch.Count == 0) return answers;
        loadTile(batch[0].X, batch[0].Y);
        for (int i = 0; i < batch.Count; i++)
            answers[i] = isOutdoors(batch[i].X, batch[i].Y, batch[i].Z + 1f);
        return answers;
    }

    /// <summary>
    /// World thread: the creature survey for the graveyard lookup, over the creature spawns of the content in force (immutable lists;
    /// templates read through <see cref="CreatureContent.FindTemplate"/>, safe from any thread), the faction templates combat uses, and
    /// without the spawns of positive game events (absent while no event runs). Null when the radius is 0 or either is missing.
    /// </summary>
    private Func<WorldSafeLoc, Team, int>? Surveyor(WorldRuntime world, int radius)
    {
        if (radius <= 0 || services.GetService<CreatureWorldFeature>()?.Content is not { } creatures) return null;
        if (FactionTemplatesOf(world) is not { Count: > 0 } factions)
        {
            logger.LogInformation("Playerbot population: no faction templates loaded; graveyards are placed by area level only (no creature survey)");
            return null;
        }

        HashSet<uint> gated = [];
        if (services.GetService<GameEventFeature>()?.Service?.Rows is { } rows)
            foreach ((int signedEvent, IReadOnlyList<uint> guids) in rows.Creatures)
                if (signedEvent > 0) gated.UnionWith(guids);
        return (location, team) => PopulationCreatureSurvey.LevelNear(location, team, radius, creatures.GetSpawns(location.MapId),
            creatures.FindTemplate, factions, gated);
    }

    /// <summary>The faction templates of the world's combat hooks (unwrapping the reputation hooks), or a registered catalog.</summary>
    private FactionTemplateCatalog? FactionTemplatesOf(WorldRuntime world)
    {
        if (services.GetService<FactionTemplateCatalog>() is { Count: > 0 } registered) return registered;
        CombatHooks hooks = CombatHooks.For(world);
        while (hooks is ReputationCombatHooks reputation) hooks = reputation.Fallback;
        return (hooks as FactionCombatHooks)?.Factions;
    }

    /// <summary>
    /// World thread, once per capture: why this pass must not create or relocate bots, or null. While the graveyard terrain lookup for
    /// the current sources runs (started, its result not yet delivered), the atlas is the unlocated fallback, which would place a low
    /// bot at Raven Hill again: creation and relocation wait. After <see cref="MaxTerrainHoldPasses"/> such passes they go ahead on the
    /// fallback (a warning is logged once), so a hung read cannot stall the population.
    /// </summary>
    private string? TerrainHold()
    {
        if (!_locateRunning) return null;
        _locateHeldPasses++;
        if (_locateHeldPasses <= MaxTerrainHoldPasses) return "waiting for the graveyard terrain lookup";
        if (_locateHeldPasses == MaxTerrainHoldPasses + 1)
            logger.LogWarning("Playerbot population: the graveyard terrain lookup is still running after {Passes} passes; creating and relocating on the fallback atlas (every graveyard link kept)", MaxTerrainHoldPasses);
        return null;
    }

    /// <summary>
    /// The valid (race, class) pairs for the configured classes. The store is resolved from the pass scope (it is a scoped service over a
    /// DbContext) and the query is awaited inside it; only the resulting value is kept, so no scoped service outlives its pass.
    /// </summary>
    private async Task<PopulationRaceClassCache> PairsFor(IServiceProvider scoped, PlayerbotPopulationOptions pop, CancellationToken ct)
    {
        string key = PopulationRaceClassCache.KeyOf(pop.Classes);
        if (_pairs is not null && _pairsKey == key) return _pairs;
        IWorldDataStore store = scoped.GetRequiredService<IWorldDataStore>();
        PopulationRaceClassCache built = await PopulationRaceClassCache.BuildAsync(store, [.. pop.Classes], ct).ConfigureAwait(false);
        _pairs = built;
        _pairsKey = key;
        return built;
    }

    private async Task RunAsync(PassInput input)
    {
        CancellationToken ct = _stop.Token;
        PlayerbotPopulationOptions pop = input.Pop;
        int effectiveTarget = PopulationBudgetController.EffectiveTarget(pop, _options.MaxBots);
        // Filled once the membership is read: a band counts only the population's own bots (PopulationCounts), which leaves out
        // the pending-deletion members.
        int[] desired = new int[input.Bands.Length];
        int[] have = new int[input.Bands.Length];
        int operatorRunning = 0, ownedTarget = 0;
        int windows = _windows.Observe(input.Samples, input.TotalSamples, input.P95Ms, pop);

        PopulationDecision decision = new(PopulationAction.Hold, 0, "error", "the pass did not complete");
        int ownedRunning = 0, ownedParked = 0;
        string actions = string.Empty;
        var pass = new PopulationPassActions();
        try
        {
            await using AsyncServiceScope scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            IPlayerbotPopulationStore store = new SplitBrainGuardedStore(scope.ServiceProvider.GetRequiredService<IPlayerbotPopulationStore>(), input.Managed);
            IReadOnlyList<ManagedPlayerbot> registered = await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
                .LoadAllAsync(ct).ConfigureAwait(false);
            var known = registered.ToDictionary(b => b.BotId);
            var members = new Dictionary<Guid, PlayerbotPopulationMember>();
            foreach (PlayerbotPopulationMember member in await store.LoadAllAsync(ct).ConfigureAwait(false))
            {
                // The bot or its character is gone (the character delete cleanup removes the row; a registry loss leaves it behind).
                if (known.TryGetValue(member.BotId, out ManagedPlayerbot? bot) && bot.CharacterId == member.CharacterId) members[member.BotId] = member;
                else await store.DeleteAsync(member.BotId, ct).ConfigureAwait(false);
            }

            // Unplaced bots whose deletion has not finished are retried first; they are not members for any other purpose (not in
            // PopulationCounts, so not in any band either, even when a restart restored the bot and it is running for a moment).
            var pendingIds = new HashSet<Guid>();
            foreach (PlayerbotPopulationMember pending in members.Values.Where(m => m.Band == PendingDeleteBand).ToArray())
            {
                members.Remove(pending.BotId);
                pendingIds.Add(pending.BotId);
                // A permanently refused deletion is retried on a growing interval (1, 2 ... 8 passes), not every pass.
                if (_pendingRetryAt.TryGetValue(pending.BotId, out long retryAt) && Volatile.Read(ref _passes) < retryAt) continue;
                await TryDeletePendingAsync(input.Managed, store, pending.BotId, null, ct).ConfigureAwait(false);
            }

            // Backoff state only for the pending rows that still exist (a character delete cleanup can remove one another way).
            foreach (Guid stale in _pendingRetryAt.Keys.Where(id => !pendingIds.Contains(id)).ToArray()) _pendingRetryAt.Remove(stale);
            foreach (Guid stale in _pendingFailures.Keys.Where(id => !pendingIds.Contains(id)).ToArray()) _pendingFailures.Remove(stale);

            // A member that is gone no longer needs its start and park times.
            foreach (Guid gone in _startedAt.Keys.Where(id => !members.ContainsKey(id)).ToArray()) _startedAt.Remove(gone);
            foreach (Guid gone in _parkedAt.Keys.Where(id => !members.ContainsKey(id)).ToArray()) _parkedAt.Remove(gone);
            _reportedParkedUnexpected.RemoveWhere(id => !members.ContainsKey(id));

            if (!_stopping && !input.Managed.ResetInProgress && !input.Managed.IsStopping)
            {
                foreach (PlayerbotPopulationMember member in members.Values)
                {
                    bool isRunning = input.States.TryGetValue(member.BotId, out ManagedPlayerbotState memberState) && memberState == ManagedPlayerbotState.Running;
                    if (isRunning)
                    {
                        _reportedParkedUnexpected.Remove(member.BotId);
                        continue;
                    }

                    // Report-only: _startedAt and _parkedAt (and so PopulationParking.RestartOrder) are left exactly as they are.
                    if (!_startedAt.ContainsKey(member.BotId) || _parkedAt.ContainsKey(member.BotId)) continue;
                    if (_reportedParkedUnexpected.Add(member.BotId))
                    {
                        string botLabel = known.TryGetValue(member.BotId, out ManagedPlayerbot? knownBot)
                            ? $"bot {knownBot.AccountName} (id {member.BotId:D}, char {member.CharacterId})"
                            : $"bot {member.BotId:D} (char {member.CharacterId})";
                        AnomalyLedger.Shared.Report(
                            PlayerbotAnomalies.Prefix + "population",
                            "parked-unexpected",
                            botLabel,
                            $"population-owned bot transitioned to {memberState} outside population park pass (zone={member.ZoneId}, band={member.Band})",
                            AnomalySeverity.Warning,
                            origin: _world?.Identity);
                    }
                }
            }

            ownedRunning = input.Running.Count(f => members.ContainsKey(f.BotId));
            ownedParked = members.Keys.Count(id => !input.States.TryGetValue(id, out ManagedPlayerbotState s) || s != ManagedPlayerbotState.Running);
            // A pending bot a restart brought back is the population's own, not an operator's: it must not lower the owned target.
            int pendingRunning = pendingIds.Count(id => input.States.TryGetValue(id, out ManagedPlayerbotState s) && s == ManagedPlayerbotState.Running);
            PopulationCounts counts = PopulationCounts.Of(input.Bands, effectiveTarget, input.RunningAll - pendingRunning,
                members.Keys.Count(id => input.States.TryGetValue(id, out ManagedPlayerbotState s) && s == ManagedPlayerbotState.Running),
                input.Running.Where(f => members.ContainsKey(f.BotId)).Select(f => f.Level));
            (desired, have, operatorRunning, ownedTarget) = (counts.Desired, counts.Have, counts.OperatorRunning, counts.OwnedTarget);
            if (input.RushCount > 0)
            {
                // A rush bypasses the budget loop and its confirmation windows (its own brake watches the tick p95), and plans against
                // the bots it is about to add: the bands are filled as if the target were what the population owns now plus the batch.
                if (!pop.FreshLaunch) desired = PopulationBandPlanner.DesiredPerBand(ownedRunning + ownedParked + input.RushCount, input.Bands);
                decision = new PopulationDecision(PopulationAction.Add, input.RushCount, "rush", $"rush batch of {input.RushCount}");
            }
            else
            {
                // Shed-only (a rush is running): the budget may stop bots, nothing else. A rush deliberately exceeds the target, so the
                // target is taken as unlimited (it would otherwise answer "over-target" before the budget is looked at).
                decision = PopulationBudgetController.Decide(input.Samples, input.P95Ms, input.RunningAll, ownedRunning,
                    input.ShedOnly ? int.MaxValue : effectiveTarget, pop, windows);
                if (input.ShedOnly && decision.Action == PopulationAction.Add)
                    decision = new PopulationDecision(PopulationAction.Hold, 0, "rush-running", "a rush is running: only parks for the tick budget run");
            }

            var ctx = new PassContext(input, scope.ServiceProvider, store, known, members, desired, have, effectiveTarget);
            int changed = 0;
            int homeChanges = 0;
            // Graduation: a start home follows its bot's level. Not in a rush batch pass (the interval passes that run beside a rush do it).
            if (input.RushCount == 0 && pop.FreshLaunch && pop.FreshStartHome && input.Managed.HomesAvailable)
            {
                homeChanges = await SweepStartHomesAsync(ctx, ct).ConfigureAwait(false);
            }

            switch (decision.Action)
            {
                case PopulationAction.Add:
                    (int started, int created) = await AddAsync(ctx, decision.Count, ct).ConfigureAwait(false);
                    changed += started + created;
                    pass.Started = started;
                    pass.Created = created;
                    actions = $"started {started}, created {created}";
                    if (input.RushCount > 0)
                    {
                        if (_rush.Report(decision.Count, created, ctx.Note, _lastError, ClockMs))
                        {
                            PopulationRushStatus ended = _rush.Snapshot(ClockMs);
                            logger.LogInformation("Playerbot population rush ended ({State}): {Created} of {Requested} created, {Failed} failed, {Reason}",
                                ended.State, ended.Created, ended.Requested, ended.Failed, ended.Reason);
                        }
                    }

                    break;
                case PopulationAction.Park:
                    // Over the target the operator's limit is honoured, recent starts included (last); for the budget they are kept.
                    IReadOnlyList<string> reasons = await ParkAsync(ctx, decision.Count, keepRecent: decision.State != "over-target", ct).ConfigureAwait(false);
                    changed += reasons.Count;
                    pass.Parked.AddRange(reasons);
                    actions = $"parked {reasons.Count}" + (reasons.Count == 0 ? string.Empty : " (" + PopulationPassActions.Tally(reasons) + ")");
                    break;
            }

            bool relocate = decision.State is "under-budget" or "under-budget-confirming" or "at-target" or "hysteresis" && pop.Relocation == PopulationRelocation.Teleport
                && pop.MaxRelocationsPerInterval > 0 && !pop.FreshLaunch && !input.ShedOnly; // a fresh-launch bot is never moved out of its starting zone
            if (relocate && input.TerrainHold is { } relocationHold) ctx.Note ??= relocationHold; // no relocation on the fallback atlas either
            else if (relocate)
            {
                int moved = await RelocateAsync(ctx, ct).ConfigureAwait(false);
                pass.Moved = moved; // not counted in changed: a move leaves the count of running bots, and so the tick load the windows measure, alone
                if (moved > 0) actions += (actions.Length == 0 ? string.Empty : ", ") + $"moved {moved}";
            }

            if (homeChanges > 0) actions += (actions.Length == 0 ? string.Empty : ", ") + $"start homes {homeChanges}";
            if (ctx.Note is not null) actions += (actions.Length == 0 ? string.Empty : ", ") + "held back: " + ctx.Note;
            if (input.RushCount > 0 && ctx.Note is not null && ctx.Note.StartsWith(PopulationRush.StartZonesFullNote, StringComparison.Ordinal))
                Volatile.Write(ref _rushDeferredUntilMs, ClockMs + (pop.IntervalSeconds * 1000L));
            // A rush keeps the tick window: its brake reads the load the rush itself causes.
            if (changed > 0 && input.RushCount == 0 && _world is { } world)
            {
                await world.InvokeAsync(() => { Source.Reset(); return true; }, ct).ConfigureAwait(false);
                _windows.Reset();
            }

            ownedParked = members.Keys.Count(id => !ctx.RunningNow.Contains(id));
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested || _stopping || _world?.IsStopped == true)
        {
            logger.LogInformation("Playerbot population pass canceled during shutdown ({Type})", ex.GetType().Name);
            return;
        }
        catch (ManagedPlayerbotFeature.PlayerbotSplitBrainException)
        {
            // Split brain was latched while the pass ran: it writes nothing more and ends quietly (no fault, no warning, no status of its own).
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"pass failed: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
            logger.LogFault("playerbot.population", ex, "Playerbot population pass failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            decision = decision with { State = "error", Reason = "the pass failed: " + ex.Message };
            if (input.RushCount > 0) _rush.Report(input.RushCount, 0, null, _lastError, ClockMs);
        }

        long now = input.ClockMs;
        while (_createTimes.Count > 0 && now - _createTimes.Peek() >= 60_000) _createTimes.Dequeue();
        if (!pop.FreshLaunch && !input.ShedOnly && input.RushCount == 0 && pop.TargetOnline > 0)
        {
            ObserveBandDeficits(input.Bands, desired, have, now, pop, ctxNote: decision.Reason);
        }

        PopulationBandStatus[] bands = [.. input.Bands.Select((b, i) => new PopulationBandStatus(b.MinLevel, b.MaxLevel, b.Weight, desired[i], have[i],
            input.Atlas.BandCapacity(b, pop.Factions switch { PopulationFactions.Alliance => [Team.Alliance], PopulationFactions.Horde => [Team.Horde], _ => [Team.Alliance, Team.Horde] }, PopulationLevelRule.From(pop))))];
        _actionLog.Add(pass);
        if (_actionLog.Due(now, pop.ActionLogSeconds) is { } line)
            logger.LogInformation(
                "Playerbot population: {Actions}; state {State}: {Reason}; operator bots running {Operator}, population share {OwnedTarget}; bands {Bands}",
                line, decision.State, decision.Reason, operatorRunning, ownedTarget,
                string.Join(", ", bands.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.MinLevel}-{b.MaxLevel} want {b.Want} have {b.Have}"))));
        Publish(new PopulationStatus(true, decision.State, actions.Length == 0 ? decision.Reason : decision.Reason + "; " + actions,
            input.P95Ms, input.Samples, pop.TickP95BudgetMs, pop.TargetOnline, effectiveTarget, input.RunningAll, ownedRunning, ownedParked,
            _createTimes.Count, input.BoostAvailable ? "available" : "missing: " + input.BoostMissing,
            input.Atlas.CountFor(Team.Alliance), input.Atlas.CountFor(Team.Horde), bands,
            _lastError, Interlocked.Increment(ref _passes), operatorRunning, ownedTarget, pop.FreshLaunch));
    }

    private void ObserveBandDeficits(PopulationBand[] bands, int[] desired, int[] have, long nowMs, PlayerbotPopulationOptions pop, string? ctxNote)
    {
        long thresholdMs = Math.Max(BandDeficitThresholdMs, pop.IntervalSeconds * 3000L);
        for (int i = 0; i < bands.Length && i < desired.Length && i < have.Length; i++)
        {
            int want = desired[i];
            int current = have[i];
            if (want <= 0 || current >= want)
            {
                _bandDeficits.Remove(i);
                continue;
            }

            if (!_bandDeficits.TryGetValue(i, out var state) || current > state.LastHave)
            {
                _bandDeficits[i] = (nowMs, 1, current, false);
                continue;
            }

            int passes = state.Passes + 1;
            bool reported = state.Reported;
            long elapsedMs = Math.Max(0L, nowMs - state.SinceMs);
            if (!reported && passes >= BandDeficitMinPasses && elapsedMs >= thresholdMs)
            {
                reported = true;
                PopulationBand band = bands[i];
                AnomalyLedger.Shared.Report(
                    PlayerbotAnomalies.Prefix + "population",
                    "band-deficit",
                    $"band {band.MinLevel}-{band.MaxLevel}",
                    $"band {band.MinLevel}-{band.MaxLevel} below target for {elapsedMs}ms across {passes} passes (want={want}, have={current}, note={ctxNote ?? "none"})",
                    AnomalySeverity.Info,
                    origin: _world?.Identity);
            }

            _bandDeficits[i] = (state.SinceMs, passes, current, reported);
        }
    }

    /// <summary>What one pass works on: the captured facts, the stores, the membership read at its start, and the band counts it keeps current.</summary>
    private sealed class PassContext(PassInput input, IServiceProvider scoped, IPlayerbotPopulationStore store,
        IReadOnlyDictionary<Guid, ManagedPlayerbot> known, Dictionary<Guid, PlayerbotPopulationMember> members, int[] desired, int[] have, int target)
    {
        public PassInput Input { get; } = input;
        public IServiceProvider Scoped { get; } = scoped;
        public IPlayerbotPopulationStore Store { get; } = store;
        public IReadOnlyDictionary<Guid, ManagedPlayerbot> Known { get; } = known;
        public Dictionary<Guid, PlayerbotPopulationMember> Members { get; } = members;
        public int[] Desired { get; } = desired;
        public int[] Have { get; } = have;
        public int Target { get; } = target;
        public HashSet<Guid> RunningNow { get; } = [.. input.Running.Select(f => f.BotId)];
        public Dictionary<uint, int> Assigned { get; } = input.Running.GroupBy(f => f.ZoneId).ToDictionary(g => g.Key, g => g.Count());
        public int Alliance { get; set; } = input.Running.Count(f => f.Team == Team.Alliance);
        public int Horde { get; set; } = input.Running.Count(f => f.Team == Team.Horde);
        public string? Note { get; set; }

        /// <summary>Bots this pass has started again, and created (the running cap and the registry cap count them).</summary>
        public int Started { get; set; }

        public int Created { get; set; }
    }

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Fail(string error)
    {
        // The managed feature stops first at shutdown and refuses every start and creation with playerbots-disabled: that is the shutdown, not a failure.
        // Only those refusals are muted; any other fault during shutdown (registry, start home, delete) is still recorded and logged.
        if (Managed is { IsStopping: true } && (error.EndsWith(": playerbots-disabled", StringComparison.Ordinal) || error.EndsWith(": playerbots-stopping", StringComparison.Ordinal)
            || error.EndsWith(": playerbot-stopping", StringComparison.Ordinal))) return;
        _lastError = error;
        logger.LogWarning("Playerbot population: {Error}", error);
    }

    private async Task<(int Started, int Created)> AddAsync(PassContext ctx, int count, CancellationToken ct)
    {
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        if (managed.IsStopping) return (0, 0);
        int remaining = count, started = 0, created = 0;
        string? stoppedBecause = null;

        // The population's own stopped bots, in rotation: the one parked longest ago first, whatever its band (PopulationParking.RestartOrder).
        // A faulted bot (quarantined or disabled) is not started here: an operator start would clear its fault history, and the manager
        // retries a quarantined bot itself. The startable, not-running members are ordered first and only then capped, so a registry of
        // more than 128 owned bots never hides a parked bot behind running or faulted ones.
        ICharacterStore characters = ctx.Scoped.GetRequiredService<ICharacterStore>();
        var restart = new List<(PlayerbotPopulationMember Member, int Band)>();
        // A rush makes new bots only; the parked ones come back through the ordinary passes.
        foreach (PlayerbotPopulationMember member in PopulationParking.RestartOrder(
            ctx.Members.Values.Where(m => ctx.Input.RushCount == 0 && !ctx.RunningNow.Contains(m.BotId) && IsStartable(ctx, m)), _parkedAt).Take(128))
        {
            CharacterRecord? character = await characters.GetByIdAsync(member.CharacterId, ct).ConfigureAwait(false);
            if (character is null) continue;
            restart.Add((member, PopulationBandPlanner.BandOf(ctx.Input.Bands, character.Level)));
        }

        // New bots are planned against the population's own bots running or waiting to be restarted (a parked bot fills its band once it
        // is back), so only a band its parked bots cannot fill gets new ones. While parked bots wait as well, such a band gets at most
        // half the pass's slots first, the rotation the rest, and creation any slots the rotation leaves: neither starves the other.
        int[] planned = (int[])ctx.Have.Clone();
        foreach ((_, int band) in restart)
            if (band >= 0) planned[band]++;

        bool creationFailed = false;
        if (restart.Count > 0 && PopulationBandPlanner.NextBand(ctx.Desired, planned) >= 0)
        {
            int first = (remaining + 1) / 2;
            (int made, string? because, bool failed) = await CreateAsync(ctx, planned, first, quietWhenNothingFits: true, ct).ConfigureAwait(false);
            created += made;
            remaining -= made;
            stoppedBecause = because;
            creationFailed = failed;
        }

        int failures = 0;
        bool capacity = false;
        foreach ((PlayerbotPopulationMember member, int band) in restart)
        {
            if (remaining == 0 || failures >= 3) break;
            PlayerbotOperationResult result = Checked(await managed.StartAsync(member.BotId.ToString(), ct).ConfigureAwait(false));
            if (result.Success)
            {
                started++;
                ctx.Started++;
                remaining--;
                ctx.RunningNow.Add(member.BotId);
                _startedAt[member.BotId] = ctx.Input.ClockMs;
                _parkedAt.Remove(member.BotId);
                if (band >= 0) ctx.Have[band]++;
            }
            else if (result.Code == "playerbot-capacity") { capacity = true; stoppedBecause = "the running bot cap (MaxBots) is reached"; break; }
            else
            {
                failures++;
                Fail($"start of {member.BotId} refused: {result.Code}");
                ReportLoginFailed(ctx.Known, member.BotId, member.CharacterId, null, result, "restart");
            }
        }

        if (remaining > 0 && stoppedBecause is null && !capacity && !creationFailed)
        {
            if (ctx.Input.TerrainHold is { } hold) stoppedBecause = hold;
            else
            {
                (int made, string? because, _) = await CreateAsync(ctx, planned, remaining, quietWhenNothingFits: false, ct).ConfigureAwait(false);
                created += made;
                stoppedBecause = because;
            }
        }

        ctx.Note = stoppedBecause;
        return (started, created);
    }

    private void ReportLoginFailed(IReadOnlyDictionary<Guid, ManagedPlayerbot> known, Guid botId, int characterId, string? fallbackName,
        PlayerbotOperationResult result, string stage)
    {
        if (result.Code is "playerbot-capacity" or "playerbot-stopping" or "playerbots-disabled" or "playerbots-reset")
        {
            return;
        }

        string name = fallbackName ?? (known.TryGetValue(botId, out ManagedPlayerbot? knownBot) ? knownBot.AccountName : botId.ToString("D"));
        string subject = $"bot {name} (id {botId:D}, char {characterId})";
        AnomalyLedger.Shared.Report(
            PlayerbotAnomalies.Prefix + "population",
            "login-failed",
            subject,
            $"population {stage} failed to bring bot online: code={result.Code}",
            AnomalySeverity.Warning,
            origin: _world?.Identity);
    }

    /// <summary>
    /// Create up to <paramref name="count"/> new bots, within the rate limit, the registry and the running cap, each planned against
    /// <paramref name="planned"/> (the population's own bots running or waiting to be restarted, per band; kept current). Returns the
    /// bots created, why creation stopped short when a cap or a hold stopped it (the status line's <c>held back</c>), and whether a
    /// creation, start or placement was refused (recorded as the last problem; no more creation this pass). With
    /// <paramref name="quietWhenNothingFits"/> a plan that finds no band ends it without a reason (the rotation goes on).
    /// </summary>
    private async Task<(int Created, string? StoppedBecause, bool Failed)> CreateAsync(PassContext ctx, int[] planned, int count, bool quietWhenNothingFits,
        CancellationToken ct)
    {
        if (_world is not { } world) return (0, "the population feature is not attached", false);
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        int created = 0;
        bool fresh = pop.FreshLaunch;
        // Fresh launch gives each new bot its start home; the homes are also how the start areas are counted for the cap.
        bool startHomes = fresh && pop.FreshStartHome && managed.HomesAvailable;
        Dictionary<string, int> startCounts = startHomes ? StartCounts(managed, ctx.RunningNow.Union(ctx.Members.Keys)) : [];
        if (!fresh && ctx.Input.TerrainHold is { } hold) return (0, quietWhenNothingFits ? null : hold, false);
        if (!fresh && !ctx.Input.TeleportAvailable) return (0, "the teleport service is not available", false);
        if (_placementRefusals >= MaxPlacementRefusals && Volatile.Read(ref _passes) - _pausedAtPass > PauseLiftsAfterPasses)
            _placementRefusals = 0; // the pause lifts on its own: content may have been reloaded
        while (created < count)
        {
            bool rush = ctx.Input.RushCount > 0;
            // Between bots, not only between passes: a brake that trips mid-pass stops the creation at once.
            if (rush && _rush.CreationBlock is { } blocked) return (created, blocked, false);
            if (!rush && pop.MaxCreatePerMinute == 0) return (created, null, false);
            if (_placementRefusals >= MaxPlacementRefusals) return (created, $"creation paused: {MaxPlacementRefusals} placements refused in a row", false);
            while (_createTimes.Count > 0 && ctx.Input.ClockMs - _createTimes.Peek() >= 60_000) _createTimes.Dequeue();
            if (!rush && _createTimes.Count >= pop.MaxCreatePerMinute) return (created, $"creation is limited to {pop.MaxCreatePerMinute} per minute", false);
            if (ctx.Known.Count + ctx.Created >= _options.MaxRegisteredBots) return (created, "the bot registry is full (MaxRegisteredBots)", false);
            if (ctx.Input.RunningAll + ctx.Started + ctx.Created >= _options.MaxBots) return (created, "the running bot cap (MaxBots) is reached", false);

            PopulationRaceClassCache cache = await PairsFor(ctx.Scoped, pop, ct).ConfigureAwait(false);
            PopulationPlan? plan = null;
            Team planTeam;
            byte planRace, planClass;
            int planBand;
            if (fresh)
            {
                bool full = false;
                PopulationFreshPlan? freshPlan = startHomes
                    ? PopulationBandPlanner.PlanFresh(pop, ctx.Alliance, ctx.Horde, cache, _random, startCounts, pop.FreshStartZoneCap, out full)
                    : PopulationBandPlanner.PlanFresh(pop, ctx.Alliance, ctx.Horde, cache, _random);
                if (freshPlan is null && full)
                    return (created, string.Create(CultureInfo.InvariantCulture,
                        $"{PopulationRush.StartZonesFullNote}: every start area a new bot could use holds {pop.FreshStartZoneCap} bots (FreshStartZoneCap); creation resumes as bots graduate at level {pop.FreshGraduateLevel}"), false);
                if (freshPlan is null) return (created, quietWhenNothingFits ? null : "no valid race and class for the configured factions and classes", false);
                (planTeam, planRace, planClass, planBand) = (freshPlan.Team, freshPlan.Race, freshPlan.Class, 0);
            }
            else
            {
                PopulationPlacementFacts facts = ctx.Input.Facts!; // a non-fresh pass without a hold has it
                plan = await world.InvokeAsync(() => PopulationBandPlanner.PlanSafe(pop, ctx.Desired, planned,
                    ctx.Alliance, ctx.Horde, cache, ctx.Input.Atlas, ctx.Assigned, ctx.Input.BoostAvailable, _random,
                    (location, level, faction) => _provisioner.IsSafe(facts, location, level, faction),
                    ctx.Input.ZoneValue, PopulationValueWeights.From(pop), zone => _trouble.Count(zone, NowUnix())), ct).ConfigureAwait(false);
                if (plan is null)
                    return (created, quietWhenNothingFits ? null : "no safe zone, level and race-class placement is available (see the zone counts and boost line)", false);
                (planTeam, planRace, planClass, planBand) = (plan.Team, plan.Race, plan.Class, plan.Band);
            }

            PlayerbotOperationResult? made = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                made = Checked(await managed.CreateAsync(_names.Next(), planRace, planClass, ct).ConfigureAwait(false));
                if (made.Success || !(made.Code == "name-in-use" || made.Code.StartsWith("character-create-refused", StringComparison.Ordinal))) break;
            }

            if (made is not { Success: true, BotId: { } botId })
            {
                Fail($"creation refused: {made?.Code}");
                return (created, null, true);
            }

            created++;
            ctx.Created++;
            _createTimes.Enqueue(ctx.Input.ClockMs);
            ManagedPlayerbot? record = await ctx.Scoped.GetRequiredService<IManagedPlayerbotStore>().FindAsync(botId, ct).ConfigureAwait(false);
            if (record is null) { Fail($"created bot {botId} is not in the registry"); return (created, null, true); }
            long now = NowUnix();
            var member = new PlayerbotPopulationMember(botId, record.CharacterId, 0, (byte)planBand, now, now);
            await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
            ctx.Members[botId] = member;
            planned[planBand]++;

            // The home goes on before the start, so the first brain already has it. A bot whose home could not be set is not refused: it
            // runs homeless and the next sweep tries again.
            if (startHomes)
            {
                string? homeError = await ApplyStartHomeAsync(managed, ctx.Scoped, botId, planRace, planClass, StartHomeStage.Start, ct).ConfigureAwait(false);
                if (homeError is not null) Fail($"the start home of {made.Name} was not set: {homeError}");
                // Counted whether or not the home was set: the cap fails closed (the next sweep sets the home).
                if (PopulationStartAreas.AreaOf(planRace) is { } startArea) startCounts[startArea] = startCounts.GetValueOrDefault(startArea) + 1;
            }

            PlayerbotOperationResult start = Checked(await managed.StartAsync(botId.ToString(), ct).ConfigureAwait(false));
            if (!start.Success)
            {
                Fail($"start of new bot {made.Name} refused: {start.Code}");
                ReportLoginFailed(ctx.Known, botId, record.CharacterId, made.Name, start, "create-start");
                return (created, null, true);
            }

            ctx.RunningNow.Add(botId);
            _startedAt[botId] = ctx.Input.ClockMs;

            // Fresh launch: no boost and no teleport; the bot stands where CMSG_CHAR_CREATE put it. Its zone is recorded as it is.
            string? refusal = plan is not { } activePlan ? null
                : await world.InvokeAsync(() => _provisioner.Provision(botId, activePlan.Level, activePlan.Placement.Location, ctx.Input.Facts!), ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                // The plan was safe when it was made (a content reload can change that before the teleport). A level-1 bot that
                // cannot be placed is deleted rather than left in its race's starting area, where it would hold a registry slot and
                // count towards a band; the next pass plans again.
                Fail($"placement of {made.Name} refused: {refusal}");
                string? paused = null;
                // A stale refusal (the content was reloaded since the plan) says nothing about the placements: it does not count to the pause.
                if (refusal != PopulationProvisioner.StaleCode && ++_placementRefusals >= MaxPlacementRefusals)
                {
                    _pausedAtPass = Volatile.Read(ref _passes);
                    paused = $"creation paused: {MaxPlacementRefusals} placements refused in a row";
                }

                ctx.RunningNow.Remove(botId);
                ctx.Members.Remove(botId);
                _startedAt.Remove(botId);
                planned[planBand]--;
                // Mark first (not cancellable: a shutdown must not lose the mark), then delete the bot, then the row.
                await ctx.Store.UpsertAsync(member with { Band = PendingDeleteBand, UpdatedUnix = NowUnix() }, CancellationToken.None).ConfigureAwait(false);
                await TryDeletePendingAsync(managed, ctx.Store, botId, made.Name, ct).ConfigureAwait(false);
                return (created, paused, true);
            }

            _placementRefusals = 0;

            uint zone = plan is { } placedPlan
                ? placedPlan.Placement.Zone.ZoneId
                : await world.InvokeAsync(() => managed.FindSession(botId)?.Player?.ZoneId ?? 0u, ct).ConfigureAwait(false);
            member = member with { ZoneId = zone, UpdatedUnix = NowUnix() };
            await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
            ctx.Members[botId] = member;
            ctx.Have[planBand]++;
            ctx.Assigned[zone] = ctx.Assigned.GetValueOrDefault(zone) + 1;
            if (planTeam == Team.Alliance) ctx.Alliance++; else ctx.Horde++;
        }

        return (created, null, false);
    }

    /// <summary>
    /// The most start-home attempts one sweep makes (a start position query, a world-thread hop and a database write each; failures
    /// count as much as successes, so a map the server does not allow can never make a sweep cost more than this).
    /// </summary>
    internal const int MaxHomeChangesPerSweep = 25;

    /// <summary>Sweeps a bot whose start home could not be set waits before the next attempt (about 5 minutes at the default interval), so failing bots never starve the others of the 25 attempts.</summary>
    private const int HomeRetrySweeps = 30;

    /// <summary>
    /// A population member row with this <c>Band</c> belongs to a bot whose start home an operator cleared (<c>.playerbot home clear</c>):
    /// the sweep never gives it one again. Like <see cref="PendingDeleteBand"/> the band is only a marker (a fresh-launch pool has the one band 0).
    /// </summary>
    internal const byte HomeOptOutBand = byte.MaxValue - 1;

    private int _sweeps;
    private int _homeCursor;
    private readonly Dictionary<Guid, int> _homeRetrySweep = [];
    private readonly Dictionary<byte, StartPosition> _starts = [];
    private readonly object _optOutGate = new();
    private readonly HashSet<Guid> _optedOut = [];
    private readonly List<Guid> _optOutToPersist = [];

    /// <summary>An operator cleared these bots' start homes: they stay homeless (called before the homes are removed; any thread).</summary>
    private void OnHomesClearedByOperator(IReadOnlyList<Guid> botIds)
    {
        lock (_optOutGate)
            foreach (Guid id in botIds)
                if (_optedOut.Add(id)) _optOutToPersist.Add(id);
    }

    private bool IsOptedOut(Guid botId)
    {
        lock (_optOutGate) return _optedOut.Contains(botId);
    }

    /// <summary>Writes the opt-out marker of the members whose homes an operator cleared since the last sweep.</summary>
    private async Task PersistOptOutsAsync(PassContext ctx, CancellationToken ct)
    {
        Guid[] pending;
        lock (_optOutGate)
        {
            pending = [.. _optOutToPersist];
            _optOutToPersist.Clear();
        }

        // An id leaves the queue only once its row is stored (or needs no row): whatever was not reached, or whose write threw, is queued again.
        int done = 0;
        try
        {
            for (; done < pending.Length; done++)
            {
                Guid id = pending[done];
                if (ctx.Input.Managed.SplitBrain) return; // another world owns the rows: no member row is written
                if (!ctx.Members.TryGetValue(id, out PlayerbotPopulationMember? member) || member.Band == HomeOptOutBand) continue;
                member = member with { Band = HomeOptOutBand, UpdatedUnix = NowUnix() };
                await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
                ctx.Members[id] = member;
            }
        }
        finally
        {
            if (done < pending.Length)
                lock (_optOutGate)
                    for (int i = done; i < pending.Length; i++) _optOutToPersist.Add(pending[i]);
        }
    }

    /// <summary>The bots of each start area that hold a start-stage home, running or parked (the cap counts these; a graduated bot has left the valley).</summary>
    private static Dictionary<string, int> StartCounts(ManagedPlayerbotFeature managed, IEnumerable<Guid> bots)
    {
        var counts = new Dictionary<string, int>();
        foreach (Guid id in bots)
            if (managed.HomeOf(id) is { } home && PopulationStartAreas.TryParse(home.Place, out StartHomeStage stage, out string area) && stage == StartHomeStage.Start)
                counts[area] = counts.GetValueOrDefault(area) + 1;
        return counts;
    }

    /// <summary>
    /// Gives a bot the home of <paramref name="stage"/> round its race's start position (<see cref="IWorldDataStore.GetStartPositionAsync"/>,
    /// asked once per race). Null on success, otherwise why not. The map is checked against <c>AllowedMaps</c> before any query.
    /// </summary>
    private async Task<string?> ApplyStartHomeAsync(ManagedPlayerbotFeature managed, IServiceProvider scoped, Guid botId, byte race, byte cls,
        StartHomeStage stage, CancellationToken ct)
    {
        if (PopulationStartAreas.AreaOf(race) is not { } area) return $"race {race} has no start area";
        uint continent = PopulationStartAreas.ContinentOf(race);
        if (!managed.AllowsHomeMap(continent)) return $"map {continent} is not an allowed playerbot map";
        StartPosition? start = await GetStartAsync(scoped, race, cls, ct).ConfigureAwait(false);
        if (start is null) return $"no start position for race {race} class {cls}";
        return await SetStartHomeAsync(managed, botId, start.MapId, new Vector3(start.X, start.Y, start.Z), area, stage, ct).ConfigureAwait(false);
    }

    /// <summary>The start position of a race, asked of the store once and kept (the position does not depend on the class).</summary>
    private async Task<StartPosition?> GetStartAsync(IServiceProvider scoped, byte race, byte cls, CancellationToken ct)
    {
        if (_starts.TryGetValue(race, out StartPosition? known)) return known;
        StartPosition? start = await scoped.GetRequiredService<IWorldDataStore>().GetStartPositionAsync(race, cls, ct).ConfigureAwait(false);
        if (start is not null) _starts[race] = start;
        return start;
    }

    /// <summary>Stores and applies one start home, clipped to the start zone when the terrain knows the zone of the centre (a centre it does not know would give a home that contains nothing). Null on success, otherwise why not.</summary>
    private Task<string?> SetStartHomeAsync(ManagedPlayerbotFeature managed, Guid botId, uint mapId, Vector3 centre, string area,
        StartHomeStage stage, CancellationToken ct)
        => SetPopulationHomeAsync(managed, botId, mapId, centre, PopulationStartAreas.RadiusOf(stage, _options.Population), PopulationStartAreas.PlaceOf(area, stage), ct);

    /// <summary>Stores and applies one population home (start, zone or town), zone-clipped when the terrain knows the zone of the centre. Null on success, otherwise why not.</summary>
    private async Task<string?> SetPopulationHomeAsync(ManagedPlayerbotFeature managed, Guid botId, uint mapId, Vector3 centre, float radius, string place,
        CancellationToken ct)
    {
        if (!managed.AllowsHomeMap(mapId)) return $"map {mapId} is not an allowed playerbot map";
        if (_world is not { } world) return "the population feature is not attached";
        uint zone = await world.InvokeAsync(() => managed.ZoneAt(mapId, centre), ct).ConfigureAwait(false);
        try
        {
            ManagedPlayerbotFeature.HomeOutcome outcome = await managed.SetHomesAsync([new ManagedPlayerbotFeature.HomeTarget(botId, mapId, centre)],
                radius, place, ct, zoneClipped: zone != 0, logFailures: false).ConfigureAwait(false);
            return outcome.Set == 1 ? null : "the home was not stored";
        }
        catch (Exception ex) when ((ex is InvalidOperationException or ArgumentException) && ex is not ManagedPlayerbotFeature.PlayerbotSplitBrainException)
        {
            FaultLedger.Shared.Record("playerbot.population", ex);
            AnomalyLedger.Shared.Report("bot-population", "adoption-fault", "population", $"{place} for bot {botId:D}: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// The start zone a capital that lies inside a WMO stands for. Undercity (1497) is under the Tirisfal Glades (85) and Ironforge (1537) is
    /// in Dun Morogh (1): the terrain cell there reads the overland zone, so before the WMO area was read a bot in either city counted as
    /// in the start zone; with it, the city is its own zone. For the start-country test the city is still part of the country around it
    /// (a graduate who walks to its race's capital for a trainer has not left home). Every other capital is a terrain zone of its own and
    /// is not mapped; the relocation pass leaves a bot in a capital where it is (the atlas has no such zone).
    /// </summary>
    internal static uint StartCountryZone(uint zoneId) => zoneId switch
    {
        1497 => 85,
        1537 => 1,
        _ => zoneId,
    };

    /// <summary>Whether a bot is still in its start country: on the start map and either in the start zone (a WMO capital counting as the zone around it, <see cref="StartCountryZone"/>) or within the graduated home's radius of the start position.</summary>
    internal static bool InStartCountry(PopulationBotFacts bot, StartPosition start, PlayerbotPopulationOptions pop)
    {
        if (bot.MapId != start.MapId) return false;
        if (StartCountryZone(bot.ZoneId) == start.ZoneId) return true;
        float dx = bot.X - start.X, dy = bot.Y - start.Y;
        return (dx * dx) + (dy * dy) <= (float)pop.FreshZoneHomeRadius * pop.FreshZoneHomeRadius;
    }

    // ---- town progression (PopulationTownProgression) ----

    private enum TownStepResult : byte
    {
        Unchanged,
        Changed,
        Clear,
        Failed,
    }

    private Dictionary<string, ResolvedTown>? _towns;
    private CreatureContent? _townsContent;
    private readonly Dictionary<Guid, (string Active, long SinceMs)> _townPhase = [];
    private readonly HashSet<string> _townAnomaliesReported = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _unfitSweeps = [];
    private int _adoptRequestedGeneration;
    private int _adoptDoneGeneration;

    /// <summary>Tests: the creature content the town centres are read from, instead of the creature feature's.</summary>
    internal Func<CreatureContent?>? TownContentSource { get; set; }

    /// <summary>
    /// <c>.playerbot population adopt</c>: the next sweeps take over the operator homes of the population's bots, until a sweep has done so
    /// completely. Any thread: a request is a new generation, and a sweep only retires the generation it began with, so a command that
    /// arrives while a sweep runs is never lost.
    /// </summary>
    internal void RequestAdopt() => Interlocked.Increment(ref _adoptRequestedGeneration);

    /// <summary>Whether an adoption requested by the command is still under way.</summary>
    internal bool AdoptPending => Volatile.Read(ref _adoptRequestedGeneration) != Volatile.Read(ref _adoptDoneGeneration);

    /// <summary>Tests: forget the stay clocks, as a restart does. The next sweep starts a fresh stay for every bot at the town it is homed in.</summary>
    internal void ForgetTownPhases()
    {
        _townPhase.Clear();
        _unfitSweeps.Clear();
    }

    /// <summary>Sweeps in a row a bot's secondary town may hold no creature of its level before the bot moves on (the fit hysteresis).</summary>
    internal const int UnfitSweepsBeforeMove = 2;

    /// <summary>Explore-atlas rings (160 yards each) round a town centre that must hold a creature of the bot's level for the town to fit it.</summary>
    internal const int TownFitRings = 6;

    /// <summary>Tests: whether a town (map, bot level, centre) holds creatures of the bot's level, instead of the explore atlas.</summary>
    internal Func<uint, int, Vector3, bool>? TownFits { get; set; }

    /// <summary>
    /// Whether the area round a town centre has creatures of the bot's level (the explore atlas, <see cref="TownFitRings"/> rings): an
    /// over-levelled bot in a town whose zone has nothing left for it moves on to the town of its level. True when the atlas does not
    /// know the map (no data: the band alone decides).
    /// </summary>
    private bool FitsLevel(uint mapId, int level, Vector3 centre)
    {
        if (TownFits is { } fits) return fits(mapId, level, centre);
        PlayerbotExploreAtlas? atlas = services.GetService<CreatureWorldFeature>()?.ExploreAtlas;
        return atlas is null || !atlas.HasIndexedMap(mapId) || atlas.HasCellNear(mapId, (uint)level, centre, TownFitRings);
    }

    private IPlayerbotHomeTravel Travel => services.GetService<IPlayerbotHomeTravel>() ?? DefaultHomeTravel.Instance;

    /// <summary>The resolved town centres, read on the world thread once per creature content; null while the content is not installed.</summary>
    private async Task<Dictionary<string, ResolvedTown>?> TownsAsync(CancellationToken ct)
    {
        if (_world is not { } world) return null;
        return await world.InvokeAsync(() =>
        {
            CreatureContent? content = TownContentSource is { } source ? source()
                : services.GetService<CreatureWorldFeature>() is { ContentInstalled: true } feature ? feature.Content : null;
            if (content is null) return null;
            if (_towns is not null && ReferenceEquals(content, _townsContent)) return _towns;
            _townsContent = content;
            return _towns = PopulationTownProgression.Resolve(content);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The bots (running or parked) per level-band town they hold as their secondary home.</summary>
    private static Dictionary<string, int> TownCounts(ManagedPlayerbotFeature managed, IEnumerable<Guid> bots)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Guid id in bots)
            if (managed.HomeOf(id) is { } home && PopulationTownProgression.TryParsePlace(home.Place, out TownPlace place) && place.Secondary is { } secondary)
                counts[secondary] = counts.GetValueOrDefault(secondary) + 1;
        return counts;
    }

    /// <summary>One anomaly (not one per bot, nor again for the same set) naming the towns the sweep needed and could not find in the world data.</summary>
    private void ReportUnresolvedTowns(SortedSet<string> unresolved)
    {
        // Once per town key per process: the round-robin sweep meets different towns each time, a re-report would repeat the same fact.
        string[] fresh = [.. unresolved.Where(key => _townAnomaliesReported.Add("unresolved:" + key))];
        if (fresh.Length == 0) return;
        AnomalyLedger.Shared.Report(
            PlayerbotAnomalies.Prefix + "population",
            "town-unresolved",
            "population towns",
            $"the centre of {fresh.Length} town(s) could not be found (no spawn of their anchor NPC on the map): {string.Join(',', fresh)}; bots fall back to the towns that did resolve, or to no home",
            AnomalySeverity.Warning,
            origin: _world?.Identity);
    }

    /// <summary>One anomaly per town (per process) whose map the server does not allow for playerbot homes, so a capital or town that vanishes from the progression is explained.</summary>
    private void ReportDisallowedTown(string key, uint mapId)
    {
        if (!_townAnomaliesReported.Add("map:" + key)) return;
        AnomalyLedger.Shared.Report(
            PlayerbotAnomalies.Prefix + "population",
            "town-map-not-allowed",
            $"town {key}",
            $"the town {key} is on map {mapId}, which World:Playerbots:AllowedMaps does not allow for homes: no bot is homed there",
            AnomalySeverity.Warning,
            origin: _world?.Identity);
    }

    /// <summary>
    /// One bot's town homes: its master (capital) and secondary (level-band town), and which of them it is homed in now (the stay
    /// cycle). Stores the home when the place text, the centre or the radius differ from the stored one. A bot with no resolvable capital
    /// is cleared (the old behaviour: no home); one whose towns are all out of reach without a teleport (<c>TownProgressionTeleport</c>
    /// off) keeps its home; one with no usable secondary stays at its capital.
    /// </summary>
    private async Task<(TownStepResult Result, string? Error)> TownStepAsync(PassContext ctx, PopulationBotFacts bot, PlayerbotHome? home,
        Dictionary<string, ResolvedTown> towns, Dictionary<string, int> counts, SortedSet<string> unresolved, CancellationToken ct)
    {
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        long now = ctx.Input.ClockMs;
        IPlayerbotHomeTravel travel = Travel;
        bool blockedByTravel = false;
        bool Usable(string key)
        {
            if (!towns.TryGetValue(key, out ResolvedTown town)) { unresolved.Add(key); return false; }
            if (!managed.AllowsHomeMap(town.Row.MapId)) { ReportDisallowedTown(key, town.Row.MapId); return false; }
            if (pop.TownProgressionTeleport || travel.CanReachWithoutTeleport(bot.MapId, new Vector3(bot.X, bot.Y, 0f), town.Row.MapId, town.Centre)) return true;
            blockedByTravel = true;
            return false;
        }

        TownPlace? current = home is not null && PopulationTownProgression.TryParsePlace(home.Place, out TownPlace parsed) ? parsed : null;

        string? master = current?.Master;
        bool masterOk = master is not null && PopulationTownProgression.Find(master) is { } masterRow && PopulationTownProgression.ServesTeam(masterRow, bot.Team) && Usable(master);
        if (!masterOk) master = PopulationTownProgression.MasterFor(bot.Team, bot.Race, bot.BotId, pop.CapitalMajorSharePct, Usable);
        if (master is null)
        {
            if (!blockedByTravel) return (TownStepResult.Clear, null);
            ReportUnreachable(bot, noCapital: true);
            return (TownStepResult.Unchanged, null); // no capital reachable: the bot keeps the home it has
        }

        string? secondary = current?.Secondary;
        TownRow? secondaryRow = PopulationTownProgression.Find(secondary);
        bool secondaryOk = secondaryRow is not null && PopulationTownProgression.ServesTeam(secondaryRow, bot.Team)
            && PopulationTownProgression.StillServes(secondaryRow, bot.Level, pop.TownBandMargin) && Usable(secondary!);
        if (secondaryOk)
        {
            // A town whose zone has nothing of the bot's level is left only after UnfitSweepsBeforeMove sweeps in a row: the atlas test is exact
            // to the level, and a mid-band bot must not hop towns on one level without a creature near.
            if (FitsLevel(secondaryRow!.MapId, bot.Level, towns[secondary!].Centre)) _unfitSweeps.Remove(bot.BotId);
            else secondaryOk = (_unfitSweeps[bot.BotId] = _unfitSweeps.GetValueOrDefault(bot.BotId) + 1) < UnfitSweepsBeforeMove;
        }
        else _unfitSweeps.Remove(bot.BotId);
        bool secondaryChanged = false;
        if (!secondaryOk)
        {
            if (secondary is not null && counts.TryGetValue(secondary, out int held) && held > 0) counts[secondary] = held - 1;
            string? previous = secondary;
            uint continent = PopulationStartAreas.ContinentOf(bot.Race);
            Func<string, int> cap = key => PopulationTownProgression.CapOf(pop, key);
            // A town whose zone holds creatures of the bot's level first; any town of the band when none does.
            secondary = (PopulationTownProgression.PickSecondary(bot.Team, continent, bot.Level, counts, cap,
                    key => Usable(key) && FitsLevel(towns[key].Row.MapId, bot.Level, towns[key].Centre))
                ?? PopulationTownProgression.PickSecondary(bot.Team, continent, bot.Level, counts, cap, Usable))?.Key;
            secondaryChanged = previous is not null && secondary != previous;
            if (secondaryChanged || previous is null) _unfitSweeps.Remove(bot.BotId); // the count belonged to the town the bot left
            if (secondary is not null) counts[secondary] = counts.GetValueOrDefault(secondary) + 1;
            if (secondary is null && PopulationTownProgression.BelowBands(bot.Team, continent, bot.Level))
            {
                // Younger than the first town of its own continent (a lowered FreshReleaseLevel): no town, rather than one overseas and a
                // capital-only home. A town home it has is dropped, any other home stays.
                return (home is not null && current is not null ? TownStepResult.Clear : TownStepResult.Unchanged, null);
            }

            if (secondary is null && blockedByTravel)
            {
                // Nothing to progress to that the bot can reach: it must not be parked at a capital (a capital home lingers at the inn for
                // good). A town home of the progression is dropped, so the bot explores as an unhomed one; any other home stays.
                ReportUnreachable(bot, noCapital: false);
                return (home is not null && current is not null ? TownStepResult.Clear : TownStepResult.Unchanged, null);
            }
        }

        string active;
        if (current is not { } existing) active = master; // a bot that has just graduated starts at its capital (trainers) before it heads out
        else
        {
            string wasActive = existing.AtCapital ? master : existing.Active;
            long since = _townPhase.TryGetValue(bot.BotId, out var phase) && phase.Active == wasActive ? phase.SinceMs : now;
            active = PopulationTownProgression.NextActive(new TownPlace(wasActive, master, secondary), secondary, secondaryChanged, now - since,
                PopulationTownProgression.StayMs(pop.CapitalStayMinMinutes, pop.CapitalStayMaxMinutes, bot.BotId),
                PopulationTownProgression.StayMs(pop.SecondaryStayMinMinutes, pop.SecondaryStayMaxMinutes, bot.BotId));
        }

        ResolvedTown target = towns[active];
        string place = PopulationTownProgression.PlaceOf(active, master, secondary);
        float radius = target.Row.Capital ? pop.CapitalHomeRadius : pop.TownHomeRadius;
        bool sameActive = _townPhase.TryGetValue(bot.BotId, out var known) && known.Active == active;
        if (home is not null && home.Place == place && home.MapId == target.Row.MapId && (int)MathF.Round(home.Radius) == (int)radius)
        {
            if (!sameActive) _townPhase[bot.BotId] = (active, now); // first sight since the start: a fresh stay, no switch
            return (TownStepResult.Unchanged, null);
        }

        string? error = await SetPopulationHomeAsync(managed, bot.BotId, target.Row.MapId, target.Centre, radius, place, ct).ConfigureAwait(false);
        if (error is not null) return (TownStepResult.Failed, error);
        if (!sameActive) _townPhase[bot.BotId] = (active, now);
        return (TownStepResult.Changed, null);
    }

    /// <summary>
    /// With <c>TownProgressionTeleport</c> off: one Info anomaly per reason (per process) that a town was left unassigned. The text names
    /// the real reason: a bot on a continent needs a boat, a zeppelin or a teleport; a bot on another map (a dungeon, a battleground) is
    /// not on a continent at all.
    /// </summary>
    private void ReportUnreachable(PopulationBotFacts bot, bool noCapital)
    {
        bool continent = bot.MapId is 0 or 1;
        if (!_townAnomaliesReported.Add($"unreachable:{(continent ? "continent" : "other-map")}:{(noCapital ? "no-capital" : "no-secondary")}")) return;
        // The outcome is what the sweep did: a bot with a reachable capital but no reachable secondary gets no town home (a capital-only
        // home would park it; a town home it had is dropped, any other home stays); a bot with no reachable capital at all keeps its home.
        string outcome = noCapital
            ? "no capital is reachable, so it keeps the home it has (a town home included)"
            : "it gets no town home (a capital-only home would park it at the inn; a town home it had is dropped, a start, zone or operator home stays) and it explores until a town is reachable";
        string detail = continent
            ? $"a town of the progression is across water from the bot (map {bot.MapId}) and needs a boat, a zeppelin or a teleport, and World:Playerbots:Population:TownProgressionTeleport is off: {outcome}; further bots with this outcome are not reported"
            : $"the bot stands on map {bot.MapId}, which is not a continent (a dungeon, raid or battleground), so every town needs a teleport and World:Playerbots:Population:TownProgressionTeleport is off: {outcome}; further bots with this outcome are not reported";
        AnomalyLedger.Shared.Report(
            PlayerbotAnomalies.Prefix + "population",
            "town-unreachable",
            $"bot {bot.BotId:D}",
            detail,
            AnomalySeverity.Info,
            origin: _world?.Identity);
    }

    /// <summary>Bots holding town state (stay clock, fit count): tests.</summary>
    internal int TownStateCount => _townPhase.Keys.Union(_unfitSweeps.Keys).Count();

    /// <summary>Drops the per-bot town state (stay clock, fit count) of every bot that is not in <paramref name="members"/> (the population's bots).</summary>
    internal void ForgetDeletedBots(ISet<Guid> members)
    {
        foreach (Guid gone in _townPhase.Keys.Where(id => !members.Contains(id)).ToArray()) _townPhase.Remove(gone);
        foreach (Guid gone in _unfitSweeps.Keys.Where(id => !members.Contains(id)).ToArray()) _unfitSweeps.Remove(gone);
    }

    /// <summary>
    /// Graduation (see <see cref="PopulationStartAreas"/>): each running bot of the population gets the home of its level's stage.
    /// A bot with no home gets one round its race's start position (the live fresh launch made bots with none), but a bot at or above
    /// <c>FreshGraduateLevel</c> only while it is still in its start country: one that has already moved on is left alone (it counts as
    /// released). A start home widens at <c>FreshGraduateLevel</c> and is removed at <c>FreshReleaseLevel</c>; a home whose radius no
    /// longer matches the live option is resized. A home an operator set (any other place text), and a bot whose home an operator
    /// cleared (<see cref="HomeOptOutBand"/>), are never touched. At most <see cref="MaxHomeChangesPerSweep"/> attempts per sweep,
    /// failures included, and one sweep per interval. Failures are summed up in one warning. Returns the homes changed.
    /// </summary>
    private async Task<int> SweepStartHomesAsync(PassContext ctx, CancellationToken ct)
    {
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        if (managed.IsStopping) return 0;
        if (ctx.Input.ClockMs - _lastHomeSweepMs < pop.IntervalSeconds * 1000L) return 0;
        _lastHomeSweepMs = ctx.Input.ClockMs;
        int sweep = ++_sweeps;
        await PersistOptOutsAsync(ctx, ct).ConfigureAwait(false);
        int attempts = 0, changes = 0, failed = 0;
        string? firstError = null;
        var cleared = new List<Guid>();
        // The generation this sweep began with: it is retired only by a sweep that took every operator home over, and a request that
        // arrives meanwhile (a later generation) stays pending.
        int adoptGeneration = Volatile.Read(ref _adoptRequestedGeneration);
        bool adopt = pop.AdoptOperatorHomes || adoptGeneration != Volatile.Read(ref _adoptDoneGeneration), completed = true, townsLoaded = false, adoptIncomplete = false;
        Dictionary<string, ResolvedTown>? towns = null;
        Dictionary<string, int>? townCounts = null;
        var unresolvedTowns = new SortedSet<string>(StringComparer.Ordinal);
        // Round robin: a sweep that spent its attempts stops at the bot it reached, and the next one goes on from there, so the bots
        // whose homes change on a cycle (the stays of the town homes) never starve the ones still waiting for their first.
        IReadOnlyList<PopulationBotFacts> running = ctx.Input.Running;
        int first = running.Count == 0 ? 0 : _homeCursor % running.Count;
        _homeCursor = 0;
        for (int offset = 0; offset < running.Count; offset++)
        {
            PopulationBotFacts bot = running[(first + offset) % running.Count];
            if (attempts >= MaxHomeChangesPerSweep) { completed = false; _homeCursor = first + offset; break; }
            if (!ctx.Members.TryGetValue(bot.BotId, out PlayerbotPopulationMember? member)) continue;
            if (member.Band == HomeOptOutBand || IsOptedOut(bot.BotId)) continue;
            if (_homeRetrySweep.TryGetValue(bot.BotId, out int retryAt) && sweep < retryAt)
            {
                if (adopt && managed.HomeOf(bot.BotId) is { } waiting && !PopulationStartAreas.TryParse(waiting.Place, out _, out _)) adoptIncomplete = true; // its operator home waits for the retry
                continue;
            }

            StartHomeStage want = PopulationStartAreas.StageFor(bot.Level, pop);
            PlayerbotHome? home = managed.HomeOf(bot.BotId);
            bool ours = false, adopted = false;
            StartHomeStage have = StartHomeStage.None;
            string area = string.Empty;
            if (home is not null && !(ours = PopulationStartAreas.TryParse(home.Place, out have, out area)))
            {
                if (!adopt) continue; // an operator's home
                // AdoptOperatorHomes / .playerbot population adopt: the operator's home of a population bot is replaced by the home of its level.
                (home, adopted) = (null, true);
            }

            // A town home of a bot that is below the town levels again (a reset to level 1) is no start home: it starts over at its race's start.
            if (ours && have == StartHomeStage.Town && want is not (StartHomeStage.Town or StartHomeStage.None)) (ours, home) = (false, null);
            if (want == StartHomeStage.None)
            {
                if (ours || adopted) { cleared.Add(bot.BotId); attempts++; }
                continue;
            }

            if (want == StartHomeStage.Town)
            {
                if (!townsLoaded) { towns = await TownsAsync(ct).ConfigureAwait(false); townsLoaded = true; }
                if (towns is null)
                {
                    adoptIncomplete |= adopted; // the creature content is not installed yet: the next sweep
                    continue;
                }

                townCounts ??= TownCounts(managed, ctx.Members.Keys);
                (TownStepResult step, string? townError) = await TownStepAsync(ctx, bot, home, towns, townCounts, unresolvedTowns, ct).ConfigureAwait(false);
                switch (step)
                {
                    case TownStepResult.Changed:
                        attempts++;
                        changes++;
                        _homeRetrySweep.Remove(bot.BotId);
                        break;
                    case TownStepResult.Clear:
                        if (managed.HomeOf(bot.BotId) is not null) { cleared.Add(bot.BotId); attempts++; }
                        _townPhase.Remove(bot.BotId);
                        break;
                    case TownStepResult.Failed:
                        attempts++;
                        failed++;
                        firstError ??= townError;
                        _homeRetrySweep[bot.BotId] = sweep + HomeRetrySweeps;
                        break;
                    default:
                        adoptIncomplete |= adopted; // a town it cannot reach yet: the operator home stays, adoption is not done
                        break;
                }

                continue;
            }

            string? error;
            if (!ours || home is not { } existingHome)
            {
                if (PopulationStartAreas.AreaOf(bot.Race) is not { } botArea) continue;
                uint continent = PopulationStartAreas.ContinentOf(bot.Race);
                if (!managed.AllowsHomeMap(continent))
                {
                    // Nothing is queried for a map the server does not allow.
                    failed++;
                    firstError ??= $"map {continent} is not an allowed playerbot map";
                    continue;
                }

                if (!_starts.ContainsKey(bot.Race)) attempts++; // the start position query
                StartPosition? start = await GetStartAsync(ctx.Scoped, bot.Race, bot.Class, ct).ConfigureAwait(false);
                if (start is null)
                {
                    failed++;
                    firstError ??= $"no start position for race {bot.Race} class {bot.Class}";
                    _homeRetrySweep[bot.BotId] = sweep + HomeRetrySweeps;
                    continue;
                }

                // A bot of the graduated levels that already left its start country is released, not pulled back.
                if (want == StartHomeStage.Zone && !InStartCountry(bot, start, pop))
                {
                    // An operator home being adopted is cleared (the bot is released, as the level says); adoption is not left pending for it.
                    if (adopted) { cleared.Add(bot.BotId); attempts++; }
                    continue;
                }

                attempts++;
                error = await SetStartHomeAsync(managed, bot.BotId, start.MapId, new Vector3(start.X, start.Y, start.Z), botArea, want, ct).ConfigureAwait(false);
            }
            else
            {
                if (have == want && (int)MathF.Round(existingHome.Radius) == (want == StartHomeStage.Zone ? pop.FreshZoneHomeRadius : pop.FreshStartHomeRadius)) continue;
                attempts++;
                error = await SetStartHomeAsync(managed, bot.BotId, existingHome.MapId, new Vector3(existingHome.X, existingHome.Y, existingHome.Z), area, want, ct).ConfigureAwait(false);
            }

            if (error is null) { changes++; _homeRetrySweep.Remove(bot.BotId); continue; }
            failed++;
            firstError ??= error;
            _homeRetrySweep[bot.BotId] = sweep + HomeRetrySweeps;
        }

        if (cleared.Count > 0)
        {
            if (managed.SplitBrain) adoptIncomplete = true; // nothing cleared: the adoption is retried after a clear
            else
            {
                try { changes += await managed.ClearHomesAsync(cleared, ct, byOperator: false).ConfigureAwait(false); }
                catch (ManagedPlayerbotFeature.PlayerbotSplitBrainException) { adoptIncomplete = true; } // latched since the check: skip quietly, retry after a clear
            }
        }
        // Every operator home of the population is taken over: retire the generation this sweep began with (and no later one).
        if (completed && failed == 0 && !adoptIncomplete) Interlocked.Exchange(ref _adoptDoneGeneration, adoptGeneration);
        ForgetDeletedBots(ctx.Members.Keys.ToHashSet());
        ReportUnresolvedTowns(unresolvedTowns);
        if (_homeRetrySweep.Count > 4096)
            foreach (Guid stale in _homeRetrySweep.Where(r => r.Value <= sweep).Select(r => r.Key).ToArray()) _homeRetrySweep.Remove(stale);
        if (failed > 0)
        {
            _lastError = $"{failed} start homes were not set (first: {firstError})";
            logger.LogWarning("Playerbot population: {Failed} of {Attempts} start home attempts failed this sweep (first: {Error}); each is retried after {Sweeps} sweeps", failed, attempts, firstError, HomeRetrySweeps);
        }

        if (changes > 0) logger.LogInformation("Playerbot population: {Changes} start home changes (graduation at level {Graduate}, release at level {Release})", changes, pop.FreshGraduateLevel, pop.FreshReleaseLevel);
        return changes;
    }

    /// <summary>
    /// Delete the bot of a pending-deletion member (character, then registry row) and only then the member row. Any failure, a
    /// cancellation included, leaves the marked row for the next pass. True when the member is gone.
    /// </summary>
    private async Task<bool> TryDeletePendingAsync(ManagedPlayerbotFeature managed, IPlayerbotPopulationStore store, Guid botId, string? name, CancellationToken ct)
    {
        try
        {
            // Not cancellable: a stop that a shutdown interrupts would leave a desired-enabled bot for the next start to restore.
            Checked(await managed.StopAsync(botId.ToString(), CancellationToken.None).ConfigureAwait(false));
            PlayerbotOperationResult deleted = Checked(await managed.DeleteAsync(botId.ToString(), ct).ConfigureAwait(false));
            // "bot-not-found" means the registry row is already gone (a deletion that finished before a crash).
            if (!deleted.Success && deleted.Code != "bot-not-found")
            {
                Backoff(botId);
                Fail($"delete of unplaced bot {name ?? botId.ToString()} refused: {deleted.Code}; retried later");
                return false;
            }

            await store.DeleteAsync(botId, CancellationToken.None).ConfigureAwait(false);
            _pendingFailures.Remove(botId);
            _pendingRetryAt.Remove(botId);
            return true;
        }
        catch (ManagedPlayerbotFeature.PlayerbotSplitBrainException)
        {
            throw; // not a delete failure: the pass ends quietly
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Backoff(botId);
            AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"delete of unplaced bot {name ?? botId.ToString()} failed ({ex.GetType().Name}: {ex.Message})", origin: _world?.Identity);
            logger.LogFault("playerbot.population", ex, "Playerbot population: deleting the unplaced bot {Bot} failed", name ?? botId.ToString());
            Fail($"delete of unplaced bot {name ?? botId.ToString()} failed ({ex.GetType().Name}); retried later");
            return false;
        }
    }

    /// <summary>Ends the pass quietly when a managed operation was refused because split brain is latched.</summary>
    private static PlayerbotOperationResult Checked(PlayerbotOperationResult result)
        => result.Code == "playerbot-split-brain" ? throw new ManagedPlayerbotFeature.PlayerbotSplitBrainException(0) : result;

    /// <summary>
    /// The pass's member store: every write first checks that split brain is not latched (another world owns the playerbot rows), and ends the pass quietly if it is.
    /// One store call already in flight when the latch is set can still land.
    /// </summary>
    private sealed class SplitBrainGuardedStore(IPlayerbotPopulationStore inner, ManagedPlayerbotFeature managed) : IPlayerbotPopulationStore
    {
        public Task<IReadOnlyList<PlayerbotPopulationMember>> LoadAllAsync(CancellationToken cancellationToken = default) => inner.LoadAllAsync(cancellationToken);

        public Task UpsertAsync(PlayerbotPopulationMember member, CancellationToken cancellationToken = default)
            => managed.SplitBrain ? throw new ManagedPlayerbotFeature.PlayerbotSplitBrainException(0) : inner.UpsertAsync(member, cancellationToken);

        public Task<bool> DeleteAsync(Guid botId, CancellationToken cancellationToken = default)
            => managed.SplitBrain ? throw new ManagedPlayerbotFeature.PlayerbotSplitBrainException(0) : inner.DeleteAsync(botId, cancellationToken);
    }

    private void Backoff(Guid botId)
    {
        int failures = Math.Min(8, _pendingFailures.GetValueOrDefault(botId) + 1);
        _pendingFailures[botId] = failures;
        _pendingRetryAt[botId] = Volatile.Read(ref _passes) + failures;
    }

    private static bool IsStartable(PassContext ctx, PlayerbotPopulationMember member)
    {
        if (ctx.Input.States.TryGetValue(member.BotId, out ManagedPlayerbotState state)) return state == ManagedPlayerbotState.Stopped;
        return ctx.Known[member.BotId].State != ManagedPlayerbotState.Faulted;
    }

    /// <summary>Park up to <paramref name="count"/> bots in <see cref="PopulationParking.Order"/>; returns the reason of each bot parked.</summary>
    private async Task<IReadOnlyList<string>> ParkAsync(PassContext ctx, int count, bool keepRecent, CancellationToken ct)
    {
        if (_world is not { } world) return [];
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        IReadOnlyList<PopulationParkPick> order = PopulationParking.Order(ctx.Input.Running, ctx.Members.Keys.ToHashSet(), ctx.Input.Bands, ctx.Desired,
            ctx.Have, _startedAt, ctx.Input.ClockMs, ctx.Input.Pop.MinRunSeconds * 1000L, keepRecent);
        var parked = new List<string>();
        foreach (PopulationParkPick pick in order)
        {
            if (parked.Count == count) break;
            PopulationBotFacts bot = pick.Bot;
            // The facts are seconds old: look again, on the world thread, right before stopping.
            bool stillParkable = await world.InvokeAsync(() => IsParkableNow(managed, bot.BotId), ct).ConfigureAwait(false);
            if (!stillParkable) continue;
            PlayerbotOperationResult result = Checked(await managed.StopAsync(bot.BotId.ToString(), ct).ConfigureAwait(false));
            if (!result.Success) { Fail($"park of {bot.BotId} refused: {result.Code}"); continue; }
            parked.Add(pick.Reason);
            ctx.RunningNow.Remove(bot.BotId);
            _parkedAt[bot.BotId] = ctx.Input.ClockMs;
            int band = PopulationBandPlanner.BandOf(ctx.Input.Bands, bot.Level);
            if (band >= 0) ctx.Have[band]--;
        }

        return parked;
    }

    private bool IsParkableNow(ManagedPlayerbotFeature managed, Guid botId)
    {
        if (managed.FindSession(botId)?.Player is not { IsInWorld: true } player) return false;
        if (managed.IsPartyDriven(botId) || managed.IsScripted(botId) || services.GetService<PlayerbotGroupCoordinator>()?.Drives(botId) == true) return false;
        // Dead or a ghost may be parked (it logs out like any bot and comes back as it was); in combat or teleporting may not.
        return !player.Combat.IsInCombat && services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) != true;
    }

    private async Task<int> RelocateAsync(PassContext ctx, CancellationToken ct)
    {
        if (_world is not { } world) return 0;
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        PopulationZoneAtlas atlas = ctx.Input.Atlas;
        PopulationLevelRule rule = PopulationLevelRule.From(pop);
        int moved = 0;
        bool loggedNoSafe = false;
        // Only a bot standing in a zone the atlas knows can be out-levelled. A zone outside the atlas (a capital or city, another map) is
        // somewhere the bot went on purpose (town, trainer, travel goals): it is left where it is. A bot below every place of its zone it
        // could have been put (UnderLevelled: a low-level bot placed by an older build in Duskwood at Raven Hill, or one that walked over a
        // border into a harder zone) is moved the same way. Two more kinds of bot are moved whatever their level (issue #1454: bots placed
        // in Deadwind Pass before it was excluded stalled there for good, escaped to a bind point in the same area and stalled again):
        // a bot standing in an excluded zone of an allowed map (the operator took the zone out, the atlas no longer knows it), and a
        // stalled bot in an atlas zone it escaped (a recorded veto) or that holds more bots than its creatures feed (SpawnsPerBot).
        // Those first, then the worst misfit. The vetoes are world-thread state, so the candidates are read there.
        PopulationZoneValue? value = ctx.Input.ZoneValue;
        PopulationValueWeights weights = PopulationValueWeights.From(pop);
        // With value routing on, a level-capped bot in a zone it reaches within CapReach counts as fitting (the rule Rank uses), also while
        // the zone value is not built yet (after an area-table change it is dropped until rebuilt): bots a valued move placed there are
        // not out-levelled and mass-relocated meanwhile.
        bool valued = pop.ValueRouting && value is not null;
        bool Reaches(PopulationZone zone, PopulationBotFacts bot)
            => pop.ValueRouting && PopulationZoneAtlas.CapReaches(zone, bot.Level, bot.Team, rule, weights.CapReach);
        (PopulationBotFacts Bot, PopulationZone? Zone, bool Stuck)[] candidates = await world.InvokeAsync(() => ctx.Input.Running
            .Where(f => ctx.Members.ContainsKey(f.BotId) && f.Autonomous && !f.Busy && !f.Dead && !f.Vetoed)
            .Select(f => (Bot: f, Zone: atlas.Find(f.ZoneId)))
            .Where(x => x.Zone is not null || Excluded(x.Bot))
            .Select(x => (x.Bot, x.Zone, Stuck: x.Zone is null || StuckIn(x.Zone, x.Bot)))
            .OrderByDescending(x => x.Stuck).ThenByDescending(x => x.Zone is null || Reaches(x.Zone, x.Bot) ? 0 : Misfit(x.Zone, x.Bot, rule)).ThenBy(x => x.Bot.BotId)
            .ToArray(), ct).ConfigureAwait(false);
        // With the zone value on: a level-capped bot that fits where it stands and has stayed a while in a zone worth much less than the
        // best zone for it moves there. A few such bots per pass in turn (round robin by BotId, a cursor across passes), so the check
        // costs the same every pass. The bots that would otherwise take the "fine where it stands" branch are the ones checked.
        Func<uint, int> troubleOf = zone => _trouble.Count(zone, NowUnix());
        var lowValue = new Dictionary<Guid, double>(); // bot -> the least total a zone must have to be worth the move
        if (valued && pop.ValueChecksPerPass > 0 && pop.ValueRelocateBelowPct > 0)
        {
            long stayedSince = NowUnix() - pop.ValueRelocateMinStayMinutes * 60L;
            PopulationBotFacts[] eligible = [.. candidates
                .Where(c => !c.Stuck && c.Zone is not null && c.Bot.Level >= PlayerbotPopulationOptions.MaxLevel && !c.Bot.Stalled
                    && ctx.Members.TryGetValue(c.Bot.BotId, out PlayerbotPopulationMember? m) && m.UpdatedUnix <= stayedSince
                    && (c.Bot.Level <= c.Zone.MaxLevel + pop.OutlevelMargin || Reaches(c.Zone, c.Bot))
                    && !PopulationZoneAtlas.UnderLevelled(c.Zone, c.Bot.Level, c.Bot.Team, rule))
                .Select(c => c.Bot)
                .OrderBy(f => f.BotId)];
            int start = Array.FindIndex(eligible, f => f.BotId.CompareTo(_valueCursor) > 0);
            PopulationBotFacts[] checks = [.. Enumerable.Range(0, Math.Min(pop.ValueChecksPerPass, eligible.Length))
                .Select(i => eligible[((start < 0 ? 0 : start) + i) % eligible.Length])];
            if (checks.Length > 0)
            {
                (Guid BotId, bool Evaluated, double MinTotal)[] results = await world.InvokeAsync(
                    () => checks.Select(f => (f.BotId, Evaluated: IsLowValue(f, out double minTotal), MinTotal: minTotal)).ToArray(), ct).ConfigureAwait(false);
                // The cursor moves past the bots that were evaluated only (one in transit or gone is checked again next pass).
                for (int i = results.Length - 1; i >= 0; i--)
                    if (results[i].Evaluated) { _valueCursor = results[i].BotId; break; }
                foreach (var (botId, evaluated, minTotal) in results)
                    if (evaluated && !double.IsNaN(minTotal)) lowValue[botId] = minTotal;
            }
        }

        foreach ((PopulationBotFacts bot, PopulationZone? current, bool stuck) in candidates)
        {
            if (moved >= pop.MaxRelocationsPerInterval) break;
            PlayerbotPopulationMember member = ctx.Members[bot.BotId];
            bool lowValueMove = lowValue.TryGetValue(bot.BotId, out double lowValueMin);
            if (!stuck && !lowValueMove && current is not null && (bot.Level <= current.MaxLevel + pop.OutlevelMargin || Reaches(current, bot))
                && !PopulationZoneAtlas.UnderLevelled(current, bot.Level, bot.Team, rule))
            {
                // Fine where it stands (it may have travelled on by itself): only record the zone.
                if (member.ZoneId != current.ZoneId)
                {
                    member = member with { ZoneId = current.ZoneId, UpdatedUnix = NowUnix() };
                    await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
                    ctx.Members[bot.BotId] = member;
                }

                continue;
            }

            (PopulationSafeSelection? selected, bool inTransit) = await world.InvokeAsync(() =>
            {
                Player? player = ctx.Input.Managed.FindSession(bot.BotId)?.Player;
                if (player is null || PopulationProvisioner.IsInTransit(player)) return (null, true);
                Func<WorldSafeLoc, int, bool> safe = (location, level) => _provisioner.IsSafe(ctx.Input.Facts!, location, checked((byte)level), player.FactionTemplate);
                Func<uint, bool> vetoed = zone => _vetoes.IsVetoed(bot.BotId, zone, NowUnix());
                PopulationSafeSelection? picked = null;
                if (valued)
                    picked = atlas.PickValued(ProfileOf(player), rule, value!, weights, ctx.Assigned, _random, safe,
                        zone => zone == bot.ZoneId || vetoed(zone), troubleOf,
                        minTotal: lowValueMove ? lowValueMin : double.NegativeInfinity);
                // A bot that only wants a better zone stays put rather than going to a random fitting one.
                if (picked is null && !lowValueMove) picked = atlas.PickSafe(bot.Level, bot.Level, bot.Team, rule, ctx.Assigned, _random, safe, vetoed);
                return (picked, false);
            }, ct).ConfigureAwait(false);
            if (inTransit) continue;
            if (selected is null)
            {
                if (lowValueMove) continue;
                if (!loggedNoSafe) logger.LogInformation("Playerbot population: no safe zone for out-levelled bot {BotId} (level {Level}); it stays where it is", bot.BotId, bot.Level);
                loggedNoSafe = true;
                continue;
            }

            PopulationPlacement placement = selected.Placement;
            string? refusal = await world.InvokeAsync(() => _provisioner.Relocate(bot.BotId, placement.Location, ctx.Input.Facts!), ct).ConfigureAwait(false);
            if (refusal is "bot-in-transit" or PopulationProvisioner.StaleCode) continue; // stale: the next pass plans again on new data
            if (refusal is not null) { Fail($"move of {bot.BotId} refused: {refusal}"); continue; }
            moved++;
            member = member with { ZoneId = placement.Zone.ZoneId, UpdatedUnix = NowUnix() };
            await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
            ctx.Members[bot.BotId] = member;
            ctx.Assigned[placement.Zone.ZoneId] = ctx.Assigned.GetValueOrDefault(placement.Zone.ZoneId) + 1;
        }

        return moved;

        // World thread: evaluated is false when the bot cannot be judged now (gone, in transit, zone unknown). Otherwise minTotal is the total
        // a zone needs to be worth the move, or NaN when the bot's zone is not worth less than ValueRelocateBelowPct percent of the best
        // zone for it. A move for value only lands in a zone worth at least 100 / ValueRelocateBelowPct times the zone it leaves.
        bool IsLowValue(PopulationBotFacts bot, out double minTotal)
        {
            minTotal = double.NaN;
            if (pop.ValueRelocateBelowPct <= 0) return false;
            if (ctx.Input.Managed.FindSession(bot.BotId)?.Player is not { } player || PopulationProvisioner.IsInTransit(player)) return false;
            if (atlas.Find(bot.ZoneId) is not { } zone) return false;
            PopulationBotProfile profile = ProfileOf(player);
            IReadOnlyList<ZoneScore> ranking = atlas.Rank(profile, rule, value!, weights, ctx.Assigned,
                zoneId => zoneId == bot.ZoneId || _vetoes.IsVetoed(bot.BotId, zoneId, NowUnix()), troubleOf);
            if (ranking.Count == 0) return true;
            ZoneScore here = PopulationZoneScoring.Score(zone, value!.Find(zone.ZoneId), profile, weights, troubleOf(zone.ZoneId), ctx.Assigned.GetValueOrDefault(zone.ZoneId));
            if (here.Total < ranking[0].Total * pop.ValueRelocateBelowPct / 100.0)
                minTotal = here.Total * 100.0 / pop.ValueRelocateBelowPct;
            return true;
        }

        // A zone the operator excluded, on a map bots may be placed on (another map is left alone, like a capital).
        bool Excluded(PopulationBotFacts bot)
            => pop.ExcludedZones.Contains(bot.ZoneId) && _options.AllowedMaps.Contains(bot.MapId);

        // A stalled bot in a zone it escaped from, or in one over its capacity at the bot's level (the bot itself counts among the assigned).
        bool StuckIn(PopulationZone zone, PopulationBotFacts bot)
            => bot.Stalled && (_vetoes.IsVetoed(bot.BotId, zone.ZoneId, NowUnix())
                || atlas.CapOf(zone, bot.Team, bot.Level, bot.Level) is { } cap && ctx.Assigned.GetValueOrDefault(zone.ZoneId) > cap);

        // How far the bot is outside its zone's levels, above or below (0 or less when it fits).
        static int Misfit(PopulationZone zone, PopulationBotFacts bot, PopulationLevelRule rule)
            => Math.Max(bot.Level - zone.MaxLevel, zone.LowestLevelFor(bot.Team, rule) - bot.Level);
    }

    /// <summary>The status as chat lines (<c>.playerbot population status</c>).</summary>
    internal static IEnumerable<string> Describe(PopulationStatus s)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        yield return string.Create(c, $"Population: {(s.Enabled ? "on" : "off")}, state {s.State}: {s.Reason}.");
        if (!s.Enabled && s.Passes == 0) yield break;
        yield return string.Create(c, $"Target {s.Target} (effective {s.EffectiveTarget}), running {s.RunningAll} ({s.OwnedRunning} owned), parked {s.OwnedParked}, created in the last minute {s.CreatesLastMinute}; {s.OperatorRunning} operator bots running leave the population {s.OwnedTarget}.");
        yield return string.Create(c, $"Tick p95 {s.P95Ms:0.0} ms over {s.Samples} ticks, budget {s.BudgetMs} ms. Boost {s.Boost}. Zones: {s.AllianceZones} Alliance, {s.HordeZones} Horde.");
        if (s.FreshLaunch)
            yield return "Fresh launch: new bots stay level 1 at their race start (no boost, no teleport, no relocation); the bands are bypassed.";
        foreach (PopulationBandStatus band in s.Bands)
            yield return string.Create(c, $"  Levels {band.MinLevel}-{band.MaxLevel} (weight {band.Weight}): want {band.Want}, have {band.Have}.")
                + (band.Capacity is { } cap ? string.Create(c, $" Zone capacity {cap}{(cap < band.Want ? " (SHORT of want)" : string.Empty)}.") : string.Empty);
        if (s.LastError is not null) yield return "Last problem: " + s.LastError;
    }

    /// <summary>The rush as one chat line for <c>.playerbot population status</c>; none when no rush ever ran. <paramref name="p95Ms"/> is the tick p95 now.</summary>
    internal static IEnumerable<string> DescribeRush(PopulationRushStatus r, double p95Ms)
    {
        if (r.State == "idle") yield break;
        CultureInfo c = CultureInfo.InvariantCulture;
        yield return string.Create(c,
            $"Rush {r.State}: {r.Created} of {r.Requested} created ({r.Failed} failed), requested {r.PerSecond}/s, achieved {r.AchievedPerSecond:0.0}/s; {r.Reason}. Tick p95 {p95Ms:0.0} ms, brake at {r.BrakeP95Ms} ms ({r.BrakeWindowsSeen}/{r.BrakeWindows} windows).");
    }
}
