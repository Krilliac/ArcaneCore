using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Realms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// TCP listener for the logon/realm daemon (default port 3724, Charter §3). Accepts
/// connections and runs each through a <see cref="LogonSession"/> on its own DI scope.
/// Admission (connection caps, per-address connection rate) and the per-address failure budget
/// live in one <see cref="NetGuard"/> built from <c>Net:Protection</c> when the listener starts
/// (docs/ops/netguard.md); a missing registration keeps the defaults, so every protection is on.
/// </summary>
public sealed class LogonServer(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<LogonServer> logger,
    IOptions<NetProtectionOptions>? protection = null) : BackgroundService
{
    /// <summary>The guard of the running listener (null before it starts); exposed for diagnostics and tests.</summary>
    public NetGuard? Guard { get; private set; }

    /// <summary>The listener's IP-ban list (<see cref="AuthOptions.IpBanCacheSeconds"/>); null before it starts.</summary>
    public RealmIpBanCache? IpBans { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AuthOptions config = options.Value;
        IPAddress bind = IPAddress.Parse(config.BindAddress);
        var listener = new TcpListener(bind, config.Port);
        listener.Start();
        logger.LogInformation("Logon daemon listening on {Address}:{Port}", config.BindAddress, config.Port);

        try
        {
            var guard = new NetGuard(protection?.Value ?? new NetProtectionOptions(), () => config.MaxConnections, () => config.MaxConnectionsPerIp, logger, bindAddress: bind);
            Guard = guard;
            IpBans = new RealmIpBanCache(TimeSpan.FromSeconds(Math.Max(0, config.IpBanCacheSeconds)), scopes: scopeFactory);
            await AcceptLoop.RunAsync(
                ct => listener.AcceptTcpClientAsync(ct),
                client => Admit(client, guard, config, stoppingToken),
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
            logger.LogInformation("Logon daemon stopped");
        }
    }

    /// <summary>Enforce the connection caps and the connection rate before any scope or session is created.</summary>
    private void Admit(TcpClient client, NetGuard guard, AuthOptions config, CancellationToken stoppingToken)
    {
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

        _ = HandleClientAsync(client, config, guard, lease, stoppingToken);
    }

    private async Task HandleClientAsync(TcpClient client, AuthOptions config, NetGuard guard, IDisposable? lease, CancellationToken stoppingToken)
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
                IRealmStore realmStore = scope.ServiceProvider.GetRequiredService<IRealmStore>();
                IBanStore? banStore = scope.ServiceProvider.GetService<IBanStore>();

                var session = new LogonSession(
                    stream, accountStore, realmStore, config,
                    loggerFactory.CreateLogger<LogonSession>(), endpoint, banStore, guard, IpBans);

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
            client.Dispose();
            lease?.Dispose();
            logger.LogInformation("[{Endpoint}] disconnected", endpoint);
        }
    }
}
