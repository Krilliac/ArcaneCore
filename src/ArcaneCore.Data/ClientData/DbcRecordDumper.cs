using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ArcaneCore.Data.ClientData;

/// <summary>Named build-5875 DBC records for arcane-db dbc dump. Field widths and signedness come from WoWDBDefs.</summary>
public static class DbcRecordDumper
{
    public static void Dump(string path, TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(output);
        string file = Path.GetFileName(path);
        if (!ClientDbcDbdLayouts.All.TryGetValue(file, out DbdLayout? layout))
        {
            throw new InvalidDataException($"no build-5875 layout for {file}");
        }

        ClientDbcFileCheck check = ClientDbcInspector.Check(path);
        if (!check.IsUsable || check.RecordSize != layout.RecordSize)
        {
            throw new InvalidDataException($"{file}: {check.Describe()}");
        }

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] record = new byte[layout.RecordSize];
        long stringsAt = 20L + (long)check.Records * layout.RecordSize;
        // DBC strings are offset from the string block, which follows all records.
        for (int row = 0; row < check.Records; row++)
        {
            input.Position = 20L + (long)row * layout.RecordSize;
            input.ReadExactly(record);
            var parts = new List<string>(layout.Columns.Count);
            foreach (DbdField column in layout.Columns)
            {
                int count = column.ArrayLength * (column.Type == "locstring" ? 9 : 1);
                int width = column.Type is "string" or "locstring" ? 4 : column.WidthBits / 8;
                var values = new string[count];
                for (int element = 0; element < count; element++)
                {
                    ReadOnlySpan<byte> field = record.AsSpan(column.Offset + element * width, width);
                    values[element] = column.Type switch
                    {
                        "float" => BinaryPrimitives.ReadSingleLittleEndian(field).ToString("R", CultureInfo.InvariantCulture),
                        "string" or "locstring" when column.Type != "locstring" || element % 9 != 8
                            => Quote(ReadString(input, stringsAt, BinaryPrimitives.ReadUInt32LittleEndian(field))),
                        _ => Integer(field, column.IsSigned),
                    };
                }

                parts.Add(column.Name + "=" + (count == 1 ? values[0] : "[" + string.Join(",", values) + "]"));
            }

            output.WriteLine($"row {row}: {string.Join(" ", parts)}");
        }
    }

    private static string Integer(ReadOnlySpan<byte> value, bool signed)
        => (signed ? value.Length switch
        {
            1 => ((sbyte)value[0]).ToString(CultureInfo.InvariantCulture),
            2 => BinaryPrimitives.ReadInt16LittleEndian(value).ToString(CultureInfo.InvariantCulture),
            4 => BinaryPrimitives.ReadInt32LittleEndian(value).ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException("unsupported signed DBC integer width"),
        } : value.Length switch
        {
            1 => value[0].ToString(CultureInfo.InvariantCulture),
            2 => BinaryPrimitives.ReadUInt16LittleEndian(value).ToString(CultureInfo.InvariantCulture),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(value).ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidDataException("unsupported unsigned DBC integer width"),
        });

    private static string ReadString(FileStream input, long stringsAt, uint offset)
    {
        long at = stringsAt + offset;
        if (at >= input.Length)
        {
            throw new InvalidDataException($"string offset {offset} exceeds the DBC string block");
        }

        input.Position = at;
        using var bytes = new MemoryStream();
        int next;
        while ((next = input.ReadByte()) > 0)
        {
            bytes.WriteByte((byte)next);
        }

        if (next < 0)
        {
            throw new InvalidDataException("unterminated DBC string");
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
