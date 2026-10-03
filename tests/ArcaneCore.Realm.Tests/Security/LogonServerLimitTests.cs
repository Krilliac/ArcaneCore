using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>The real logon listener enforces the per-IP cap before creating a session.</summary>
public sealed class LogonServerLimitTests
{
    [Fact]
    public async Task SecondConnectionFromTheSameIp_IsClosedWhenThePerIpCapIsOne()
    {
        int port = FreePort();
        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IRealmStore>(new InMemoryRealmStore([]));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = new AuthOptions
        {
            BindAddress = "127.0.0.1", Port = port, MaxConnectionsPerIp = 1, MaxSessionDurationSeconds = 0,
        };
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            await Task.Delay(200); // let the server admit the first connection

            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] buf = new byte[1];
            int read;
            try
            {
                read = await second.GetStream().ReadAsync(buf, cts.Token);
            }
            catch (IOException)
            {
                read = 0;
            }

            Assert.Equal(0, read);
            Assert.True(first.Connected);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
