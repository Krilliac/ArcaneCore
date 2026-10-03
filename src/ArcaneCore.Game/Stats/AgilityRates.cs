namespace ArcaneCore.Game.Stats;

/// <summary>
/// One class's rate per level (points of agility per percent of crit or dodge): the vmangos
/// <c>player_crit_per_agility</c> / <c>player_dodge_per_agility</c> shape. Built from sniffed rows that
/// have gaps: level 1 and the last level are mandatory, every other gap is linearly interpolated
/// from the previous level to the next defined one (vmangos ObjectMgr.cpp:5115-5146), and a level past
/// the end uses the last rate (ObjectMgr.cpp:5340-5341). The table is at least 60 levels long
/// (PLAYER_MAX_LEVEL, ObjectMgr.cpp:5084) and grows to the highest level supplied (ObjectMgr.cpp:5102-5103).
/// </summary>
public sealed class AgilityRateTable
{
    /// <summary>Minimum table length (vmangos PLAYER_MAX_LEVEL, ObjectMgr.cpp:5084).</summary>
    public const int MinLevels = 60;

    private readonly float[] _rates;

    private AgilityRateTable(float[] rates) => _rates = rates;

    /// <summary>Number of levels the table covers.</summary>
    public int Length => _rates.Length;

    /// <summary>
    /// Build from a per-level array where index 0 is level 1 and 0 means "not defined" (the loader's own
    /// representation). Throws <see cref="ArgumentException"/> when level 1 or the last level is
    /// undefined or any value is negative or not finite. A table shorter than 60 levels is padded with
    /// undefined levels, so it fails unless level 60 is covered (ObjectMgr.cpp:5084, 5124).
    /// </summary>
    public static AgilityRateTable FromSparse(ReadOnlySpan<float> perLevel)
    {
        float[] rates = new float[Math.Max(MinLevels, perLevel.Length)];
        perLevel.CopyTo(rates);
        foreach (float rate in rates)
        {
            if (rate < 0 || !float.IsFinite(rate))
            {
                throw new ArgumentException($"invalid agility rate {rate}", nameof(perLevel));
            }
        }

        if (rates[0] == 0)
        {
            throw new ArgumentException("missing agility rate for level 1", nameof(perLevel));
        }

        if (rates[^1] == 0)
        {
            throw new ArgumentException($"missing agility rate for level {rates.Length}", nameof(perLevel));
        }

        for (int i = 1; i < rates.Length; i++)
        {
            if (rates[i] != 0)
            {
                continue;
            }

            for (int j = i + 1; j < rates.Length; j++)
            {
                if (rates[j] != 0)
                {
                    // InterpolateValueAtIndex(i - 1, rates[i - 1], j, rates[j], i) (Util.h:357-365).
                    float lambda = (i - (float)(i - 1)) / (j - (float)(i - 1));
                    rates[i] = rates[i - 1] + (lambda * (rates[j] - rates[i - 1]));
                    break;
                }
            }
        }

        return new AgilityRateTable(rates);
    }

    /// <summary>
    /// Build from (level, rate) rows. Level 0 and rates that are not positive and finite are rejected
    /// (the reference logs them, ObjectMgr.cpp:5096-5110; this table fails closed instead). A level
    /// given twice keeps the last row, as the loader's sequential assignment does.
    /// </summary>
    public static AgilityRateTable FromEntries(IEnumerable<KeyValuePair<int, float>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = new List<KeyValuePair<int, float>>(entries);
        int highest = MinLevels;
        foreach (KeyValuePair<int, float> row in rows)
        {
            if (row.Key < 1)
            {
                throw new ArgumentException($"invalid agility rate level {row.Key}", nameof(entries));
            }

            if (!(row.Value > 0) || !float.IsFinite(row.Value))
            {
                throw new ArgumentException($"invalid agility rate {row.Value} for level {row.Key}", nameof(entries));
            }

            highest = Math.Max(highest, row.Key);
        }

        float[] sparse = new float[highest];
        foreach (KeyValuePair<int, float> row in rows)
        {
            sparse[row.Key - 1] = row.Value;
        }

        return FromSparse(sparse);
    }

    /// <summary>
    /// The rate at a level (ObjectMgr.cpp:5340-5343). Levels above the table use the last rate; level 0,
    /// which the reference would index out of range, uses level 1.
    /// </summary>
    public float Get(uint level)
    {
        if (level < 1)
        {
            return _rates[0];
        }

        return level > _rates.Length ? _rates[^1] : _rates[level - 1];
    }
}

/// <summary>
/// Crit and dodge from agility per class and level: <c>agility / rate</c> (vmangos Player.cpp:5146-5156).
/// A class without a table gets rate 1, the reference's answer for an undefined class (ObjectMgr.cpp:5334-5338).
/// </summary>
public sealed class AgilityRates
{
    private readonly IReadOnlyDictionary<Class, AgilityRateTable> _crit;
    private readonly IReadOnlyDictionary<Class, AgilityRateTable> _dodge;

    public AgilityRates(IReadOnlyDictionary<Class, AgilityRateTable> crit, IReadOnlyDictionary<Class, AgilityRateTable> dodge)
    {
        _crit = crit ?? throw new ArgumentNullException(nameof(crit));
        _dodge = dodge ?? throw new ArgumentNullException(nameof(dodge));
    }

    /// <summary>ObjectMgr::GetPlayerCritPerAgility.</summary>
    public float CritPerAgility(Class playerClass, uint level)
        => _crit.TryGetValue(playerClass, out AgilityRateTable? table) ? table.Get(level) : 1.0f;

    /// <summary>ObjectMgr::GetPlayerDodgePerAgility.</summary>
    public float DodgePerAgility(Class playerClass, uint level)
        => _dodge.TryGetValue(playerClass, out AgilityRateTable? table) ? table.Get(level) : 1.0f;

    /// <summary>Player::GetMeleeCritFromAgility (Player.cpp:5146-5150).</summary>
    public float MeleeCritFromAgility(Class playerClass, uint level, float agility)
        => agility / CritPerAgility(playerClass, level);

    /// <summary>Player::GetDodgeFromAgility (Player.cpp:5152-5156).</summary>
    public float DodgeFromAgility(Class playerClass, uint level, float agility)
        => agility / DodgePerAgility(playerClass, level);
}
