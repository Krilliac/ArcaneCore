using System.Net;
using ArcaneCore.Atlas.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>Bind parsing, trusted proxies and the client address (design 0.2): never used for the tier.</summary>
public sealed class PublicAddressesTests
{
    [Theory]
    [InlineData("[::]:8443", "::", 8443, false, false)]
    [InlineData("0.0.0.0:8443", "0.0.0.0", 8443, false, false)]
    [InlineData("127.0.0.1:1", "127.0.0.1", 1, true, true)]
    [InlineData("[::1]:8443", "::1", 8443, true, true)]
    [InlineData("atlas.example.org:443", "atlas.example.org", 443, false, false)]
    public void Binds_Parse(string text, string host, int port, bool singleLiteral, bool loopback)
    {
        Assert.True(PublicBind.TryParse(text, out PublicBind bind, out _));

        Assert.Equal(host, bind.Host);
        Assert.Equal(port, bind.Port);
        Assert.Equal(singleLiteral, bind.IsSingleLiteral);
        Assert.Equal(loopback, bind.IsLoopback);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8443")]
    [InlineData("::1:8443")]
    [InlineData("[::1]8443")]
    [InlineData("host:99999")]
    [InlineData("host:x")]
    [InlineData(":8443")]
    public void BadBinds_AreRefused(string text)
    {
        Assert.False(PublicBind.TryParse(text, out _, out string problem));
        Assert.Contains("Bind", problem);
    }

    private static TrustedProxies Proxies(params string[] entries)
    {
        Assert.True(TrustedProxies.TryParse(entries, out TrustedProxies proxies, out string problem), problem);
        return proxies;
    }

    [Fact]
    public void TrustedProxies_MatchAddressesAndCidrRanges_IncludingMappedIpv4()
    {
        TrustedProxies proxies = Proxies("127.0.0.1", "10.0.0.0/8", "fd00::/8", "2001:db8::/32");

        Assert.True(proxies.Contains(IPAddress.Parse("127.0.0.1")));
        Assert.True(proxies.Contains(IPAddress.Parse("10.200.1.1")));
        Assert.True(proxies.Contains(IPAddress.Parse("::ffff:10.1.1.1")));
        Assert.True(proxies.Contains(IPAddress.Parse("fd12::1")));
        Assert.True(proxies.Contains(IPAddress.Parse("2001:db8:ffff::1")));
        Assert.False(proxies.Contains(IPAddress.Parse("11.0.0.1")));
        Assert.False(proxies.Contains(IPAddress.Parse("2001:db9::1")));
        Assert.False(proxies.Contains(IPAddress.Parse("127.0.0.2")));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("10.0.0.0/40")]
    [InlineData("10.0.0.0/x")]
    public void BadProxyEntries_AreRefused(string entry)
    {
        Assert.False(TrustedProxies.TryParse([entry], out _, out string problem));
        Assert.Contains(entry, problem);
    }

    [Fact]
    public void FromAnUntrustedPeer_TheForwardedHeaderIsIgnored()
    {
        IPAddress client = PublicAddresses.ClientAddress(IPAddress.Parse("198.51.100.7"), ["1.2.3.4"], Proxies("10.0.0.1"));

        Assert.Equal(IPAddress.Parse("198.51.100.7"), client);
    }

    [Fact]
    public void FromATrustedProxy_TheRightMostUntrustedHopIsTheClient()
    {
        // The visitor sends "X-Forwarded-For: 9.9.9.9" itself; the proxy appends the real address. The right-most untrusted entry wins.
        IPAddress client = PublicAddresses.ClientAddress(IPAddress.Parse("10.0.0.1"), ["9.9.9.9, 203.0.113.50, 10.0.0.2"], Proxies("10.0.0.0/8"));

        Assert.Equal(IPAddress.Parse("203.0.113.50"), client);
    }

    [Fact]
    public void SeveralHeaderLines_AreWalkedAsOne_AndAMalformedEntryStopsTheWalkAtTheLastTrustedHop()
    {
        IPAddress joined = PublicAddresses.ClientAddress(IPAddress.Parse("10.0.0.1"), ["8.8.8.8", "203.0.113.50"], Proxies("10.0.0.1"));
        IPAddress malformed = PublicAddresses.ClientAddress(IPAddress.Parse("10.0.0.1"), ["8.8.8.8, not-an-address, 10.0.0.2"], Proxies("10.0.0.0/8"));
        IPAddress allTrusted = PublicAddresses.ClientAddress(IPAddress.Parse("10.0.0.1"), ["10.0.0.9, 10.0.0.2"], Proxies("10.0.0.0/8"));
        IPAddress none = PublicAddresses.ClientAddress(IPAddress.Parse("10.0.0.1"), [], Proxies("10.0.0.0/8"));

        Assert.Equal(IPAddress.Parse("203.0.113.50"), joined);
        Assert.Equal(IPAddress.Parse("10.0.0.2"), malformed);
        Assert.Equal(IPAddress.Parse("10.0.0.9"), allTrusted);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), none);
    }

    [Fact]
    public void ALoopbackProxy_DoesNotMakeItsClientsLoopback()
    {
        IPAddress client = PublicAddresses.ClientAddress(IPAddress.Loopback, ["203.0.113.50, 127.0.0.1"], Proxies("127.0.0.1"));

        Assert.Equal(IPAddress.Parse("203.0.113.50"), client);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    public void LimitKeys_GroupIpv6By64(string address, string key)
        => Assert.Equal(key, PublicAddresses.Key(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.5.5", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fdff::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("8.8.8.8", false)]
    public void PrivateAddresses(string address, bool expected)
        => Assert.Equal(expected, PublicAddresses.IsPrivate(IPAddress.Parse(address)));
}
