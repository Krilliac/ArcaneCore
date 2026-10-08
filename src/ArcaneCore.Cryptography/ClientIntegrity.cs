using System.Security.Cryptography;

namespace ArcaneCore.Cryptography;

/// <summary>Client-side file checksum and realmd VerifyVersion proof. GenericCheck follows
/// WowSrp Integrity.cs (gtker/wow_srp_csharp 85ad800, MIT OR Apache-2.0); VerifyVersion
/// follows vmangos AuthSocket.cpp:1433-1470. See THIRD_PARTY_NOTICES.md.</summary>
public static class ClientIntegrity
{
    public static byte[] GenericCheck(ReadOnlySpan<byte> files, ReadOnlySpan<byte> checksumSalt,
        ReadOnlySpan<byte> clientPublicKey)
    {
        byte[] checksum = HMACSHA1.HashData(checksumSalt, files);
        byte[] input = new byte[clientPublicKey.Length + checksum.Length];
        clientPublicKey.CopyTo(input);
        checksum.CopyTo(input.AsSpan(clientPublicKey.Length));
        return SHA1.HashData(input);
    }

    public static byte[] VersionProof(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> configuredHash)
    {
        if (publicKey.Length is not (16 or 32) || configuredHash.Length != 20)
            throw new ArgumentException("Version proof requires a 16/32-byte public value and a 20-byte integrity hash");
        Span<byte> input = stackalloc byte[publicKey.Length + 20];
        publicKey.CopyTo(input);
        configuredHash.CopyTo(input[publicKey.Length..]);
        return SHA1.HashData(input);
    }
}
