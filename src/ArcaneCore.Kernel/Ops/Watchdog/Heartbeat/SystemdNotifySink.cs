using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;

/// <summary>
/// <c>sd_notify(3)</c> in managed code: datagrams on the unix socket named by
/// <c>NOTIFY_SOCKET</c> (a leading <c>@</c> is the abstract namespace, encoded as a leading NUL).
/// Messages are pre-encoded once; a beat is one <c>SendTo</c> with no allocation. On a platform
/// without unix datagram sockets (Windows has stream sockets only) the constructor throws and the
/// factory falls back to no heartbeat with one log line; systemd does not exist there anyway.
/// </summary>
public sealed class SystemdNotifySink : IHeartbeatSink
{
    private static readonly byte[] WatchdogMessage = "WATCHDOG=1\n"u8.ToArray();
    private readonly Socket _socket;
    private readonly SocketAddress _address;

    public SystemdNotifySink(string notifySocket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notifySocket);
        string path = notifySocket.StartsWith('@') ? "\0" + notifySocket[1..] : notifySocket;
        // Serialized once: the SocketAddress overload of SendTo allocates nothing per beat.
        _address = new UnixDomainSocketEndPoint(path).Serialize();
        // Non-blocking: a notify socket whose reader stopped draining (a full datagram queue) must fail the beat,
        // never park the watchdog thread in sendmsg.
        _socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified) { Blocking = false };
        Description = "systemd NOTIFY_SOCKET " + notifySocket;
    }

    public string Description { get; }

    public string? LastError { get; private set; }

    public bool Ready(string status) => Send(Encoding.UTF8.GetBytes("READY=1\nSTATUS=" + Sanitize(status) + "\n"));

    public bool Beat() => Send(WatchdogMessage);

    public bool Stopping(string status) => Send(Encoding.UTF8.GetBytes("STOPPING=1\nSTATUS=" + Sanitize(status) + "\n"));

    /// <summary>Send an arbitrary notification (e.g. <c>STATUS=...</c>); for the status updates of other lanes.</summary>
    public bool Notify(string message) => Send(Encoding.UTF8.GetBytes(message.EndsWith('\n') ? message : message + "\n"));

    public void Dispose() => _socket.Dispose();

    /// <summary>The sd_notify environment: NOTIFY_SOCKET, and WATCHDOG_USEC / WATCHDOG_PID (the interval is 0 when the watchdog is off or meant for another process).</summary>
    public static bool TryReadEnvironment(Func<string, string?> environment, int processId, out string? socket, out long watchdogMicros)
    {
        socket = environment("NOTIFY_SOCKET");
        watchdogMicros = 0;
        if (string.IsNullOrWhiteSpace(socket))
        {
            socket = null;
            return false;
        }

        string? usec = environment("WATCHDOG_USEC");
        string? pid = environment("WATCHDOG_PID");
        if (long.TryParse(usec, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long micros) && micros > 0
            && (string.IsNullOrWhiteSpace(pid) || (int.TryParse(pid, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int target) && target == processId)))
        {
            watchdogMicros = micros;
        }

        return true;
    }

    private bool Send(ReadOnlySpan<byte> message)
    {
        try
        {
            _socket.SendTo(message, SocketFlags.None, _address);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private static string Sanitize(string status) => status.Replace('\n', ' ').Replace('\r', ' ');
}
