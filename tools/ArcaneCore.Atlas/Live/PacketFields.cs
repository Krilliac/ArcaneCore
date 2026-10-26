using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Protocol;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Builds a packet payload for <c>live.inject</c> and <c>live.send</c> (docs/areas/atlas.md, "Driving the server headlessly"): raw hex,
/// or typed fields written the way the server and the 1.12.1 client lay them out, all little-endian. A field is <c>type:value</c>:
/// <c>u8</c>, <c>u16</c>, <c>u32</c>, <c>u64</c> (decimal, or hex after <c>0x</c>; a negative decimal is written as its two's complement),
/// <c>f32</c> (an invariant-culture number), <c>guid</c> (a full 8-byte guid in hex, not the packed form) and <c>cstring</c> (UTF-8 text
/// and a 0 terminator; everything after the first colon is the text). Pure; any thread.
/// </summary>
internal static class PacketFields
{
    // ref: vmangos@0e3ff01 src/framework/Utilities/ByteConverter.h:51-56 (EndianConvert is a no-op on little-endian hosts: integers and floats go on the wire as the x86 client keeps them)
    // ref: vmangos@0e3ff01 src/shared/ByteBuffer.h:180-185 (a std::string is appended with a trailing 0)
    // ref: src/ArcaneCore.Protocol/PacketWriter.cs:27-55 (the server's WriteUInt16/32/64 and WriteSingle: BinaryPrimitives little-endian)
    // ref: src/ArcaneCore.Protocol/PacketWriter.cs:105-117 (WriteCString: UTF-8 then a 0 byte)

    /// <summary>The field types, in the order the help lists them.</summary>
    public static readonly IReadOnlyList<string> Types = ["u8", "u16", "u32", "u64", "f32", "guid", "cstring"];

    /// <summary>The largest payload this client builds (the server checks its own, smaller limits per direction).</summary>
    public const int MaxPayload = 16 * 1024;

    /// <summary>The payload of <paramref name="hex"/> (whitespace ignored) or of <paramref name="fields"/>; at most one may be given.</summary>
    /// <exception cref="CommandException">BadArguments, naming the field or the problem.</exception>
    public static byte[] Build(string command, string? hex, IReadOnlyList<string> fields)
    {
        if (hex is not null && fields.Count > 0)
        {
            throw Bad(command, "give the payload as 'payload' (hex) or as 'fields', not both.");
        }

        byte[] payload = hex is not null ? ParseHex(command, hex) : BuildFields(command, fields);
        return payload.Length > MaxPayload
            ? throw Bad(command, string.Create(CultureInfo.InvariantCulture, $"the payload is {payload.Length} bytes; at most {MaxPayload}."))
            : payload;
    }

    /// <summary>Hex digits, two per byte; spaces, tabs and a leading <c>0x</c> are ignored.</summary>
    public static byte[] ParseHex(string command, string hex)
    {
        string digits = new([.. hex.Where(c => !char.IsWhiteSpace(c))]);
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        try
        {
            return Convert.FromHexString(digits);
        }
        catch (FormatException)
        {
            throw Bad(command, "'payload' must be an even number of hex digits (spaces are ignored).");
        }
    }

