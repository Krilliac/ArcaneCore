using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ArcaneCore.TestSupport;

/// <summary>
/// Loopback TCP listeners for test hosts. A bind(0) port comes from the kernel's ephemeral range, which is only a few thousand ports on
/// some hosts (Linux's default is 32768-60999, narrowed on some CI nodes) and is shared with every outbound connection of every concurrently running test
/// process; under full-gate load bind(0) itself fails with "Address already in use". These listeners are bound below the ephemeral
/// range, where only other listeners compete, and a failed bind is retried on the next port. Each process claims an exclusive stripe of
/// the range (<see cref="PortStripeClaim"/>: a file lock in the temp directory, held for the process lifetime) and hands out ports only
/// from it, so a port that <see cref="Next"/> probed and released cannot be taken by another test process before the caller binds it.
/// With more concurrent processes than stripes the extra ones fall back to a random start in the whole range (the previous behaviour:
/// a collision is then possible, not certain). Within a process the counter never hands out one port twice until the stripe wraps. The range is disjoint from the one
/// ClusterProcessFixture (ArcaneCore.Cluster.E2E.Tests) probes for its child processes (12000-19999): a probed port is free again until
/// the child binds it seconds later, so a listener here must never be able to take it.
/// </summary>
public static class LoopbackPorts
{
    private const int RangeStart = PortStripeClaim.RangeStart;
    private const int FullRangeSize = PortStripeClaim.StripeCount * PortStripeClaim.StripeSize;

    // Held for the process lifetime on purpose: the OS releases the stripe lock when the process exits.
    private static readonly PortStripeClaim? s_stripe = PortStripeClaim.TryClaim(
        Path.Combine(Path.GetTempPath(), "arcanecore-loopback-stripes"), RandomNumberGenerator.GetInt32(PortStripeClaim.StripeCount));

    private static readonly int s_first = s_stripe?.FirstPort ?? RangeStart;
    private static readonly int s_size = s_stripe is null ? FullRangeSize : PortStripeClaim.StripeSize;
    private static int s_next = RandomNumberGenerator.GetInt32(s_size);

    /// <summary>Returns a listener that is already started on a free loopback port; read the port from <see cref="TcpListener.LocalEndpoint"/>. The caller owns and stops it.</summary>
    public static TcpListener StartListener()
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            int port = s_first + (int)((uint)Interlocked.Increment(ref s_next) % (uint)s_size);
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }

        var fallback = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start();
        return fallback;
    }

    /// <summary>A listener that is not started yet, on a loopback port that was free a moment ago. Call sites that start it themselves accept the small probe-to-bind window.</summary>
    public static TcpListener CreateListener()
    {
        using TcpListener probe = StartListener();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>A free loopback port number (probed and released), for code that takes a port setting rather than a socket.</summary>
    public static int Next()
    {
        using TcpListener probe = StartListener();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
