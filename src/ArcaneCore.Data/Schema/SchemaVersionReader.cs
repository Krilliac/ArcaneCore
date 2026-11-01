using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Schema;

/// <summary>
/// Reads the schema version a database actually records, with no side effects (it writes nothing and never creates the
/// version table). Used by the cluster link manifest so the versions two nodes compare are the databases' own.
/// </summary>
public static class SchemaVersionReader
{
    /// <summary>
    /// The version in the component's schema-version table (<see cref="SchemaDefinition.VersionTable"/>), or null when the table
    /// or its row does not exist. 0 means a create is in progress (<see cref="SchemaBootstrapper.CreatingVersion"/>). The
    /// context must map the version table (<see cref="SchemaBootstrapper.MapVersionTable"/>), as every ArcaneCore context does.
    /// </summary>
    /// <param name="db">A context on the database to read; its connection is opened when closed and joined to its current transaction.</param>
    /// <param name="definition">The component's schema definition (names the version table).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<int?> ReadAsync(DbContext db, SchemaDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(definition);
        if (!await SchemaCatalog.TableExistsAsync(db, definition.VersionTable, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        SchemaVersionRow? row = await db.Set<SchemaVersionRow>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == 1, cancellationToken).ConfigureAwait(false);
        return row?.Version;
    }
}
