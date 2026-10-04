using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace ArcaneCore.Kernel.Net;

/// <summary>
/// A client address as a fixed 16-byte value (the IPv6 form; an IPv4 address and its IPv4-mapped
/// IPv6 twin ::ffff:a.b.c.d are the same key, as in <see cref="ConnectionLimiter"/>). A struct key
/// keeps the per-address table free of <see cref="IPAddress"/> references and of per-lookup
/// allocations; scope ids are ignored on purpose (one link-local peer is one peer).
/// </summary>
public readonly record struct IpKey(ulong High, ulong Low)
{
    /// <summary>Build the key from an address without allocating (TryWriteBytes writes into a stack span).</summary>
    public static IpKey From(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Span<byte> bytes = stackalloc byte[16];
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // ::ffff:a.b.c.d
            bytes.Clear();
            bytes[10] = 0xFF;
            bytes[11] = 0xFF;
            if (!address.TryWriteBytes(bytes[12..], out _))
            {
                throw new ArgumentException("not a 4-byte IPv4 address", nameof(address));
            }
        }
        else if (!address.TryWriteBytes(bytes, out int written) || written != 16)
        {
            throw new ArgumentException("not an IPv4 or IPv6 address", nameof(address));
        }

        return new IpKey(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    /// <summary>Parse an address string (<c>a.b.c.d</c>, <c>::1</c>, or an <c>ip:port</c> / <c>[ipv6]:port</c> endpoint).</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out IpKey key)
    {
        text = text.Trim();
        if (IPAddress.TryParse(text, out IPAddress? address) || (IPEndPoint.TryParse(text, out IPEndPoint? endpoint) && (address = endpoint.Address) is not null))
        {
            key = From(address);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>A well-mixed hash of both halves (the record default would xor two correlated words).</summary>
    public int Mix()
    {
        ulong h = High * 0x9E3779B97F4A7C15UL;
        h ^= Low + 0x632BE59BD9B4E019UL;
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        return unchecked((int)h);
    }
}
