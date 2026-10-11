using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
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
using ArcaneCore.World.Teleport;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>One level band in <see cref="PopulationStatus"/>: the bots wanted and the managed bots running in it.</summary>
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
    long Passes)
{
    public static PopulationStatus Idle(bool enabled, string state, string reason) => new(enabled, state, reason, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        "unknown", 0, 0, [], null, 0);
}

/// <summary>The world-thread facts about one running managed bot that a pass decides on.</summary>
internal sealed record PopulationBotFacts(Guid BotId, byte Level, Team Team, uint ZoneId, bool Autonomous, bool Busy, bool PlayerInZone, bool Vetoed);

/// <summary>Which running bots may be parked, and in which order.</summary>
internal static class PopulationParking
{
    /// <summary>
    /// The bots to park, best first. Only bots the population owns that are autonomous (not scripted, in a real player's party or in a
    /// bot-led group) and idle (alive, out of combat, not teleporting) qualify. Order: the band with the largest surplus first (a bot in
    /// no band counts as the largest), then a bot with no real player in its zone, then the most recently started one. The surplus is
    /// recounted after each pick, so a band is not drained below its share while another still has a surplus.
    /// </summary>
    public static IReadOnlyList<PopulationBotFacts> Order(IReadOnlyCollection<PopulationBotFacts> running, IReadOnlySet<Guid> owned,
        IReadOnlyList<PopulationBand> bands, int[] desired, int[] have, IReadOnlyDictionary<Guid, long> startedAt)
    {
        var remaining = running.Where(f => owned.Contains(f.BotId) && f.Autonomous && !f.Busy).ToList();
        var counts = (int[])have.Clone();
        var ordered = new List<PopulationBotFacts>(remaining.Count);
        while (remaining.Count > 0)
        {
            PopulationBotFacts best = remaining
                .OrderByDescending(f => Surplus(f, bands, desired, counts))
                .ThenBy(f => f.PlayerInZone)
                .ThenByDescending(f => startedAt.GetValueOrDefault(f.BotId))
                .ThenBy(f => f.BotId)
                .First();
            remaining.Remove(best);
            ordered.Add(best);
            int band = PopulationBandPlanner.BandOf(bands, best.Level);
            if (band >= 0) counts[band]--;
        }

        return ordered;
    }

    private static int Surplus(PopulationBotFacts bot, IReadOnlyList<PopulationBand> bands, int[] desired, int[] counts)
    {
        int band = PopulationBandPlanner.BandOf(bands, bot.Level);
        return band < 0 ? int.MaxValue : counts[band] - desired[band];
    }
}

