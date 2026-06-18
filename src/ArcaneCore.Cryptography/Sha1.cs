using System.Security.Cryptography;

namespace ArcaneCore.Cryptography;

/// <summary>
/// Small helper for the chained SHA-1 hashing the WoW SRP6 flavor relies on.
/// SHA-1 is cryptographically broken, but the 1.12.1 protocol mandates it for the
/// auth handshake — this is protocol conformance, not security design.
/// </summary>
public static class Sha1
{
    public const int DigestLength = 20;

    /// <summary>Hash the concatenation of the supplied byte chunks with SHA-1.</summary>
    public static byte[] Hash(params byte[][] chunks)
    {
        using var sha = SHA1.Create();
        for (int i = 0; i < chunks.Length; i++)
        {
            byte[] chunk = chunks[i];
            sha.TransformBlock(chunk, 0, chunk.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }
}
