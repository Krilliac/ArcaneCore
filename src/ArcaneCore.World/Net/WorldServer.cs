using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Net;

/// <summary>
/// TCP listener for the world daemon (default port 8085, charter §3). Each connection gets
/// its own DI scope and a <see cref="WorldSession"/>.
/// </summary>
public sealed class WorldServer(
    IServiceScopeFactory scopeFactory,
    IOptions<WorldOptions> options,
    IOptions<WorldSessionOptions> sessionOptions,
    OpcodeTable opcodes,
    WorldRuntime world,
    SessionRegistry registry,
    ILoggerFactory loggerFactory,
    ILogger<WorldServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorldOptions config = options.Value;
        var listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.Port);
        listener.Start();
        logger.LogInformation("World daemon listening on {Address}:{Port} ({Handlers} opcode handlers)",
            config.BindAddress, config.Port, opcodes.Count);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            listener.Stop();
            logger.LogInformation("World daemon stopped listening");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        logger.LogInformation("[{Endpoint}] connected", endpoint);

        try
        {
            client.NoDelay = true;
            using (client)
            await using (NetworkStream stream = client.GetStream())
            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                var session = new WorldSession(
                    stream, endpoint, scope.ServiceProvider, opcodes, world, registry,
                    sessionOptions.Value, loggerFactory.CreateLogger<WorldSession>());
                await session.RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Endpoint}] session error", endpoint);
        }
    }
}
