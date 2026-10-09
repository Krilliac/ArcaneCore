using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcaneCore.Data.ClientData;

/// <summary>Read named build-5875 DBC rows using the generated WoWDBDefs physical layout.</summary>
public static class DbcDump
{
    // A float column holding NaN or infinity (a mislabelled or corrupt cell) prints as "NaN" instead of failing the dump.
    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static async Task<int> RunAsync(string[] args, string? defaultDirectory, TextWriter output, TextWriter error)
    {
        if (args.Length < 1)
        {
            await error.WriteLineAsync("usage: arcane-db dbc dump <File> [--id N] [--json]");
            return 2;
        }
        string file = args[0];
        uint? selectedId = null;
        bool json = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json") json = true;
            else if (args[i] == "--id" && ++i < args.Length && uint.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) selectedId = id;
            else
            {
                await error.WriteLineAsync("usage: arcane-db dbc dump <File> [--id N] [--json]");
                return 2;
            }
        }

        string name = Path.GetFileName(file);
        if (!ClientDbcDbdLayouts.All.TryGetValue(name, out DbdLayout? layout))
        {
            await error.WriteLineAsync("unknown build-5875 DBC layout: " + name);
            return 2;
        }
        DbdField? idField = layout.Columns.FirstOrDefault(f => f.Name == "ID");
        if (selectedId is not null && (idField is null || idField.WidthBits != 32))
        {
            await error.WriteLineAsync("--id requires a 32-bit ID field in this DBC layout");
            return 2;
        }
        string path = File.Exists(file) ? file : Path.Combine(defaultDirectory ?? string.Empty, file);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 20 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WDBC") throw new InvalidDataException("invalid WDBC header");
            uint records = reader.ReadUInt32(), fields = reader.ReadUInt32(), recordSize = reader.ReadUInt32(), stringSize = reader.ReadUInt32();
            if (fields != layout.Fields || recordSize != layout.RecordSize || 20UL + (ulong)records * recordSize + stringSize != (ulong)stream.Length)
                throw new InvalidDataException("DBC header disagrees with the build-5875 layout or file length");
            if (stringSize > 64 * 1024 * 1024) throw new InvalidDataException("DBC string block exceeds 64 MiB");
            long stringStart = 20L + records * recordSize;
            stream.Position = stringStart;
            byte[] strings = reader.ReadBytes((int)stringSize);
            stream.Position = 20;
            byte[] row = new byte[recordSize];
            bool found = false;
            for (uint index = 0; index < records; index++)
            {
                stream.ReadExactly(row);
                if (selectedId is { } target && (idField!.Offset + 4 > row.Length || BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(idField.Offset)) != target)) continue;
                var values = new Dictionary<string, object?>();
                foreach (DbdField field in layout.Columns)
                {
                    int count = field.Type == "locstring" ? 9 : field.ArrayLength;
                    int width = field.WidthBits / 8;
                    if (width is not (1 or 2 or 4) || field.Offset < 0 || (long)field.Offset + (long)count * width > row.Length)
                        throw new InvalidDataException("field outside record: " + field.Name);
                    object?[] items = new object?[count];
                    for (int n = 0; n < count; n++)
                    {
                        ReadOnlySpan<byte> cell = row.AsSpan(field.Offset + n * width, width);
                        uint value = width switch { 1 => cell[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(cell), _ => BinaryPrimitives.ReadUInt32LittleEndian(cell) };
                        items[n] = field.Type is "string" or "locstring" && !(field.Type == "locstring" && n == 8)
                            ? ReadString(strings, value)
                            : field.Type == "float" ? BitConverter.Int32BitsToSingle(unchecked((int)value))
                            : value;
                    }
                    values[field.Name] = count == 1 ? items[0] : items;
                }
                if (json) await output.WriteLineAsync(JsonSerializer.Serialize(new { file = name, index, fields = values }, Json));
                else await output.WriteLineAsync($"{name} row {index}: " + string.Join(", ", values.Select(p => p.Key + "=" + JsonSerializer.Serialize(p.Value, Json))));
                found = true;
            }
            if (selectedId is not null && !found) return 5;
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await error.WriteLineAsync("dbc dump: " + ex.Message);
            return 5;
        }
    }

    private static string ReadString(byte[] block, uint offset)
    {
        if (offset >= block.Length) throw new InvalidDataException("string offset outside string block");
        int end = Array.IndexOf(block, (byte)0, (int)offset);
        if (end < 0) throw new InvalidDataException("unterminated DBC string");
        return Encoding.UTF8.GetString(block, (int)offset, end - (int)offset);
    }
}
