using System.Security.Cryptography;

namespace ArcaneCore.World.Warden;

/// <summary>
/// The delivered Windows Warden module for build 5875 and its challenge values (MaNGOS Zero src/game/Warden/WardenModuleCatalog.cpp
/// ModuleWin5875 and WardenModuleWin5875Data.cpp). The 18 756 encrypted, compressed module bytes ship as an embedded base64 resource and are
/// revalidated against both pinned digests before any session uses them (WardenModuleCatalog::Validate). The scan opcodes are the ones
/// this module decodes (MaNGOS Zero WardenPacketCodec.cpp EncodeCheckRequest: timing 0x57, memory 0xF3, MPQ 0x98, Lua 0x8B; the page,
/// driver and module opcodes 0xB2 / 0xBF / 0x71 / 0xD9 are the same module's, TrinityCore Warden.h enum WardenCheckType for module 79C0768D...).
/// </summary>
internal sealed class WardenModuleProfile
{
    public const uint Build = 5875;
    public const string ResourceName = "ArcaneCore.World.Warden.WardenModuleWin5875.b64";

    public static readonly byte[] ModuleId = Convert.FromHexString("79C0768D657977D697E10BAD956CCED1");
    public static readonly byte[] ModuleSha256 = Convert.FromHexString("6C68006A2F1FD31E7208204B3F7CEB94A6CE977876E13F2F703E9CD644482289");
    public static readonly byte[] ModuleKey = Convert.FromHexString("AE25BC51063B77BD363C3EFE0FC173F9");
    public static readonly byte[] HashSeed = Convert.FromHexString("4D808D2C77D905C41A6380EC08586AFE");
    public static readonly byte[] ClientKeySeedHash = Convert.FromHexString("568C054C781A972A6037A2290C22B52571A06F4E");
    public static readonly byte[] ClientKeySeed = Convert.FromHexString("7F96EEFDA5B63D20A4DF8E00CBF48304");
    public static readonly byte[] ServerKeySeed = Convert.FromHexString("C2B7ADEDFCCCA9C2BFB3F85602BA809B");

    /// <summary>Command-3 record 1 (archive callbacks): four ABI selectors, then the open / size / read / close RVAs.</summary>
    public static readonly byte[] ArchiveSelectors = [0x01, 0x00, 0x02, 0x00];
    public static readonly uint[] ArchiveRvas = [0x002477A0, 0x002487F0, 0x00248460, 0x00248730];

    /// <summary>Command-3 record 2 (FrameScript Lua callback): prefix, RVA, selector.</summary>
    public static readonly byte[] LuaPrefix = [0x04, 0x00, 0x00];
    public const uint LuaRva = 0x00303BF0;
    public const byte LuaSelector = 0x00;

    /// <summary>Command-3 record 3 (client clock callback): prefix, RVA, install flag.</summary>
    public static readonly byte[] TimingPrefix = [0x01, 0x01, 0x00];
    public const uint TimingRva = 0x0002C010;
    public const byte TimingInstall = 0x01;

    /// <summary>The scan opcodes before the per-session xor (the first byte of <see cref="ClientKeySeed"/>).</summary>
    public const byte OpTiming = 0x57, OpMemory = 0xF3, OpPageA = 0xB2, OpPageB = 0xBF, OpDriver = 0x71, OpMpq = 0x98, OpLua = 0x8B, OpModule = 0xD9;

    /// <summary>The byte the module answers for "found" in page and driver scans (vmangos WardenScan.hpp PatternFound / Found = 0x4A).</summary>
    public const byte Found = 0x4A;

    private static readonly Lazy<byte[]?> LoadedModule = new(Load);

    /// <summary>The validated module bytes, or null when the resource is missing or does not match the pinned digests.</summary>
    public static byte[]? Module => LoadedModule.Value;

    public static byte XorByte => ClientKeySeed[0];

    private static byte[]? Load()
    {
        using Stream? stream = typeof(WardenModuleProfile).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(reader.ReadToEnd());
        }
        catch (FormatException)
        {
            return null;
        }

        return Validate(bytes) ? bytes : null;
    }

    /// <summary>MaNGOS Zero WardenModuleCatalog::Validate: the length, the wire MD5 identity and the server-only SHA-256 identity.</summary>
    public static bool Validate(ReadOnlySpan<byte> module)
        => module.Length == 18756
            && CryptographicOperations.FixedTimeEquals(MD5.HashData(module), ModuleId)
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(module), ModuleSha256);
}
