using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>Batched inserts for the importers (same batch size and tracker discipline as the creature importer).</summary>
public static class ImportBatch
{
    /// <summary>Rows per <c>SaveChanges</c>; keeps the change tracker small.</summary>
    public const int Size = 2000;

    /// <summary>
    /// Insert <paramref name="rows"/> in batches of <see cref="Size"/>, clearing the tracker after
    /// each. The caller owns the transaction and the empty-tracker precondition
    /// (<see cref="ImportTransaction"/>).
    /// </summary>
    public static async Task InsertAsync<T>(DbContext db, IEnumerable<T> rows, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);
        int pending = 0;
        foreach (T row in rows)
        {
            db.Set<T>().Add(row);
            if (++pending == Size)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }
}
