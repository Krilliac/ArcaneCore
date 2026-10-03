namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// What a scan found in one source table. <see cref="Columns"/> are the table's columns (its
/// <c>CREATE TABLE</c> plus any <c>INSERT</c> column lists); <see cref="MappedColumns"/> are the
/// ones an importer reads and <see cref="UnmappedColumns"/> the ones it ignores.
/// <see cref="DuplicateKeys"/> counts rows whose key was already seen (later rows replace earlier ones).
/// </summary>
public sealed record TableScan(
    string Table,
    IReadOnlyList<string> Columns,
    long Rows,
    long DistinctKeys,
    long DuplicateKeys,
    bool HasSpec,
    ContentDialect Dialect,
    IReadOnlyList<string> MappedColumns,
    IReadOnlyList<string> UnmappedColumns);

/// <summary>The outcome of scanning every input before anything is written.</summary>
/// <param name="Tables">Every table seen, by (case-insensitive) name.</param>
/// <param name="Inputs">The input names in the order they were read.</param>
/// <param name="DbVersion">The <c>version</c> text of the first <c>db_version</c> row, when the dump has one.</param>
/// <param name="UnappliedStatements">Counts of <c>UPDATE</c>/<c>DELETE</c>/<c>ALTER</c>/<c>TRUNCATE</c> statements that were read past, as <c>"VERB table"</c>.</param>
public sealed record ScanResult(
    IReadOnlyDictionary<string, TableScan> Tables,
    IReadOnlyList<string> Inputs,
    string? DbVersion,
    IReadOnlyDictionary<string, int> UnappliedStatements)
{
    /// <summary>The dialect family names (<c>cmangos</c>/<c>vmangos</c>) of the tables that have one.</summary>
    public IReadOnlySet<string> Families => Tables.Values
        .Where(t => t.Dialect != ContentDialect.Unknown)
        .Select(t => t.Dialect.Family())
        .ToHashSet(StringComparer.Ordinal);
}
