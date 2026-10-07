namespace ArcaneCore.Kernel.Ops.Watchdog.Heartbeat;

/// <summary>
/// Where heartbeats go. All methods are called on the watchdog thread; each returns false when
/// the beat could not be delivered (the writer logs it, rate-limited) and never throws.
/// </summary>
public interface IHeartbeatSink : IDisposable
{
    /// <summary>For the start-up log line ("systemd NOTIFY_SOCKET /run/systemd/notify", "file var/alive", ...).</summary>
    string Description { get; }

    /// <summary>The process finished starting (systemd READY=1).</summary>
    bool Ready(string status);

    /// <summary>One liveness beat (systemd WATCHDOG=1).</summary>
    bool Beat();

    /// <summary>The process is stopping (systemd STOPPING=1).</summary>
    bool Stopping(string status);

    /// <summary>The last delivery failure, for the log line.</summary>
    string? LastError { get; }
}

/// <summary>No heartbeat (mode None, or Auto without a supervisor).</summary>
public sealed class NullHeartbeatSink : IHeartbeatSink
{
    public static NullHeartbeatSink Instance { get; } = new();

    public string Description => "none";

    public string? LastError => null;

    public bool Ready(string status) => true;

    public bool Beat() => true;

    public bool Stopping(string status) => true;

    public void Dispose()
    {
    }
}
