using ArcaneCore.Data.Reputation;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Reputation;

/// <summary>
/// Character reputation in the world daemon (docs/integration/reputation.md): Faction.dbc
/// content, per-character load/save (characters schema v7), the login reputation packet,
/// direct creature-kill rewards and the reaction source the NPC adapter uses. Other features
/// reach <see cref="Reputation"/> with <c>GetService&lt;ReputationFeature&gt;()</c>.
/// </summary>
public sealed class ReputationFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature, ICharacterHooks, IAsyncDisposable
{
    private readonly ILogger _logger = loggers.CreateLogger<ReputationFeature>();
    private readonly HashSet<MapCombat> _combat = [];
    private readonly Lock _loadLock = new();
    private ReputationWriteQueue? _writes;
    private WorldRuntime? _world;
    private ReputationService? _service;

    public ReputationOptions Options { get; } = new();

    /// <summary>
    /// The world-thread reputation owner. Content loads on first use, so a feature attached
    /// earlier (the quest/NPC adapter) can take it during its own attachment.
    /// </summary>
    public ReputationService Service => _service ?? Load();

    /// <summary>The cross-feature reputation seam.</summary>
    public IReputationService Reputation => Service;

    /// <summary>Writes queued or in progress.</summary>
    public int PendingWrites => _writes?.Pending ?? 0;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the reputation feature is already attached");
        }

        ReputationService service = Service;
        _writes!.Start();
        _world = world;
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        world.PlayerLoggingOut += OnPlayerLoggingOut;
        _logger.LogInformation("Loaded {Factions} factions and {OnKill} kill reputation entries", service.Factions.Count, service.OnKillCount);
    }

    /// <summary>Clear rows left by a deleted character whose id was reused (after older writes drain).</summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (_writes is not null)
        {
            await _writes.FlushAsync().ConfigureAwait(false);
        }

        if (session.Services.GetService<ICharacterReputationStore>() is { } store)
        {
            await store.DeleteCharacterAsync(character.Id).ConfigureAwait(false);
        }
    }

    /// <summary>ReputationMgr::LoadFromDB on the session task, after earlier writes for anyone have drained.</summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        if (_writes is not null)
        {
            await _writes.FlushAsync().ConfigureAwait(false);
        }

        CharacterReputationData stored = session.Services.GetService<ICharacterReputationStore>() is { } store
            ? await store.LoadAsync(character.Id).ConfigureAwait(false)
            : CharacterReputationData.Empty;
        Service.Track(player, Service.Create(player, stored));
    }

    /// <summary>
    /// Queue the removal of a deleted character's reputation, run by
    /// <see cref="ReputationCharacterDeleteHook"/> after the deletion committed (docs/integration/reputation.md,
    /// docs/integration/character-delete.md). The queued removal applies only while the id has no
    /// character row, so it cannot wipe a character recreated with the same id.
    /// </summary>
    public void DeleteCharacter(int characterId) => _writes?.DeleteCharacter(characterId);

    /// <summary>Wait until every queued write has been attempted (tests, deletion ordering).</summary>
    public Task FlushAsync() => _writes?.FlushAsync() ?? Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_writes is not null)
        {
            await _writes.StopAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_world is { } world)
        {
            world.MapCreated -= OnMapCreated;
            world.MapUnloading -= OnMapUnloading;
            world.PlayerLoggingOut -= OnPlayerLoggingOut;
        }

        foreach (MapCombat combat in _combat)
        {
            combat.UnitKilled -= OnUnitKilled;
        }

        _combat.Clear();
        await StopAsync().ConfigureAwait(false);
    }

    private void OnMapCreated(Map map)
    {
        if (_combat.Add(map.Combat))
        {
            map.Combat.UnitKilled += OnUnitKilled;
        }
    }

    private void OnPlayerLoggingOut(Player player) => Service.Untrack(player);

    private void OnMapUnloading(Map map)
    {
        if (_combat.Remove(map.Combat))
        {
            map.Combat.UnitKilled -= OnUnitKilled;
        }
    }

    /// <summary>Player::RewardReputation(Unit*, 1.0) for a direct, live, same-map player kill.</summary>
    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (killer is not Player { IsAlive: true, IsInWorld: true } player || victim is not Creature creature
            || player.Map is not { } map || !creature.IsInWorld || !ReferenceEquals(creature.Map, map)
            || !ReferenceEquals(map.FindPlayer(player.Guid), player))
        {
            return;
        }

        Service.RewardKill(player, creature);
    }

    /// <summary>Startup content failures (malformed Faction.dbc, duplicate kill rows) stop attachment.</summary>
    private ReputationService Load()
    {
        lock (_loadLock)
        {
            if (_service is { } loaded)
            {
                return loaded;
            }

            services.GetService<IConfiguration>()?.GetSection(ReputationOptions.SectionName).Bind(Options);
            FactionCatalog factions = services.GetService<FactionCatalog>()
                ?? (string.IsNullOrWhiteSpace(Options.FactionDbcPath) ? FactionCatalog.Empty : FactionDbcReader.Load(Options.FactionDbcPath));
            IReadOnlyList<ReputationOnKillEntry> onKill;
            using (IServiceScope scope = scopes.CreateScope())
            {
                onKill = scope.ServiceProvider.GetService<IReputationOnKillSource>() is { } source
                    ? source.LoadAsync().GetAwaiter().GetResult()
                    : [];
            }

            _writes = new ReputationWriteQueue(scopes, loggers.CreateLogger<ReputationWriteQueue>());
            var rates = new ReputationRates { Gain = Options.RateGain, LowLevelKill = Options.RateLowLevelKill };
            return _service = new ReputationService(factions, onKill, rates, new Sink(_writes));
        }
    }

    private sealed class Sink(ReputationWriteQueue writes) : IReputationSink
    {
        public void FactionsChanged(Player player, IReadOnlyList<CharacterReputationRow> rows) => writes.SaveFactions((int)player.Guid.Low, rows);

        public void WatchedFactionChanged(Player player, int watchedFaction) => writes.SaveWatchedFaction((int)player.Guid.Low, watchedFaction);
    }
}
