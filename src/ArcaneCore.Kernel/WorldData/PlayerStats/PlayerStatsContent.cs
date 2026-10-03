namespace ArcaneCore.Kernel.WorldData.PlayerStats;

/// <summary>
/// Base health and mana of a class at a level: vmangos <c>player_classlevelstats</c> (class, level,
/// basehp, basemana; ObjectMgr.cpp:4801, 4856-4857).
/// </summary>
public sealed record ClassLevelStats(byte Class, byte Level, uint BaseHealth, uint BaseMana);

/// <summary>
/// The five base stats of a race/class at a level: vmangos <c>player_levelstats</c> (race, class, level,
/// str, agi, sta, inte, spi; ObjectMgr.cpp:4898, 4958-4959).
/// </summary>
public sealed record LevelStats(byte Race, byte Class, byte Level, byte Strength, byte Agility, byte Stamina, byte Intellect, byte Spirit);

/// <summary>
/// One sniffed row of <c>player_crit_per_agility</c> / <c>player_dodge_per_agility</c> (class, level, rate:
/// points of agility per percent; vmangos ObjectMgr.cpp:5087). Rows have gaps; the consumer interpolates
/// (ObjectMgr.cpp:5115-5146).
/// </summary>
public sealed record AgilityRateRow(byte Class, byte Level, float Rate);

/// <summary>
/// The player base data of the world database, read-only after load: class health/mana, race/class base
/// stats, the XP needed per level and the crit/dodge per agility rows.
/// <para>
/// Lookups follow vmangos' "fill level gaps" rule: a level without a row uses the nearest lower level
/// that has one (ObjectMgr.cpp:4884-4892 and 4996-5004, which log a DB error and copy the previous level);
/// level 1 is mandatory (ObjectMgr.cpp:4876-4882, 4988-4994), which <see cref="Validate"/> checks.
/// </para>
/// </summary>
public sealed class PlayerStatsContent
{
    public static readonly PlayerStatsContent Empty = new([], [], [], [], []);

    private readonly Dictionary<(byte Class, byte Level), ClassLevelStats> _classLevel;
    private readonly Dictionary<(byte Race, byte Class, byte Level), LevelStats> _levelStats;
    private readonly Dictionary<uint, uint> _xp;

    public PlayerStatsContent(
        IEnumerable<ClassLevelStats> classLevelStats,
        IEnumerable<LevelStats> levelStats,
        IEnumerable<(uint Level, uint XpForNextLevel)> xpForLevel,
        IEnumerable<AgilityRateRow> critPerAgility,
        IEnumerable<AgilityRateRow> dodgePerAgility)
    {
        ArgumentNullException.ThrowIfNull(classLevelStats);
        ArgumentNullException.ThrowIfNull(levelStats);
        ArgumentNullException.ThrowIfNull(xpForLevel);
        ArgumentNullException.ThrowIfNull(critPerAgility);
        ArgumentNullException.ThrowIfNull(dodgePerAgility);
        _classLevel = classLevelStats.ToDictionary(r => (r.Class, r.Level));
        _levelStats = levelStats.ToDictionary(r => (r.Race, r.Class, r.Level));
        _xp = xpForLevel.ToDictionary(r => r.Level, r => r.XpForNextLevel);
        CritPerAgility = [.. critPerAgility.OrderBy(r => r.Class).ThenBy(r => r.Level)];
        DodgePerAgility = [.. dodgePerAgility.OrderBy(r => r.Class).ThenBy(r => r.Level)];
    }

    public int ClassLevelStatsCount => _classLevel.Count;

    public int LevelStatsCount => _levelStats.Count;

    public int XpRowCount => _xp.Count;

    /// <summary>Crit per agility rows, ordered by class and level.</summary>
    public IReadOnlyList<AgilityRateRow> CritPerAgility { get; }

    /// <summary>Dodge per agility rows, ordered by class and level.</summary>
    public IReadOnlyList<AgilityRateRow> DodgePerAgility { get; }

    /// <summary>Every class/level row, ordered.</summary>
    public IEnumerable<ClassLevelStats> ClassLevelRows => _classLevel.Values.OrderBy(r => r.Class).ThenBy(r => r.Level);

