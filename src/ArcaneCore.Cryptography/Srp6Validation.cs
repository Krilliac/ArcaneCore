using System.Numerics;

namespace ArcaneCore.Cryptography;

/// <summary>
/// Rejects SRP6 inputs that make the shared secret independent of the password.
///
/// A stored verifier v with v mod N in {0, 1} (or any v >= N, which is non-canonical) lets
/// a client compute the session key from public values alone: for v = 0 or N the secret
/// S = (A * v^u)^b is 0, and for v = 1 it is A^b = (B - k)^a. vmangos refuses a zero
/// verifier and a zero salt (src/shared/Crypto/Authentication/SRP6.cpp:188-199); the
/// v = 1 / v >= N cases extend that rule. Client keys must be canonical 32-byte values
/// (vmangos SRP6.cpp:69-78 rejects A == 0 and A % N == 0).
/// </summary>
public static class Srp6Validation
{
    /// <summary>True when the verifier is a usable, canonical value in (1, N).</summary>
    public static bool IsUsableVerifier(BigInteger verifier)
        => verifier > BigInteger.One && verifier < WowSrp6.N;

    /// <summary>True when the salt has the wire length and is not all zero.</summary>
    public static bool IsUsableSalt(byte[]? salt)
    {
        if (salt is null || salt.Length != WowSrp6.SaltLength)
        {
            return false;
        }

        foreach (byte b in salt)
        {
            if (b != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a client public key A has the wire length and lies in (0, N).</summary>
    public static bool IsUsableClientKey(byte[]? clientPublicKey)
    {
        if (clientPublicKey is null || clientPublicKey.Length != WowSrp6.KeyLength)
        {
            return false;
        }

        BigInteger a = WowSrp6.FromLittleEndian(clientPublicKey);
        return a > BigInteger.Zero && a < WowSrp6.N;
    }
}
