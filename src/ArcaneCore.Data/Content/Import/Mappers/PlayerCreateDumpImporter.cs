using System.Globalization;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a new-character content import read and wrote.</summary>
/// <param name="LevelStatRows">Race/class/level rows of <c>player_levelstats</c> that have <c>player_classlevelstats</c> values and so can be written to the level-stats file.</param>
public sealed record PlayerCreateImportReport(
    int StartPositions,
    int CreateSpells,
    int SpellTargetPositions,
    int LevelStatRows,
    int SkippedRows,
    IReadOnlyList<string> Warnings);

/// <summary>One <c>player_levelstats</c> row (race, class, level and the five base stats).</summary>
internal sealed class PlayerLevelStatsSourceRow
{
    public uint Race { get; set; }

    public uint Class { get; set; }

    public uint Level { get; set; }

    public uint Str { get; set; }

    public uint Agi { get; set; }

    public uint Sta { get; set; }

    public uint Inte { get; set; }

    public uint Spi { get; set; }
}

/// <summary>One <c>player_classlevelstats</c> row (class, level, base health and mana).</summary>
internal sealed class PlayerClassLevelStatsSourceRow
{
    public uint Class { get; set; }

    public uint Level { get; set; }

    public uint BaseHp { get; set; }

    public uint BaseMana { get; set; }
}

/// <summary>
/// New-character content of a cmangos classic-db or vmangos world dump:
/// <list type="bullet">
/// <item><c>playercreateinfo</c> -> <see cref="PlayerCreateInfoRow"/> (start map, zone, position;
/// vmangos <c>ObjectMgr::LoadPlayerInfo</c>, src/game/ObjectMgr.cpp:4525);</item>
/// <item><c>playercreateinfo_spell</c> -> <see cref="PlayerCreateSpellRow"/> (the spells a new
/// character knows; vmangos selects <c>WHERE 5875 BETWEEN build_min AND build_max</c>, :4679);</item>
/// <item><c>spell_target_position</c> -> <see cref="SpellTargetPositionRow"/> (fixed teleport
/// destinations; vmangos <c>SpellMgr::LoadSpellTargetPositions</c>, src/game/Spells/SpellMgr.cpp:52, the
/// same build filter);</item>
/// <item><c>player_levelstats</c> joined with <c>player_classlevelstats</c> (base health/mana by class
/// and level, :4801, and the five stats by race/class/level, :4898) -> the text file
/// <c>Progression:LevelStatsPath</c> reads (<see cref="WriteLevelStats"/>), which is how the game
/// takes level-up base values.</item>
/// </list>
/// Rows of the first three tables are written to the world database like the other importers; a
/// level-stats row without class values cannot be written and is reported. The dumps are GPL data and
/// are never committed.
/// </summary>
public sealed class PlayerCreateDumpImporter
{
    /// <summary>Client build 5875 (vmangos <c>SUPPORTED_CLIENT_BUILD</c>).</summary>
    public const int ClientBuild = CreatureDumpImporter.ClientBuild;

    // The row's own names for the start position; the other columns match by name.
    private static readonly RowMapper<PlayerCreateInfoRow> s_startMapper = new(new Dictionary<string, string>
    {
        ["map"] = nameof(PlayerCreateInfoRow.MapId),
        ["zone"] = nameof(PlayerCreateInfoRow.ZoneId),
        ["position_x"] = nameof(PlayerCreateInfoRow.X),
        ["position_y"] = nameof(PlayerCreateInfoRow.Y),
        ["position_z"] = nameof(PlayerCreateInfoRow.Z),
    });

    private static readonly RowMapper<PlayerCreateSpellRow> s_spellMapper = new();
    private static readonly RowMapper<SpellTargetPositionRow> s_targetMapper = new();
    private static readonly RowMapper<PlayerLevelStatsSourceRow> s_levelMapper = new();
    private static readonly RowMapper<PlayerClassLevelStatsSourceRow> s_classMapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<(uint, uint), PlayerCreateInfoRow> _start = [];
    private readonly Dictionary<(uint, uint, uint), PlayerCreateSpellRow> _spells = [];
    private readonly Dictionary<uint, SpellTargetPositionRow> _targets = [];
    private readonly Dictionary<(uint, uint, uint), PlayerLevelStatsSourceRow> _levels = [];
    private readonly Dictionary<(uint, uint), PlayerClassLevelStatsSourceRow> _classes = [];
    private readonly MapDiagnostics _diagnostics = new();
    private int _skipped;

    internal static bool ReadsStartColumn(string column) => s_startMapper.Maps(column);

    internal static bool ReadsSpellColumn(string column) => s_spellMapper.Maps(column) || IsBuildColumn(column);

    internal static bool ReadsTargetColumn(string column) => s_targetMapper.Maps(column) || IsBuildColumn(column);

    internal static bool ReadsLevelColumn(string column) => s_levelMapper.Maps(column);

    internal static bool ReadsClassColumn(string column) => s_classMapper.Maps(column);

