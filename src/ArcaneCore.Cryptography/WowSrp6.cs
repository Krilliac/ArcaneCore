using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace ArcaneCore.Cryptography;

/// <summary>
/// Constants and primitive operations for the WoW (1.12.1 / build 5875) flavor of SRP6.
///
/// Verified against:
///   * vmangos  src/shared/Crypto/Authentication/SRP6.cpp  (N, g=7, k=3, B, u, S,
///              interleave, M1, M2 — the authoritative vanilla reference, Charter §4)
///   * gtker/wow_srp  src/srp_internal.rs                  (independent cross-check)
///
/// Byte-order rules confirmed from both references:
///   * Numbers are hashed as their **minimal little-endian** byte representation
///     (the client parses the fixed-width wire fields into big-numbers, dropping
///     leading zeros, then hashes the minimal form — so the server must too).
///   * The session-key interleave consumes a **fixed 32-byte** little-endian S.
///   * SHA-1 digests that are reinterpreted as integers (x, u) are read as
///     little-endian.
/// </summary>
public static class WowSrp6
{
    /// <summary>The well-known 32-byte WoW safe prime N, big-endian (vmangos SRP6.cpp:31).</summary>
    private const string NHex = "894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7";

    /// <summary>SRP6 generator g = 7 (vmangos SRP6.cpp:32).</summary>
    public const byte Generator = 7;

    /// <summary>SRP6 multiplier k = 3 (the WoW-fixed value; vmangos SRP6.cpp:39 uses "* 3").</summary>
    public const int Multiplier = 3;

    public const int KeyLength = 32;
    public const int SaltLength = 32;
    public const int SessionKeyLength = 40;

    /// <summary>N as a positive big integer.</summary>
    public static readonly BigInteger N = FromBigEndianHex(NHex);

    /// <summary>g as a big integer.</summary>
    public static readonly BigInteger G = Generator;

    /// <summary>N serialized as the fixed 32-byte little-endian array sent on the wire.</summary>
    public static byte[] NLittleEndian => ToFixedLittleEndian(N, KeyLength);

    // --- Credential / verifier generation (account registration) -----------------

    /// <summary>
    /// SHA-1( UPPER(username) ":" UPPER(password) ) — the inner credentials hash.
    /// Username and password are uppercased before hashing (Charter §3).
    /// </summary>
    public static byte[] CredentialsHash(string username, string password)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            $"{username.ToUpperInvariant()}:{password.ToUpperInvariant()}");
        return Sha1.Hash(payload);
    }

    /// <summary>
    /// x = SHA-1( salt || SHA-1(UPPER(user):UPPER(pass)) ), read little-endian
    /// (gtker calculate_x; vmangos CalculateVerifier).
    /// </summary>
    public static BigInteger ComputeX(byte[] salt, string username, string password)
    {
        byte[] xDigest = Sha1.Hash(salt, CredentialsHash(username, password));
        return FromLittleEndian(xDigest);
    }

    /// <summary>v = g^x mod N — the password verifier stored per account.</summary>
    public static BigInteger ComputeVerifier(byte[] salt, string username, string password)
        => BigInteger.ModPow(G, ComputeX(salt, username, password), N);

    /// <summary>
    /// Generate a random 32-byte salt whose most-significant (big-endian) byte is
    /// non-zero, so its minimal and fixed-width representations are identical. This
    /// sidesteps the well-known SRP6 leading-zero ambiguity entirely.
    /// </summary>
    public static byte[] GenerateSalt()
    {
        byte[] salt = new byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        salt[^1] |= 0x80; // top big-endian byte (last little-endian byte) non-zero
        return salt;
    }

    // --- Byte-order helpers ------------------------------------------------------

    /// <summary>Interpret raw bytes as a non-negative little-endian integer.</summary>
    public static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes)
        => new(bytes, isUnsigned: true, isBigEndian: false);

    /// <summary>
    /// Minimal little-endian byte representation, matching mangos <c>BigNumber::AsByteArray()</c>.
    /// Used wherever a number is fed into a SHA-1 hash.
    /// </summary>
    public static byte[] ToMinimalLittleEndian(BigInteger value)
        => value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: false);

    /// <summary>Fixed-length little-endian representation, zero-padded (wire fields, interleave S).</summary>
    public static byte[] ToFixedLittleEndian(BigInteger value, int length)
    {
        byte[] minimal = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: false);
        if (minimal.Length == length)
        {
            return minimal;
        }

        if (minimal.Length > length)
        {
            throw new ArgumentException(
                $"value requires {minimal.Length} bytes, exceeds fixed length {length}", nameof(value));
        }

        byte[] buffer = new byte[length];
        Array.Copy(minimal, buffer, minimal.Length); // high bytes remain zero (little-endian)
        return buffer;
    }

    private static BigInteger FromBigEndianHex(string hex)
    {
        byte[] be = new byte[hex.Length / 2];
        for (int i = 0; i < be.Length; i++)
        {
            be[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return new BigInteger(be, isUnsigned: true, isBigEndian: true);
    }
}
