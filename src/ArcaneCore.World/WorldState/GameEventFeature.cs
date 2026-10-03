using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The world daemon's game-event feature (docs/areas/game-events-weather.md): binds <c>World:GameEvents</c>, loads the game-event
/// tables and the running set of the last run, owns the <see cref="GameEventService"/>, and drives it from
/// <see cref="WorldRuntime.WorldTick"/> the way vmangos drives <c>sGameEventMgr.Update</c> from <c>World::Update</c>
/// (World.cpp:1826-1827 and :2106-2111): the first tick initialises the system, later ticks accumulate until the delay the last
/// update asked for has passed. It is the <see cref="IGameEventState"/> consumers (conditions, battleground weekends, EventAI)
/// ask; with the feature off or no tables no event is ever active.
/// </summary>
public sealed class GameEventFeature(IServiceProvider services, ILogger<GameEventFeature> logger) : IWorldFeature, IGameEventState
{
    private WorldRuntime? _world;
    private WorldStateHooks? _hooks;
    private GameEventService? _service;
    private IReadOnlySet<ushort>? _initialiseWith;
    private uint _elapsedMs;
    private uint _delayMs;
    private GameEventStatusWriter? _writer;

    /// <summary>The running service (null before the feature is attached). With the feature off it exists but never starts an event.</summary>
    public GameEventService? Service => _service;

    /// <summary>
    /// Raised each time a service is built (start-up, reload, tests), before it initialises, so features that own one kind of
    /// event effect (spawns, quests, creature data) can register on it. A feature attached later reads <see cref="Service"/>.
    /// </summary>
    public event Action<GameEventService>? ServiceCreated;

    public bool IsActiveEvent(ushort eventId) => _service?.IsActiveEvent(eventId) ?? false;

    public bool IsActiveHoliday(uint holidayId) => _service?.IsActiveHoliday(holidayId) ?? false;

    public IReadOnlyCollection<ushort> ActiveEvents => _service?.ActiveEvents ?? [];

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _hooks = WorldStateHooks.For(world);
        services.GetService<IConfiguration>()?.GetSection(GameEventOptions.SectionName).Bind(_hooks.GameEventSettings);
        // Disabled: the tables are still loaded (spawns listed in them stay out of the world, as in vmangos while no event runs),
        // but the system never initialises or ticks, so no event ever starts.
        bool enabled = _hooks.GameEventSettings.Enabled;
        if (enabled)
        {
            world.WorldTick += OnTick;
        }
        else
        {
            logger.LogInformation("game events are disabled (World:GameEvents:Enabled=false): no event will run");
        }

        GameEventContent content = GameEventContent.Empty;
        var active = new HashSet<ushort>();
        bool hasStatusStore;
        using (IServiceScope scope = services.CreateScope())
        {
            IGameEventDataStore? data = scope.ServiceProvider.GetService<IGameEventDataStore>();
            if (data is null)
            {
                logger.LogInformation("no game-event data store registered; no game event will run");
            }
            else
            {
                content = data.LoadAsync().GetAwaiter().GetResult();
            }

            IGameEventStatusStore? status = scope.ServiceProvider.GetService<IGameEventStatusStore>();
            hasStatusStore = status is not null;
            if (status is not null)
            {
                foreach (int id in status.LoadActiveAsync().GetAwaiter().GetResult().Where(i => i is > 0 and <= ushort.MaxValue))
                {
                    active.Add((ushort)id);
                }
            }
            else
            {
                logger.LogInformation("no game-event status store registered; running events are not remembered across restarts");
            }
        }