    /// <summary>Every race/class/level row, ordered.</summary>
    public IEnumerable<LevelStats> LevelRows => _levelStats.Values.OrderBy(r => r.Race).ThenBy(r => r.Class).ThenBy(r => r.Level);

    /// <summary>The XP rows (level, XP to reach the next level), ordered.</summary>
    public IEnumerable<(uint Level, uint XpForNextLevel)> XpRows => _xp.OrderBy(r => r.Key).Select(r => (r.Key, r.Value));

    /// <summary>The exact row for a class and level, or null.</summary>
    public ClassLevelStats? FindClassLevelExact(byte playerClass, byte level) => _classLevel.GetValueOrDefault((playerClass, level));

    /// <summary>The exact row for a race, class and level, or null.</summary>
    public LevelStats? FindLevelExact(byte race, byte playerClass, byte level) => _levelStats.GetValueOrDefault((race, playerClass, level));

    /// <summary>
    /// The class health/mana at a level, using the nearest lower level that has a row (vmangos gap fill);
    /// null when the class has no row at or below the level.
    /// </summary>
    public ClassLevelStats? ClassLevel(byte playerClass, byte level)
    {
        for (int l = level; l >= 1; l--)
        {
            if (_classLevel.TryGetValue((playerClass, (byte)l), out ClassLevelStats? row))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// The race/class base stats at a level, using the nearest lower level that has a row (vmangos gap
    /// fill); null when the pair has no row at or below the level.
    /// </summary>
    public LevelStats? Level(byte race, byte playerClass, byte level)
    {
        for (int l = level; l >= 1; l--)
        {
            if (_levelStats.TryGetValue((race, playerClass, (byte)l), out LevelStats? row))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The XP needed to go from <paramref name="level"/> to the next, or null without a row.</summary>
    public uint? XpForNextLevel(uint level) => _xp.TryGetValue(level, out uint xp) ? xp : null;

    /// <summary>
    /// The startup integrity check the reference performs (and exits on): for every playable race/class
    /// pair level 1 stats exist, each class has level 1 health/mana, XP rows exist for levels 1 up to
    /// <paramref name="maxLevel"/> - 1, and every class in <paramref name="classes"/> has a crit and a dodge
    /// per agility rate for level 1 and the last level (vmangos ObjectMgr.cpp:4876, 4988, 5011-5015, 5117-5129).
    /// Returns one message per problem; empty means valid. Level gaps are not problems (they are filled).
    /// </summary>
    public IReadOnlyList<string> Validate(IEnumerable<(byte Race, byte Class)> pairs, int maxLevel)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var problems = new List<string>();
        var pairList = pairs.Distinct().OrderBy(p => p.Race).ThenBy(p => p.Class).ToList();
        foreach ((byte race, byte cls) in pairList)
        {
            if (!_levelStats.ContainsKey((race, cls, 1)))
            {
                problems.Add($"race {race} class {cls} level 1 does not have stats data");
            }
        }

        foreach (byte cls in pairList.Select(p => p.Class).Distinct().Order())
        {
            if (!_classLevel.ContainsKey((cls, 1)))
            {
                problems.Add($"class {cls} level 1 does not have health/mana data");
            }

            RequireRate(problems, "crit", CritPerAgility, cls, maxLevel);
            RequireRate(problems, "dodge", DodgePerAgility, cls, maxLevel);
        }

        for (uint level = 1; level < maxLevel; level++)
        {
            if (!_xp.ContainsKey(level))
            {
                problems.Add($"level {level} does not have xp for next level data");
            }
        }

        return problems;
    }

    private static void RequireRate(List<string> problems, string kind, IReadOnlyList<AgilityRateRow> rows, byte cls, int maxLevel)
    {
        if (!rows.Any(r => r.Class == cls && r.Level == 1))
        {
            problems.Add($"missing {kind} per agility rate for class {cls} and level 1");
        }

        int highest = rows.Where(r => r.Class == cls).Select(r => (int)r.Level).DefaultIfEmpty(0).Max();
        if (highest < maxLevel)
        {
            problems.Add($"missing {kind} per agility rate for class {cls} and level {maxLevel}");
        }
    }
}

/// <summary>Reads the player base data from the world database.</summary>
public interface IPlayerStatsContentStore
{
    Task<PlayerStatsContent> LoadAsync(CancellationToken cancellationToken = default);
}
