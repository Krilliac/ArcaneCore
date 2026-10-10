using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Warden;

/// <summary>
/// One validated scan and its wire form (vmangos WardenScan.cpp: WindowsMemoryScan, WindowsCodeScan, WindowsDriverScan,
/// WindowsTimeScan). <see cref="Build"/> appends the request, <see cref="Check"/> reads the reply and says whether the scan failed.
/// </summary>
internal sealed class WardenCheck
{
    private WardenCheck(WardenCheckOptions source, byte[] bytes)
    {
        Source = source;
        Bytes = bytes;
    }

    public WardenCheckOptions Source { get; }

    public uint Id => Source.Id;

    public WardenCheckKind Kind => Source.Kind;

    /// <summary>Memory: the expected bytes; page: the pattern.</summary>
    public byte[] Bytes { get; }

    /// <summary>The built-in timing scan (vmangos WindowsTimeScan), sent when no scan is configured.</summary>
    public static WardenCheck Timing { get; } = new(new WardenCheckOptions { Id = 0, Kind = WardenCheckKind.Timing, Comment = "timing" }, []);

    /// <summary>Validate a configured scan; returns the problem, or null with <paramref name="check"/> set.</summary>
    public static string? TryCreate(WardenCheckOptions options, out WardenCheck? check)
    {
        check = null;
        byte[] bytes;
        switch (options.Kind)
        {
            case WardenCheckKind.Memory:
                if (!TryHex(options.Expected, out bytes) || bytes.Length is 0 or > 255)
                {
                    return "Expected must be 1-255 bytes of hex.";
                }

                if (options.Module.Length > 255)
                {
                    return "Module is too long.";
                }

                break;
            case WardenCheckKind.PageA or WardenCheckKind.PageB:
                if (!TryHex(options.Pattern, out bytes) || bytes.Length is 0 or > 255)
                {
                    return "Pattern must be 1-255 bytes of hex.";
                }

                break;
            case WardenCheckKind.Driver:
                bytes = [];
                if (options.DriverName.Length is 0 or > 255 || options.DriverPath.Length == 0)
                {
                    return "DriverName (1-255 characters) and DriverPath are required.";
                }

                break;
            case WardenCheckKind.Timing:
                bytes = [];
                break;
            case WardenCheckKind.ModuleByName:
                bytes = [];
                if (options.Module.Length == 0)
                {
                    return "Module is required.";
                }

                break;
            case WardenCheckKind.Mpq:
                if (options.Path.Length is 0 or > 255 || !TryHex(options.Expected, out bytes) || bytes.Length is not (0 or 20))
                {
                    return "Path (1-255 characters) is required and Expected must be empty or a 20-byte SHA-1 in hex.";
                }

                break;
            case WardenCheckKind.Lua:
                bytes = System.Text.Encoding.ASCII.GetBytes(options.Expected);
                if (options.Path.Length is 0 or > 255 || bytes.Length > 255)
                {
                    return "Path (1-255 characters) is required and Expected is at most 255 characters.";
                }

                break;
            default:
                return $"unknown Kind {options.Kind}.";
        }

        check = new WardenCheck(options, bytes);
        return null;
    }

