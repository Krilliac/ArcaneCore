using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Items;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a page text and enchantment template import read.</summary>
public sealed record ItemTextImportReport(int Pages, int EnchantmentTemplates, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads <c>page_text</c> (vmangos ObjectMgr::LoadPageTexts, cmangos classic-db: <c>entry</c>, <c>text</c>, <c>next_page</c>) and
/// <c>item_enchantment_template</c> (vmangos LoadRandomEnchantmentsTable: <c>entry</c>, <c>ench</c>, <c>chance</c>; vmangos rows outside patch 10
/// are dropped) out of a MySQL world dump and writes them to the world's <c>page_text</c> and <c>item_enchantment_template</c> (world 70). The
/// world used to read the dump itself at every start; it now reads these tables. The parsing is <see cref="PageTextDumpReader.Parse"/> and
/// <see cref="ItemEnchantmentTemplateDumpReader.Parse"/>. A later row with the same key replaces an earlier one. The catalog rules (a page whose
/// next page is missing, a loop) are the world's, applied when it builds the catalog, so every row is kept. Not-owned state: one instance per
/// import run, not thread-safe. The dumps are GPL data and are never committed.
/// </summary>
public sealed class ItemTextDumpImporter
{
    private readonly SortedDictionary<uint, PageTextRow> _pages = [];
    private readonly SortedDictionary<(uint Entry, uint Ench), ItemEnchantmentTemplateRow> _templates = [];

    /// <summary>Whether an input carried <c>page_text</c> rows (a refresh replaces the table only then).</summary>
    public bool HasPages => _pages.Count > 0;

    /// <summary>Whether an input carried <c>item_enchantment_template</c> rows inside patch 10 (a refresh replaces the table only then).</summary>
    public bool HasEnchantmentTemplates => _templates.Count > 0;

    /// <summary>The page rows that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<PageTextRow> PageSnapshot() => [.. _pages.Values];

    /// <summary>The enchantment template rows that would be written (for inspection and tests).</summary>
    public IReadOnlyCollection<ItemEnchantmentTemplateRow> TemplateSnapshot() => [.. _templates.Values];

    public ItemTextImportReport BuildReport() => new(_pages.Count, _templates.Count, []);

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    /// <exception cref="InvalidDataException">A row has no key or a value that is not a number.</exception>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (PageTextDumpReader.Parse(row) is { } page)
            {
                _pages[page.Entry] = new PageTextRow { Entry = page.Entry, Text = page.Text, NextPage = page.NextPage };
            }
            else if (ItemEnchantmentTemplateDumpReader.Parse(row) is { } chance)
            {
                _templates[(chance.Entry, chance.EnchantId)] = new ItemEnchantmentTemplateRow { Entry = chance.Entry, Ench = chance.EnchantId, Chance = chance.Chance };
            }
        }
    }

    /// <summary>
    /// Write the rows atomically with the contract of <see cref="ImportTransaction"/>. With <paramref name="replace"/> a table is emptied first only
    /// when the dump carried rows for it, so importing a dump without one of the tables never wipes what an earlier import wrote. Without
    /// <paramref name="replace"/> an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<ItemTextImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                await WriteRowsAsync(db, replace, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }

    /// <summary>Replace the tables the dump carried rows for (a refresh; inside the caller's transaction).</summary>
    public async Task ReplaceAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await WriteRowsAsync(db, replace: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteRowsAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken)
    {
        if (replace && HasPages)
        {
            await db.Set<PageTextRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (replace && HasEnchantmentTemplates)
        {
            await db.Set<ItemEnchantmentTemplateRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await ImportBatch.InsertAsync(db, _pages.Values, cancellationToken).ConfigureAwait(false);
        await ImportBatch.InsertAsync(db, _templates.Values, cancellationToken).ConfigureAwait(false);
    }
}
