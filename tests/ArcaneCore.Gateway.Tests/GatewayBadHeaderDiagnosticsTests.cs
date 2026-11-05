using System.Net;
using System.Net.Sockets;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// The gateway's own wiring of the bad-client-header diagnostics (<c>GatewayClientConnection.LogBadHeader</c>): a rejected header produces one
/// WARN from the connection's logger carrying the classification, the bytes, the connection state and the worldport flag, and the connection closes.
/// The text itself is pinned by <c>BadClientHeaderTests</c>; the world session's copy by <c>BadHeaderDiagnosticsTests</c>.
/// </summary>
public sealed class GatewayBadHeaderDiagnosticsTests
{
    private const string Secret = "0123456789abcdef-gateway-header-secret";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task OutOfRangeSizeBeforeAuthentication_LogsOneClassifiedWarning_AndClosesTheConnection()
    {
        using var provider = new CapturingLoggerProvider();
        using ILoggerFactory loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        using var gateway = new GatewayServer(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GatewayOptions
            {
                BindAddress = IPAddress.Loopback.ToString(),
                Port = LoopbackPorts.Next(),
                WorkerAddress = IPAddress.Loopback.ToString(),
                WorkerPort = LoopbackPorts.Next(),
                LinkSecret = Secret,
                LinkRetryDelay = TimeSpan.FromMilliseconds(100),
            }),
            loggers);
        using var cts = new CancellationTokenSource(Bound);
        await gateway.StartAsync(cts.Token);
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(gateway.ListenEndPoint, cts.Token);
            NetworkStream stream = tcp.GetStream();

            // No cipher is engaged yet, so the plain header is read as sent: size 0xFFFF is outside [4, 0x2800].
            await stream.WriteAsync(new byte[] { 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00 }, cts.Token);
            await stream.FlushAsync(cts.Token);

            // The gateway closes the connection (after SMSG_AUTH_CHALLENGE): read until end of stream or reset.
            byte[] sink = new byte[64];
            try
            {
                while (await stream.ReadAsync(sink, cts.Token) > 0)
                {
                }
            }
            catch (IOException)
            {
                // reset by the peer: closed
            }

            CapturedCategoryLog[] warns = [.. provider.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("bad packet header", StringComparison.Ordinal))];
            CapturedCategoryLog warn = Assert.Single(warns);
            Assert.Contains(nameof(GatewayClientConnection), warn.Category, StringComparison.Ordinal);
            Assert.Contains("cipher-desync-or-plaintext", warn.Message, StringComparison.Ordinal);
            Assert.Contains("plain FFFF01000000", warn.Message, StringComparison.Ordinal);
            Assert.Contains("state Connected,", warn.Message, StringComparison.Ordinal);
            Assert.Contains("teleport pending False;", warn.Message, StringComparison.Ordinal);
            Assert.EndsWith("disconnecting", warn.Message, StringComparison.Ordinal);
        }
        finally
        {
            await gateway.StopAsync(CancellationToken.None);
        }
    }
}
