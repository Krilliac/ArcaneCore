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

    /// <summary>True when <paramref name="role"/> is one of the canonical roles above (case-insensitive).</summary>
    public static bool IsKnown(string? role) =>
        role is not null
        && (string.Equals(role, Combined, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Realm, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Gateway, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Worker, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Social, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Coordinator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Supervisor, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Position in the start sequence (default 0): processes start in ascending order, ties broken by the configuration key
    /// (ordinal), and drain in the reverse. Needed because configuration binding returns keys sorted, not in file order;
    /// for example give <c>realm</c> 0, <c>gateway</c> 1 and the workers 2. Also decides which process is denied first when admission runs out.
    /// </summary>
    public int StartOrder { get; set; }

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
    public const string SectionName = "Cluster:Supervisor";

    /// <summary>
    /// Runs this World process as the host supervisor only (default: false): it launches and watches the configured
    /// <see cref="Processes"/> and starts no world, database, listener or feature. Off, a World start is unchanged.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of concurrent supervised child processes allowed on this host (default: 9).
    /// </summary>
    public int MaxConcurrentProcesses { get; set; } = 9;

    /// <summary>
    /// Minimum number of supervised processes a supervisor start must be able to admit (realm, gateway and one worker; default: 3).
    /// A start that can admit fewer launches nothing and refuses with a message naming this key.
    /// </summary>
    public int MinClusterProcesses { get; set; } = 3;

    /// <summary>
    /// Minimum free host memory reserve in MiB that must remain after admitting a child process (default: 1024 MiB).
    /// </summary>
    public long MinFreeMemoryReserveMb { get; set; } = 1024;

    /// <summary>
    /// Optional cap on the sum of <see cref="SupervisedProcessConfig.RequiredMemoryMb"/> across all active supervised processes.
    /// </summary>
    public long? MaxTotalAllocatedMemoryMb { get; set; }

    /// <summary>
    /// Supervised process definitions, started in ascending <see cref="SupervisedProcessConfig.StartOrder"/> (ties by key), keyed by a configuration key that must not contain <c>:</c>
    /// (e.g. <c>worker-EK</c>); the display name may, via <see cref="SupervisedProcessConfig.Name"/> (<c>worker:EK</c>).
    /// </summary>
    public Dictionary<string, SupervisedProcessConfig> Processes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns every problem with these options (empty when valid), for the startup check and the supervisor host.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        const string section = SectionName;
        var problems = new List<string>();

        if (MaxConcurrentProcesses is < 1 or > 64)
            problems.Add($"{section}:MaxConcurrentProcesses must be 1 to 64 (was {MaxConcurrentProcesses}).");
        if (MinClusterProcesses < 1 || MinClusterProcesses > MaxConcurrentProcesses)
            problems.Add($"{section}:MinClusterProcesses must be at least 1 and no more than MaxConcurrentProcesses ({MaxConcurrentProcesses}) (was {MinClusterProcesses}).");
        if (MinFreeMemoryReserveMb < 0)
            problems.Add($"{section}:MinFreeMemoryReserveMb must not be negative (was {MinFreeMemoryReserveMb}).");
        if (MaxTotalAllocatedMemoryMb is <= 0)
            problems.Add($"{section}:MaxTotalAllocatedMemoryMb must be unset or greater than 0 (was {MaxTotalAllocatedMemoryMb}).");
        if (Enabled && Processes.Count < MinClusterProcesses)
            problems.Add($"{section}:Processes defines {Processes.Count} process(es) but {section}:MinClusterProcesses is {MinClusterProcesses}.");

        var mapOwners = new Dictionary<uint, string>();
        foreach ((string key, SupervisedProcessConfig config) in Processes)
        {
            if (key.Contains(':', StringComparison.Ordinal))
                problems.Add($"{section}:Processes key '{key}' must not contain ':' (use e.g. 'worker-EK' and set Name to 'worker:EK').");

            string name = string.IsNullOrWhiteSpace(config.Name) ? key : config.Name;
            try
            {
                var effective = new SupervisedProcessConfig
                {
                    Name = name,
                    Role = config.Role,
                    ExecutablePath = config.ExecutablePath,
                    RestartBackoffMinMs = config.RestartBackoffMinMs,
                    RestartBackoffMaxMs = config.RestartBackoffMaxMs,
                    DrainTimeoutMs = config.DrainTimeoutMs,
                    RequiredMemoryMb = config.RequiredMemoryMb,
                    StartOrder = config.StartOrder,
                };
                effective.Validate();
            }
            catch (InvalidOperationException ex)
            {
                problems.Add($"{section}:Processes:{key}: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(config.Role) && !SupervisedRoles.IsKnown(config.Role))
                problems.Add($"{section}:Processes:{key}:Role '{config.Role}' is not one of combined, realm, gateway, worker, social, coordinator, supervisor.");

            if (string.Equals(config.Role, SupervisedRoles.Worker, StringComparison.OrdinalIgnoreCase))
            {
                foreach (uint mapId in config.MapAssignments)
                {
                    if (mapOwners.TryGetValue(mapId, out string? owner))
                        problems.Add($"{section}:Processes:{key}:MapAssignments lists map {mapId}, already assigned to '{owner}'.");
                    else
                        mapOwners[mapId] = key;
                }
            }
        }

        return problems;
    }

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
