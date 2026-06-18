using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// TCP listener for the logon/realm daemon (default port 3724, Charter §3). Accepts
/// connections and runs each through a <see cref="LogonSession"/> on its own DI scope.
/// </summary>
public sealed class LogonServer(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<LogonServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AuthOptions config = options.Value;
        IPAddress bind = IPAddress.Parse(config.BindAddress);
        var listener = new TcpListener(bind, config.Port);
        listener.Start();
        logger.LogInformation("Logon daemon listening on {Address}:{Port}", config.BindAddress, config.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                _ = HandleClientAsync(client, config, stoppingToken);
            }
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

    private async Task HandleClientAsync(TcpClient client, AuthOptions config, CancellationToken stoppingToken)
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

                var session = new LogonSession(
                    stream, accountStore, realmStore, config,
                    loggerFactory.CreateLogger<LogonSession>(), endpoint);

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
