using ArcaneCore.Data.World.WorldState;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.World.Features;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.EventScenarios;

/// <summary>The intervals in use (tests shorten them).</summary>
internal sealed class EventScenarioTimings
{
    /// <summary>How often the scenarios are reconciled, in milliseconds of world time.</summary>
    public uint TickIntervalMs { get; set; } = 2_000;
}

/// <summary>
/// Bot scenarios on game events (docs/areas/playbots-scenarios-events.md): a game event that has rows in <c>game_event_playerbot_*</c>
/// brings its bots up when it starts (claim or create the bots, boost, place, give them a defend post or send them on a siege) and
/// takes the posts and the siege down when it stops. The work is <see cref="EventScenarioEngine"/>'s; this feature loads and validates the
/// rows, listens to <see cref="GameEventFeature"/>, and ticks the engine on the world thread.
/// <para>
/// Lifecycle (hot-reloadable pack, docs/design/hot-reload-tiers.md): <see cref="DetachKind.Stateless"/>. The game-event listener and the
/// tick go through the subscription bag, so a detach removes both and a re-attach leaves exactly one listener; claims are memory only and
/// are rebuilt from the tables and the running events, so a bot already placed is recognised by where it stands. With no valid rows the
/// feature subscribes to nothing.
/// </para>
/// Thread affinity: attach, detach, the listener, the tick and the commands run on the world thread; <see cref="QuiesceAsync"/> does not.
/// </summary>
public sealed class PlayerbotEventScenarioFeature(IServiceProvider services, ILogger<PlayerbotEventScenarioFeature> logger) : IDetachableWorldFeature
{
    private WorldRuntime? _world;
    private FeatureSubscriptions? _subscriptions;
    private GameEventFeature? _events;
    private EventScenarioEngine? _engine;
    private ManagedEventScenarioHost? _host;
    private GameEventPlayerbotContent _content = GameEventPlayerbotContent.Empty;
    private EventScenarioCatalog _catalog = EventScenarioCatalog.Empty;
    private uint _sinceMs;
    private int _faults;
    private volatile bool _quiesced;

    /// <summary>The intervals in use.</summary>
    internal EventScenarioTimings Timings { get; } = new();

    /// <summary>The engine once the first tick built it, null before and after a detach (tests).</summary>
    internal EventScenarioEngine? Engine => _engine;

    /// <summary>The validated scenarios, empty until the first tick built them (tests).</summary>
    internal EventScenarioCatalog Catalog => _catalog;

    /// <summary>Reconcile passes run so far (tests wait on it).</summary>
    internal int Passes { get; private set; }

    /// <summary>Whether this feature is listening to game events.</summary>
    internal bool Listening => _subscriptions is { IsDisposed: false, Count: > 0 } && _events is not null;

    /// <summary>Test seam: replaces the table read (rows from a fixture instead of the world database).</summary>
    internal Func<GameEventPlayerbotContent>? ContentSource { get; set; }

    /// <summary>Test seam: replaces the real host (a fake world).</summary>
    internal Func<IEventScenarioHost?>? HostFactory { get; set; }

    public DetachKind DetachKind => DetachKind.Stateless;

