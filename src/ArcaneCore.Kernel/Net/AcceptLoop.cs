using System.Net.Sockets;
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
        DateTime lastLog = DateTime.MinValue;
        int suppressed = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await accept(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                suppressed++;
                if (DateTime.UtcNow - lastLog >= LogInterval)
                {
                    logger.LogWarning(ex, "accept failed ({Count} error(s) since the last report); retrying", suppressed);
                    lastLog = DateTime.UtcNow;
                    suppressed = 0;
                }

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                onClient(client);
            }
            catch (Exception ex)
            {
                // a failure admitting one client must never end the loop
                logger.LogError(ex, "failed to start a connection handler");
                client.Dispose();
            }
        }
    }
}