    /// <summary>
    /// Append this scan to a CHEAT_CHECKS_REQUEST. Strings it needs go into <paramref name="strings"/>; the request carries their 1-based
    /// index (vmangos Warden::RequestScans; MaNGOS Zero EncodeCheckRequest).
    /// </summary>
    public void Build(PacketWriter scans, List<string> strings, byte xor, Func<uint> seeds)
    {
        switch (Kind)
        {
            case WardenCheckKind.Timing:
                scans.WriteByte((byte)(WardenModuleProfile.OpTiming ^ xor));
                break;
            case WardenCheckKind.Memory:
                scans.WriteByte((byte)(WardenModuleProfile.OpMemory ^ xor));
                scans.WriteByte(Source.Module.Length == 0 ? (byte)0 : StringIndex(strings, Source.Module.ToUpperInvariant()));
                scans.WriteUInt32(Source.Address);
                scans.WriteByte((byte)Bytes.Length);
                break;
            case WardenCheckKind.PageA or WardenCheckKind.PageB:
            {
                uint seed = seeds();
                scans.WriteByte((byte)((Kind == WardenCheckKind.PageA ? WardenModuleProfile.OpPageA : WardenModuleProfile.OpPageB) ^ xor));
                scans.WriteUInt32(seed);
                scans.WriteBytes(Hmac(seed, Bytes));
                scans.WriteUInt32(Source.Address);
                scans.WriteByte((byte)Bytes.Length);
                break;
            }

            case WardenCheckKind.ModuleByName:
            {
                // vmangos WindowsModuleScan: seed, HMAC-SHA1(seed, upper-case name); the client depends on the upper case.
                uint seed = seeds();
                scans.WriteByte((byte)(WardenModuleProfile.OpModule ^ xor));
                scans.WriteUInt32(seed);
                scans.WriteBytes(Hmac(seed, Encoding.ASCII.GetBytes(Source.Module.ToUpperInvariant())));
                break;
            }

            case WardenCheckKind.Mpq:
                // vmangos WindowsFileHashScan; MaNGOS Zero EncodeCheckRequest (0x98, string index).
                scans.WriteByte((byte)(WardenModuleProfile.OpMpq ^ xor));
                scans.WriteByte(StringIndex(strings, Source.Path));
                break;
            case WardenCheckKind.Lua:
                // vmangos WindowsLuaScan; MaNGOS Zero EncodeCheckRequest (0x8B, string index).
                scans.WriteByte((byte)(WardenModuleProfile.OpLua ^ xor));
                scans.WriteByte(StringIndex(strings, Source.Path));
                break;
            case WardenCheckKind.Driver:
            {
                uint seed = seeds();
                byte index = StringIndex(strings, Source.DriverName);
                scans.WriteByte((byte)(WardenModuleProfile.OpDriver ^ xor));
                scans.WriteUInt32(seed);
                scans.WriteBytes(Hmac(seed, Encoding.ASCII.GetBytes(Source.DriverPath)));
                scans.WriteByte(index);
                break;
            }
        }
    }

    /// <summary>Read this scan's reply; true when the scan failed. Throws the malformed-packet exception (<see cref="MalformedPacket"/>) on a short reply.</summary>
    public bool Check(ref PacketReader reply)
    {
        switch (Kind)
        {
            case WardenCheckKind.Timing:
            {
                byte stable = reply.ReadByte();
                reply.ReadUInt32(); // the client tick
                return stable == 0; // vmangos WindowsTimeScan: a zero result is a failed timing check
            }

            case WardenCheckKind.Memory:
            {
                if (reply.ReadByte() != 0)
                {
                    return true; // non-zero: the read failed (vmangos WindowsMemoryScan checker)
                }

                ReadOnlySpan<byte> actual = reply.ReadBytes(Bytes.Length);
                return !actual.SequenceEqual(Bytes);
            }

            case WardenCheckKind.Mpq:
            {
                // vmangos WindowsFileHashScan checker: status 0 is found, then the SHA-1.
                bool found = reply.ReadByte() == 0;
                bool wanted = Bytes.Length != 0;
                if (!found)
                {
                    return wanted;
                }

                ReadOnlySpan<byte> hash = reply.ReadBytes(20);
                return !wanted || !hash.SequenceEqual(Bytes);
            }

            case WardenCheckKind.Lua:
            {
                // vmangos WindowsLuaScan checkers: status 0 is found, then u8 length and the value.
                bool found = reply.ReadByte() == 0;
                ReadOnlySpan<byte> value = found ? reply.ReadBytes(reply.ReadByte()) : default;
                if (Bytes.Length == 0)
                {
                    return found != Source.Wanted;
                }

                return !found || !value.SequenceEqual(Bytes);
            }

            default:
                return (reply.ReadByte() == WardenModuleProfile.Found) != Source.Wanted;
        }
    }

    /// <summary>The reply bytes this scan can take, at most (vmangos Scan::replySize).</summary>
    public int MaxReplySize => Kind switch
    {
        WardenCheckKind.Timing => 5,
        WardenCheckKind.Memory => 1 + Bytes.Length,
        WardenCheckKind.Mpq => 21,
        WardenCheckKind.Lua => 2 + 255,
        _ => 1,
    };

    private static byte StringIndex(List<string> strings, string value)
    {
        int index = strings.IndexOf(value);
        if (index < 0)
        {
            strings.Add(value);
            index = strings.Count - 1;
        }

        return (byte)(index + 1);
    }

    /// <summary>HMAC-SHA1 keyed with the little-endian seed (vmangos Crypto::Hash::HMACSHA1::Generator(&amp;seed, 4)).</summary>
    internal static byte[] Hmac(uint seed, ReadOnlySpan<byte> data)
    {
        Span<byte> key = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(key, seed);
        return HMACSHA1.HashData(key, data);
    }

    private static bool TryHex(string text, out byte[] bytes)
    {
        string compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        try
        {
            bytes = Convert.FromHexString(compact);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
