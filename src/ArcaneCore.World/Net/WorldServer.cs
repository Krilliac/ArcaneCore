using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
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
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sessions = new HashSet<Task>();

        try
        {
            var limiter = new ConnectionLimiter(() => config.MaxConnections, () => config.MaxConnectionsPerIp);
            await AcceptLoop.RunAsync(
                ct => listener.AcceptTcpClientAsync(ct),
                client =>
                {
                    // Admission control runs before a DI scope or WorldSession exists.
                    IDisposable? lease = null;
                    try
                    {
                        if (client.Client.RemoteEndPoint is IPEndPoint remote)
                        {
                            lease = limiter.TryAcquire(remote.Address);
                            if (lease is null)
                            {
                                logger.LogDebug("[{Endpoint}] world connection refused (connection limit)", remote);
                                client.Dispose();
                                return;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        client.Dispose(); // the peer already went away
                        return;
                    }

                    sessions.RemoveWhere(static session => session.IsCompleted);
                    sessions.Add(HandleClientAsync(client, lease, sessionStop.Token));
                },
                logger,
                stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            listener.Stop();
            sessionStop.Cancel();
            await Task.WhenAll(sessions).ConfigureAwait(false);
            logger.LogInformation("World daemon stopped listening");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Session handlers and async scope disposal may ignore cancellation. Keep owning
            // them before WorldHost drains storage, even if the host's shutdown budget expires.
            if (ExecuteTask is { } execution)
            {
                await execution.ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, IDisposable? lease, CancellationToken stoppingToken)
    {
        string endpoint = "unknown";
        try
        {
            // RemoteEndPoint throws on a socket the peer already reset; it must not escape unobserved.
            endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            logger.LogInformation("[{Endpoint}] connected", endpoint);
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
        finally
        {
            client.Dispose(); // idempotent; covers a failure before the using block was entered
            lease?.Dispose();
        }
    }
}
