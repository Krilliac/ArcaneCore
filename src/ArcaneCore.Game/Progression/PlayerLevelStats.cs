using System.Globalization;

namespace ArcaneCore.Game.Progression;

/// <summary>
/// Base values of one race/class at one level: vmangos <c>player_classlevelstats</c>
/// (base health and mana) joined with <c>player_levelstats</c> (strength, agility, stamina,
/// intellect, spirit).
/// </summary>
public sealed record PlayerLevelStats(uint BaseHealth, uint BaseMana, uint Strength, uint Agility, uint Stamina, uint Intellect, uint Spirit)
{
    /// <summary>The five stats in STAT_STRENGTH … STAT_SPIRIT order.</summary>
    public uint Stat(int index) => index switch
    {
        0 => Strength,
        1 => Agility,
        2 => Stamina,
        3 => Intellect,
        4 => Spirit,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

/// <summary>Where level stats come from. Absent data means level-ups do not change base values.</summary>
public interface IPlayerLevelStatsSource
{
    /// <summary>The base values for (race, class, level), or null when unknown.</summary>
    PlayerLevelStats? Find(byte race, byte playerClass, byte level);
}

/// <summary>
/// An immutable level stats table. The text form is one row per line,
/// <c>race,class,level,basehp,basemana,str,agi,sta,int,spi</c>; blank lines and lines starting
/// with <c>#</c> are ignored. A malformed or duplicate row rejects the whole table.
/// </summary>
public sealed class PlayerLevelStatsTable : IPlayerLevelStatsSource
{
    private readonly Dictionary<(byte Race, byte Class, byte Level), PlayerLevelStats> _rows;

    public PlayerLevelStatsTable(IEnumerable<KeyValuePair<(byte Race, byte Class, byte Level), PlayerLevelStats>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = [];
        foreach (KeyValuePair<(byte Race, byte Class, byte Level), PlayerLevelStats> row in rows)
        {
            if (row.Key.Level == 0 || !_rows.TryAdd(row.Key, row.Value))
            {
                throw new ArgumentException($"invalid or duplicate level stats row {row.Key}", nameof(rows));
            }
        }
    }

    public static PlayerLevelStatsTable Empty { get; } = new([]);

    public int Count => _rows.Count;

    public PlayerLevelStats? Find(byte race, byte playerClass, byte level) => _rows.GetValueOrDefault((race, playerClass, level));

    /// <summary>Parse the text form; throws <see cref="FormatException"/> naming the first bad line.</summary>
    public static PlayerLevelStatsTable Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rows = new List<KeyValuePair<(byte, byte, byte), PlayerLevelStats>>();
        int lineNumber = 0;
        var seen = new HashSet<(byte, byte, byte)>();
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            string text = line.Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }

            string[] parts = text.Split(',');
            if (parts.Length != 10 || !TryByte(parts[0], out byte race) || !TryByte(parts[1], out byte cls)
                || !TryByte(parts[2], out byte level) || level == 0 || race == 0 || cls == 0)
            {
                throw new FormatException($"level stats line {lineNumber}: expected race,class,level,basehp,basemana,str,agi,sta,int,spi");
            }

            uint[] values = new uint[7];
            for (int i = 0; i < values.Length; i++)
            {
                if (!uint.TryParse(parts[i + 3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out values[i]) || values[i] > 1_000_000)
                {
                    throw new FormatException($"level stats line {lineNumber}: value {i + 4} is not a valid unsigned number");
                }
            }

            if (!seen.Add((race, cls, level)))
            {
                throw new FormatException($"level stats line {lineNumber}: duplicate race {race} class {cls} level {level}");
            }

            rows.Add(new((race, cls, level), new PlayerLevelStats(values[0], values[1], values[2], values[3], values[4], values[5], values[6])));
        }

        return new PlayerLevelStatsTable(rows);
    }

    /// <summary>Load the text form from a file (fail closed: a missing or malformed file throws).</summary>
    public static PlayerLevelStatsTable Load(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader);
    }

    private static bool TryByte(string text, out byte value)
        => byte.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
