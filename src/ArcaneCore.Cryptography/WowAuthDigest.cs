using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Cryptography;

/// <summary>
/// The CMSG_AUTH_SESSION proof of the 1.12 world handshake: SHA1(account, u32 0, clientSeed, serverSeed, K) with the
/// seeds little-endian (vmangos WorldSocket::HandleAuthSession). Pure; the caller compares it (fixed-time) and decides
/// what a mismatch costs.
/// </summary>
public static class WowAuthDigest
{
    public static byte[] Compute(string account, uint clientSeed, uint serverSeed, byte[] sessionKey)
        => Sha1.Hash(Encoding.ASCII.GetBytes(account), new byte[4], Le(clientSeed), Le(serverSeed), sessionKey);

    /// <summary>A uint as four little-endian bytes.</summary>
    public static byte[] Le(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
