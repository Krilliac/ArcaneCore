using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.WorldState;

/// <summary>
/// Reads <c>game_weather</c> and <c>exploration_basexp</c> out of a mysqldump-style SQL file (the
/// cmangos classic-db and vmangos world dumps), by COLUMN NAME taken from the dump's own
/// <c>CREATE TABLE</c>, so a different column order or extra columns do not shift values. Nothing is
/// bundled: the operator points the importer at their own dump. Parsing completes before the
/// database is touched and the import is one transaction, so a mangled file changes nothing.
/// </summary>
public static class WorldStateDumpImporter
{
    private static readonly string[] s_weatherColumns =
    [
        "spring_rain_chance", "spring_snow_chance", "spring_storm_chance",
        "summer_rain_chance", "summer_snow_chance", "summer_storm_chance",
        "fall_rain_chance", "fall_snow_chance", "fall_storm_chance",
        "winter_rain_chance", "winter_snow_chance", "winter_storm_chance",
    ];

    /// <summary>Parse the two tables out of <paramref name="dump"/>; throws <see cref="InvalidDataException"/> on anything malformed.</summary>
    public static WorldStateContent Parse(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var columns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var rows = new Dictionary<string, List<long[]>>(StringComparer.Ordinal)
        {
            [WorldStateDataModule.GameWeatherTable] = [],
            [WorldStateDataModule.ExplorationBaseXpTable] = [],
        };

        string? creating = null;
        while (dump.ReadLine() is { } line)
        {
            if (creating is not null)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith('`'))
                {
                    int end = trimmed.IndexOf('`', 1);
                    if (end < 0)
                    {
                        throw new InvalidDataException($"unterminated column name in CREATE TABLE {creating}");
                    }

                    columns[creating].Add(trimmed[1..end]);
                }
                else if (trimmed.StartsWith(')'))
                {
                    creating = null;
                }

                continue;
            }