/// <summary>
/// The population manager (<c>World:Playerbots:Population</c>; docs/areas/playbots-population.md): keeps <c>TargetOnline</c> managed bots
/// running, spread over the level bands and over the zones that fit their levels, as long as the world's tick p95 allows.
/// <para>
/// Every <c>IntervalSeconds</c> of world time the world thread captures the facts of every running bot and the tick p95 (a pass), and one
/// task decides and acts: it never waits on the world thread, and the world thread never waits on it. Starts, creations, parks and
/// moves go through <see cref="IPlayerbotService"/> and the world thread; the tick window is reset after any of them, so the next decision
/// sees only ticks measured after the change. Bots it created are recorded in <c>playerbot_population_member</c>; only those are ever
/// parked or relocated. A bot an operator created counts towards the target and is never stopped here.
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
    private readonly Queue<long> _createTimes = new();
    private readonly Dictionary<Guid, long> _startedAt = [];
    private WorldRuntime? _world;
    private long _clockMs;
    private long _sinceMs;
    private long _passes;
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
    private string _coverageKey = string.Empty;
    private object? _locating;
    private bool _locateRunning;
    private int _locateHeldPasses;
    private CancellationTokenSource? _locateCts;

    /// <summary>The most passes creation and relocation wait for a running graveyard terrain lookup before going ahead on the fallback atlas.</summary>
    internal const int MaxTerrainHoldPasses = 6;
    private string? _lastError;

    private ManagedPlayerbotFeature? Managed => services.GetService<ManagedPlayerbotFeature>();

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
        PlayerbotPopulationOptions pop = _options.Population;
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

    /// <summary>Run one pass now and wait for it (tests; the world's own timer starts passes the same way). A pass already running is awaited instead.</summary>
    internal async Task RunPassNowAsync()
    {
        WorldRuntime world = _world ?? throw new InvalidOperationException("the population feature is not attached");
        Task pass = await world.InvokeAsync(() => _pass.IsCompleted ? StartPass() : _pass).ConfigureAwait(false);
        await pass.ConfigureAwait(false);
    }

    /// <summary>World thread: capture the facts and start the pass task.</summary>
    private Task StartPass()
    {
        PassInput? input;
        try { input = Capture(); }
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

    private void Publish(PopulationStatus status) => Volatile.Write(ref _status, status);

    private sealed record PassInput(
        PlayerbotPopulationOptions Pop,
        PopulationBand[] Bands,
        int Samples,
        double P95Ms,
        int RunningAll,
        IReadOnlyList<PopulationBotFacts> Running,
        IReadOnlyDictionary<Guid, ManagedPlayerbotState> States,
        PopulationZoneAtlas Atlas,
        bool BoostAvailable,
        string BoostMissing,
        long ClockMs,
        ManagedPlayerbotFeature Managed,
        string? TerrainHold);

    private PassInput? Capture()
    {
        WorldRuntime world = _world!;
        ManagedPlayerbotFeature? managed = Managed;
        if (managed is null)
        {
            Publish(PopulationStatus.Idle(true, "unavailable", "the managed playerbot feature is not available"));
            return null;
        }

        PlayerbotPopulationOptions pop = _options.Population;
        PopulationZoneAtlas atlas = AtlasFor(world, pop);
        string? terrainHold = TerrainHold();
        var realZones = new HashSet<uint>();
        foreach (Player online in world.OnlinePlayers)
            if (online.Session is not WorldSession { IsManaged: true }) realZones.Add(online.ZoneId);

        PlayerbotGroupCoordinator? coordinator = services.GetService<PlayerbotGroupCoordinator>();
        TeleportService? teleports = services.GetService<TeleportFeature>()?.Teleports;
        IPopulationPlacementVeto[] vetoes = [.. services.GetServices<IPopulationPlacementVeto>()];
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
            bool busy = !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat || teleports?.IsBeingTeleported(player) == true;
            running.Add(new PopulationBotFacts(status.BotId, player.Level, player.Team, player.ZoneId, autonomous, busy,
                realZones.Contains(player.ZoneId), vetoes.Any(v => v.OwnsPlacement(status.BotId))));
        }

        IPopulationTickSource source = Source;
        int samples = source.Samples;
        bool boost = _provisioner.BoostAvailable(out string missing);
        return new PassInput(pop, pop.Bands, samples, samples == 0 ? 0 : source.P95Ms(pop.WindowTicks), runningAll, running, states, atlas, boost, missing,
            ClockMs, managed, terrainHold);
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
        string key = string.Join(',', _options.AllowedMaps) + "|" + string.Join(',', pop.ExcludedZones) + "|" + pop.CreatureSurveyRadius;
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
        _coverageKey = string.Empty;
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
        Team[] teams = pop.Factions switch
        {
            PopulationFactions.Alliance => [Team.Alliance],
            PopulationFactions.Horde => [Team.Horde],
            _ => [Team.Alliance, Team.Horde],
        };
        string key = string.Create(CultureInfo.InvariantCulture,
            $"{RuntimeHelpers.GetHashCode(_atlas)}|{string.Join(',', pop.Bands.Select(b => b.ToString()))}|{rule}|{pop.Factions}");
        if (key == _coverageKey) return;
        _coverageKey = key;
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
        int[] desired = PopulationBandPlanner.DesiredPerBand(effectiveTarget, input.Bands);
        int[] have = new int[input.Bands.Length];
        foreach (PopulationBotFacts bot in input.Running)
            if (PopulationBandPlanner.BandOf(input.Bands, bot.Level) is var band and >= 0) have[band]++;

        PopulationDecision decision = new(PopulationAction.Hold, 0, "error", "the pass did not complete");
        int ownedRunning = 0, ownedParked = 0;
        string actions = string.Empty;
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

            ownedRunning = input.Running.Count(f => members.ContainsKey(f.BotId));
            ownedParked = members.Keys.Count(id => !input.States.TryGetValue(id, out ManagedPlayerbotState s) || s != ManagedPlayerbotState.Running);
            decision = PopulationBudgetController.Decide(input.Samples, input.P95Ms, input.RunningAll, ownedRunning, effectiveTarget, pop);

            var ctx = new PassContext(input, scope.ServiceProvider, store, known, members, desired, have, effectiveTarget);
            int changed = 0;
            switch (decision.Action)
            {
                case PopulationAction.Add:
                    (int started, int created) = await AddAsync(ctx, decision.Count, ct).ConfigureAwait(false);
                    changed += started + created;
                    actions = $"started {started}, created {created}";
                    break;
                case PopulationAction.Park:
                    int parked = await ParkAsync(ctx, decision.Count, ct).ConfigureAwait(false);
                    changed += parked;
                    actions = $"parked {parked}";
                    break;
            }

            bool relocate = decision.State is "under-budget" or "at-target" or "hysteresis" && pop.Relocation == PopulationRelocation.Teleport
                && pop.MaxRelocationsPerInterval > 0;
            if (relocate && input.TerrainHold is { } relocationHold) ctx.Note ??= relocationHold; // no relocation on the fallback atlas either
            else if (relocate)
            {
                int moved = await RelocateAsync(ctx, ct).ConfigureAwait(false);
                changed += moved;
                if (moved > 0) actions += (actions.Length == 0 ? string.Empty : ", ") + $"moved {moved}";
            }

            if (ctx.Note is not null) actions += (actions.Length == 0 ? string.Empty : ", ") + "held back: " + ctx.Note;
            if (changed > 0) await _world!.InvokeAsync(() => { Source.Reset(); return true; }, ct).ConfigureAwait(false);
            ownedParked = members.Keys.Count(id => !ctx.RunningNow.Contains(id));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Playerbot population pass failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            decision = decision with { State = "error", Reason = "the pass failed: " + ex.Message };
        }

        long now = input.ClockMs;
        while (_createTimes.Count > 0 && now - _createTimes.Peek() >= 60_000) _createTimes.Dequeue();
        Publish(new PopulationStatus(true, decision.State, actions.Length == 0 ? decision.Reason : decision.Reason + "; " + actions,
            input.P95Ms, input.Samples, pop.TickP95BudgetMs, pop.TargetOnline, effectiveTarget, input.RunningAll, ownedRunning, ownedParked,
            _createTimes.Count, input.BoostAvailable ? "available" : "missing: " + input.BoostMissing,
            input.Atlas.CountFor(Team.Alliance), input.Atlas.CountFor(Team.Horde),
            [.. input.Bands.Select((b, i) => new PopulationBandStatus(b.MinLevel, b.MaxLevel, b.Weight, desired[i], have[i]))],
            _lastError, Interlocked.Increment(ref _passes)));
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
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        int remaining = count, started = 0, created = 0;
        string? stoppedBecause = null;

        // 1. Bots the population owns that are stopped come back first, those in a band with a deficit before the others. A faulted bot
        //    (quarantined or disabled) is not started here: an operator start would clear its fault history, and the manager retries a
        //    quarantined bot itself.
        ICharacterStore characters = ctx.Scoped.GetRequiredService<ICharacterStore>();
        var restart = new List<(PlayerbotPopulationMember Member, int Band, byte Level)>();
        //    The startable, not-running members are chosen first and only then capped (oldest first), so a registry of more than 128
        //    owned bots never hides a parked bot behind running or faulted ones.
        foreach (PlayerbotPopulationMember member in ctx.Members.Values
            .Where(m => !ctx.RunningNow.Contains(m.BotId) && IsStartable(ctx, m))
            .OrderBy(m => m.UpdatedUnix).ThenBy(m => m.BotId).Take(128))
        {
            CharacterRecord? character = await characters.GetByIdAsync(member.CharacterId, ct).ConfigureAwait(false);
            if (character is null) continue;
            restart.Add((member, PopulationBandPlanner.BandOf(ctx.Input.Bands, character.Level), character.Level));
        }

        int[] deficit = PopulationBandPlanner.Deficits(ctx.Desired, ctx.Have);
        int failures = 0;
        foreach ((PlayerbotPopulationMember member, int band, _) in restart.OrderByDescending(r => r.Band >= 0 ? deficit[r.Band] : -1))
        {
            if (remaining == 0 || failures >= 3) break;
            PlayerbotOperationResult result = await managed.StartAsync(member.BotId.ToString(), ct).ConfigureAwait(false);
            if (result.Success)
            {
                started++;
                remaining--;
                ctx.RunningNow.Add(member.BotId);
                _startedAt[member.BotId] = ctx.Input.ClockMs;
                if (band >= 0) ctx.Have[band]++;
            }
            else if (result.Code == "playerbot-capacity") { stoppedBecause = "the running bot cap (MaxBots) is reached"; break; }
            else
            {
                failures++;
                Fail($"start of {member.BotId} refused: {result.Code}");
            }
        }

        // 2. New bots, within the rate limit, the registry and the running cap.
        if (ctx.Input.TerrainHold is { } hold && remaining > 0) stoppedBecause = hold;
        while (remaining > 0 && stoppedBecause is null)
        {
            if (pop.MaxCreatePerMinute == 0) break;
            while (_createTimes.Count > 0 && ctx.Input.ClockMs - _createTimes.Peek() >= 60_000) _createTimes.Dequeue();
            if (_createTimes.Count >= pop.MaxCreatePerMinute) { stoppedBecause = $"creation is limited to {pop.MaxCreatePerMinute} per minute"; break; }
            if (ctx.Known.Count + created >= _options.MaxRegisteredBots) { stoppedBecause = "the bot registry is full (MaxRegisteredBots)"; break; }
            if (ctx.Input.RunningAll + started + created >= _options.MaxBots) { stoppedBecause = "the running bot cap (MaxBots) is reached"; break; }

            PopulationRaceClassCache cache = await PairsFor(ctx.Scoped, pop, ct).ConfigureAwait(false);
            PopulationPlan? plan = PopulationBandPlanner.Plan(pop, ctx.Desired, ctx.Have, ctx.Alliance, ctx.Horde, cache, ctx.Input.Atlas, ctx.Assigned,
                ctx.Input.BoostAvailable, _random);
            if (plan is null) { stoppedBecause = "no band has a zone and a race and class to place a new bot (see the zone counts and boost line)"; break; }

            PlayerbotOperationResult? made = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                made = await managed.CreateAsync(_names.Next(), plan.Race, plan.Class, ct).ConfigureAwait(false);
                if (made.Success || !(made.Code == "name-in-use" || made.Code.StartsWith("character-create-refused", StringComparison.Ordinal))) break;
            }

            if (made is not { Success: true, BotId: { } botId })
            {
                Fail($"creation refused: {made?.Code}");
                break;
            }

            created++;
            remaining--;
            _createTimes.Enqueue(ctx.Input.ClockMs);
            ManagedPlayerbot? record = await ctx.Scoped.GetRequiredService<IManagedPlayerbotStore>().FindAsync(botId, ct).ConfigureAwait(false);
            if (record is null) { Fail($"created bot {botId} is not in the registry"); break; }
            long now = NowUnix();
            var member = new PlayerbotPopulationMember(botId, record.CharacterId, 0, (byte)plan.Band, now, now);
            await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
            ctx.Members[botId] = member;

            PlayerbotOperationResult start = await managed.StartAsync(botId.ToString(), ct).ConfigureAwait(false);
            if (!start.Success) { Fail($"start of new bot {made.Name} refused: {start.Code}"); break; }
            ctx.RunningNow.Add(botId);
            _startedAt[botId] = ctx.Input.ClockMs;

            string? refusal = await _world!.InvokeAsync(() => _provisioner.Provision(botId, plan.Level, plan.Placement.Location), ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                // The bot stays owned and stopped; the next pass restarts it where it stands (its race's starting area).
                Fail($"placement of {made.Name} refused: {refusal}");
                await managed.StopAsync(botId.ToString(), ct).ConfigureAwait(false);
                ctx.RunningNow.Remove(botId);
                break;
            }

            member = member with { ZoneId = plan.Placement.Zone.ZoneId, UpdatedUnix = NowUnix() };
            await ctx.Store.UpsertAsync(member, ct).ConfigureAwait(false);
            ctx.Members[botId] = member;
            ctx.Have[plan.Band]++;
            ctx.Assigned[plan.Placement.Zone.ZoneId] = ctx.Assigned.GetValueOrDefault(plan.Placement.Zone.ZoneId) + 1;
            if (plan.Team == Team.Alliance) ctx.Alliance++; else ctx.Horde++;
        }

        ctx.Note = stoppedBecause;
        return (started, created);
    }

    private static bool IsStartable(PassContext ctx, PlayerbotPopulationMember member)
    {
        if (ctx.Input.States.TryGetValue(member.BotId, out ManagedPlayerbotState state)) return state == ManagedPlayerbotState.Stopped;
        return ctx.Known[member.BotId].State != ManagedPlayerbotState.Faulted;
    }

    private async Task<int> ParkAsync(PassContext ctx, int count, CancellationToken ct)
    {
        ManagedPlayerbotFeature managed = ctx.Input.Managed;
        IReadOnlyList<PopulationBotFacts> order = PopulationParking.Order(ctx.Input.Running, ctx.Members.Keys.ToHashSet(), ctx.Input.Bands, ctx.Desired,
            ctx.Have, _startedAt);
        int parked = 0;
        foreach (PopulationBotFacts bot in order)
        {
            if (parked == count) break;
            // The facts are seconds old: look again, on the world thread, right before stopping.
            bool stillParkable = await _world!.InvokeAsync(() => IsParkableNow(managed, bot.BotId), ct).ConfigureAwait(false);
            if (!stillParkable) continue;
            PlayerbotOperationResult result = await managed.StopAsync(bot.BotId.ToString(), ct).ConfigureAwait(false);
            if (!result.Success) { Fail($"park of {bot.BotId} refused: {result.Code}"); continue; }
            parked++;
            ctx.RunningNow.Remove(bot.BotId);
            int band = PopulationBandPlanner.BandOf(ctx.Input.Bands, bot.Level);
            if (band >= 0) ctx.Have[band]--;
        }

        return parked;
    }

    private bool IsParkableNow(ManagedPlayerbotFeature managed, Guid botId)
    {
        if (managed.FindSession(botId)?.Player is not { IsInWorld: true } player) return false;
        if (managed.IsPartyDriven(botId) || managed.IsScripted(botId) || services.GetService<PlayerbotGroupCoordinator>()?.Drives(botId) == true) return false;
        return player.IsAlive && !player.Combat.IsInCombat && services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) != true;
    }

    private async Task<int> RelocateAsync(PassContext ctx, CancellationToken ct)
    {
        PlayerbotPopulationOptions pop = ctx.Input.Pop;
        PopulationZoneAtlas atlas = ctx.Input.Atlas;
        PopulationLevelRule rule = PopulationLevelRule.From(pop);
        int moved = 0;
        // Only a bot standing in a zone the atlas knows can be out-levelled. A zone outside the atlas (a capital or city, an excluded zone,
        // another map) is somewhere the bot went on purpose (town, trainer, travel goals): it is left where it is. A bot below every place
        // of its zone it could have been put (UnderLevelled: a low-level bot placed by an older build in Duskwood at Raven Hill, or one that
        // walked over a border into a harder zone) is moved the same way. The worst misfit first.
        foreach (PopulationBotFacts bot in ctx.Input.Running.Where(f => ctx.Members.ContainsKey(f.BotId) && f.Autonomous && !f.Busy && !f.Vetoed
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

            PopulationPlacement? placement = atlas.Pick(bot.Level, bot.Level, bot.Team, rule, ctx.Assigned, _random);
            if (placement is null) continue;
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
        yield return string.Create(c, $"Target {s.Target} (effective {s.EffectiveTarget}), running {s.RunningAll} ({s.OwnedRunning} owned), parked {s.OwnedParked}, created in the last minute {s.CreatesLastMinute}.");
        yield return string.Create(c, $"Tick p95 {s.P95Ms:0.0} ms over {s.Samples} ticks, budget {s.BudgetMs} ms. Boost {s.Boost}. Zones: {s.AllianceZones} Alliance, {s.HordeZones} Horde.");
        foreach (PopulationBandStatus band in s.Bands)
            yield return string.Create(c, $"  Levels {band.MinLevel}-{band.MaxLevel} (weight {band.Weight}): want {band.Want}, have {band.Have}.");
        if (s.LastError is not null) yield return "Last problem: " + s.LastError;
    }
}
