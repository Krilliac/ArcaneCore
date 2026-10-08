using System.Buffers.Binary;
using System.Globalization;

namespace ArcaneCore.Data.ClientData;

/// <summary>What the header check of one DBC file found.</summary>
public enum ClientDbcStatus
{
    /// <summary>A well-formed WDBC whose field count and record size match the reference layout (or that has none).</summary>
    Loaded,

    /// <summary>No file at the path.</summary>
    Missing,

    /// <summary>Not a WDBC file, or its size does not match its header.</summary>
    Malformed,

    /// <summary>A well-formed WDBC with another field count or record size than the reference layout (another client build).</summary>
    FormatMismatch,
}

/// <summary>The header check of one DBC file.</summary>
public sealed record ClientDbcFileCheck(
    string File, string Path, ClientDbcStatus Status, int Records, int Fields, int RecordSize, ClientDbcLayout? Layout, string? Detail)
{
    public bool IsUsable => Status == ClientDbcStatus.Loaded;

    /// <summary>One line: "loaded 32 records (14 fields, vmangos ...)", "missing", "format mismatch: ...".</summary>
    public string Describe()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        return Status switch
        {
            ClientDbcStatus.Loaded => Layout is null
                ? string.Create(c, $"loaded {Records} records ({Fields} fields, {RecordSize}-byte records; no reference layout, header only)")
                : string.Create(c, $"loaded {Records} records ({Fields} fields, {Layout.Source})"),
            ClientDbcStatus.Missing => "missing",
            ClientDbcStatus.Malformed => "malformed: " + Detail,
            _ => "format mismatch: " + Detail,
        };
    }
}

/// <summary>
/// Checks a DBC file's WDBC header (magic, record count, field count, record size, string block size and the file length they
/// imply) against its reference layout (<see cref="ClientDbcLayouts"/>), as vmangos DBCFileLoader::Load does with its format
/// string. Only the 20-byte header is read, so a whole client directory is checked in milliseconds.
/// </summary>
public static class ClientDbcInspector
{
    private const uint Magic = 0x43424457; // "WDBC"

    /// <summary>Check the file at <paramref name="path"/> against the reference layout of its file name.</summary>
    public static ClientDbcFileCheck Check(string path) => Check(path, ClientDbcLayouts.Find(System.IO.Path.GetFileName(path)));

    /// <summary>Check the file at <paramref name="path"/> against <paramref name="layout"/> (null: header only).</summary>
    public static ClientDbcFileCheck Check(string path, ClientDbcLayout? layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string file = System.IO.Path.GetFileName(path);
        if (!File.Exists(path))
        {
            return new ClientDbcFileCheck(file, path, ClientDbcStatus.Missing, 0, 0, 0, layout, null);
        }

        long length;
        Span<byte> header = stackalloc byte[20];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = stream.Length;
            if (length < 20)
            {
                return new ClientDbcFileCheck(file, path, ClientDbcStatus.Malformed, 0, 0, 0, layout, string.Create(CultureInfo.InvariantCulture, $"{length} bytes, shorter than a WDBC header"));
            }

            stream.ReadExactly(header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClientDbcFileCheck(file, path, ClientDbcStatus.Malformed, 0, 0, 0, layout, "cannot be read: " + ex.Message);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
        {
            return new ClientDbcFileCheck(file, path, ClientDbcStatus.Malformed, 0, 0, 0, layout, "not a WDBC file");
        }

        uint records = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        uint fields = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        uint strings = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        int r = (int)Math.Min(records, int.MaxValue), f = (int)Math.Min(fields, int.MaxValue), s = (int)Math.Min(recordSize, int.MaxValue);
        if (20UL + ((ulong)records * recordSize) + strings != (ulong)length)
        {
            return new ClientDbcFileCheck(file, path, ClientDbcStatus.Malformed, r, f, s, layout,
                string.Create(CultureInfo.InvariantCulture, $"{length} bytes, but the header ({records} records of {recordSize} bytes, {strings}-byte string block) implies {20UL + ((ulong)records * recordSize) + strings}"));
        }

        if (layout is not null && (fields != layout.Fields || recordSize != layout.RecordSize))
        {
            return new ClientDbcFileCheck(file, path, ClientDbcStatus.FormatMismatch, r, f, s, layout,
                string.Create(CultureInfo.InvariantCulture, $"{fields} fields in {recordSize}-byte records, expected {layout.Fields} fields in {layout.RecordSize}-byte records ({layout.Source}); another client build?"));
        }

        return new ClientDbcFileCheck(file, path, ClientDbcStatus.Loaded, r, f, s, layout, null);
    }

    /// <summary>Check every *.dbc file in <paramref name="directory"/> (sorted by name) plus every reference layout file it lacks.</summary>
    public static IReadOnlyList<ClientDbcFileCheck> CheckDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var checks = new Dictionary<string, ClientDbcFileCheck>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.dbc"))
            {
                checks[System.IO.Path.GetFileName(path)] = Check(path);
            }
        }

        foreach (ClientDbcLayout layout in ClientDbcLayouts.All.Values)
        {
            if (!checks.ContainsKey(layout.File))
            {
                checks[layout.File] = Check(System.IO.Path.Combine(directory, layout.File), layout);
            }
        }

        return [.. checks.Values.OrderBy(c => c.File, StringComparer.OrdinalIgnoreCase)];
    }
}
