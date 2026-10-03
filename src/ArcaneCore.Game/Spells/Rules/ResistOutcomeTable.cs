using System.Globalization;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// The partial-resist outcome distribution (vmangos Unit::RollMagicResistanceMultiplierOutcomeAgainst,
/// Unit.cpp:2427-2458): rows of probabilities (percent) of resisting 100/75/50/25/0 percent of the damage per
/// average resist chance, interpolated linearly between the two rows around the chance, one roll deciding the
/// outcome, with a 100% outcome rounded down to 75%. The 31 rows are retail data that the repository does not
/// ship; a server operator supplies them from their own copy (<see cref="SpellRuleOptions.ResistTablePath"/>).
/// </summary>
public sealed class ResistOutcomeTable
{
    /// <summary>The number of rows vmangos' table has (resistance 0 to 300 in steps of 10).</summary>
    public const int RowCount = 31;

    private readonly (float R100, float R75, float R50, float R25, float R0, float Chance)[] _rows;

    private ResistOutcomeTable((float, float, float, float, float, float)[] rows) => _rows = rows;

    /// <summary>
    /// Parse the table: one row per line, comma separated <c>resist100,resist75,resist50,resist25,resist0,chanceResist</c>
    /// (the field order of vmangos' <c>ResistanceValues</c>); blank lines and lines starting with <c>#</c> are skipped.
    /// Every outcome set must sum to 100 and the chance column must rise from the first row to the last.
    /// </summary>
    /// <exception cref="InvalidDataException">The text is not a well-formed table of <see cref="RowCount"/> rows.</exception>
    public static ResistOutcomeTable Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rows = new List<(float, float, float, float, float, float)>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 6)
            {
                throw new InvalidDataException($"Resist table row {rows.Count + 1} needs 6 comma separated values, found {parts.Length}.");
            }

            var values = new float[6];
            for (int i = 0; i < 6; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || values[i] < 0f)
                {
                    throw new InvalidDataException($"Resist table row {rows.Count + 1}, value {i + 1} is not a non-negative number.");
                }
            }

            if (MathF.Abs(values[0] + values[1] + values[2] + values[3] + values[4] - 100f) > 0.5f)
            {
                throw new InvalidDataException($"Resist table row {rows.Count + 1}: the five outcome percentages must sum to 100.");
            }

            if (rows.Count > 0 && values[5] <= rows[^1].Item6)
            {
                throw new InvalidDataException($"Resist table row {rows.Count + 1}: the chance column must rise.");
            }

            rows.Add((values[0], values[1], values[2], values[3], values[4], values[5]));
        }

        if (rows.Count != RowCount)
        {
            throw new InvalidDataException($"A resist table has {RowCount} rows, found {rows.Count}.");
        }

        if (rows[0].Item6 != 0f)
        {
            throw new InvalidDataException("The first resist table row must have chance 0.");
        }

        return new ResistOutcomeTable([.. rows]);
    }

    /// <summary>Read and parse <paramref name="path"/> (see <see cref="Parse"/>).</summary>
    public static ResistOutcomeTable Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllLines(path));
    }

    /// <summary>
    /// The multiplier of the damage that is resisted, 0, 0.25, 0.5 or 0.75, for an average resist chance of
    /// <paramref name="chancePercent"/> (0-75, already scaled for damage over time) and a roll in [0, 99].
    /// </summary>
    public float Multiplier(float chancePercent, int roll)
    {
        int next = 1;
        while (next < _rows.Length - 1 && _rows[next].Chance < chancePercent)
        {
            next++;
        }

        var low = _rows[next - 1];
        var high = _rows[next];
        float coefficient = (chancePercent - low.Chance) / (high.Chance - low.Chance);
        float r100 = low.R100 + ((high.R100 - low.R100) * coefficient);
        float r75 = low.R75 + ((high.R75 - low.R75) * coefficient);
        float r50 = low.R50 + ((high.R50 - low.R50) * coefficient);
        float r25 = low.R25 + ((high.R25 - low.R25) * coefficient);
        if (roll < r100 + r75)
        {
            return 0.75f; // Unit.cpp:2448-2452: players cannot resist 100%, it is rounded down to 75%
        }

        if (roll < r100 + r75 + r50)
        {
            return 0.5f;
        }

        return roll < r100 + r75 + r50 + r25 ? 0.25f : 0f;
    }
}
