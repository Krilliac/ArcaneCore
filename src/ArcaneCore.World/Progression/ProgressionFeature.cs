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
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        _logger.LogInformation("Progression: max level {Level}, level stats for {Rows} race/class/level rows",
            Progression.Options.MaxPlayerLevel, (Progression.Stats as PlayerLevelStatsTable)?.Count ?? 0);
    }

    /// <summary>Before the player is visible: next-level XP for its level and its level base values.</summary>
    public Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Progression.InitializeLoadedPlayer(player);
        return Task.CompletedTask;
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
        if (_combat.Add(map.Combat))
        {
            map.Combat.UnitKilled += OnUnitKilled;
        }
    }

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (killer is not Player { IsInWorld: true } player || victim is not Creature creature
            || creature.Map is not { } map || !ReferenceEquals(player.Map, map)
            || !ReferenceEquals(map.FindPlayer(player.Guid), player))
        {
            return;
        }

        bool nonRaidDungeon = _world is { } world
            && WorldMaps.Of(world).Registry.Find(map.MapId) is { IsDungeon: true, IsRaid: false };
        IReadOnlyList<Player> recipients = KillRewards.Recipients(player, creature, _groups(player), Progression.Options.GroupXpDistance);
        KillRewards.AwardExperience(Progression, recipients, creature, nonRaidDungeon);
    }

    private void OnLevelChanged(Player player) => _world?.SavePlayer(player);
}
