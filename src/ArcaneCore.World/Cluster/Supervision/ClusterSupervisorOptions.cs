namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// Per-process console routing mode under the M15.4 host supervisor
/// (<c>Cluster:Supervisor:Processes:&lt;name&gt;:Console</c>).
/// </summary>
public enum SupervisedConsoleMode
{
    /// <summary>
    /// Default: starts the child windowless and merges its stdout/stderr into the supervisor's
    /// console prefixed with <c>[&lt;name&gt;]</c>, while also writing the process's own log file.
    /// </summary>
    Merged = 0,

    /// <summary>
    /// Gives the child process its own visible console window (like <c>deploy --visible-consoles</c>).
    /// Errors and exceptions are still surfaced in the supervisor's merged console.
    /// </summary>
    Window = 1,

    /// <summary>
    /// Starts the child windowless and suppresses routine informational lines from the merged console,
    /// while still writing the child's own log file and surfacing all errors/exceptions in the merged console.
    /// </summary>
    None = 2,
}

/// <summary>
/// Canonical cluster process roles managed by the host supervisor.
/// </summary>
public static class SupervisedRoles
{
    public const string Combined = "combined";
    public const string Realm = "realm";
    public const string Gateway = "gateway";
    public const string Worker = "worker";
    public const string Social = "social";
    public const string Coordinator = "coordinator";
    public const string Supervisor = "supervisor";
}

/// <summary>
/// Configuration for a single supervised child process (<c>Cluster:Supervisor:Processes:&lt;name&gt;</c>).
/// </summary>
public sealed class SupervisedProcessConfig
{
    /// <summary>
    /// Logical process name and merged-console tag (e.g. <c>"realm"</c>, <c>"gateway"</c>,
    /// <c>"social"</c>, <c>"coordinator"</c>, <c>"worker:EK"</c>, <c>"worker:Kalimdor"</c>, <c>"worker:instances"</c>).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Process role (<see cref="SupervisedRoles"/>).
    /// </summary>
    public string Role { get; set; } = SupervisedRoles.Worker;

    /// <summary>
    /// Path to the executable to launch (e.g. <c>ArcaneCore.Realm</c> or <c>ArcaneCore.World</c>).
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// Command-line arguments passed to the child process.
    /// </summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Optional working directory for the child process.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Optional path to the child process's dedicated log file. Every stdout/stderr line from the
    /// child is written here regardless of <see cref="Console"/> mode.
    /// </summary>
    public string? LogFilePath { get; set; }

    /// <summary>
    /// Console routing mode (<c>merged | window | none</c>). Defaults to <see cref="SupervisedConsoleMode.Merged"/>.
    /// Errors and exceptions are always routed to the merged console regardless of this setting.
    /// </summary>
    public SupervisedConsoleMode Console { get; set; } = SupervisedConsoleMode.Merged;

    /// <summary>
    /// Initial restart backoff in milliseconds after an unexpected process exit (default: 1000 ms = 1 s).
    /// </summary>
    public int RestartBackoffMinMs { get; set; } = 1000;

    /// <summary>
    /// Maximum capped restart backoff in milliseconds (default: 60000 ms = 60 s).
    /// </summary>
    public int RestartBackoffMaxMs { get; set; } = 60_000;

    /// <summary>
    /// Graceful drain timeout in milliseconds before the supervisor forcibly terminates an owned child process.
    /// </summary>
    public int DrainTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Memory budget in MiB required to admit and launch this process on the host.
    /// </summary>
    public long RequiredMemoryMb { get; set; } = 256;

    /// <summary>
    /// Static map IDs assigned to this worker (empty for non-worker roles or dynamically assigned workers).
    /// </summary>
    public IReadOnlyList<uint> MapAssignments { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("SupervisedProcessConfig.Name must not be empty.");
        if (string.IsNullOrWhiteSpace(Role))
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' must specify a Role.");
        if (string.IsNullOrWhiteSpace(ExecutablePath))
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' must specify an ExecutablePath.");
        if (RestartBackoffMinMs <= 0)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' RestartBackoffMinMs must be positive.");
        if (RestartBackoffMaxMs < RestartBackoffMinMs)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' RestartBackoffMaxMs must be >= RestartBackoffMinMs.");
        if (DrainTimeoutMs <= 0)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' DrainTimeoutMs must be positive.");
        if (RequiredMemoryMb < 0)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' RequiredMemoryMb must be non-negative.");
    }
}

/// <summary>
/// Host supervisor options (<c>Cluster:Supervisor</c>).
/// </summary>
public sealed class ClusterSupervisorOptions
{
    public const string ConfigurationSection = "Cluster:Supervisor";

    /// <summary>
    /// Maximum number of concurrent supervised child processes allowed on this host (default: 9).
    /// </summary>
    public int MaxConcurrentProcesses { get; set; } = 9;

    /// <summary>
    /// Minimum process count for a full multi-process cluster topology (realm, gateway, social, coordinator, plus workers; default: 6).
    /// </summary>
    public int MinClusterProcesses { get; set; } = 6;

    /// <summary>
    /// Minimum free host memory reserve in MiB that must remain after admitting a child process (default: 1024 MiB).
    /// </summary>
    public long MinFreeMemoryReserveMb { get; set; } = 1024;

    /// <summary>
    /// Optional cap on the sum of <see cref="SupervisedProcessConfig.RequiredMemoryMb"/> across all active supervised processes.
    /// </summary>
    public long? MaxTotalAllocatedMemoryMb { get; set; }

    /// <summary>
    /// Supervised process definitions keyed by logical name (e.g. <c>"worker:EK"</c>).
    /// </summary>
    public Dictionary<string, SupervisedProcessConfig> Processes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Computes capped exponential backoff in milliseconds for the given 1-based <paramref name="consecutiveFailures"/> attempt
    /// (<c>minMs * 2^(attempt - 1)</c>, clamped to <c>[minMs, maxMs]</c>), matching server-Zero's <c>WorkerSupervisor</c> (1 s to 60 s).
    /// </summary>
    public static int ComputeBackoffDelayMs(int consecutiveFailures, int minMs = 1000, int maxMs = 60_000)
    {
        if (minMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(minMs), minMs, "minMs must be positive.");
        if (maxMs < minMs)
            throw new ArgumentOutOfRangeException(nameof(maxMs), maxMs, "maxMs must be >= minMs.");

        if (consecutiveFailures <= 1)
            return minMs;

        int shift = Math.Min(consecutiveFailures - 1, 20);
        long candidate = (long)minMs << shift;
        return (int)Math.Min(candidate, maxMs);
    }
}