    private static bool IsBuildColumn(string column)
        => column.Equals("build_min", StringComparison.OrdinalIgnoreCase) || column.Equals("build_max", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            switch (row.Table.ToLowerInvariant())
            {
                case "playercreateinfo":
                    uint[] startKey = _keys.Read(row, "playercreateinfo");
                    _start[(startKey[0], startKey[1])] = s_startMapper.Map(row, _diagnostics);
                    break;
                case "playercreateinfo_spell":
                    uint[] spellKey = _keys.Read(row, "playercreateinfo_spell");
                    if (InBuild(row))
                    {
                        _spells[(spellKey[0], spellKey[1], spellKey[2])] = s_spellMapper.Map(row, _diagnostics);
                    }

                    break;
                case "spell_target_position":
                    uint[] targetKey = _keys.Read(row, "spell_target_position");
                    if (InBuild(row))
                    {
                        _targets[targetKey[0]] = s_targetMapper.Map(row, _diagnostics);
                    }

                    break;
                case "player_levelstats":
                    uint[] levelKey = _keys.Read(row, "player_levelstats");
                    _levels[(levelKey[0], levelKey[1], levelKey[2])] = s_levelMapper.Map(row, _diagnostics);
                    break;
                case "player_classlevelstats":
                    uint[] classKey = _keys.Read(row, "player_classlevelstats");
                    _classes[(classKey[0], classKey[1])] = s_classMapper.Map(row, _diagnostics);
                    break;
            }
        }
    }

    /// <summary>The database rows that would be written (for inspection and tests).</summary>
    public (IReadOnlyCollection<PlayerCreateInfoRow> StartPositions, IReadOnlyCollection<PlayerCreateSpellRow> CreateSpells,
        IReadOnlyCollection<SpellTargetPositionRow> SpellTargets) Snapshot()
        => ([.. _start.Values], [.. _spells.Values], [.. _targets.Values]);

    public PlayerCreateImportReport BuildReport()
    {
        (List<PlayerLevelStatsSourceRow> _, List<string> joinWarnings) = JoinLevelStats();
        var warnings = new List<string>(_diagnostics.Samples);
        warnings.AddRange(joinWarnings);
        return new PlayerCreateImportReport(
            _start.Count, _spells.Count, _targets.Count, _levels.Count - joinWarnings.Count, _skipped, warnings);
    }

    /// <summary>
    /// Write the three database tables atomically with the contract of <see cref="ImportTransaction"/>.
    /// With <paramref name="replace"/> they are emptied first (this replaces the dev seeds of
    /// <c>player_create_info</c>); without it an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<PlayerCreateImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var snapshot = Snapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<PlayerCreateInfoRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<PlayerCreateSpellRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<SpellTargetPositionRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, snapshot.StartPositions, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.CreateSpells, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, snapshot.SpellTargets, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    /// <summary>
    /// Write the level-stats file (<c>race,class,level,basehp,basemana,str,agi,sta,int,spi</c>, one row
    /// per line, <c>#</c> comments), the text form of <c>PlayerLevelStatsTable</c>, in race, class,
    /// level order. A <c>player_levelstats</c> row whose class and level have no
    /// <c>player_classlevelstats</c> row is left out (see <see cref="BuildReport"/>).
    /// </summary>
    /// <returns>The number of rows written.</returns>
    public int WriteLevelStats(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        (List<PlayerLevelStatsSourceRow> rows, _) = JoinLevelStats();
        writer.Write("# race,class,level,basehp,basemana,str,agi,sta,int,spi\n");
        writer.Write("# written by arcane-content-importer from player_levelstats and player_classlevelstats; set Progression:LevelStatsPath to this file\n");
        foreach (PlayerLevelStatsSourceRow row in rows)
        {
            PlayerClassLevelStatsSourceRow cls = _classes[(row.Class, row.Level)];
            writer.Write(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Race},{row.Class},{row.Level},{cls.BaseHp},{cls.BaseMana},{row.Str},{row.Agi},{row.Sta},{row.Inte},{row.Spi}\n"));
        }

        return rows.Count;
    }

    private (List<PlayerLevelStatsSourceRow> Rows, List<string> Warnings) JoinLevelStats()
    {
        var rows = new List<PlayerLevelStatsSourceRow>();
        var warnings = new List<string>();
        foreach (PlayerLevelStatsSourceRow row in _levels.Values.OrderBy(r => r.Race).ThenBy(r => r.Class).ThenBy(r => r.Level))
        {
            if (_classes.ContainsKey((row.Class, row.Level)))
            {
                rows.Add(row);
            }
            else
            {
                warnings.Add($"player_levelstats race {row.Race} class {row.Class} level {row.Level}: no player_classlevelstats row for class {row.Class} level {row.Level}; left out of the level-stats file");
            }
        }

        return (rows, warnings);
    }

    /// <summary>vmangos <c>WHERE build_min &lt;= 5875 AND build_max &gt;= 5875</c>; rows without the columns always apply.</summary>
    private bool InBuild(DumpRow row)
    {
        if (!row.Has("build_min"))
        {
            return true;
        }

        int min = Int(row, "build_min", 0);
        int max = Int(row, "build_max", int.MaxValue);
        if (min <= ClientBuild && ClientBuild <= max)
        {
            return true;
        }

        _skipped++;
        return false;
    }

    private static int Int(DumpRow row, string column, int fallback)
        => row.TryGet(out string? raw, column) && !string.IsNullOrEmpty(raw)
            ? (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;
}
