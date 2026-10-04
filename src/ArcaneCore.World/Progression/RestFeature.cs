using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.World.Rest;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Progression;

/// <summary>
/// Rested experience in the daemon (docs/areas/rested-xp.md): a character rests in a capital city (zone flags, through
/// <see cref="IPlayerLocationListener"/>) or an inn (<c>areatrigger_tavern</c>, through <see cref="IAreaTriggerListener"/>), gains the
/// pool while it rests, gains it offline from the logout second, and keeps it across logins. The pool and what it does to kill XP
/// are <see cref="PlayerProgression"/>'s; the rules are <see cref="RestService"/>'s; this feature wires them to the world.
/// <para>
/// Threads. The login hook runs on the session task, before the player is visible: it reads the stored state and applies it to the
/// player's own fields. Everything else runs on the world thread: the per-tick accrual (<see cref="WorldRuntime.WorldTick"/>, resting
/// players only), the zone and trigger events, and the capture of the state to persist. Persistence is a retained-write queue
/// (<see cref="RestWriteQueue"/>): a change is written off the world thread, a failed write is kept and retried (next change, login
/// barrier, logout, shutdown), and a login fails closed while an earlier write of the character is not durable.
/// </para>
/// </summary>
public sealed class RestFeature : IWorldFeature, ICharacterHooks, IAreaTriggerListener, IPlayerLocationListener, IRestEnvironment
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RestFeature> _logger;
    private readonly Lazy<RestService> _rest;
    private readonly Dictionary<Player, Tracked> _tracked = [];
    private WorldRuntime? _world;
    private uint _sinceSaveScanMs;

    public RestFeature(IServiceProvider services, ILoggerFactory loggers)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(loggers);
        _logger = loggers.CreateLogger<RestFeature>();
        Writes = new RestWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), loggers.CreateLogger<RestWriteQueue>());
        _rest = new Lazy<RestService>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The rest rules (built on first use from configuration section "Rest" and the progression service).</summary>
    public RestService Rest => _rest.Value;

    /// <summary>The inn triggers (the <c>areatrigger_tavern</c> content; replaced by <c>.reload areatrigger_tavern</c>).</summary>
    public TavernTriggers Taverns { get; } = new();

    /// <summary>The queued writes of the rested state.</summary>
    public RestWriteQueue Writes { get; }

    /// <summary>
    /// Order among the zone listeners: after the core's (int.MinValue). A listener that reads the rest state of a zone entry
    /// sees the type this feature just set.
    /// </summary>
    public int Order => 0;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the rest feature is already attached");
        }

        _world = world;
        Writes.Start();
        LoadTaverns();
        world.PlayerLoggedIn += OnPlayerLoggedIn;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.WorldTick += OnWorldTick;
        _logger.LogInformation("Rest: rates in-game {InGame}, offline in tavern or city {Tavern}, offline in the wilderness {Wild}; {Taverns} inn triggers; save every {Save} s",
            Rest.Options.RateInGame, Rest.Options.RateOfflineInTavernOrCity, Rest.Options.RateOfflineInWilderness, Taverns.Count, Rest.Options.SaveIntervalSeconds);
    }

    /// <summary>
    /// Shutdown: the world thread has stopped, so every player still online is captured once more (the world's final save does not
    /// raise the logout event), then the queue drains and throws if storage is still failing.
    /// </summary>
    public async Task StopAsync()
    {
        if (_world is { } world)
        {
            long now = DeathHooks.For(world).Clock.UnixSeconds;
            foreach (Player player in _tracked.Keys)
            {
                Writes.Save((int)player.Guid.Low, ToRow(Rest.Capture(player, now)));
            }

            _tracked.Clear();
        }

        await Writes.StopAsync().ConfigureAwait(false);
    }

    /// <summary>A new character gets an empty pool: clear what an earlier character with the same id left (after older writes drained).</summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        await Writes.FlushAsync().ConfigureAwait(false);
        Writes.Forget(character.Id);
        if (session.Services.GetService<ICharacterRestStore>() is { } store)
        {
            await store.DeleteAsync(character.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Session task, after every loading hook set the level and its next-level experience: restore the stored pool and add the offline
    /// gain (vmangos PlayerLoad.cpp:618-622). The login barrier faults while an earlier write of this character is not durable.
    /// </summary>
    public async Task OnPlayerLoadedAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        await Writes.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        RestSnapshot? stored = null;
        if (session.Services.GetService<ICharacterRestStore>() is { } store
            && await store.LoadAsync(character.Id).ConfigureAwait(false) is { } state)
        {
            stored = new RestSnapshot(state.RestBonus, state.LogoutUnixSeconds, state.WasResting);
        }

        Rest.ApplyLogin(player, stored, DeathHooks.For(session.World).Clock.UnixSeconds);
    }

    /// <summary>
    /// Entering an inn trigger (vmangos HandleAreaTriggerOpcode): the world calls this for a trigger that exists and that the player is
    /// inside. A trigger that is no inn is ignored.
    /// </summary>
    public void OnAreaTrigger(Player player, uint triggerId)
    {
        if (_world is { } world && Taverns.Contains(triggerId))
        {
            Rest.OnTavernTrigger(player, triggerId, world.NowMs);
        }
    }

    /// <summary>A zone change: a capital city starts a city rest, any other zone ends a rest that is not an inn's.</summary>
    public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
    {
        if (_world is { } world)
        {
            Rest.OnZoneEntered(player, zoneEntry, world.NowMs);
        }
    }

    AreaTriggerTemplate? IRestEnvironment.FindAreaTrigger(uint triggerId)
        => _services.GetRequiredService<TeleportFeature>().Maps.FindAreaTrigger(triggerId);

    bool IRestEnvironment.IsOutdoors(Player player)
        => player.Map is not { } map || map.Collision.IsOutdoors(player.X, player.Y, player.Z);

    /// <summary>
    /// Read the <c>areatrigger_tavern</c> rows (startup; blocking, as the other content loads of <c>Attach</c>). Whether a row names an
    /// existing area trigger needs the map content, which loads after this feature attaches: the area-trigger handler only calls
    /// <see cref="OnAreaTrigger"/> for a trigger that exists, which is vmangos' check (ObjectMgr.cpp:1686-1691) made at use.
    /// </summary>
    private void LoadTaverns()
    {
        using IServiceScope scope = _services.CreateScope();
        if (scope.ServiceProvider.GetService<IAreaTriggerTavernStore>() is not { } store)
        {
            _logger.LogInformation("Rest: no areatrigger_tavern store registered; no inns");
            return;
        }

        IReadOnlyList<uint> ids = store.LoadAsync().GetAwaiter().GetResult();
        Taverns.Replace(TavernTriggers.Build(ids));
    }

    private void OnPlayerLoggedIn(Player player)
    {
        uint now = _world?.NowMs ?? 0;
        _tracked[player] = new Tracked { NextSaveMs = now + (Rest.Options.SaveIntervalSeconds * 1000u) };
    }

    private void OnPlayerLoggingOut(Player player)
    {
        if (_world is not { } world)
        {
            return;
        }

        int characterId = (int)player.Guid.Low;
        _tracked.Remove(player);
        Writes.Save(characterId, ToRow(Rest.Capture(player, DeathHooks.For(world).Clock.UnixSeconds)));
        Rest.Forget(player);
        if (Writes.HasRetainedFailure(characterId))
        {
            Writes.RequestRetry(characterId);
        }
    }

    /// <summary>
    /// Per tick: the accrual of the resting players (<see cref="RestService.Update"/>), and once a second the periodic write of the
    /// players whose save time came (a walk over the online players: no allocation, one comparison each).
    /// </summary>
    private void OnWorldTick(uint diffMs)
    {
        if (_world is not { } world)
        {
            return;
        }

        uint now = world.NowMs;
        Rest.Update(now, this);
        uint interval = Rest.Options.SaveIntervalSeconds;
        _sinceSaveScanMs += diffMs;
        if (interval == 0 || _sinceSaveScanMs < 1000)
        {
            return;
        }

        _sinceSaveScanMs = 0;
        long unixNow = DeathHooks.For(world).Clock.UnixSeconds;
        foreach ((Player player, Tracked tracked) in _tracked)
        {
            if (unchecked(now - tracked.NextSaveMs) < uint.MaxValue / 2)
            {
                tracked.NextSaveMs = now + (interval * 1000u);
                Writes.Save((int)player.Guid.Low, ToRow(Rest.Capture(player, unixNow)));
            }
        }
    }

    private RestService Build()
    {
        var options = new RestOptions();
        _services.GetService<IConfiguration>()?.GetSection(RestOptions.SectionName).Bind(options);
        PlayerProgression progression = _services.GetRequiredService<ProgressionFeature>().Progression;
        return new RestService(progression, options);
    }

    private static CharacterRestState ToRow(RestSnapshot snapshot) => new(snapshot.RestBonus, snapshot.UnixSeconds, snapshot.WasResting);

    private sealed class Tracked
    {
        public uint NextSaveMs;
    }
}

/// <summary>Character deletion for the rested state: queued writes drain before the row is removed, and nothing retained can resurrect it.</summary>
public sealed class RestDeleteHook(RestFeature rest) : IWorldFeature, ICharacterDeleteHook
{
    public void Attach(WorldRuntime world)
    {
    }

    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => rest.Writes.FlushAsync();

    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        rest.Writes.Forget(character.Id);
        await rest.Writes.FlushAsync().WaitAsync(CharacterDeletion.DrainTimeout).ConfigureAwait(false);
        rest.Writes.Forget(character.Id);
    }
}
