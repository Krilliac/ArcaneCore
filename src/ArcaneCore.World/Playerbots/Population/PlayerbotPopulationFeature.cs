using ArcaneCore.Kernel.Diagnostics;
using System.Globalization;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
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

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// One level band in <see cref="PopulationStatus"/>: <see cref="Want"/> is its weighted share of the population's own target (the
/// effective target less the operator bots running), <see cref="Have"/> the population's own bots running in it. An operator's bot
/// never counts in a band.
/// </summary>
public sealed record PopulationBandStatus(int MinLevel, int MaxLevel, int Weight, int Want, int Have);

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
public sealed class PlayerbotPopulationFeature(IServiceProvider services, ILogger<PlayerbotPopulationFeature> logger) : IWorldFeature
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
    private readonly PopulationBudgetWindows _windows = new();
    private readonly PopulationActionLog _actionLog = new();
    private WorldRuntime? _world;
    private long _clockMs;
    private long _sinceMs;
    private long _passes;
    // Pass-thread-only (one pass at a time): placements refused in a row after a bot was created, and the pass the pause began at.
    private int _placementRefusals;
    private long _pausedAtPass;
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

    public void Attach(WorldRuntime world)
    {
        _world = world;
        world.Updated += OnUpdated;
        world.TickCompleted += OnTickCompleted;
    }

    public async Task StopAsync()
    {
        _stopping = true;
        if (_world is { } world)
        {
            world.Updated -= OnUpdated;
            world.TickCompleted -= OnTickCompleted;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _pass.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { /* a pass cut short by the shutdown */ }
        catch (Exception ex) when (ex is not OutOfMemoryException) { logger.LogWarning(ex, "Playerbot population pass ended with an error at shutdown"); }
    }

    // World thread, once per tick: one ring write (WorldTickWindow.Add), no allocation.
    private void OnTickCompleted(long durationMicros, long bytes, TickPhases phases) => _window.Add(durationMicros);

    private void OnUpdated(uint elapsedMs)
    {
        _clockMs += elapsedMs;
        _sinceMs += elapsedMs;
        if (_stopping) return;
        // A fleet reset (.playerbot fleet reset) deletes every bot: the population stands still until it is done.
        if (Managed is { ResetInProgress: true })
        {
            StopRush("a fleet reset started");
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
        _rush.Pacer.Add(elapsedMs, _rush.PerSecond);
        if (!_pass.IsCompleted) return;
        int batch = _rush.Pacer.Take(Math.Min(_rush.Remaining, PopulationRush.MaxPerPass));
        if (batch > 0) StartPass(batch);
    }

    /// <summary>Run one pass now and wait for it (tests; the world's own timer starts passes the same way). A pass already running is awaited instead.</summary>
    internal async Task RunPassNowAsync(bool shedOnly = false)
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the population feature is not attached");
        Task pass = await world.InvokeAsync(() => _pass.IsCompleted ? StartPass(shedOnly: shedOnly) : _pass).ConfigureAwait(false);
        await pass.ConfigureAwait(false);
    }

    /// <summary>World thread: capture the facts and start the pass task.</summary>
    private Task StartPass(int rushCount = 0, bool shedOnly = false)
    {
        PassInput? input;
        try { input = Capture(rushCount, shedOnly); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Playerbot population could not capture the world ({Type}: {Message})", ex.GetType().Name, ex.Message);
            _lastError = "capture: " + ex.Message;
            Publish(_status with { Enabled = true, State = "error", Reason = "capture failed: " + ex.Message, LastError = _lastError });
            return _pass = Task.CompletedTask;
        }

        if (input is null) return _pass = Task.CompletedTask;
        return _pass = Task.Run(() => RunAsync(input));
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
        string? TerrainHold,
        bool TeleportAvailable,
        int RushCount = 0,
        bool ShedOnly = false);

    private PassInput? Capture(int rushCount = 0, bool shedOnly = false)
    {
        WorldRuntime world = _world!;
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
            bool stalled = managed.FindBrain(status.BotId)?.StallReport is not null;
            running.Add(new PopulationBotFacts(status.BotId, player.Level, player.Team, player.ZoneId, autonomous, busy,
                realZones.Contains(player.ZoneId), vetoes.Any(v => v.OwnsPlacement(status.BotId)), !player.IsAlive, sick, stalled));
        }

        IPopulationTickSource source = Source;
        int samples = source.Samples;
        bool boost = _provisioner.BoostAvailable(out string missing);
        return new PassInput(pop, pop.FreshLaunch ? FreshBands : pop.Bands, samples, source.TotalSamples, samples == 0 ? 0 : source.P95Ms(pop.WindowTicks), runningAll, running, states, atlas, boost, missing,
            ClockMs, managed, terrainHold, teleports is not null, rushCount, shedOnly);
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

        if (!sourcesChanged && (_located is null || _atlasLocated))
        {
            LogCoverage(pop);
            return _atlas;
        }

        PopulationLocated? located = _located;
        _atlas = PopulationZoneAtlas.Build(areas.All, graveyards, _options.AllowedMaps, new HashSet<uint>(pop.ExcludedZones),
            located is null ? null : l => located.Where.GetValueOrDefault(l.Id),
            located is null || located.Creatures.Count == 0 ? null : (l, t) => located.Creatures.GetValueOrDefault((l.Id, t)));
        _atlasLocated = located is not null;
        logger.LogInformation(
            "Playerbot population atlas{Pending}: {Zones} zones, {Locations} locations ({Alliance} zones open to the Alliance, {Horde} to the Horde); {Surveyed} graveyard/faction pairs with a creature survey level; {Dropped} graveyard links left out as standing in another zone{List}",
            located is null ? " (graveyard terrain lookup not in yet)" : string.Empty,
            _atlas.Zones.Count, _atlas.LocationCount, _atlas.CountFor(Team.Alliance), _atlas.CountFor(Team.Horde),
            _atlas.Zones.SelectMany(z => z.CreatureLevels.Keys).Distinct().Count(), _atlas.Dropped.Count,
            _atlas.Dropped.Count == 0 ? string.Empty
                : ": " + string.Join(", ", _atlas.Dropped.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.LocationId} (zone {d.ZoneId}, stands in {d.TerrainZoneId})"))));
        if (located is null && _locating is null) StartLocating(world, areas, pop);
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
        _ = Task.Run(() =>
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
                logger.LogWarning(ex, "Playerbot population: the graveyard terrain lookup or creature survey failed; the atlas keeps every graveyard link until the area table, graveyards, creatures or maps change");
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
            IPlayerbotPopulationStore store = scope.ServiceProvider.GetRequiredService<IPlayerbotPopulationStore>();
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

            if (ctx.Note is not null) actions += (actions.Length == 0 ? string.Empty : ", ") + "held back: " + ctx.Note;
            // A rush keeps the tick window: its brake reads the load the rush itself causes.
            if (changed > 0 && input.RushCount == 0)
            {
                await _world!.InvokeAsync(() => { Source.Reset(); return true; }, ct).ConfigureAwait(false);
                _windows.Reset();
            }

            ownedParked = members.Keys.Count(id => !ctx.RunningNow.Contains(id));
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested || _stopping || _world?.IsStopped == true)
        {
            logger.LogInformation("Playerbot population pass canceled during shutdown ({Type})", ex.GetType().Name);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Playerbot population pass failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            decision = decision with { State = "error", Reason = "the pass failed: " + ex.Message };
            if (input.RushCount > 0) _rush.Report(input.RushCount, 0, null, _lastError, ClockMs);
        }

        long now = input.ClockMs;
        while (_createTimes.Count > 0 && now - _createTimes.Peek() >= 60_000) _createTimes.Dequeue();
        PopulationBandStatus[] bands = [.. input.Bands.Select((b, i) => new PopulationBandStatus(b.MinLevel, b.MaxLevel, b.Weight, desired[i], have[i]))];
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
        _lastError = error;
        logger.LogWarning("Playerbot population: {Error}", error);
    }

    private async Task<(int Started, int Created)> AddAsync(PassContext ctx, int count, CancellationToken ct)
    {
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
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
            PlayerbotOperationResult result = await managed.StartAsync(member.BotId.ToString(), ct).ConfigureAwait(false);
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
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        int created = 0;
        bool fresh = pop.FreshLaunch;
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
            PopulationFreshPlan? freshPlan = null;
            if (fresh)
            {
                freshPlan = PopulationBandPlanner.PlanFresh(pop, ctx.Alliance, ctx.Horde, cache, _random);
                if (freshPlan is null) return (created, quietWhenNothingFits ? null : "no valid race and class for the configured factions and classes", false);
            }
            else
            {
                plan = await _world!.InvokeAsync(() => PopulationBandPlanner.PlanSafe(pop, ctx.Desired, planned,
                    ctx.Alliance, ctx.Horde, cache, ctx.Input.Atlas, ctx.Assigned, ctx.Input.BoostAvailable, _random,
                    (location, level, faction) => _provisioner.IsSafe(_world!, location, level, faction)), ct).ConfigureAwait(false);
                if (plan is null)
                    return (created, quietWhenNothingFits ? null : "no safe zone, level and race-class placement is available (see the zone counts and boost line)", false);
            }

            (Team planTeam, byte planRace, byte planClass, int planBand) = fresh
                ? (freshPlan!.Team, freshPlan.Race, freshPlan.Class, 0)
                : (plan!.Team, plan.Race, plan.Class, plan.Band);

            PlayerbotOperationResult? made = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                made = await managed.CreateAsync(_names.Next(), planRace, planClass, ct).ConfigureAwait(false);
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

            PlayerbotOperationResult start = await managed.StartAsync(botId.ToString(), ct).ConfigureAwait(false);
            if (!start.Success) { Fail($"start of new bot {made.Name} refused: {start.Code}"); return (created, null, true); }
            ctx.RunningNow.Add(botId);
            _startedAt[botId] = ctx.Input.ClockMs;

            // Fresh launch: no boost and no teleport; the bot stands where CMSG_CHAR_CREATE put it. Its zone is recorded as it is.
            string? refusal = fresh ? null
                : await _world!.InvokeAsync(() => _provisioner.Provision(botId, plan!.Level, plan.Placement.Location), ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                // The plan was safe when it was made (a content reload can change that before the teleport). A level-1 bot that
                // cannot be placed is deleted rather than left in its race's starting area, where it would hold a registry slot and
                // count towards a band; the next pass plans again.
                Fail($"placement of {made.Name} refused: {refusal}");
                string? paused = null;
                if (++_placementRefusals >= MaxPlacementRefusals)
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

            uint zone = fresh
                ? await _world!.InvokeAsync(() => managed.FindSession(botId)?.Player?.ZoneId ?? 0u, ct).ConfigureAwait(false)
                : plan!.Placement.Zone.ZoneId;
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
    /// Delete the bot of a pending-deletion member (character, then registry row) and only then the member row. Any failure, a
    /// cancellation included, leaves the marked row for the next pass. True when the member is gone.
    /// </summary>
    private async Task<bool> TryDeletePendingAsync(ManagedPlayerbotFeature managed, IPlayerbotPopulationStore store, Guid botId, string? name, CancellationToken ct)
    {
        try
        {
            // Not cancellable: a stop that a shutdown interrupts would leave a desired-enabled bot for the next start to restore.
            await managed.StopAsync(botId.ToString(), CancellationToken.None).ConfigureAwait(false);
            PlayerbotOperationResult deleted = await managed.DeleteAsync(botId.ToString(), ct).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Backoff(botId);
            logger.LogFault("playerbot-population", ex, "Playerbot population: deleting the unplaced bot {Bot} failed", name ?? botId.ToString());
            Fail($"delete of unplaced bot {name ?? botId.ToString()} failed ({ex.GetType().Name}); retried later");
            return false;
        }
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
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        IReadOnlyList<PopulationParkPick> order = PopulationParking.Order(ctx.Input.Running, ctx.Members.Keys.ToHashSet(), ctx.Input.Bands, ctx.Desired,
            ctx.Have, _startedAt, ctx.Input.ClockMs, ctx.Input.Pop.MinRunSeconds * 1000L, keepRecent);
        var parked = new List<string>();
        foreach (PopulationParkPick pick in order)
        {
            if (parked.Count == count) break;
            PopulationBotFacts bot = pick.Bot;
            // The facts are seconds old: look again, on the world thread, right before stopping.
            bool stillParkable = await _world!.InvokeAsync(() => IsParkableNow(managed, bot.BotId), ct).ConfigureAwait(false);
            if (!stillParkable) continue;
            PlayerbotOperationResult result = await managed.StopAsync(bot.BotId.ToString(), ct).ConfigureAwait(false);
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
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        PopulationZoneAtlas atlas = ctx.Input.Atlas;
        PopulationLevelRule rule = PopulationLevelRule.From(pop);
        int moved = 0;
        bool loggedNoSafe = false;
        // Only a bot standing in a zone the atlas knows can be out-levelled. A zone outside the atlas (a capital or city, an excluded zone,
        // another map) is somewhere the bot went on purpose (town, trainer, travel goals): it is left where it is. A bot below every place
        // of its zone it could have been put (UnderLevelled: a low-level bot placed by an older build in Duskwood at Raven Hill, or one that
        // walked over a border into a harder zone) is moved the same way. The worst misfit first.
        foreach (PopulationBotFacts bot in ctx.Input.Running.Where(f => ctx.Members.ContainsKey(f.BotId) && f.Autonomous && !f.Busy && !f.Dead && !f.Vetoed
                && atlas.Find(f.ZoneId) is not null)
            .OrderByDescending(f => Misfit(atlas.Find(f.ZoneId)!, f, rule)).ThenBy(f => f.BotId))
        {
            if (moved >= pop.MaxRelocationsPerInterval) break;
            PlayerbotPopulationMember member = ctx.Members[bot.BotId];
            PopulationZone current = atlas.Find(bot.ZoneId)!;
            if (bot.Level <= current.MaxLevel + pop.OutlevelMargin && !PopulationZoneAtlas.UnderLevelled(current, bot.Level, bot.Team, rule))
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

            PopulationSafeSelection? selected = await _world!.InvokeAsync(() =>
            {
                Player? player = ctx.Input.Managed.FindSession(bot.BotId)?.Player;
                return player is null ? null : atlas.PickSafe(bot.Level, bot.Level, bot.Team, rule, ctx.Assigned, _random,
                    (location, level) => _provisioner.IsSafe(_world!, location, checked((byte)level), player.FactionTemplate));
            }, ct).ConfigureAwait(false);
            if (selected is null)
            {
                if (!loggedNoSafe) logger.LogInformation("Playerbot population: no safe zone for out-levelled bot {BotId} (level {Level}); it stays where it is", bot.BotId, bot.Level);
                loggedNoSafe = true;
                continue;
            }

            PopulationPlacement placement = selected.Placement;
            string? refusal = await _world!.InvokeAsync(() => _provisioner.Relocate(bot.BotId, placement.Location), ct).ConfigureAwait(false);
            if (refusal is not null) { Fail($"move of {bot.BotId} refused: {refusal}"); continue; }
            moved++;
            member = member with { ZoneId = placement.Zone.ZoneId, UpdatedUnix = NowUnix() };
            await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
            ctx.Members[bot.BotId] = member;
            ctx.Assigned[placement.Zone.ZoneId] = ctx.Assigned.GetValueOrDefault(placement.Zone.ZoneId) + 1;
        }

        return moved;

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
            yield return string.Create(c, $"  Levels {band.MinLevel}-{band.MaxLevel} (weight {band.Weight}): want {band.Want}, have {band.Have}.");
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
