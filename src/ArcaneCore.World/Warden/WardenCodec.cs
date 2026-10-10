using System.Buffers.Binary;
using System.Security.Cryptography;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Warden;

/// <summary>Commands inside the encrypted SMSG / CMSG_WARDEN_DATA body (MaNGOS Zero WardenProtocol.h).</summary>
internal enum WardenClientCommand : byte
{
    ModuleMissing = 0,
    ModuleOk = 1,
    CheckResult = 2,
    HashResult = 4,
    ModuleFailed = 5,
}

internal enum WardenServerCommand : byte
{
    ModuleUse = 0,
    ModuleCache = 1,
    CheatChecksRequest = 2,
    ModuleInitialize = 3,
    HashRequest = 5,
}

/// <summary>The plaintext Warden bodies (MaNGOS Zero src/game/Warden/WardenPacketCodec.cpp).</summary>
internal static class WardenCodec
{
    /// <summary>MODULE_USE: command, module MD5, module key, compressed size.</summary>
    public static byte[] ModuleUse(int moduleLength)
    {
        var w = new PacketWriter(37);
        w.WriteByte((byte)WardenServerCommand.ModuleUse);
        w.WriteBytes(WardenModuleProfile.ModuleId);
        w.WriteBytes(WardenModuleProfile.ModuleKey);
        w.WriteUInt32((uint)moduleLength);
        return w.ToArray();
    }

    /// <summary>MODULE_CACHE: command, u16 chunk length, chunk.</summary>
    public static byte[] ModuleCache(ReadOnlySpan<byte> chunk)
    {
        var w = new PacketWriter(3 + chunk.Length);
        w.WriteByte((byte)WardenServerCommand.ModuleCache);
        w.WriteUInt16((ushort)chunk.Length);
        w.WriteBytes(chunk);
        return w.ToArray();
    }

    /// <summary>HASH_REQUEST: command and the 16-byte seed.</summary>
    public static byte[] HashRequest()
    {
        var w = new PacketWriter(17);
        w.WriteByte((byte)WardenServerCommand.HashRequest);
        w.WriteBytes(WardenModuleProfile.HashSeed);
        return w.ToArray();
    }

    /// <summary>
    /// MODULE_INITIALIZE (MaNGOS Zero EncodeModuleInitialize): three records, each command 3, u16 payload length, the folded SHA-1
    /// checksum of the payload and the payload: archive callbacks, the Lua callback, the clock callback.
    /// </summary>
    public static byte[] ModuleInitialize()
    {
        var archive = new PacketWriter(20);
        archive.WriteBytes(WardenModuleProfile.ArchiveSelectors);
        foreach (uint rva in WardenModuleProfile.ArchiveRvas)
        {
            archive.WriteUInt32(rva);
        }

        var lua = new PacketWriter(8);
        lua.WriteBytes(WardenModuleProfile.LuaPrefix);
        lua.WriteUInt32(WardenModuleProfile.LuaRva);
        lua.WriteByte(WardenModuleProfile.LuaSelector);

        var timing = new PacketWriter(8);
        timing.WriteBytes(WardenModuleProfile.TimingPrefix);
        timing.WriteUInt32(WardenModuleProfile.TimingRva);
        timing.WriteByte(WardenModuleProfile.TimingInstall);

        var w = new PacketWriter(57);
        foreach (PacketWriter record in new[] { archive, lua, timing })
        {
            w.WriteByte((byte)WardenServerCommand.ModuleInitialize);
            w.WriteUInt16((ushort)record.Length);
            w.WriteUInt32(Checksum(record.AsSpan()));
            w.WriteBytes(record.AsSpan());
        }

        return w.ToArray();
    }

    /// <summary>
    /// CHEAT_CHECKS_REQUEST (MaNGOS Zero EncodeCheckRequest; vmangos Warden::RequestScans): command 2, the string table (u8 length +
    /// bytes each, then 0), the scans, and the terminator (the xor byte).
    /// </summary>
    public static byte[] CheckRequest(IReadOnlyList<WardenCheck> checks, Func<uint> seeds)
    {
        byte xor = WardenModuleProfile.XorByte;
        var strings = new List<string>();
        var scans = new PacketWriter(64);
        foreach (WardenCheck check in checks)
        {
            check.Build(scans, strings, xor, seeds);
        }

        var w = new PacketWriter(16 + scans.Length);
        w.WriteByte((byte)WardenServerCommand.CheatChecksRequest);
        foreach (string value in strings)
        {
            w.WriteByte((byte)value.Length);
            w.WriteBytes(System.Text.Encoding.ASCII.GetBytes(value));
        }

        w.WriteByte(0);
        w.WriteBytes(scans.AsSpan());
        w.WriteByte(xor);
        return w.ToArray();
    }

    /// <summary>
    /// Decode a CHECK_RESULT (MaNGOS Zero DecodeCheckResult): command 2, u16 length, u32 folded SHA-1 checksum, then each scan's reply in
    /// request order. Returns the failed scans, or null when the body is malformed or its checksum is wrong.
    /// </summary>
    public static List<WardenCheck>? CheckResult(ReadOnlySpan<byte> body, IReadOnlyList<WardenCheck> checks)
    {
        if (body.Length < 7 || body[0] != (byte)WardenClientCommand.CheckResult)
        {
            return null;
        }

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(body[1..]);
        if (body.Length != 7 + length)
        {
            return null;
        }

        ReadOnlySpan<byte> results = body[7..];
        if (BinaryPrimitives.ReadUInt32LittleEndian(body[3..]) != Checksum(results))
        {
            return null;
        }

        var failed = new List<WardenCheck>();
        var reader = new PacketReader(results);
        try
        {
            foreach (WardenCheck check in checks)
            {
                if (check.Check(ref reader))
                {
                    failed.Add(check);
                }
            }
        }
        catch (ArgumentOutOfRangeException e) when (MalformedPacket.Is(e))
        {
            return null;
        }

        return reader.Remaining == 0 ? failed : null;
    }

    /// <summary>The folded checksum: SHA-1 of the bytes, its five little-endian words xored (MaNGOS Zero BuildChecksum; vmangos Warden::BuildChecksum).</summary>
    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        Span<byte> digest = stackalloc byte[20];
        SHA1.HashData(data, digest);
        uint checksum = 0;
        for (int i = 0; i < 20; i += 4)
        {
            checksum ^= BinaryPrimitives.ReadUInt32LittleEndian(digest[i..]);
        }

        return checksum;
    }
}