    /// <summary>The fields written one after another.</summary>
    public static byte[] BuildFields(string command, IReadOnlyList<string> fields)
    {
        var bytes = new List<byte>();
        Span<byte> scratch = stackalloc byte[8];
        for (int i = 0; i < fields.Count; i++)
        {
            string field = fields[i];
            int colon = field.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw Bad(command, $"field {i + 1} '{Short(field)}' is not type:value (types: {string.Join(", ", Types)}).");
            }

            string type = field[..colon].Trim().ToLowerInvariant();
            string value = type == "cstring" ? field[(colon + 1)..] : field[(colon + 1)..].Trim();
            switch (type)
            {
                case "u8":
                    bytes.Add(unchecked((byte)Integer(command, i, type, value, byte.MaxValue, sbyte.MinValue)));
                    break;
                case "u16":
                    BinaryPrimitives.WriteUInt16LittleEndian(scratch, unchecked((ushort)Integer(command, i, type, value, ushort.MaxValue, short.MinValue)));
                    bytes.AddRange(scratch[..2]);
                    break;
                case "u32":
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch, unchecked((uint)Integer(command, i, type, value, uint.MaxValue, int.MinValue)));
                    bytes.AddRange(scratch[..4]);
                    break;
                case "u64":
                    BinaryPrimitives.WriteUInt64LittleEndian(scratch, Integer(command, i, type, value, ulong.MaxValue, long.MinValue));
                    bytes.AddRange(scratch[..8]);
                    break;
                case "f32":
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float single) || !float.IsFinite(single))
                    {
                        throw Bad(command, $"field {i + 1}: '{Short(value)}' is not a finite number for f32.");
                    }

                    BinaryPrimitives.WriteSingleLittleEndian(scratch, single);
                    bytes.AddRange(scratch[..4]);
                    break;
                case "guid":
                    string guidText = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
                    if (guidText.Length is 0 or > 16 || !ulong.TryParse(guidText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong guid))
                    {
                        throw Bad(command, $"field {i + 1}: '{Short(value)}' is not a guid in hex (1 to 16 digits).");
                    }

                    BinaryPrimitives.WriteUInt64LittleEndian(scratch, guid);
                    bytes.AddRange(scratch[..8]);
                    break;
                case "cstring":
                    if (value.Contains('\0', StringComparison.Ordinal))
                    {
                        throw Bad(command, $"field {i + 1}: a cstring cannot hold a 0 character (it ends the string).");
                    }

                    bytes.AddRange(Encoding.UTF8.GetBytes(value));
                    bytes.Add(0);
                    break;
                default:
                    throw Bad(command, $"field {i + 1}: unknown type '{Short(type)}' (types: {string.Join(", ", Types)}).");
            }

            if (bytes.Count > MaxPayload)
            {
                throw Bad(command, string.Create(CultureInfo.InvariantCulture, $"the fields make more than {MaxPayload} bytes."));
            }
        }

        return [.. bytes];
    }

    /// <summary>An opcode argument: a name of the build-5875 table in any case, a decimal number or <c>0x</c> hex, checked against the table.</summary>
    public static ushort ParseOpcode(string command, string text)
    {
        string trimmed = text.Trim();
        ushort value;
        bool parsed = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : ushort.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (parsed)
        {
            return IsKnown(value)
                ? value
                : throw Bad(command, $"{trimmed} is not an opcode of the build-5875 table (opcodes.find lists them).");
        }

        foreach (WorldOpcode opcode in Enum.GetValues<WorldOpcode>())
        {
            if (string.Equals(WorldOpcodeNames.GetName(opcode), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return (ushort)opcode;
            }
        }

        throw Bad(command, $"'{Short(trimmed)}' is not an opcode name or number of the build-5875 table (opcodes.find searches them).");
    }

    /// <summary>Whether <paramref name="value"/> has a name in the build-5875 table.</summary>
    public static bool IsKnown(uint value) => !WorldOpcodeNames.GetName(value).StartsWith("0x", StringComparison.Ordinal);

    private static ulong Integer(string command, int index, string type, string value, ulong max, long min)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong hex) && hex <= max)
            {
                return hex;
            }
        }
        else if (value.StartsWith('-'))
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long negative) && negative >= min)
            {
                return unchecked((ulong)negative);
            }
        }
        else if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) && number <= max)
        {
            return number;
        }

        throw Bad(command, string.Create(CultureInfo.InvariantCulture, $"field {index + 1}: '{Short(value)}' does not fit {type} ({min} to {max}, or 0x hex)."));
    }

    private static string Short(string text) => text.Length > 40 ? text[..40] + "..." : text;

    private static CommandException Bad(string command, string message) => new(CommandException.BadArguments, $"{command}: {message}");
}
