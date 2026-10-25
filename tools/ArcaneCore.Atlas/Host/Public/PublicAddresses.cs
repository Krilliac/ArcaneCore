using System.Net;
using System.Net.Sockets;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>A parsed <c>Atlas:Public:Bind</c> value: <c>[::]:8443</c>, <c>0.0.0.0:8443</c>, <c>127.0.0.1:8443</c> or <c>name:8443</c>.</summary>
/// <param name="Address">The literal address, or null when the host is a name.</param>
/// <param name="Host">The host text as written (without brackets).</param>
/// <param name="Port">The port (0 asks the operating system for one; used by tests).</param>
internal sealed record PublicBind(IPAddress? Address, string Host, int Port)
{
    /// <summary>Whether the bind is one literal, non-wildcard address (the origin can then be derived without an <c>Origin</c> setting).</summary>
    public bool IsSingleLiteral => Address is not null && !IPAddress.Any.Equals(Address) && !IPAddress.IPv6Any.Equals(Address);

    /// <summary>Whether the address is loopback (127.0.0.0/8 or ::1).</summary>
    public bool IsLoopback => Address is not null && IPAddress.IsLoopback(Address);

    /// <summary>Whether the address is loopback or private (RFC 1918 or IPv6 unique-local fc00::/7), the only places plain HTTP may bind.</summary>
    public bool IsLoopbackOrPrivate => Address is not null && (IsLoopback || PublicAddresses.IsPrivate(Address));

    /// <summary>Parses a bind text; false with the reason when it is not <c>host:port</c> with a port of 0 to 65535.</summary>
    public static bool TryParse(string? text, out PublicBind bind, out string problem)
    {
        bind = new PublicBind(null, string.Empty, 0);
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "Atlas:Public:Bind is empty; use host:port, for example [::]:8443.";
            return false;
        }

        string value = text.Trim();
        string host;
        string portText;
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || value.Length <= close + 2 || value[close + 1] != ':')
            {
                problem = $"Atlas:Public:Bind '{value}' is not [address]:port.";
                return false;
            }

            host = value[1..close];
            portText = value[(close + 2)..];
        }
        else
        {
            int colon = value.LastIndexOf(':');
            if (colon <= 0 || value.IndexOf(':', StringComparison.Ordinal) != colon)
            {
                problem = $"Atlas:Public:Bind '{value}' is not host:port (an IPv6 address needs brackets, [::]:8443).";
                return false;
            }

            host = value[..colon];
            portText = value[(colon + 1)..];
        }

        if (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) || port > 65535)
        {
            problem = $"Atlas:Public:Bind '{value}' has no valid port (0 to 65535).";
            return false;
        }

        if (host.Length == 0)
        {
            problem = $"Atlas:Public:Bind '{value}' has no host.";
            return false;
        }

        IPAddress.TryParse(host, out IPAddress? address);
        bind = new PublicBind(address, host, port);
        return true;
    }
}

/// <summary>Trusted reverse proxies: peer addresses and CIDR ranges whose <c>X-Forwarded-For</c> may be read (for rate limits and logs, never for tier).</summary>
internal sealed class TrustedProxies
{
    private readonly List<(IPAddress Network, int Bits)> _ranges = [];

    public static TrustedProxies None { get; } = new();

    public int Count => _ranges.Count;

    /// <summary>Parses entries such as <c>127.0.0.1</c>, <c>10.0.0.0/8</c> and <c>::1</c>; the first bad entry is the problem.</summary>
    public static bool TryParse(IEnumerable<string> entries, out TrustedProxies result, out string problem)
    {
        result = new TrustedProxies();
        problem = string.Empty;
        foreach (string raw in entries)
        {
            string entry = raw.Trim();
            string addressText = entry;
            int? bits = null;
            int slash = entry.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                addressText = entry[..slash];
                if (!int.TryParse(entry[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedBits))
                {
                    problem = $"TrustedProxies entry '{entry}' has a bad prefix length.";
                    return false;
                }

                bits = parsedBits;
            }

            if (!IPAddress.TryParse(addressText, out IPAddress? address))
            {
                problem = $"TrustedProxies entry '{entry}' is not an address or CIDR range.";
                return false;
            }

            bool mapped = address.IsIPv4MappedToIPv6;
            address = PublicAddresses.Normalize(address);
            int max = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (mapped && bits is >= 96)
            {
                bits -= 96; // ::ffff:10.0.0.0/104 is the IPv4 range 10.0.0.0/8
            }

            int prefix = bits ?? max;
            if (prefix < 0 || prefix > max)
            {
                problem = $"TrustedProxies entry '{entry}' has a prefix length outside 0 to {max}.";
                return false;
            }

            result._ranges.Add((address, prefix));
        }

        return true;
    }

