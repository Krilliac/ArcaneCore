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
    IOptionsMonitor<AutoPatchOptions>? autoPatch = null,
    IOptionsMonitor<RealmLivenessOptions>? liveness = null,
    TimeProvider? timeProvider = null) : BackgroundService
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

    private TcpListener? _listener;

    /// <summary>
    /// Bind before returning: BackgroundService runs <see cref="ExecuteAsync"/> on the thread pool, so a listener bound there could be
    /// unbound when StartAsync returns, and a client connecting at once is refused (at once on Linux). A bind failure surfaces here too.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        AuthOptions config = options.Value;
        var listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.Port);
        listener.Start();
        _listener = listener;
        logger.LogInformation("Logon daemon listening on {Address}:{Port}", config.BindAddress, config.Port);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // ExecuteAsync stops it; this covers a stop that came before ExecuteAsync ever ran. Stop is idempotent.
            _listener?.Stop();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AuthOptions config = options.Value;
        IPAddress bind = IPAddress.Parse(config.BindAddress);
        TcpListener listener = _listener ?? throw new InvalidOperationException("the logon listener was not started (StartAsync binds it)");

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
            else
            {
                logger.LogWarning("Logon connection had no IPEndPoint ({Endpoint}); closing", client.Client.RemoteEndPoint);
                client.Dispose();
                return;
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
        string endpoint = "unknown";
        Interlocked.Increment(ref _activeSessions);

        try
        {
            try
            {
                endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (Exception ex) when (LogonSession.IsExpectedDisconnect(ex))
            {
                endpoint = "unknown";
            }

            logger.LogInformation("[{Endpoint}] connected", endpoint);

            using (client)
            await using (NetworkStream stream = client.GetStream())
            await using (AsyncServiceScope scope = scopeFactory.CreateAsyncScope())
            {
                IAccountStore accountStore = scope.ServiceProvider.GetRequiredService<IAccountStore>();
                IRealmStore realmStore = scope.ServiceProvider.GetRequiredService<IRealmStore>();
                IBanStore? banStore = scope.ServiceProvider.GetService<IBanStore>();
                IAccountLoginInfoStore? loginInfoStore = scope.ServiceProvider.GetService<IAccountLoginInfoStore>();
                IRealmCharacterCountStore? characterCounts = scope.ServiceProvider.GetService<IRealmCharacterCountStore>();

                var session = new LogonSession(
                    stream, accountStore, realmStore, config,
                    loggerFactory.CreateLogger<LogonSession>(), endpoint, banStore, guard, IpBans, patches: patches,
                    timeProvider: timeProvider, loginInfoStore: loginInfoStore,
                    liveness: liveness?.CurrentValue, characterCounts: characterCounts);

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