            foreach (string table in rows.Keys)
            {
                if (line.StartsWith($"CREATE TABLE `{table}` (", StringComparison.Ordinal))
                {
                    creating = table;
                    columns[table] = [];
                }
                else if (line.StartsWith($"INSERT INTO `{table}` VALUES ", StringComparison.Ordinal))
                {
                    rows[table].AddRange(ParseTuples(table, line[$"INSERT INTO `{table}` VALUES ".Length..]));
                }
            }
        }

        return Build(columns, rows);
    }

    private static WorldStateContent Build(Dictionary<string, List<string>> columns, Dictionary<string, List<long[]>> rows)
    {
        List<GameWeatherRecord> weather = [];
        if (rows[WorldStateDataModule.GameWeatherTable].Count > 0)
        {
            List<string> cols = Columns(columns, WorldStateDataModule.GameWeatherTable);
            int zone = Index(cols, "zone", WorldStateDataModule.GameWeatherTable);
            int[] chance = s_weatherColumns.Select(c => Index(cols, c, WorldStateDataModule.GameWeatherTable)).ToArray();
            foreach (long[] row in rows[WorldStateDataModule.GameWeatherTable])
            {
                Check(row, cols.Count, WorldStateDataModule.GameWeatherTable);
                weather.Add(new GameWeatherRecord(ToUInt(row[zone], "zone"), chance.Select(i => ToUInt(row[i], cols[i])).ToArray()));
            }
        }

        List<ExplorationBaseXpRecord> baseXp = [];
        if (rows[WorldStateDataModule.ExplorationBaseXpTable].Count > 0)
        {
            List<string> cols = Columns(columns, WorldStateDataModule.ExplorationBaseXpTable);
            int level = Index(cols, "level", WorldStateDataModule.ExplorationBaseXpTable);
            int xp = Index(cols, "basexp", WorldStateDataModule.ExplorationBaseXpTable);
            foreach (long[] row in rows[WorldStateDataModule.ExplorationBaseXpTable])
            {
                Check(row, cols.Count, WorldStateDataModule.ExplorationBaseXpTable);
                baseXp.Add(new ExplorationBaseXpRecord(ToUInt(row[level], "level"), ToUInt(row[xp], "basexp")));
            }
        }

        if (weather.Select(w => w.Zone).Distinct().Count() != weather.Count)
        {
            throw new InvalidDataException("game_weather has duplicate zones");
        }

        if (baseXp.Select(b => b.Level).Distinct().Count() != baseXp.Count)
        {
            throw new InvalidDataException("exploration_basexp has duplicate levels");
        }

        return new WorldStateContent(weather, baseXp);
    }

    private static List<string> Columns(Dictionary<string, List<string>> columns, string table)
        => columns.TryGetValue(table, out List<string>? cols)
            ? cols
            : throw new InvalidDataException($"the dump has INSERT rows for {table} but no CREATE TABLE to name the columns");

    private static int Index(List<string> columns, string name, string table)
    {
        int index = columns.IndexOf(name);
        return index >= 0 ? index : throw new InvalidDataException($"{table} has no column '{name}'");
    }

    private static void Check(long[] row, int columnCount, string table)
    {
        if (row.Length != columnCount)
        {
            throw new InvalidDataException($"{table}: a row has {row.Length} values but the table has {columnCount} columns");
        }
    }

    private static uint ToUInt(long value, string column)
        => value is >= 0 and <= uint.MaxValue ? (uint)value : throw new InvalidDataException($"column '{column}': {value} is out of range");

    // (1,2,3),(4,5,6); — numbers only; anything else (NULL, strings) is malformed for these tables.
    private static List<long[]> ParseTuples(string table, string text)
    {
        var tuples = new List<long[]>();
        int i = 0;
        while (true)
        {
            if (i >= text.Length || text[i] != '(')
            {
                throw new InvalidDataException($"{table}: expected '(' at offset {i}");
            }

            i++;
            var values = new List<long>();
            while (true)
            {
                int start = i;
                if (i < text.Length && text[i] == '-')
                {
                    i++;
                }

                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                if (!long.TryParse(text.AsSpan(start, i - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
                {
                    throw new InvalidDataException($"{table}: not a number at offset {start}");
                }

                values.Add(value);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    continue;
                }

                break;
            }

            if (i >= text.Length || text[i] != ')')
            {
                throw new InvalidDataException($"{table}: expected ')' at offset {i}");
            }

            i++;
            tuples.Add([.. values]);
            if (i < text.Length && text[i] == ',')
            {
                i++;
                continue;
            }

            if (i < text.Length && text[i] == ';' && text[(i + 1)..].Trim().Length == 0)
            {
                return tuples;
            }

            throw new InvalidDataException($"{table}: expected ',' or the end of the statement at offset {i}");
        }
    }

    /// <summary>
    /// The content-importer CLI's write (same contract as the other <c>WriteAsync</c> importers, see <c>ImportTransaction</c>): with
    /// <paramref name="replace"/> both tables are emptied first, without it an existing key fails the write and nothing changes.
    /// Joins the caller's transaction through a savepoint. Returns the row counts written.
    /// </summary>
    public static async Task<(int Weather, int BaseXp)> WriteAsync(WorldDbContext db, WorldStateContent content, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(content);
        GameWeatherRow[] weather = content.Weather.Select(GameWeatherRow.From).ToArray();
        ExplorationBaseXpRow[] baseXp = content.BaseXp.Select(b => new ExplorationBaseXpRow { Level = b.Level, BaseXp = b.BaseXp }).ToArray();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<GameWeatherRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<ExplorationBaseXpRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, weather, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, baseXp, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return (weather.Length, baseXp.Length);
    }

    /// <summary>
    /// Replace both tables with <paramref name="content"/> in one transaction (a failure leaves the
    /// previous rows). Returns the row counts written.
    /// </summary>
    public static async Task<(int Weather, int BaseXp)> ImportAsync(WorldDbContext db, WorldStateContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(content);
        GameWeatherRow[] weather = content.Weather.Select(GameWeatherRow.From).ToArray();
        ExplorationBaseXpRow[] baseXp = content.BaseXp.Select(b => new ExplorationBaseXpRow { Level = b.Level, BaseXp = b.BaseXp }).ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        db.Set<GameWeatherRow>().RemoveRange(await db.Set<GameWeatherRow>().ToListAsync(cancellationToken).ConfigureAwait(false));
        db.Set<ExplorationBaseXpRow>().RemoveRange(await db.Set<ExplorationBaseXpRow>().ToListAsync(cancellationToken).ConfigureAwait(false));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.Set<GameWeatherRow>().AddRange(weather);
        db.Set<ExplorationBaseXpRow>().AddRange(baseXp);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return (weather.Length, baseXp.Length);
    }
}
