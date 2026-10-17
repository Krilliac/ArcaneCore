namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// Whether this worker admits new gateway sessions (Tier 3b, docs/design/hot-reload-tiers.md section 5.3). A standby world
/// (<see cref="GatewayWorkerOptions.Standby"/>) starts closed and opens when it is promoted; a draining world closes before it freezes.
/// A refused open is answered <c>AUTH_UNAVAILABLE</c> through the gateway; sessions already open are not affected. Thread-safe.
/// </summary>
public sealed class WorkerAdmission
{
    /// <summary>The closed reason of a standby world that was not promoted yet.</summary>
    public const string StandbyReason = "standby";

    private volatile string? _closedReason;

    public WorkerAdmission(string? closedReason = null) => _closedReason = closedReason;

    /// <summary>True when new sessions are admitted.</summary>
    public bool IsOpen => _closedReason is null;

    /// <summary>Why new sessions are refused (null while open).</summary>
    public string? ClosedReason => _closedReason;

    /// <summary>Refuse new sessions from now on.</summary>
    public void Close(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _closedReason = reason;
    }

    /// <summary>Admit new sessions again.</summary>
    public void Open() => _closedReason = null;
}
