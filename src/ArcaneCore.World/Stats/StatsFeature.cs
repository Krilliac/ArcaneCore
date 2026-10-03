using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.PlayerStats;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Stats;

/// <summary>
/// The player stat system in the daemon (docs/areas/stats.md): the imported player base data (world schema
/// <c>PlayerStatsDataModule</c>) is read once at the first login, the level stats it holds become the level base
/// values unless <c>Progression:LevelStatsPath</c> names a file, every player is attached to a
/// <see cref="PlayerStatSystem"/> before it is handed to the world thread, and every map's combat asks the system
/// about off-hand weapons, parry, block and the shield block value.
/// <para>
/// Without a registered <see cref="IPlayerStatsContentStore"/> (a host without the world database) or with an
/// empty one the feature still runs: level stats stay as <c>Progression</c> configures them and the agility terms
/// of crit and dodge are left out, with one warning. <c>Stats:RequireImportedData</c> makes an incomplete
/// import refuse the login instead, like vmangos refuses to start. A failed load is not cached.
/// </para>
/// </summary>
public sealed class StatsFeature : IWorldFeature, ICharacterHooks, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StatsFeature> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ForwardingSource _source = new();
    private volatile PlayerStatSystem? _system;
    private PlayerProgression? _progression;
    private WorldRuntime? _world;

    public StatsFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<StatsFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The system every player is attached to, once the data was loaded (the first login).</summary>
    public PlayerStatSystem? System => _system;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_world is not null)
        {
            throw new InvalidOperationException("the stats feature is already attached");
        }

        _world = world;
        world.MapCreated += OnMapCreated;
        foreach (Map map in world.Maps)
        {
            OnMapCreated(map);
        }

        // A level-up or a login base value change moves the stat fields the derived values come from.
        _progression = _services.GetService<ProgressionFeature>()?.Progression;
        if (_progression is not null)
        {
            _progression.BaseValuesApplied += OnBaseValuesApplied;
        }
    }

    public void Dispose()
    {
        if (_world is { } world)
        {
            world.MapCreated -= OnMapCreated;
        }

        if (_progression is not null)
        {
            _progression.BaseValuesApplied -= OnBaseValuesApplied;
        }
    }

    /// <summary>
    /// Load the player base data once (thread safe): build the system, install the imported level stats when no
    /// file is configured, and answer for the maps' combat from then on.
    /// </summary>
    public async Task<PlayerStatSystem> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_system is { } loaded)
        {
            return loaded;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_system is { } raced)
            {
                return raced;
            }

            var options = new StatsOptions();
            _services.GetService<IConfiguration>()?.GetSection(StatsOptions.SectionName).Bind(options);
            PlayerStatsContent content = PlayerStatsContent.Empty;
            await using (AsyncServiceScope scope = _scopes.CreateAsyncScope())
            {
                if (scope.ServiceProvider.GetService<IPlayerStatsContentStore>() is { } store)
                {
                    content = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            ProgressionOptions? progression = _progression?.Options;
            int maxLevel = (int)(progression?.MaxPlayerLevel ?? 60);
            IReadOnlyList<string> problems = Problems(content, maxLevel);
            if (problems.Count > 0 && options.RequireImportedData)
            {
                throw new InvalidOperationException("Stats:RequireImportedData is set and the player base data is incomplete: " + string.Join("; ", problems.Take(5)));
            }

            AgilityRates? rates = BuildRates(content);
            if (rates is null)
            {
                _logger.LogWarning("Stats: no crit/dodge per agility data is imported (player_crit_per_agility, player_dodge_per_agility); crit and dodge get no agility term");
            }

            if (_progression is not null && string.IsNullOrWhiteSpace(progression?.LevelStatsPath) && content.LevelStatsCount > 0 && content.ClassLevelStatsCount > 0)
            {
                _progression.UseLevelStats(BuildLevelStats(content, maxLevel));
            }

            _logger.LogInformation("Stats: {Levels} level stats rows, {Classes} class rows, {Xp} xp rows, {Crit}/{Dodge} agility rate rows{Problems}",
                content.LevelStatsCount, content.ClassLevelStatsCount, content.XpRowCount, content.CritPerAgility.Count, content.DodgePerAgility.Count,
                problems.Count > 0 ? $" ({problems.Count} problems, first: {problems[0]})" : string.Empty);

            var system = new PlayerStatSystem(rates);
            _source.Target = system;
            _system = system;
            return system;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Attach the player to the stat system after its items and base values are loaded (hook order: Items, Progression, Stats).</summary>
    public async Task OnPlayerLoadingAsync(WorldSession session, CharacterRecord character, Player player)
    {
        PlayerStatSystem system = await EnsureLoadedAsync().ConfigureAwait(false);
        system.Attach(player);
    }

    private void OnMapCreated(Map map) => map.Combat.Stats = _source;

    private void OnBaseValuesApplied(Player player)
    {
        if (player.StatState.Maintainer is { } system)
        {
            system.UpdateAll(player);
        }
    }

    /// <summary>
    /// The problems of the data against the playable race/class pairs it holds rows for: level 1 stats per pair and
    /// class, XP up to the maximum level and the rate tables per class; an empty import is one problem.
    /// </summary>
    private static IReadOnlyList<string> Problems(PlayerStatsContent content, int maxLevel)
    {
        if (content.LevelStatsCount == 0 || content.ClassLevelStatsCount == 0)
        {
            return ["the player_levelstats / player_classlevelstats tables are empty"];
        }

        var pairs = content.LevelRows.Select(r => (r.Race, r.Class)).Distinct().ToList();
        return content.Validate(pairs, maxLevel);
    }

    private static AgilityRates? BuildRates(PlayerStatsContent content)
    {
        if (content.CritPerAgility.Count == 0 || content.DodgePerAgility.Count == 0)
        {
            return null;
        }

        return new AgilityRates(Tables(content.CritPerAgility), Tables(content.DodgePerAgility));
    }

    private static Dictionary<Class, AgilityRateTable> Tables(IReadOnlyList<AgilityRateRow> rows)
        => rows.Where(r => Enum.IsDefined((Class)r.Class)).GroupBy(r => (Class)r.Class)
            .ToDictionary(g => g.Key, g => AgilityRateTable.FromEntries(g.Select(r => new KeyValuePair<int, float>(r.Level, r.Rate))));

    /// <summary>
    /// The level base values the progression applies: for each race/class and level up to the maximum the race/class
    /// stats joined with the class health and mana, both with the reference's gap fill.
    /// </summary>
    private static PlayerLevelStatsTable BuildLevelStats(PlayerStatsContent content, int maxLevel)
    {
        var rows = new List<KeyValuePair<(byte, byte, byte), PlayerLevelStats>>();
        foreach ((byte race, byte cls) in content.LevelRows.Select(r => (r.Race, r.Class)).Distinct())
        {
            for (int level = 1; level <= maxLevel && level <= byte.MaxValue; level++)
            {
                LevelStats? stats = content.Level(race, cls, (byte)level);
                ClassLevelStats? health = content.ClassLevel(cls, (byte)level);
                if (stats is null || health is null)
                {
                    continue;
                }

                rows.Add(new((race, cls, (byte)level),
                    new PlayerLevelStats(health.BaseHealth, health.BaseMana, stats.Strength, stats.Agility, stats.Stamina, stats.Intellect, stats.Spirit)));
            }
        }

        return new PlayerLevelStatsTable(rows);
    }

    /// <summary>The maps are given the source before the data is loaded; it answers null (the hooks decide) until then.</summary>
    private sealed class ForwardingSource : ICombatStatSource
    {
        public volatile ICombatStatSource? Target;

        public bool? HasOffhandWeapon(Unit unit) => Target?.HasOffhandWeapon(unit);

        public bool? PlayerCanParry(Player player) => Target?.PlayerCanParry(player);

        public bool? PlayerCanBlock(Player player) => Target?.PlayerCanBlock(player);

        public uint? ShieldBlockValue(Unit unit) => Target?.ShieldBlockValue(unit);
    }
}
