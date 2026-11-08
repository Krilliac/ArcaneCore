using System.Net.Sockets;

namespace ArcaneCore.World.Net;

/// <summary>
/// A listener this process needs could not bind its endpoint (the world port, probed early by <see cref="WorldHost"/> and bound by <see cref="WorldServer"/>, or the gateway link port):
/// the start failed for a reason the socket layer names. <see cref="WorldStartup.RunAsync"/> turns it into a logged failure and a non-zero exit code instead of an unhandled exception,
/// which the crash handler would turn into a fail-fast with a dump. The message carries the true description, so an address this host does not have, a privileged or reserved port or fd
/// exhaustion is reported as exactly that. Only <see cref="WorldPortInUseException"/> says the endpoint is held by another listener.
/// </summary>
internal class WorldBindFailedException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    /// <summary>
    /// The exception for a failed bind of <paramref name="endpoint"/> (<paramref name="what"/> names the listener, for example "world port"): a <see cref="WorldPortInUseException"/> for a
    /// <see cref="SocketError.AddressAlreadyInUse"/> clash, otherwise a <see cref="WorldBindFailedException"/> that names the socket error. <see cref="SocketError.AccessDenied"/> is not claimed to be
    /// a clash: it is what a privileged port (EACCES on Linux), a port in a reserved range or a port held with exclusive address use (Windows) all report, and the message says that. Pure; any thread.
    /// </summary>
    internal static WorldBindFailedException For(string what, string endpoint, SocketException ex, string? suffix = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        string tail = suffix is null ? "" : "; " + suffix;
        if (WorldPortInUseException.IsClash(ex))
            return new WorldPortInUseException($"the {what} {endpoint} is already in use (another world process?){tail}", ex);
        string reason = ex.SocketErrorCode == SocketError.AccessDenied
            ? "access denied (a privileged port, a reserved port range, or a port another socket holds exclusively)"
            : $"{ex.SocketErrorCode}: {ex.Message}";
        return new WorldBindFailedException($"the {what} {endpoint} could not be bound: {reason}{tail}", ex);
    }
}

/// <summary>
/// The configured endpoint is taken by another listener (another world process): <see cref="WorldBindFailedException"/> for the one error that means so, <see cref="SocketError.AddressAlreadyInUse"/>.
/// </summary>
internal sealed class WorldPortInUseException(string message, Exception? innerException = null) : WorldBindFailedException(message, innerException)
{
    /// <summary>
    /// True when a failed bind means the endpoint is held by another listener: <see cref="SocketError.AddressAlreadyInUse"/> only. Every other error, <see cref="SocketError.AccessDenied"/> included
    /// (EACCES on Linux is a privileged port, not a clash), is a bind failure described as itself by <see cref="WorldBindFailedException.For"/>. Pure; callable from any thread.
    /// </summary>
    internal static bool IsClash(SocketException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.SocketErrorCode == SocketError.AddressAlreadyInUse;
    }
}
