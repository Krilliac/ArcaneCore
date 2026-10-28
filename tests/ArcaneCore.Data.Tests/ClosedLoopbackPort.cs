using System.Net;
using System.Net.Sockets;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// A loopback TCP port with nothing listening, for "unreachable server" fixtures. The port must be refused, not blackholed:
/// port 1 is filtered on some hosts, which sends the database driver down its connect-timeout path, and that path disposes a
/// socket with a connect still in flight. On .NET 11 / Linux that can crash the test process (ObjectDisposedException from
/// SafeSocketHandle.SetBlocking in the runtime's connect callback). A port that was just bound and released is refused at once.
/// </summary>
internal static class ClosedLoopbackPort
{
    /// <summary>Binds an ephemeral loopback port, releases it and returns its number.</summary>
    public static int Next()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
