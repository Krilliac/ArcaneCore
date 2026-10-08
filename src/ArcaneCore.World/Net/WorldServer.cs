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
/// its own DI scope and a <see cref="WorldSession"/>. Admission (connection caps, per-address
/// connection rate) and the per-address failure budget live in one <see cref="NetGuard"/> built
/// from <c>Net:Protection</c> when the listener starts (docs/ops/netguard.md); a missing
/// registration keeps the defaults, so every protection is on.
/// </summary>
public sealed class WorldServer(
    IServiceScopeFactory scopeFactory,
    IOptions<WorldOptions> options,
    IOptions<WorldSessionOptions> sessionOptions,
    OpcodeTable opcodes,
    WorldRuntime world,
    SessionRegistry registry,
    ILoggerFactory loggerFactory,
    ILogger<WorldServer> logger,
    IOptions<NetProtectionOptions>? protection = null) : BackgroundService
{
    /// <summary>The guard of the running listener (null before it starts); exposed for diagnostics and tests.</summary>
    public NetGuard? Guard { get; private set; }

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
            var guard = new NetGuard(protection?.Value ?? new NetProtectionOptions(), () => config.MaxConnections, () => config.MaxConnectionsPerIp, logger,
                bindAddress: IPAddress.Parse(config.BindAddress));
            Guard = guard;
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
                            lease = guard.TryAdmit(remote.Address);
                            if (lease is null)
                            {
                                client.Dispose(); // refused and already logged (rate-limited) by the guard
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
                    sessions.Add(HandleClientAsync(client, guard, lease, sessionStop.Token));
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

    private async Task HandleClientAsync(TcpClient client, NetGuard guard, IDisposable? lease, CancellationToken stoppingToken)
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
                    sessionOptions.Value, loggerFactory.CreateLogger<WorldSession>(), guard);
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