    public void Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(nameof(PlayerbotEventScenarioFeature)));

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(subscriptions);
        _world = world;
        _subscriptions = subscriptions;
        _quiesced = false;
        _sinceMs = 0;
        _engine = null;
        _host = null;
        _catalog = EventScenarioCatalog.Empty;
        if (services.GetService<IOptions<PlayerbotOptions>>()?.Value is not { Enabled: true }) return;
        _content = LoadContent();
        if (_content.Groups.Count == 0) return; // no rows: nothing to listen to and no tick
        subscriptions.OnUpdated(world, Update);
        if (services.GetService<GameEventFeature>() is not { } events)
        {
            logger.LogWarning("Bot event scenarios: there is no game event feature, so no scenario can start");
            return;
        }

        _events = events;
        subscriptions.Add(events.AddListener(new Listener(this)));
    }

    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        _quiesced = true;
        if (_host is { } host) await host.IdleAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Detach(FeatureHandover handover) => Release();

    public Task StopAsync()
    {
        try
        {
            _subscriptions?.Dispose(); // the feature is done: its listener and tick come off the world
        }
        finally
        {
            Release();
        }

        return Task.CompletedTask;
    }

    /// <summary>Forget the world. The subscription bag removes the listener and the tick; bots, posts and a siege are left as they are.</summary>
    private void Release()
    {
        _engine?.Dispose();
        _engine = null;
        _host = null;
        _events = null;
        _subscriptions = null;
        _world = null;
        _catalog = EventScenarioCatalog.Empty;
    }

    /// <summary>Read the four tables (blocking, like the other content loads of an attach). A read that fails is an empty scenario set, logged.</summary>
    private GameEventPlayerbotContent LoadContent()
    {
        if (ContentSource is { } source) return source();
        if (services.GetService<IServiceScopeFactory>() is not { } scopes) return GameEventPlayerbotContent.Empty;
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IGameEventPlayerbotStore>() is not { } store) return GameEventPlayerbotContent.Empty;
            return store.LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.event-scenarios", ex);
            AnomalyLedger.Shared.Report("bot-event", "scenario-fault", "event-scenarios", $"{ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
            logger.LogWarning(ex, "Bot event scenarios: the game_event_playerbot tables could not be read ({Type}: {Message}); no scenario will run", ex.GetType().Name, ex.Message);
            return GameEventPlayerbotContent.Empty;
        }
    }

    private sealed class Listener(PlayerbotEventScenarioFeature owner) : IGameEventListener
    {
        public void OnEventChanged(ushort eventId, bool active, bool resume) => owner._engine?.EventChanged(eventId, active);
    }

    // --- the tick ----------------------------------------------------------------------------------------------------------

    private void Update(uint elapsedMs)
    {
        if (_world is null || _quiesced) return;
        _sinceMs += elapsedMs;
        if (_sinceMs < Timings.TickIntervalMs) return;
        _sinceMs = 0;
        try
        {
            if (_engine is null && !BuildEngine()) return;
            _engine!.Tick();
            Passes++;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A scenario is never worth the world thread: log (the first few times in full) and try again at the next pass.
            FaultLedger.Shared.Record("playerbot.event-scenarios", error);
            AnomalyLedger.Shared.Report("bot-event", "scenario-fault", "event-scenarios", $"{error.GetType().Name}: {error.Message}", origin: _world?.Identity);
            if (_faults++ < 5) logger.LogWarning(error, "Bot event scenario pass failed ({Type}: {Message})", error.GetType().Name, error.Message);
        }
    }

    /// <summary>
    /// First tick after an attach: every feature is attached now, so the events and the bot service exist. Validate the rows against
    /// <c>game_event</c>, build the engine, and hand it the events that are already running (startup resume, a re-attach).
    /// </summary>
    private bool BuildEngine()
    {
        if (_world is not { } world) return false;
        IEventScenarioHost? host = HostFactory is { } factory ? factory()
            : services.GetService<ManagedPlayerbotFeature>() is { } bots ? _host = new ManagedEventScenarioHost(services, world, bots, logger) : null;
        if (host is null) return false;
        HashSet<uint> known = [.. _events?.Content.Events.Select(e => e.Entry) ?? []];
        _catalog = EventScenarioCatalog.Build(_content, known.Contains);
        foreach (string issue in _catalog.Issues) logger.LogWarning("game_event_playerbot: {Issue}", issue);
        var engine = new EventScenarioEngine(host, logger);
        engine.SetCatalog(_catalog);
        _engine = engine;
        foreach (ushort id in _events?.ActiveEvents ?? []) engine.EventChanged(id, active: true);
        logger.LogInformation("Bot event scenarios: {Groups} groups of {Events} events loaded, {Rejected} rows rejected", _catalog.GroupCount, _catalog.Events.Count(), _catalog.Issues.Count);
        return true;
    }

    // --- commands (.playerbot eventscenario) -------------------------------------------------------------------------------

    private string? Describe(ushort eventId) => _events?.Content.Events.FirstOrDefault(e => e.Entry == eventId)?.Description;

    /// <summary>The lines of <c>.playerbot eventscenario list</c> (world thread).</summary>
    internal IReadOnlyList<string> ListLines()
        => _engine is { } engine ? engine.ListLines(Describe) : NotLoaded();

    /// <summary>The lines of <c>.playerbot eventscenario status [#event]</c> (world thread).</summary>
    internal IReadOnlyList<string> StatusLines(ushort? eventId)
        => _engine is { } engine ? engine.StatusLines(eventId, Describe) : NotLoaded();

    private IReadOnlyList<string> NotLoaded()
        => _content.Groups.Count == 0
            ? ["No bot event scenario is configured (the game_event_playerbot_group table is empty, or the playerbots are off)."]
            : ["The bot event scenarios are read on the first world tick after startup; try again in a moment."];
}
