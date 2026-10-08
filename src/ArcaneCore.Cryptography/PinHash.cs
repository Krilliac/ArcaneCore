using System.Security.Cryptography;

namespace ArcaneCore.Cryptography;

/// <summary>Vanilla PIN-grid proof. Derived from WowSrp Pin.cs and Internal/PinImplementation.cs
/// (gtker/wow_srp_csharp 85ad800, MIT OR Apache-2.0). See THIRD_PARTY_NOTICES.md.</summary>
public static class PinHash
{
    public const int SaltLength = 16;
    public const int HashLength = 20;

    public static byte[] Calculate(ReadOnlySpan<byte> digits, uint gridSeed, ReadOnlySpan<byte> serverSalt,
        ReadOnlySpan<byte> clientSalt)
    {
        if (digits.Length is < 4 or > 10 || serverSalt.Length != SaltLength || clientSalt.Length != SaltLength)
            throw new ArgumentException("PIN must have 4-10 digits and both salts must have 16 bytes");

        Span<byte> remaining = stackalloc byte[10];
        Span<byte> grid = stackalloc byte[10];
        for (int i = 0; i < 10; i++) remaining[i] = (byte)i;
        ulong seed = gridSeed;
        for (int n = 10, position = 0; n > 0; n--, position++)
        {
            int index = (int)(seed % (uint)n);
            seed /= (uint)n;
            grid[position] = remaining[index];
            remaining[(index + 1)..n].CopyTo(remaining[index..]);
        }

        byte[] inner = new byte[SaltLength + digits.Length];
        serverSalt.CopyTo(inner);
        for (int i = 0; i < digits.Length; i++)
        {
            if (digits[i] > 9) throw new ArgumentException("PIN contains a non-digit", nameof(digits));
            int index = grid.IndexOf(digits[i]);
            inner[SaltLength + i] = (byte)('0' + index);
        }

        byte[] first = SHA1.HashData(inner);
        Span<byte> outer = stackalloc byte[SaltLength + HashLength];
        clientSalt.CopyTo(outer);
        first.CopyTo(outer[SaltLength..]);
        return SHA1.HashData(outer);
    }

    public static bool Verify(ReadOnlySpan<byte> digits, uint gridSeed, ReadOnlySpan<byte> serverSalt,
        ReadOnlySpan<byte> clientSalt, ReadOnlySpan<byte> hash)
        => hash.Length == HashLength && CryptographicOperations.FixedTimeEquals(
            Calculate(digits, gridSeed, serverSalt, clientSalt), hash);
}
