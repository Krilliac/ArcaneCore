using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
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
    IOptions<NetProtectionOptions>? protection = null,
    IOptionsMonitor<AutoPatchOptions>? autoPatch = null) : BackgroundService
{
    // Auth:AutoPatch is read through the monitor on every challenge, so an appsettings edit applies to the next connection.
    private readonly PatchCatalog patches = new(() => autoPatch?.CurrentValue ?? new AutoPatchOptions());

    /// <summary>The auto-patcher's catalog (Auth:AutoPatch, off by default).</summary>
    public PatchCatalog Patches => patches;

    private int _activeSessions;

    /// <summary>Logon connections currently being served (admitted and not yet disconnected). Thread-safe.</summary>
    public int ActiveSessions => Volatile.Read(ref _activeSessions);

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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            FaultRecording recording = FaultLedger.Shared.Record("logon/listener", ex);
            string stack = LogonSession.CondensedStack("logon/listener", ex);
            logger.LogError(
                ex,
                "Logon listener fault on {Address}:{Port} (category={Category}, faultId={FaultId}, stack={Stack})",
                config.BindAddress,
                config.Port,
                LogonSession.ClassifyFaultCategory(ex),
                recording.Id,
                stack);
            throw;
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
        catch (Exception ex) when (LogonSession.IsExpectedDisconnect(ex))
        {
            client.Dispose(); // the peer already went away
            return;
        }
        catch (Exception ex)
        {
            FaultRecording recording = FaultLedger.Shared.Record("logon/admit", ex);
            string stack = LogonSession.CondensedStack("logon/admit", ex);
            logger.LogError(
                ex,
                "Logon admission fault (category={Category}, faultId={FaultId}, stack={Stack})",
                LogonSession.ClassifyFaultCategory(ex),
                recording.Id,
                stack);
            client.Dispose();
            return;
        }

        _ = HandleClientAsync(client, config, guard, lease, stoppingToken);
    }

    private async Task HandleClientAsync(TcpClient client, AuthOptions config, NetGuard guard, IDisposable? lease, CancellationToken stoppingToken)
    {
        string endpoint;
        try
        {
            endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }
        catch (Exception ex) when (LogonSession.IsExpectedDisconnect(ex))
        {
            endpoint = "unknown";
        }

        Interlocked.Increment(ref _activeSessions);
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
                    loggerFactory.CreateLogger<LogonSession>(), endpoint, banStore, guard, IpBans, patches: patches);

                await session.RunAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (LogonSession.IsExpectedDisconnect(ex))
        {
            // Expected client disconnect (SocketException, IOException, EndOfStreamException) or cancellation.
        }
        catch (Exception ex)
        {
            string stage = ex.Data["arcanecore.logon.stage"] as string ?? "session-scope";
            string opcode = ex.Data["arcanecore.logon.opcode"] as string ?? "none";
            string category = LogonSession.ClassifyFaultCategory(ex);
            string site = $"logon/{stage}/{category}";
            string faultId = ex.Data["arcanecore.logon.recorded"] as string
                ?? FaultLedger.Shared.Record(site, ex).Id;
            string stack = ex.Data["arcanecore.logon.stack"] as string
                ?? LogonSession.CondensedStack(site, ex);
            logger.LogError(
                ex,
                "[{Endpoint}] session error (category={Category}, stage={Stage}, opcode={Opcode}, faultId={FaultId}, stack={Stack})",
                endpoint,
                category,
                stage,
                opcode,
                faultId,
                stack);
        }
        finally
        {
            client.Dispose();
            lease?.Dispose();
            Interlocked.Decrement(ref _activeSessions);
            logger.LogInformation("[{Endpoint}] disconnected", endpoint);
        }
    }
}
