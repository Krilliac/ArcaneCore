using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData.PlayerStats;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.World.PlayerStats;

/// <summary>What a player stats import read and wrote.</summary>
/// <param name="ClassLevelStats">player_classlevelstats rows.</param>
/// <param name="LevelStats">player_levelstats rows.</param>
/// <param name="XpRows">player_xp_for_level rows.</param>
/// <param name="CritRows">player_crit_per_agility rows.</param>
/// <param name="DodgeRows">player_dodge_per_agility rows.</param>
/// <param name="MigrationStatements">Migration statements applied to tracked tables.</param>
/// <param name="UpdatedRows">Rows changed by migration UPDATE statements.</param>
/// <param name="UnmatchedUpdates">Migration UPDATE statements that matched no row.</param>
/// <param name="SkippedRows">Dump rows ignored as invalid (wrong race, class, level or value).</param>
/// <param name="Warnings">Human readable notes (skipped rows, unmatched updates).</param>
public sealed record PlayerStatsImportReport(
    int ClassLevelStats,
    int LevelStats,
    int XpRows,
    int CritRows,
    int DodgeRows,
    int MigrationStatements,
    int UpdatedRows,
    int UnmatchedUpdates,
    int SkippedRows,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Maps the player base data of a cmangos classic-db or vmangos world dump into the world player stats
/// tables by column <b>name</b> (as <see cref="CreatureDumpImporter"/>): <c>player_classlevelstats</c>
/// (class, level, basehp, basemana), <c>player_levelstats</c> (race, class, level, str, agi, sta, inte, spi),
/// <c>player_xp_for_level</c> (lvl, xp_for_next_level) and, where a source has them,
/// <c>player_crit_per_agility</c> / <c>player_dodge_per_agility</c> (class, level, rate). The classic-db dump
/// has the first three; the rate tables exist only in vmangos migrations.
/// <para>
/// vmangos corrected many base rows after the cmangos data was written (classic-db is the base, the
/// migrations are the corrections). <see cref="ApplyMigration"/> replays the tracked statements of a vmangos
/// <c>sql/migrations/*_world.sql</c> on the rows read so far, in call order
/// (see <see cref="VMangosMigrationReader"/> for the supported statements); <see cref="ApplyMigrationDirectory"/>
/// replays a whole directory in file-name (timestamp) order. The dumps and migrations are GPL data and are
/// never committed; tests use hand-written rows in each layout.
/// </para>
/// <para>
/// Row validity follows the reference loaders: wrong race or class ids and level 0 are ignored with a
/// warning (ObjectMgr.cpp:4827-4836, 4927-4936). Levels above the configured maximum are kept; the
/// consumer caps at the maximum level (the reference drops them, ObjectMgr.cpp:4837-4847).
/// </para>
/// </summary>
public sealed class PlayerStatsDumpImporter
{
    /// <summary>The tables this importer tracks, lower case.</summary>
    public static readonly IReadOnlySet<string> TrackedTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "player_classlevelstats", "player_levelstats", "player_xp_for_level", "player_crit_per_agility", "player_dodge_per_agility",
    };

    // CLASSMASK_ALL_PLAYABLE: warrior, paladin, hunter, rogue, priest, shaman, mage, warlock, druid.
    private static readonly byte[] s_playableClasses = [1, 2, 3, 4, 5, 7, 8, 9, 11];

    private readonly Table<PlayerClassLevelStatsRow> _classLevel = new(
        "player_classlevelstats",
        r => $"{r.Class}/{r.Level}",
        ("class", r => r.Class, (r, v) => r.Class = Byte(v)),
        ("level", r => r.Level, (r, v) => r.Level = Byte(v)),
        ("basehp", r => r.BaseHealth, (r, v) => r.BaseHealth = UInt(v)),
        ("basemana", r => r.BaseMana, (r, v) => r.BaseMana = UInt(v)));

    private readonly Table<PlayerLevelStatsRow> _levelStats = new(
        "player_levelstats",
        r => $"{r.Race}/{r.Class}/{r.Level}",
        ("race", r => r.Race, (r, v) => r.Race = Byte(v)),
        ("class", r => r.Class, (r, v) => r.Class = Byte(v)),
        ("level", r => r.Level, (r, v) => r.Level = Byte(v)),
        ("str", r => r.Strength, (r, v) => r.Strength = Byte(v)),
        ("agi", r => r.Agility, (r, v) => r.Agility = Byte(v)),
        ("sta", r => r.Stamina, (r, v) => r.Stamina = Byte(v)),
        ("inte", r => r.Intellect, (r, v) => r.Intellect = Byte(v)),
        ("spi", r => r.Spirit, (r, v) => r.Spirit = Byte(v)));

    private readonly Table<PlayerXpForLevelRow> _xp = new(
        "player_xp_for_level",
        r => r.Level.ToString(CultureInfo.InvariantCulture),
        ("lvl", r => r.Level, (r, v) => r.Level = UInt(v)),
        ("xp_for_next_level", r => r.XpForNextLevel, (r, v) => r.XpForNextLevel = UInt(v)));

    private readonly Table<PlayerCritPerAgilityRow> _crit = new(
        "player_crit_per_agility",
        r => $"{r.Class}/{r.Level}",
        ("class", r => r.Class, (r, v) => r.Class = Byte(v)),
        ("level", r => r.Level, (r, v) => r.Level = Byte(v)),
        ("rate", r => r.Rate, (r, v) => r.Rate = Rate(v)));

    private readonly Table<PlayerDodgePerAgilityRow> _dodge = new(
        "player_dodge_per_agility",
        r => $"{r.Class}/{r.Level}",
        ("class", r => r.Class, (r, v) => r.Class = Byte(v)),
        ("level", r => r.Level, (r, v) => r.Level = Byte(v)),
        ("rate", r => r.Rate, (r, v) => r.Rate = Rate(v)));

    private readonly List<string> _warnings = [];
    private int _skipped;
    private int _migrationStatements;
    private int _updatedRows;
    private int _unmatchedUpdates;

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row)
            {
                ReadRow(row);
            }
        }
    }

    /// <summary>
    /// Replay the tracked statements of one vmangos world migration on the rows read so far. Throws
    /// <see cref="NotSupportedException"/> for a statement the importer cannot replay faithfully.
    /// </summary>
    public void ApplyMigration(string name, TextReader sql)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (MigrationStatement statement in VMangosMigrationReader.Parse(sql, name, TrackedTables))
        {
            _migrationStatements++;
            switch (statement)
            {
                case MigrationInsert insert:
                    ReadRow(insert.Row);
                    break;
                case MigrationUpdate update:
                    ApplyUpdate(name, update);
                    break;
            }
        }
    }

    /// <summary>
    /// Replay every <c>*_world.sql</c> file of a vmangos <c>sql/migrations</c> directory in file-name order
    /// (the names start with a timestamp, so this is the order vmangos applies them). Returns the names of the
    /// files that contained tracked statements.
    /// </summary>
    public IReadOnlyList<string> ApplyMigrationDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var applied = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, "*_world.sql").Order(StringComparer.Ordinal))
        {
            int before = _migrationStatements;
            using var reader = new StreamReader(path);
            ApplyMigration(Path.GetFileName(path), reader);
            if (_migrationStatements != before)
            {
                applied.Add(Path.GetFileName(path));
            }
        }

        return applied;
    }

    /// <summary>The content that would be written (for inspection, tests and startup validation without a database).</summary>
    public PlayerStatsContent ToContent() => new(
        _classLevel.Rows.Values.Select(r => new ClassLevelStats(r.Class, r.Level, r.BaseHealth, r.BaseMana)),
        _levelStats.Rows.Values.Select(r => new LevelStats(r.Race, r.Class, r.Level, r.Strength, r.Agility, r.Stamina, r.Intellect, r.Spirit)),
        _xp.Rows.Values.Select(r => (r.Level, r.XpForNextLevel)),
        _crit.Rows.Values.Select(r => new AgilityRateRow(r.Class, r.Level, r.Rate)),
        _dodge.Rows.Values.Select(r => new AgilityRateRow(r.Class, r.Level, r.Rate)));

    public PlayerStatsImportReport BuildReport() => new(
        _classLevel.Rows.Count, _levelStats.Rows.Count, _xp.Rows.Count, _crit.Rows.Count, _dodge.Rows.Count,
        _migrationStatements, _updatedRows, _unmatchedUpdates, _skipped, [.. _warnings]);

    /// <summary>
    /// Write everything read so far atomically, with the same transaction contract as
    /// <see cref="CreatureDumpImporter.WriteAsync"/>: an empty change tracker is required, a caller's EF
    /// transaction is protected by a savepoint and stays the caller's, otherwise this method owns the
    /// transaction; a failure or cancellation restores the previous rows. With <paramref name="replace"/> the
    /// five tables are emptied first; without it a row whose key already exists fails the import (and rolls it back).
    /// </summary>
    public async Task<PlayerStatsImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Player stats imports require an empty change tracker; save caller changes and clear tracking, or use a dedicated context.");
        }

        IDbContextTransaction? callerTransaction = db.Database.CurrentTransaction;
        if (callerTransaction is null && System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Player stats imports require an explicit EF transaction when a caller owns the transaction; ambient transactions are not supported.");
        }

        if (callerTransaction is { SupportsSavepoints: false })
        {
            throw new InvalidOperationException("The caller's transaction does not support savepoints; the import cannot protect its existing work.");
        }

        await using IDbContextTransaction? ownedTransaction = callerTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        IDbContextTransaction transaction = callerTransaction ?? ownedTransaction!;
        string? savepoint = callerTransaction is not null ? "ArcanePlayerStatsImport_" + Guid.NewGuid().ToString("N") : null;
        if (savepoint is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
        }

        bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            await ClearAsync(db, replace, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _classLevel.Rows.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _levelStats.Rows.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _xp.Rows.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _crit.Rows.Values, cancellationToken).ConfigureAwait(false);
            await InsertBatchedAsync(db, _dodge.Rows.Values, cancellationToken).ConfigureAwait(false);

            if (savepoint is not null)
            {
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception importError)
        {
            try
            {
                if (savepoint is not null)
                {
                    await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                    await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Player stats import and rollback failed; discard the context and transaction.", importError, rollbackError);
            }

            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = detect;
        }

        return BuildReport();
    }

    private static async Task ClearAsync(WorldDbContext db, bool replace, CancellationToken ct)
    {
        if (!replace)
        {
            return;
        }

        await db.Set<PlayerClassLevelStatsRow>().ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Set<PlayerLevelStatsRow>().ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Set<PlayerXpForLevelRow>().ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Set<PlayerCritPerAgilityRow>().ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Set<PlayerDodgePerAgilityRow>().ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
    private static async Task InsertBatchedAsync<T>(WorldDbContext db, IEnumerable<T> rows, CancellationToken ct)
        where T : class
    {
        const int BatchSize = 2000;
        int pending = 0;
        foreach (T row in rows)
        {
            db.Add((object)row);
            if (++pending == BatchSize)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    private void ReadRow(DumpRow row)
    {
        switch (row.Table.ToLowerInvariant())
        {
            case "player_classlevelstats":
                if (ValidClass(row, "class") && ValidLevel(row, "level", out byte level1))
                {
                    _classLevel.Put(new PlayerClassLevelStatsRow
                    {
                        Class = Byte(row, "class"),
                        Level = level1,
                        BaseHealth = UInt(row, "basehp"),
                        BaseMana = UInt(row, "basemana"),
                    });
                }

                break;
            case "player_levelstats":
                if (ValidRace(row) && ValidClass(row, "class") && ValidLevel(row, "level", out byte level2) && StatsFit(row))
                {
                    _levelStats.Put(new PlayerLevelStatsRow
                    {
                        Race = Byte(row, "race"),
                        Class = Byte(row, "class"),
                        Level = level2,
                        Strength = Byte(row, "str"),
                        Agility = Byte(row, "agi"),
                        Stamina = Byte(row, "sta"),
                        Intellect = Byte(row, "inte"),
                        Spirit = Byte(row, "spi"),
                    });
                }

                break;
            case "player_xp_for_level":
                if (ValidLevel(row, "lvl", out byte level3))
                {
                    _xp.Put(new PlayerXpForLevelRow { Level = level3, XpForNextLevel = UInt(row, "xp_for_next_level") });
                }

                break;
            case "player_crit_per_agility":
                if (ValidClass(row, "class") && ValidLevel(row, "level", out byte level4) && ValidRate(row))
                {
                    _crit.Put(new PlayerCritPerAgilityRow { Class = Byte(row, "class"), Level = level4, Rate = Float(row, "rate") });
                }

                break;
            case "player_dodge_per_agility":
                if (ValidClass(row, "class") && ValidLevel(row, "level", out byte level5) && ValidRate(row))
                {
                    _dodge.Put(new PlayerDodgePerAgilityRow { Class = Byte(row, "class"), Level = level5, Rate = Float(row, "rate") });
                }

                break;
        }
    }

    private void ApplyUpdate(string migration, MigrationUpdate update)
    {
        int matched = update.Table switch
        {
            "player_classlevelstats" => _classLevel.Update(update),
            "player_levelstats" => _levelStats.Update(update),
            "player_xp_for_level" => _xp.Update(update),
            "player_crit_per_agility" => _crit.Update(update),
            "player_dodge_per_agility" => _dodge.Update(update),
            _ => throw new NotSupportedException($"migration {migration}: UPDATE on untracked table `{update.Table}`"),
        };
        if (matched == 0)
        {
            _unmatchedUpdates++;
            _warnings.Add($"migration {migration}: UPDATE `{update.Table}` WHERE {string.Join(" AND ", update.Where.Select(w => $"{w.Column}={w.Value.ToString(CultureInfo.InvariantCulture)}"))} matched no row");
        }
        else
        {
            _updatedRows += matched;
        }
    }

    private bool ValidClass(DumpRow row, string column)
    {
        string? raw = Get(row, column);
        if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && v == Math.Floor(v) && v is >= 0 and <= 255 && Array.IndexOf(s_playableClasses, (byte)v) >= 0)
        {
            return true;
        }

        Skip($"{row.Table}: wrong class '{raw}', ignoring");
        return false;
    }

    private bool ValidRace(DumpRow row)
    {
        string? raw = Get(row, "race");
        if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v == Math.Floor(v) && v is >= 1 and <= 8)
        {
            return true;
        }

        Skip($"{row.Table}: wrong race '{raw}', ignoring");
        return false;
    }

    private bool ValidLevel(DumpRow row, string column, out byte level)
    {
        string? raw = Get(row, column);
        if (raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            && v == Math.Floor(v) && v is >= 1 and <= 255)
        {
            level = (byte)v;
            return true;
        }

        level = 0;
        Skip($"{row.Table}: wrong level '{raw}', ignoring");
        return false;
    }

    private bool StatsFit(DumpRow row)
    {
        foreach (string column in (ReadOnlySpan<string>)["str", "agi", "sta", "inte", "spi"])
        {
            string? raw = Get(row, column);
            if (raw is null || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v != Math.Floor(v) || v is < 0 or > 255)
            {
                Skip($"{row.Table}: {column} '{raw}' is not a tinyint, ignoring the row");
                return false;
            }
        }

        return true;
    }

    private bool ValidRate(DumpRow row)
    {
        string? raw = Get(row, "rate");
        if (raw is not null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) && v > 0)
        {
            return true;
        }

        Skip($"{row.Table}: invalid rate '{raw}', ignoring");
        return false;
    }

    private void Skip(string message)
    {
        _skipped++;
        _warnings.Add(message);
    }

    private static string? Get(DumpRow row, string column) => row.TryGet(out string? value, column) ? value : null;

    private static byte Byte(DumpRow row, string column) => (byte)double.Parse(Get(row, column) ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    private static uint UInt(DumpRow row, string column)
    {
        double v = double.Parse(Get(row, column) ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);
        return v <= 0 ? 0 : v >= uint.MaxValue ? uint.MaxValue : (uint)v;
    }

    private static float Float(DumpRow row, string column) => float.Parse(Get(row, column) ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    private static byte Byte(double v)
        => v is >= 0 and <= 255 && v == Math.Floor(v) ? (byte)v : throw new InvalidDataException($"{v.ToString(CultureInfo.InvariantCulture)} does not fit an unsigned tinyint column");

    private static uint UInt(double v)
        => v is >= 0 and <= uint.MaxValue && v == Math.Floor(v) ? (uint)v : throw new InvalidDataException($"{v.ToString(CultureInfo.InvariantCulture)} does not fit an unsigned integer column");

    private static float Rate(double v)
        => v > 0 && double.IsFinite(v) ? (float)v : throw new InvalidDataException($"rate {v.ToString(CultureInfo.InvariantCulture)} must be positive");

    /// <summary>One tracked table: its rows by key and the numeric columns an UPDATE may read or write.</summary>
    private sealed class Table<TRow>(string name, Func<TRow, string> key, params (string Column, Func<TRow, double> Get, Action<TRow, double> Set)[] columns)
        where TRow : class
    {
        public Dictionary<string, TRow> Rows { get; } = new(StringComparer.Ordinal);

        public void Put(TRow row) => Rows[key(row)] = row;

        public int Update(MigrationUpdate update)
        {
            (Func<TRow, double> Get, Action<TRow, double> Set) Find(string column)
            {
                foreach (var c in columns)
                {
                    if (string.Equals(c.Column, column, StringComparison.OrdinalIgnoreCase))
                    {
                        return (c.Get, c.Set);
                    }
                }

                throw new NotSupportedException($"UPDATE `{name}`: unknown column `{column}`");
            }

            var conditions = update.Where.Select(w => (Find(w.Column).Get, w.Value)).ToArray();
            var assignments = update.Set.Select(s => (Find(s.Column).Set, s.Value)).ToArray();
            var matches = Rows.Values.Where(r => conditions.All(c => c.Get(r) == c.Value)).ToList();
            foreach (TRow row in matches)
            {
                string before = key(row);
                foreach ((Action<TRow, double> set, double value) in assignments)
                {
                    set(row, value);
                }

                if (key(row) != before)
                {
                    throw new NotSupportedException($"UPDATE `{name}` changes a key column; not supported");
                }
            }

            return matches.Count;
        }
    }
}