        _writer = hasStatusStore ? new GameEventStatusWriter(services, logger) : null;
        UseContent(content, active, _writer);
    }

    /// <summary>
    /// Replace the running service with one built from <paramref name="content"/>; it initialises on the next world tick, with
    /// <paramref name="activeAtShutdown"/> as the events that were running before (they resume). Used at start-up, by the
    /// reload, and by tests. Call on the world thread (or before it starts).
    /// </summary>
    public void UseContent(GameEventContent content, IReadOnlySet<ushort> activeAtShutdown, IGameEventStatusSink? status = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(activeAtShutdown);
        WorldStateHooks hooks = _hooks ?? throw new InvalidOperationException("the game event feature is not attached");
        GameEventOptions options = hooks.GameEventSettings;
        TimeZoneInfo zone = hooks.LocalZone;
        GameEventLoadResult load = GameEventLoader.Load(content, options, hooks.Time.UtcNow, zone);
        foreach (string issue in load.Issues)
        {
            logger.LogError("game events: {Issue}", issue);
        }

        logger.LogInformation(
            "loaded {Events} game events ({Dialect} dialect, {Boundary} start boundary)",
            load.Definitions.Count, load.Dialect, load.Boundary);
        var announcer = new WorldAnnouncer(_world ?? throw new InvalidOperationException("the game event feature is not attached"));
        _service = new GameEventService(load, options, () => hooks.Time.UtcNow, zone, logger, status, announcer);
        _initialiseWith = options.Enabled ? activeAtShutdown : null;
        ServiceCreated?.Invoke(_service);
        _elapsedMs = 0;
        _delayMs = 0;
    }

    private void OnTick(uint diffMs)
    {
        GameEventService? service = _service;
        if (service is null)
        {
            return;
        }

        if (_initialiseWith is { } active)
        {
            _initialiseWith = null;
            _delayMs = service.Initialize(active);
            _elapsedMs = 0;
            return;
        }

        _elapsedMs += diffMs;
        if (_elapsedMs >= _delayMs)
        {
            _elapsedMs = 0;
            _delayMs = service.Update();
        }
    }

    public Task StopAsync() => _writer?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>The announcement of an event start (vmangos <c>SendWorldText(LANG_EVENTMESSAGE)</c>, mangos_string 4).</summary>
    private sealed class WorldAnnouncer(WorldRuntime world) : IGameEventAnnouncer
    {
        public void Announce(string description)
            => world.BroadcastToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage($"|cffff0000[Event Message]: {description}|r"));
    }
}

/// <summary>
/// Writes the set of running events to <see cref="IGameEventStatusStore"/> off the world thread: the latest set wins, writes never
/// overlap, a failure is logged and retained for the next change (or the shutdown flush) and never reaches the world thread.
/// </summary>
internal sealed class GameEventStatusWriter(IServiceProvider services, ILogger logger) : IGameEventStatusSink
{
    private readonly object _gate = new();
    private IReadOnlyCollection<ushort>? _pending;
    private Task _running = Task.CompletedTask;
    private bool _active;

    public void Changed(IReadOnlyCollection<ushort> activeEvents)
    {
        lock (_gate)
        {
            _pending = activeEvents;
            if (!_active)
            {
                _active = true;
                _running = Task.Run(DrainAsync);
            }
        }
    }

    /// <summary>Wait for the running write and try a retained one once more (shutdown).</summary>
    public async Task FlushAsync()
    {
        Task running;
        lock (_gate)
        {
            running = _running;
        }

        await running.ConfigureAwait(false);
        IReadOnlyCollection<ushort>? retained;
        lock (_gate)
        {
            retained = _pending;
            _pending = null;
        }

        if (retained is not null)
        {
            await WriteAsync(retained).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            IReadOnlyCollection<ushort>? set;
            lock (_gate)
            {
                set = _pending;
                _pending = null;
                if (set is null)
                {
                    _active = false;
                    return;
                }
            }

            if (!await WriteAsync(set).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    _pending ??= set; // retained: the next change replaces it, shutdown retries it
                    _active = false;
                }

                return;
            }
        }
    }

    private async Task<bool> WriteAsync(IReadOnlyCollection<ushort> set)
    {
        try
        {
            using IServiceScope scope = services.CreateScope();
            IGameEventStatusStore? store = scope.ServiceProvider.GetService<IGameEventStatusStore>();
            if (store is null)
            {
                return true;
            }

            await store.ReplaceActiveAsync([.. set.Select(e => (int)e)]).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "storing the running game events failed; it is retried by the next change");
            return false;
        }
    }
}
