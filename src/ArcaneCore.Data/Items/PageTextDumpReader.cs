using System.Globalization;
using System.IO.Compression;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Data.Items;

/// <summary>
/// Reads the <c>page_text</c> rows (<c>entry</c>, <c>text</c>, <c>next_page</c>; vmangos and cmangos classic-db use the same names) out of a
/// MySQL world dump, plain or <c>.gz</c>, through <see cref="MySqlDumpReader"/>. Every other table of the dump is skipped. This is the data
/// contract of page text until a world-database table carries it: the operator points <c>PageText:DumpPath</c> at the content dump (or an
/// extract holding only <c>page_text</c>, which loads much faster). A row without an entry or with a value that is not a number refuses the
/// file rather than guessing.
/// </summary>
public static class PageTextDumpReader
{
    public const string Table = "page_text";

    public static IReadOnlyList<PageTextRecord> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using Stream file = File.OpenRead(path);
        using Stream input = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new StreamReader(input);
        return Read(reader);
    }

    public static IReadOnlyList<PageTextRecord> Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var pages = new List<PageTextRecord>();
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row || !string.Equals(row.Table, Table, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!row.TryGet(out string? entry, "entry") || entry is null)
            {
                throw new InvalidDataException("page_text: a row has no entry");
            }

            row.TryGet(out string? text, "text");
            row.TryGet(out string? next, "next_page");
            pages.Add(new PageTextRecord(Number(entry, "entry"), text ?? string.Empty, next is null ? 0 : Number(next, "next_page")));
        }

        return pages;
    }

    private static uint Number(string value, string column)
        => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number)
            ? number
            : throw new InvalidDataException($"page_text: {column} '{value}' is not a number");
}
