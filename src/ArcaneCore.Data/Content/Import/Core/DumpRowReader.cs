using System.Globalization;
using ArcaneCore.Data.World.Creatures;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Column parsing shared by the dump importers. The importers disagree on purpose about the
/// <see cref="NumberStyles"/> they accept and the exception a bad value raises (the CLI routes
/// <see cref="ImportSchemaException"/> and <see cref="InvalidDataException"/> to different exit
/// codes), so both are parameters here and each importer keeps its own wording.
/// </summary>
internal static class DumpRowReader
{
    /// <summary>The raw text of the first of <paramref name="columns"/> present and not <c>NULL</c>; otherwise throws what <paramref name="missing"/> builds from the first column name.</summary>
    internal static string Required(DumpRow row, Func<string, Exception> missing, params ReadOnlySpan<string> columns)
        => row.TryGet(out string? raw, columns) && raw is not null ? raw : throw missing(columns[0]);

    /// <summary>Parses <paramref name="raw"/> as an unsigned integer under <paramref name="styles"/> (invariant culture); a failure throws what <paramref name="invalid"/> builds from the column name and the raw text.</summary>
    internal static uint ParseUnsigned(string raw, string column, NumberStyles styles, Func<string, string, Exception> invalid)
        => uint.TryParse(raw, styles, CultureInfo.InvariantCulture, out uint value) ? value : throw invalid(column, raw);

    /// <summary>Parses <paramref name="raw"/> as a signed integer under <paramref name="styles"/> (invariant culture); a failure throws what <paramref name="invalid"/> builds from the column name and the raw text.</summary>
    internal static int ParseSigned(string raw, string column, NumberStyles styles, Func<string, string, Exception> invalid)
        => int.TryParse(raw, styles, CultureInfo.InvariantCulture, out int value) ? value : throw invalid(column, raw);

    /// <summary>
    /// The lenient integer the patch-range columns use: parsed as a double and truncated, so
    /// <c>"1.9"</c> reads as 1 and an out-of-range value wraps; an absent or empty column gives
    /// <paramref name="fallback"/>. A malformed value throws <see cref="FormatException"/>.
    /// </summary>
    internal static int TruncatedInt(DumpRow row, string column, int fallback)
        => row.TryGet(out string? raw, column) && !string.IsNullOrEmpty(raw)
            ? (int)double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;

    /// <summary>The schema-error wording for a missing or <c>NULL</c> required column.</summary>
    internal static ImportSchemaException SchemaMissing(string table, string column)
        => new(table, column, $"table `{table}`: the column `{column}` is missing or NULL");

    /// <summary>The schema-error wording for a column that is not an unsigned number.</summary>
    internal static ImportSchemaException SchemaNotUnsigned(string table, string column, string raw)
        => new(table, column, $"table `{table}`, column `{column}`: value '{raw}' is not an unsigned number");
}
