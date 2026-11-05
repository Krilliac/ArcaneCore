using System.Net.Sockets;

namespace ArcaneCore.World.Net;

/// <summary>
/// The configured world endpoint is taken by another listener (another world process): thrown by the early probe in <see cref="WorldHost"/> and by the real bind in
/// <see cref="WorldServer"/>. <see cref="WorldStartup.RunAsync"/> turns it into a logged failure and a non-zero exit code instead of an unhandled exception,
/// which the crash handler would turn into a fail-fast with a dump.
/// </summary>
internal sealed class WorldPortInUseException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    /// <summary>
    /// True when a failed bind means the endpoint is held by another listener: <see cref="SocketError.AddressAlreadyInUse"/>, and <see cref="SocketError.AccessDenied"/>, which Windows reports for a
    /// port another socket holds with exclusive address use. The one set every bind site uses (world port probe and bind, gateway link listener), so a clash is never missed and any other error
    /// (an address not on this host, a privileged port, fd exhaustion) is never labelled a clash. Pure; callable from any thread.
    /// </summary>
    internal static bool IsClash(SocketException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied;
    }
}
