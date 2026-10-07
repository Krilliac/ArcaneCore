using ArcaneCore.Data.Reputation;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
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
public sealed partial class ReputationFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature, ICharacterHooks, IAsyncDisposable
{
    private readonly ILogger _logger = loggers.CreateLogger<ReputationFeature>();
    private readonly HashSet<MapCombat> _combat = [];
    private readonly Lock _loadLock = new();
    private ReputationWriteQueue? _writes;
    private WorldRuntime? _world;
    private ReputationService? _service;
    private Func<Player, RewardGroup?>? _groups;

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
        if (services.GetService<SpellFeature>() is { } spells)
        {
            ReputationAuras.Bind(spells.System, service);
        }
        _writes!.Start();
        _world = world;
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        world.PlayerLoggingOut += OnPlayerLoggingOut;
        service.ReputationChanged += OnReputationChanged;
        _logger.LogInformation("Loaded {Factions} factions, {OnKill} kill reputation entries, {Spillovers} spillover templates and {Rates} reward rates",
            service.Factions.Count, service.OnKillCount, service.Content.SpilloverCount, service.Content.RateCount);
        ReportStatus(service);
    }

    /// <summary>Clear rows left by a deleted character whose id was reused (after older writes drain).</summary>
    public async Task OnCharacterCreatedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (_writes is not null)
        {
            await _writes.FlushAsync().ConfigureAwait(false);
            _writes.ForgetCharacter(character.Id);
        }

        if (session.Services.GetService<ICharacterReputationStore>() is { } store)
        {
            await store.DeleteCharacterAsync(character.Id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ReputationMgr::LoadFromDB on the session task, after earlier writes for anyone have drained.
    /// Throws (refusing the login) while this character has reputation writes that are retained
    /// and still cannot be persisted, so stale stored rows never become the live state.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(player);
        if (_writes is not null)
        {
            await _writes.FlushCharacterAsync(character.Id).ConfigureAwait(false);
        }

        CharacterReputationData stored = session.Services.GetService<ICharacterReputationStore>() is { } store
            ? await store.LoadAsync(character.Id).ConfigureAwait(false)
            : CharacterReputationData.Empty;
        Service.Track(player, Service.Create(player, stored));
        if (Service.Factions.Count > 0)
        {
            // Item reputation gates read the real rank (Player.cpp:10045). Without a catalog the fail-closed default stays.
            player.Inventory.Requirements = new ReputationItemRequirements(player.Inventory.Requirements, Service);
        }
    }

    /// <summary>
    /// Queue the removal of a deleted character's reputation, run by
    /// <see cref="ReputationCharacterDeleteHook"/> after the deletion committed (docs/integration/reputation.md,
    /// docs/integration/character-delete.md). The queued removal applies only while the id has no
    /// character row, so it cannot wipe a character recreated with the same id. Discards anything
    /// the queue retained for the character.
    /// </summary>
    public void DeleteCharacter(int characterId) => _writes?.DeleteCharacter(characterId);

    /// <summary>Wait until every queued write has been attempted (tests, deletion ordering). Never retries and never throws.</summary>
    public Task FlushAsync() => _writes?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>
    /// <see cref="FlushAsync"/> plus one more attempt at this character's retained writes; faults
    /// while they are still not durable (the login barrier).
    /// </summary>
    public Task FlushCharacterAsync(int characterId) => _writes?.FlushCharacterAsync(characterId) ?? Task.CompletedTask;

    /// <summary>True while the character has reputation writes that failed all attempts and are retained.</summary>
    public bool HasRetainedFailure(int characterId) => _writes?.HasRetainedFailure(characterId) ?? false;

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
            if (_service is { } service)
            {
                service.ReputationChanged -= OnReputationChanged;
            }
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

    /// <summary>Player::ReputationChanged: reputation-objective quests complete or revert (resolved lazily, the quest feature attaches first).</summary>
    private void OnReputationChanged(Player player, uint factionId)
        => services.GetService<QuestNpcFeature>()?.Services?.ReputationChanged(player, factionId);

    private void OnPlayerLoggingOut(Player player)
    {
        Service.Untrack(player);
        int characterId = (int)player.Guid.Low;
        if (_writes is { } writes && writes.HasRetainedFailure(characterId))
        {
            writes.RequestRetry(characterId); // an early retry; the login barrier and shutdown still retry
        }
    }

    private void OnMapUnloading(Map map)
    {
        if (_combat.Remove(map.Combat))
        {
            map.Combat.UnitKilled -= OnUnitKilled;
        }
    }

    /// <summary>
    /// Player::RewardReputation(Unit*, 1.0) for a creature kill: the player who tapped it and the group of the tap, every member at
    /// reward distance, dead or alive, whoever landed the killing blow; without a tap the killing player (a pet's or totem's owner)
    /// and his group (<see cref="ReputationKillCredit.AwardKill"/>).
    /// </summary>
    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (victim is not Creature creature || !creature.IsInWorld || creature.Map is not { } map)
        {
            return;
        }

        Player? player = killer is null ? null : DuelRules.ControllingPlayer(killer);
        if (player is not { IsInWorld: true } || !ReferenceEquals(map.FindPlayer(player.Guid), player))
        {
            player = null; // the killing blow came from no player on this map: only a tap can earn the reputation
        }

        _groups ??= RewardGroups.Resolver(services);
        float distance = (services.GetService<ProgressionFeature>()?.Progression.Options ?? new ProgressionOptions()).GroupXpDistance;
        ReputationKillCredit.AwardKill(Service, player, creature, _groups, distance);
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
            ReputationContentRows contentRows;
            using (IServiceScope scope = scopes.CreateScope())
            {
                onKill = scope.ServiceProvider.GetService<IReputationOnKillSource>() is { } source
                    ? source.LoadAsync().GetAwaiter().GetResult()
                    : [];
                contentRows = scope.ServiceProvider.GetService<IReputationContentSource>() is { } contentSource
                    ? contentSource.LoadAsync().GetAwaiter().GetResult()
                    : ReputationContentRows.Empty;
            }

            if (factions.Count > 0)
            {
                // ObjectMgr::LoadReputationOnKill skips a row whose faction does not exist (ObjectMgr.cpp:8935-8957): once, loudly, here.
                ReputationOnKillValidation valid = ReputationContentValidator.FilterOnKill(onKill, factions);
                onKill = valid.Entries;
                foreach (string warning in valid.Warnings)
                {
                    _logger.LogWarning("Reputation content: {Warning}", warning);
                }
            }

            ReputationContent content = ReputationContent.Create(contentRows, factions);
            foreach (string warning in content.Warnings)
            {
                _logger.LogWarning("Reputation content: {Warning}", warning);
            }

            _writes = new ReputationWriteQueue(scopes, loggers.CreateLogger<ReputationWriteQueue>());
            var rates = new ReputationRates { Gain = Options.RateGain, LowLevelKill = Options.RateLowLevelKill };
            return _service = new ReputationService(factions, onKill, rates, new Sink(_writes))
            {
                PeaceForcedUsesEffectiveStanding = Options.PeaceForcedUsesEffectiveStanding,
                SpilloverEnabled = Options.SpilloverEnabled,
                SendForcedReactions = Options.SendForcedReactions,
                Content = content,
            };
        }
    }

    private sealed class Sink(ReputationWriteQueue writes) : IReputationSink
    {
        public void FactionsChanged(Player player, IReadOnlyList<CharacterReputationRow> rows) => writes.SaveFactions((int)player.Guid.Low, rows);

        public void WatchedFactionChanged(Player player, int watchedFaction) => writes.SaveWatchedFaction((int)player.Guid.Low, watchedFaction);
    }
}
