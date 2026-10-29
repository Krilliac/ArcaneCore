using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What an enchantment proc/charges import read and wrote.</summary>
public sealed record EnchantProcImportReport(int ProcEnchants, int EnchantCharges, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps <c>spell_proc_item_enchant</c> (cmangos classic-db and vmangos: <c>entry</c>, <c>ppmRate</c>;
/// vmangos <c>SpellMgr::LoadSpellProcItemEnchant</c>) and <c>spell_enchant_charges</c> (vmangos only:
/// <c>entry</c>, <c>charges</c>; <c>SpellMgr::LoadSpellEnchantCharges</c>) into
/// <see cref="ItemEnchantProcRow"/> and <see cref="SpellEnchantChargesRow"/> by column name. Both tables
/// are keyed by the enchant spell; a later row with the same key replaces an earlier one. The loaders'
/// spell and first-rank checks need <c>spell_template</c> and the rank chains, so the importer keeps every
/// row; the runtime ignores spells it does not know. Not-owned state: one instance per import run, not
/// thread-safe. The dumps are GPL data and are never committed.
/// </summary>
public sealed class EnchantProcDumpImporter
{
    private const string ProcTable = "spell_proc_item_enchant";
    private const string ChargesTable = "spell_enchant_charges";

    private static readonly RowMapper<ItemEnchantProcRow> s_procMapper = new();
    private static readonly RowMapper<SpellEnchantChargesRow> s_chargesMapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<uint, ItemEnchantProcRow> _procs = [];
    private readonly Dictionary<uint, SpellEnchantChargesRow> _charges = [];
    private readonly MapDiagnostics _diagnostics = new();

    /// <summary>Whether a column of <c>spell_proc_item_enchant</c> is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsProcColumn(string column) => s_procMapper.Maps(column);

    /// <summary>Whether a column of <c>spell_enchant_charges</c> is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsChargesColumn(string column) => s_chargesMapper.Maps(column);

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

            if (row.Table.Equals(ProcTable, StringComparison.OrdinalIgnoreCase))
            {
                uint entry = _keys.Read(row, ProcTable)[0];
                ItemEnchantProcRow mapped = s_procMapper.Map(row, _diagnostics);
                mapped.Entry = entry;
                _procs[entry] = mapped;
            }
            else if (row.Table.Equals(ChargesTable, StringComparison.OrdinalIgnoreCase))
            {
                uint entry = _keys.Read(row, ChargesTable)[0];
                SpellEnchantChargesRow mapped = s_chargesMapper.Map(row, _diagnostics);
                mapped.Entry = entry;
                _charges[entry] = mapped;
            }
        }
    }

    /// <summary>The proc rows that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<ItemEnchantProcRow> ProcSnapshot() => [.. _procs.Values];

    /// <summary>The charge rows that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<SpellEnchantChargesRow> ChargesSnapshot() => [.. _charges.Values];

    public EnchantProcImportReport BuildReport() => new(_procs.Count, _charges.Count, _diagnostics.Samples);

    /// <summary>
    /// Write the rows atomically with the contract of <see cref="ImportTransaction"/>. With
    /// <paramref name="replace"/> a table is emptied first only when the dump carried rows for it: classic-db has
    /// no <c>spell_enchant_charges</c>, and an import of it must not wipe charges an earlier vmangos import wrote.
    /// Without <paramref name="replace"/> an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<EnchantProcImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyCollection<ItemEnchantProcRow> procs = ProcSnapshot();
        IReadOnlyCollection<SpellEnchantChargesRow> charges = ChargesSnapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace && procs.Count > 0)
                {
                    await db.Set<ItemEnchantProcRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                if (replace && charges.Count > 0)
                {
                    await db.Set<SpellEnchantChargesRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, procs, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, charges, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }
}
