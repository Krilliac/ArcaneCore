using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Stats;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Progression;

/// <summary>
/// Player experience and leveling in the daemon: kill XP for the killer or its nearby group
/// (vmangos Unit::Kill → RewardPlayerAndGroupAtKill), next-level requirements and level base
/// values at login, and a character save after every level-up. Quest XP is granted by quest
/// settlement through <see cref="Progression"/> (docs/integration/quest-progression.md).
/// </summary>
public sealed class ProgressionFeature : IWorldFeature, ICharacterHooks, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ProgressionFeature> _logger;
    private readonly Lazy<PlayerProgression> _progression;
    private readonly HashSet<MapCombat> _combat = [];
    private Func<Player, RewardGroup?> _groups = _ => null;
    private WorldRuntime? _world;

    public ProgressionFeature(IServiceProvider services, ILogger<ProgressionFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progression = new Lazy<PlayerProgression>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The world-thread experience service (built on first use from configuration section "Progression").</summary>
    public PlayerProgression Progression => _progression.Value;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the progression feature is already attached");
        }

        _world = world;
        _groups = RewardGroups.Resolver(_services);
        Progression.LevelChanged += OnLevelChanged;
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        _logger.LogInformation("Progression: max level {Level}, level stats for {Rows} race/class/level rows",
            Progression.Options.MaxPlayerLevel, (Progression.Stats as PlayerLevelStatsTable)?.Count ?? 0);
    }

    /// <summary>
    /// Before the player is visible: next-level XP for its level and its level base values. The stats feature loads
    /// the imported player base data first, because it supplies the level base values when no file is configured.
    /// </summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_services.GetService<StatsFeature>() is { } stats)
        {
            await stats.EnsureLoadedAsync().ConfigureAwait(false);
        }

        Progression.InitializeLoadedPlayer(player);
    }

    public void Dispose()
    {
        foreach (MapCombat combat in _combat)
        {
            combat.UnitKilled -= OnUnitKilled;
        }

        _combat.Clear();
        if (_world is { } world)
        {
            world.MapCreated -= OnMapCreated;
            world.MapUnloading -= OnMapUnloading;
        }

        if (_progression.IsValueCreated)
        {
            _progression.Value.LevelChanged -= OnLevelChanged;
        }
    }

    private PlayerProgression Build()
    {
        var options = new ProgressionOptions();
        _services.GetService<IConfiguration>()?.GetSection(ProgressionOptions.SectionName).Bind(options);
        IPlayerLevelStatsSource stats = string.IsNullOrWhiteSpace(options.LevelStatsPath)
            ? PlayerLevelStatsTable.Empty
            : PlayerLevelStatsTable.Load(options.LevelStatsPath);
        return new PlayerProgression(options, stats, () => _world?.NowMs ?? 0);
    }

    private void OnMapCreated(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _combat.Add(combat))
        {
            combat.UnitKilled += OnUnitKilled;
        }
    }

    private void OnMapUnloading(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _combat.Remove(combat))
        {
            combat.UnitKilled -= OnUnitKilled;
        }
    }

    /// <summary>
    /// vmangos Unit::Kill (Unit.cpp:981-1008, 1094-1097): the player behind the killing blow (the killer, or the player that owns
    /// or charms a pet, demon, totem, guardian or charmed creature; <see cref="KillRewards.CreditedPlayer"/>) starts the tap, the
    /// creature's original loot recipient and the group of the tap replace him (<see cref="KillRewards.ResolveTap"/>); that group
    /// shares the experience (RewardGroupAtKill), else the tapper alone (RewardSinglePlayerAtKill). The tap of a creature that
    /// players damaged 35% or less was dropped at the kill, so a guard's or another creature's kill then rewards nobody; above that
    /// the experience is scaled by the players' share (<see cref="Creature.KillXpModifier"/>).
    /// </summary>
    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (victim is not Creature creature || creature.Map is not { } map)
        {
            return;
        }

        Player? player = KillRewards.CreditedPlayer(killer);
        if (player is not { IsInWorld: true } || !ReferenceEquals(map.FindPlayer(player.Guid), player))
        {
            player = null; // no player on this map is behind the killing blow: only a tap can earn the kill
        }

        (Player? tapper, RewardGroup? group) = KillRewards.ResolveTap(player, creature, _groups);
        if (tapper is null && group is null)
        {
            return;
        }

        bool nonRaidDungeon = _world is { } world
            && WorldMaps.Of(world).Registry.Find(map.MapId) is { IsDungeon: true, IsRaid: false };
        IReadOnlyList<Player> recipients = KillRewards.TapRecipients(tapper, creature, group, Progression.Options.GroupXpDistance);
        IReadOnlyList<uint> granted = KillRewards.AwardExperience(Progression, recipients, creature, nonRaidDungeon);
        KillExperienceAwarded?.Invoke(recipients, granted, creature, nonRaidDungeon);
    }

    /// <summary>
    /// After a kill's experience went out: the recipients, what each was given, the victim and whether the map is a non-raid dungeon
    /// (the pets area gives the hunter pets their share).
    /// </summary>
    public event Action<IReadOnlyList<Player>, IReadOnlyList<uint>, Creature, bool>? KillExperienceAwarded;

    private void OnLevelChanged(Player player) => _world?.SavePlayer(player);
}
