using System.Net.Sockets;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Net;

/// <summary>
/// The accept loop shared by the logon and world daemons. A listener that throws (file
/// descriptor exhaustion, a connection reset during accept, a transient network error) must
/// not end the loop: an unhandled exception faults the BackgroundService and stops the whole
/// host. Errors are logged at most once per interval and the loop backs off.
/// </summary>
public static class AcceptLoop
{
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(
        Func<CancellationToken, ValueTask<TcpClient>> accept,
        Action<TcpClient> onClient,
        ILogger logger,
        CancellationToken stoppingToken,
        TimeSpan? backoff = null)
    {
        TimeSpan delay = backoff ?? DefaultBackoff;
        // per call: the logon and world daemons must not share one throttle
        var logGate = new LogGate(LogInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await accept(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutdown
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                Diagnostics.FaultLedger.Shared.Record("net-accept-loop", ex);
                if (logGate.TryEnter(out int suppressed))
                {
                    // the gate counts only refused calls; this error is reported too
                    logger.LogWarning(ex, "accept failed ({Count} error(s) since the last report); retrying", suppressed + 1);
                }

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // shutdown during the retry delay
                    return;
                }

                continue;
            }

            try
            {
                onClient(client);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // a failure admitting one client must never end the loop
                Diagnostics.FaultLedger.Shared.Record("net-accept-admit", ex);
                logger.LogError(ex, "failed to start a connection handler");
                client.Dispose();
            }
        }
    }
}
