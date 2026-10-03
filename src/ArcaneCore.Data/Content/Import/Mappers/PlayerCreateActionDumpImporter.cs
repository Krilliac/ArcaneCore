using ArcaneCore.Data.Content.Chr;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a starting-action-bar import read and wrote.</summary>
public sealed record PlayerCreateActionImportReport(int Rows, int SkippedRows, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps <c>playercreateinfo_action</c> of a cmangos classic-db or vmangos dump into
/// <see cref="PlayerCreateActionRow"/> by column name, applying the checks vmangos makes while
/// loading (<c>ObjectMgr::LoadPlayerInfo</c>, ObjectMgr.cpp:4737-4796): a race outside
/// <c>RACEMASK_ALL_PLAYABLE</c>, a class outside <c>CLASSMASK_ALL_PLAYABLE</c> and a button or action
/// out of range (<c>Player::IsActionButtonDataValid</c> with no player, Player.cpp:5900-5933) are
/// skipped with a warning. Whether a spell or item exists is checked when a character is created, where
/// the templates are loaded. The dumps are GPL data and are never committed.
/// </summary>
public sealed class PlayerCreateActionDumpImporter
{
    private const string TableName = "playercreateinfo_action";

    /// <summary>RACEMASK_ALL_PLAYABLE (SharedDefines.h:72): races 1..8.</summary>
    private const uint PlayableRaces = 0xFF;

    /// <summary>CLASSMASK_ALL_PLAYABLE (SharedDefines.h:101): classes 1-5, 7-9 and 11.</summary>
    private const uint PlayableClasses = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 6) | (1u << 7) | (1u << 8) | (1u << 10);

    private static readonly RowMapper<PlayerCreateActionRow> s_mapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<(uint, uint, uint), PlayerCreateActionRow> _rows = [];
    private readonly MapDiagnostics _diagnostics = new();
    private readonly List<string> _skips = [];
    private int _skipped;

    /// <summary>Whether a column of the table is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsColumn(string column) => s_mapper.Maps(column);

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && row.Table.Equals(TableName, StringComparison.OrdinalIgnoreCase))
            {
                ReadRow(row);
            }
        }
    }

    /// <summary>The rows that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<PlayerCreateActionRow> Snapshot() => [.. _rows.Values];

    public PlayerCreateActionImportReport BuildReport()
    {
        var warnings = new List<string>(_diagnostics.Samples);
        if (_skipped > 0)
        {
            warnings.Add($"{TableName}: {_skipped} row(s) skipped as vmangos does at load (first: {_skips[0]})");
        }

        return new PlayerCreateActionImportReport(_rows.Count, _skipped, warnings);
    }

    /// <summary>
    /// Write the rows atomically with the contract of <see cref="ImportTransaction"/>. With
    /// <paramref name="replace"/> the table is emptied first; without it an existing key fails the
    /// write and nothing changes.
    /// </summary>
    public async Task<PlayerCreateActionImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyCollection<PlayerCreateActionRow> rows = Snapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<PlayerCreateActionRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, rows, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    private void ReadRow(DumpRow row)
    {
        uint[] key = _keys.Read(row, TableName);
        PlayerCreateActionRow mapped = s_mapper.Map(row, _diagnostics);
        string? reason = null;
        if (key[0] is 0 or > 8 || (PlayableRaces & (1u << (int)(key[0] - 1))) == 0)
        {
            reason = $"race {key[0]} is not playable";
        }
        else if (key[1] is 0 or > 11 || (PlayableClasses & (1u << (int)(key[1] - 1))) == 0)
        {
            reason = $"class {key[1]} does not exist";
        }
        else if (!StartActionRules.IsInRange(mapped.Button, mapped.Action) || key[2] >= StartActionRules.MaxActionButtons)
        {
            reason = $"button {key[2]} / action {mapped.Action} is out of range";
        }

        if (reason is not null)
        {
            _skipped++;
            if (_skips.Count == 0)
            {
                _skips.Add($"race {key[0]} class {key[1]} button {key[2]}: {reason}");
            }

            return;
        }

        _rows[(key[0], key[1], key[2])] = mapped;
    }
}
