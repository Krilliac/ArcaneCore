using System.Globalization;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Data.Items;

/// <summary>
/// Parses the <c>page_text</c> rows (<c>entry</c>, <c>text</c>, <c>next_page</c>; vmangos and cmangos classic-db use the same names) of a MySQL
/// world dump (plain SQL text; the importer's input layer unpacks <c>.gz</c>), through <see cref="MySqlDumpReader"/>: the front end of <see cref="ArcaneCore.Data.Content.Import.ItemTextDumpImporter"/>,
/// which writes them to the world database (the world no longer reads a dump; <c>PageText:DumpPath</c> is retired). Every other table of the dump is
/// skipped. A row without an entry or with a value that is not a number refuses the file rather than guessing.
/// </summary>
public static class PageTextDumpReader
{
    public const string Table = "page_text";

    public static IReadOnlyList<PageTextRecord> Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var pages = new List<PageTextRecord>();
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && Parse(row) is { } page)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    /// <summary>One dump row as a page, or null when it belongs to another table.</summary>
    /// <exception cref="InvalidDataException">The row has no entry or a value that is not a number.</exception>
    public static PageTextRecord? Parse(DumpRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.Table, Table, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!row.TryGet(out string? entry, "entry") || entry is null)
        {
            throw new InvalidDataException("page_text: a row has no entry");
        }

        row.TryGet(out string? text, "text");
        row.TryGet(out string? next, "next_page");
        return new PageTextRecord(Number(entry, "entry"), text ?? string.Empty, next is null ? 0 : Number(next, "next_page"));
    }

    private static uint Number(string value, string column)
        => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number)
            ? number
            : throw new InvalidDataException($"page_text: {column} '{value}' is not a number");
}