    /// <summary>Whether <paramref name="address"/> is inside any trusted range.</summary>
    public bool Contains(IPAddress address)
    {
        IPAddress normalized = PublicAddresses.Normalize(address);
        foreach ((IPAddress network, int bits) in _ranges)
        {
            if (network.AddressFamily == normalized.AddressFamily && PrefixMatches(network, normalized, bits))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PrefixMatches(IPAddress network, IPAddress candidate, int bits)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        if (!network.TryWriteBytes(a, out int length) || !candidate.TryWriteBytes(b, out _))
        {
            return false;
        }

        int whole = bits / 8;
        if (!a[..whole].SequenceEqual(b[..whole]))
        {
            return false;
        }

        int rest = bits % 8;
        if (rest == 0)
        {
            return true;
        }

        int mask = 0xFF << (8 - rest) & 0xFF;
        return whole < length && (a[whole] & mask) == (b[whole] & mask);
    }
}

/// <summary>Address helpers of the public listener.</summary>
internal static class PublicAddresses
{
    /// <summary>An IPv4-mapped IPv6 address as plain IPv4; every other address unchanged.</summary>
    public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>Whether the address is RFC 1918 (10/8, 172.16/12, 192.168/16) or IPv6 unique-local (fc00::/7).</summary>
    public static bool IsPrivate(IPAddress address)
    {
        address = Normalize(address);
        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out int length))
        {
            return false;
        }

        return address.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
            : length == 16 && (b[0] & 0xFE) == 0xFC;
    }

    /// <summary>
    /// The key every per-address limit uses: an IPv4 address as text, an IPv6 address as its /64 prefix (<c>2001:db8:1:2::/64</c>), so a
    /// visitor who owns a /64 cannot multiply a limit by rotating addresses inside it. IPv4-mapped addresses are IPv4.
    /// </summary>
    public static string Key(IPAddress address)
    {
        address = Normalize(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address.ToString();
        }

        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out _))
        {
            return "unknown";
        }

        b[8..].Clear();
        return new IPAddress(b).ToString() + "/64";
    }

    /// <summary>
    /// The client address of a request for rate limits and logs (design 0.2): the socket peer when it is not a trusted proxy; else the
    /// right-most <c>X-Forwarded-For</c> entry that is not itself a trusted proxy, walking right to left. A malformed entry stops the walk and
    /// the last trusted hop is used; if every entry is trusted, the left-most is. <c>Forwarded</c> and <c>X-Real-IP</c> are never read.
    /// Never used to decide the tier.
    /// </summary>
    public static IPAddress ClientAddress(IPAddress peer, IReadOnlyList<string> forwardedFor, TrustedProxies trusted)
    {
        IPAddress current = Normalize(peer);
        if (!trusted.Contains(current))
        {
            return current;
        }

        var entries = new List<string>();
        foreach (string header in forwardedFor)
        {
            entries.AddRange(header.Split(',', StringSplitOptions.TrimEntries));
        }

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (!TryParseHop(entries[i], out IPAddress? hop))
            {
                return current;
            }

            hop = Normalize(hop);
            if (!trusted.Contains(hop))
            {
                return hop;
            }

            current = hop;
        }

        return current;
    }

    private static bool TryParseHop(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        if (text.Length is 0 or > 64)
        {
            return false;
        }

        string candidate = text;
        if (candidate.StartsWith('['))
        {
            int close = candidate.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            candidate = candidate[1..close];
        }
        else if (candidate.Count(c => c == ':') == 1)
        {
            candidate = candidate[..candidate.IndexOf(':', StringComparison.Ordinal)];
        }

        return IPAddress.TryParse(candidate, out address);
    }
}
