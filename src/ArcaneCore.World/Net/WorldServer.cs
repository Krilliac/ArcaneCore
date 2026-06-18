using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Net;

/// <summary>
/// TCP listener for the world daemon (default port 8085, Charter §3). Accepts connections
/// and runs each through a <see cref="WorldSession"/> on its own DI scope.
/// </summary>
public sealed class WorldServer(
    IServiceScopeFactory scopeFactory,
    IOptions<WorldOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<WorldServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorldOptions config = options.Value;
        var listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.Port);
        listener.Start();
        logger.LogInformation("World daemon listening on {Address}:{Port}", config.BindAddress, config.Port);

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
            logger.LogInformation("World daemon stopped");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        logger.LogInformation("[{Endpoint}] connected", endpoint);

        try
        {
            using (client)
            await using (NetworkStream stream = client.GetStream())
            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                IAccountStore accountStore = scope.ServiceProvider.GetRequiredService<IAccountStore>();
                var session = new WorldSession(
                    stream, accountStore, loggerFactory.CreateLogger<WorldSession>(), endpoint);
                await session.RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // server stopping
        }
        catch (EndOfStreamException)
        {
            // client disconnected mid-packet
        }
        catch (IOException)
        {
            // connection reset
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Endpoint}] session error", endpoint);
        }
        finally
        {
            logger.LogInformation("[{Endpoint}] disconnected", endpoint);
        }
    }
}
