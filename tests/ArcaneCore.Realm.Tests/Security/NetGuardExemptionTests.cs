using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Exemptions from the shared per-address cap (Net:Protection:MaxConnectionsPerIp). The live stress test of 2026-10-08 refused
/// the 15th local client at the default 16: every local client, the owner's own included, connects from 127.0.0.1. A loopback
/// bind exempts loopback clients; a public bind does not; an explicit list exempts the addresses it names. Nothing else is lifted.
/// </summary>
public sealed class NetGuardExemptionTests
{
    private static readonly IPAddress Remote = IPAddress.Parse("198.51.100.10");

    private static NetGuard Build(NetProtectionOptions options, IPAddress? bind, int daemonPerIp = 0, int daemonTotal = 0, NetGuardTests.CapturingLogger? log = null)
        => new(options, () => daemonTotal, () => daemonPerIp, log ?? new NetGuardTests.CapturingLogger(), () => 0, bind);

    private static int Admitted(NetGuard guard, IPAddress address, int attempts)
    {
        int admitted = 0;
        for (int i = 0; i < attempts; i++)
        {
            if (guard.TryAdmit(address) is not null)
            {
                admitted++;
            }
        }

        return admitted;
    }

    [Fact]
    public void ALoopbackBind_DoesNotCapLoopbackClients_ButStillCapsOthers()
    {
        NetGuard guard = Build(new NetProtectionOptions { ConnectionBurstPerIp = 0 }, IPAddress.Loopback);
        Assert.True(guard.ExemptLoopback);
        Assert.Equal(40, Admitted(guard, IPAddress.Loopback, 40));                      // was 16
        Assert.Equal(40, Admitted(guard, IPAddress.IPv6Loopback, 40));
        Assert.Equal(40, Admitted(guard, IPAddress.Parse("::ffff:127.0.0.1"), 40));     // dual-stack form of 127.0.0.1
        Assert.Equal(16, Admitted(guard, Remote, 40));                                   // cannot happen on loopback, but stays capped
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.0.2.5")]
    public void APublicBind_KeepsCappingLoopbackClients(string bind)
    {
        NetGuard guard = Build(new NetProtectionOptions { ConnectionBurstPerIp = 0 }, IPAddress.Parse(bind));
        Assert.False(guard.ExemptLoopback);
        Assert.Equal(16, Admitted(guard, IPAddress.Loopback, 40));
    }

    [Fact]
    public void TheLoopbackExemption_CanBeTurnedOff_AndAnUnknownBindExemptsNothing()
    {
        Assert.Equal(16, Admitted(Build(new NetProtectionOptions { ConnectionBurstPerIp = 0, ExemptLoopbackOnLoopbackBind = false }, IPAddress.Loopback),
            IPAddress.Loopback, 40));
        Assert.Equal(16, Admitted(Build(new NetProtectionOptions { ConnectionBurstPerIp = 0 }, bind: null), IPAddress.Loopback, 40));
    }

    [Fact]
    public void ExemptAddresses_ExemptTheListedAddressesAndNetworks_OnAPublicBind()
    {
        var log = new NetGuardTests.CapturingLogger();
        NetGuard guard = Build(new NetProtectionOptions
        {
            ConnectionBurstPerIp = 0, MaxConnectionsPerIp = 2,
            ExemptAddresses = ["127.0.0.1", "10.1.0.0/16", "2001:db8::/32", "not-an-address"],
        }, IPAddress.Any, log: log);

        Assert.Equal(3, guard.ExemptNetworks.Count);
        Assert.Contains(log.Messages, m => m.Contains("'not-an-address'", StringComparison.Ordinal));
        Assert.Equal(10, Admitted(guard, IPAddress.Loopback, 10));
        Assert.Equal(10, Admitted(guard, IPAddress.Parse("::ffff:127.0.0.1"), 10));
        Assert.Equal(10, Admitted(guard, IPAddress.Parse("10.1.200.3"), 10));
        Assert.Equal(10, Admitted(guard, IPAddress.Parse("2001:db8::7"), 10));
        Assert.Equal(2, Admitted(guard, IPAddress.Parse("10.2.0.1"), 10));
        Assert.Equal(2, Admitted(guard, IPAddress.Parse("127.0.0.2"), 10));
        Assert.Equal(2, Admitted(guard, Remote, 10));
    }

    [Fact]
    public void AnExemptAddress_StillHasTheDaemonCap_TheGlobalCap_AndTheConnectionRate()
    {
        // The daemon's own cap is an explicit operator choice (Auth:/World:MaxConnectionsPerIp): the exemption does not lift it.
        Assert.Equal(3, Admitted(Build(new NetProtectionOptions { ConnectionBurstPerIp = 0 }, IPAddress.Loopback, daemonPerIp: 3), IPAddress.Loopback, 10));
        Assert.Equal(5, Admitted(Build(new NetProtectionOptions { ConnectionBurstPerIp = 0 }, IPAddress.Loopback, daemonTotal: 5), IPAddress.Loopback, 10));
        Assert.Equal(4, Admitted(Build(new NetProtectionOptions { ConnectionBurstPerIp = 4, ConnectionsPerMinutePerIp = 1 }, IPAddress.Loopback), IPAddress.Loopback, 10));
    }

    [Theory]
    [InlineData("127.0.0.1", true, 32)]
    [InlineData(" ::1 ", true, 128)]
    [InlineData("::ffff:10.0.0.1", true, 32)]
    [InlineData("10.0.0.0/8", true, 8)]
    [InlineData("10.0.0.1/8", true, 8)]     // host bits are masked off (.NET 10 IPNetwork.TryParse)
    [InlineData("10.0.0.0/33", false, 0)]
    [InlineData("localhost", false, 0)]
    [InlineData("", false, 0)]
    public void TryParseExemption_AcceptsAddressesAndNetworks(string text, bool ok, int prefix)
    {
        Assert.Equal(ok, NetGuard.TryParseExemption(text, out IPNetwork network));
        if (ok)
        {
            Assert.Equal(prefix, network.PrefixLength);
        }
    }

    [Fact]
    public async Task TheLogonListener_BoundToLoopback_AdmitsMoreLocalClientsThanTheSharedCap()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IRealmStore>(new InMemoryRealmStore([]));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthOptions { BindAddress = "127.0.0.1", Port = port, MaxSessionDurationSeconds = 0 }),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance,
            Options.Create(new NetProtectionOptions { MaxConnectionsPerIp = 2, LogonUnauthenticatedLifetime = TimeSpan.Zero }));
        await server.StartAsync(CancellationToken.None);
        var clients = new List<TcpClient>();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, port);
            }

            NetGuard guard = await WaitForGuardAsync(server);
            Assert.True(guard.ExemptLoopback);
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (guard.Limiter.Count < 5 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.Equal(5, guard.Limiter.Count); // 5 > MaxConnectionsPerIp 2
            Assert.Equal(0, guard.RefusedConnections);
        }
        finally
        {
            foreach (TcpClient client in clients)
            {
                client.Dispose();
            }

            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<NetGuard> WaitForGuardAsync(LogonServer server)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.Guard is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return server.Guard ?? throw new TimeoutException("the logon listener did not start");
    }
}
