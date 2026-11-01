using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema.Upgrade.Copy;

internal static partial class DatabaseCopy
{
    /// <summary>
    /// <c>arcane-db verify</c> for one component: for each of <paramref name="tables"/> the row count and the order-independent digest of every value, read
    /// from both databases through the model's CLR types (the same reading as the copy's own verification, <see cref="ModelDigestAsync"/>). Read-only: no
    /// transaction is opened and nothing is written. Each table must exist on both sides with every model column (the caller checks that first). The contexts are
    /// owned by one thread at a time; the connections are opened here and closed again.
    /// </summary>
    public static async Task<IReadOnlyList<CopyTableResult>> VerifyAsync(
        DbContext source, DbContext target, IReadOnlyList<CopyTable> tables, CancellationToken ct)
    {
        ISqlGenerationHelper sourceSql = source.GetService<ISqlGenerationHelper>();
        ISqlGenerationHelper targetSql = target.GetService<ISqlGenerationHelper>();
        bool truncate = target.Database.ProviderName != SqliteProvider; // PostgreSQL and MariaDB keep microseconds (see CopyValues.ToTimestamp)
        var results = new List<CopyTableResult>(tables.Count);
        await source.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        await target.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            DbConnection sourceConnection = source.Database.GetDbConnection();
            DbConnection targetConnection = target.Database.GetDbConnection();
            foreach (CopyTable table in tables)
            {
                (long sourceRows, TableDigest sourceDigest) = await ModelDigestAsync(sourceConnection, null, sourceSql, table, "source", truncate, ct).ConfigureAwait(false);
                (long targetRows, TableDigest targetDigest) = await ModelDigestAsync(targetConnection, null, targetSql, table, "target", truncate, ct).ConfigureAwait(false);
                results.Add(new CopyTableResult(table.Name, sourceRows, targetRows, sourceDigest.Equals(targetDigest)));
            }
        }
        finally
        {
            await target.Database.CloseConnectionAsync().ConfigureAwait(false);
            await source.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return results;
    }
}
