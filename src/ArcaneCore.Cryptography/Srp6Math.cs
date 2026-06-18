using System.Numerics;
using System.Text;

namespace ArcaneCore.Cryptography;

/// <summary>
/// Pure SRP6 (WoW flavor) primitives, factored out so each step can be verified against
/// the gtker/wow_srp known-answer vectors (see ArcaneCore.Cryptography.Tests).
///
/// Hashing width policy (cross-checked against gtker/wow_srp, which is reverse-engineered
/// from the real client, and confirmed by KAT rows containing leading-zero keys):
///   * public keys A, B and the prime N are hashed as **fixed 32-byte little-endian**;
///   * the session key K is hashed as its full 40 bytes;
///   * the salt is hashed as its raw 32 bytes;
///   * the interleave strips an even number of leading (low-order) zero bytes from the
///     little-endian S — the canonical client algorithm (vmangos simplifies this to a
///     fixed 32 bytes, which differs only when S's least-significant byte is zero;
///     recorded in MILESTONE_M1.md).
/// </summary>
internal static class Srp6Math
{
    private static byte[]? _xorHash;

    /// <summary>B = (k*v + g^b) mod N.</summary>
    internal static BigInteger ServerPublicKey(BigInteger verifier, BigInteger privateB)
        => (WowSrp6.Multiplier * verifier + BigInteger.ModPow(WowSrp6.G, privateB, WowSrp6.N)) % WowSrp6.N;

    /// <summary>u = SHA1(A || B), read little-endian.</summary>
    internal static BigInteger Scrambler(BigInteger a, BigInteger b)
        => WowSrp6.FromLittleEndian(Sha1.Hash(Key(a), Key(b)));

    /// <summary>S = (A * v^u)^b mod N.</summary>
    internal static BigInteger SessionImplicitKey(BigInteger a, BigInteger verifier, BigInteger u, BigInteger privateB)
        => BigInteger.ModPow(a * BigInteger.ModPow(verifier, u, WowSrp6.N), privateB, WowSrp6.N);

    /// <summary>SHA1-interleave of S into the 40-byte session key K.</summary>
    internal static byte[] Interleave(BigInteger s)
    {
        byte[] full = WowSrp6.ToFixedLittleEndian(s, 32);

        // Strip an even number of leading (low-order) zero bytes.
        int lead = 0;
        while (lead < full.Length && full[lead] == 0)
        {
            lead++;
        }

        if ((lead & 1) != 0)
        {
            lead++;
        }

        int sliceLength = full.Length - lead;
        int half = sliceLength / 2;

        byte[] even = new byte[half];
        byte[] odd = new byte[half];
        for (int i = 0; i < half; i++)
        {
            even[i] = full[lead + i * 2];
            odd[i] = full[lead + i * 2 + 1];
        }

        byte[] hashEven = Sha1.Hash(even);
        byte[] hashOdd = Sha1.Hash(odd);

        byte[] key = new byte[WowSrp6.SessionKeyLength];
        for (int i = 0; i < Sha1.DigestLength; i++)
        {
            key[i * 2] = hashEven[i];
            key[i * 2 + 1] = hashOdd[i];
        }

        return key;
    }

    /// <summary>M1 = SHA1( (SHA1(N) xor SHA1(g)) || SHA1(user) || salt || A || B || K ).</summary>
    internal static byte[] ClientProof(string username, byte[] salt, BigInteger a, BigInteger b, byte[] sessionKey)
    {
        byte[] userHash = Sha1.Hash(Encoding.UTF8.GetBytes(username.ToUpperInvariant()));
        return Sha1.Hash(XorHash(), userHash, salt, Key(a), Key(b), sessionKey);
    }

    /// <summary>M2 = SHA1( A || M1 || K ).</summary>
    internal static byte[] ServerProof(BigInteger a, byte[] clientProof, byte[] sessionKey)
        => Sha1.Hash(Key(a), clientProof, sessionKey);

    /// <summary>Fixed 32-byte little-endian form of a key-sized number.</summary>
    private static byte[] Key(BigInteger value) => WowSrp6.ToFixedLittleEndian(value, WowSrp6.KeyLength);

    /// <summary>SHA1(N) xor SHA1(g) — constant, computed once.</summary>
    private static byte[] XorHash()
    {
        if (_xorHash is not null)
        {
            return _xorHash;
        }

        byte[] hashN = Sha1.Hash(WowSrp6.NLittleEndian);
        byte[] hashG = Sha1.Hash([WowSrp6.Generator]);
        byte[] xor = new byte[Sha1.DigestLength];
        for (int i = 0; i < xor.Length; i++)
        {
            xor[i] = (byte)(hashN[i] ^ hashG[i]);
        }

        _xorHash = xor;
        return xor;
    }
}
