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
    /// Graceful drain timeout in milliseconds before the supervisor forcibly terminates an owned child process (1 to 600000; default 30000).
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

    /// <summary>
    /// Seconds without a world-tick heartbeat after which the supervisor treats a beating-then-silent child as hung and drains, kills and relaunches it
    /// (default 120; 0 turns hang detection off; otherwise 30 to 3600). The watch is armed by the child's first <c>alive</c> beat and is suspended while
    /// the child has announced that it is stopping.
    /// </summary>
    public int HangTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Seconds after launch (default 300; 30 to 86400) before a child that has never sent an <c>alive</c> beat gets one warning that hang detection is not
    /// armed. A child that starts slowly is never killed for silence before its first beat.
    /// </summary>
    public int HeartbeatGraceSeconds { get; set; } = 300;

    /// <summary>Milliseconds a hung child gets to drain on the supervisor's drain request before it is killed (default 15000; 1 to 600000).</summary>
    public int HangDrainTimeoutMs { get; set; } = 15_000;

    /// <summary>
    /// Milliseconds a child that announced its stop may take to exit before the supervisor kills it (default 0, which means <see cref="DrainTimeoutMs"/>;
    /// otherwise 1 to 600000). The kill is classified by what the child announced (restart relaunches at once, shutdown stays down).
    /// </summary>
    public int StopGraceMs { get; set; }

    /// <summary>What the supervisor captures from a hung child before it drains it: <c>None</c> (default), <c>Stacks</c> or <c>Dump</c> (a full memory dump, which holds secrets).</summary>
    public SupervisedHangCaptureMode HangCapture { get; set; } = SupervisedHangCaptureMode.None;

    /// <summary>
    /// The realm id this child serves (default 0: none). The supervisor routes a GM's cross-world <c>.server shutdown|restart #delay #exitcode #realmid</c>
    /// to the child with that id, and tells every child its own id so it can tell a local request from a remote one. Unique per supervisor.
    /// </summary>
    public int RealmId { get; set; }

    /// <summary>The per-supervisor secret the supervisor puts in the child's environment; set by the supervisor at each launch, never read from configuration.</summary>
    internal string? ProtocolToken { get; set; }

    /// <summary>Upper bound of <see cref="DrainTimeoutMs"/> (10 minutes): the host's shutdown time is sized from the sum of these.</summary>
    public const int MaxDrainTimeoutMs = 600_000;

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
        if (DrainTimeoutMs > MaxDrainTimeoutMs)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' DrainTimeoutMs must be at most {MaxDrainTimeoutMs} (10 minutes).");
        if (RequiredMemoryMb < 0)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' RequiredMemoryMb must be non-negative.");
        if (HangTimeoutSeconds != 0 && HangTimeoutSeconds is < 30 or > 3600)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' HangTimeoutSeconds must be 0 (off) or 30 to 3600 (was {HangTimeoutSeconds}).");
        if (HeartbeatGraceSeconds is < 30 or > 86_400)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' HeartbeatGraceSeconds must be 30 to 86400 (was {HeartbeatGraceSeconds}).");
        if (HangDrainTimeoutMs is <= 0 or > MaxDrainTimeoutMs)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' HangDrainTimeoutMs must be 1 to {MaxDrainTimeoutMs} (was {HangDrainTimeoutMs}).");
        if (StopGraceMs is < 0 or > MaxDrainTimeoutMs)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' StopGraceMs must be 0 (use DrainTimeoutMs) or 1 to {MaxDrainTimeoutMs} (was {StopGraceMs}).");
        if (!Enum.IsDefined(HangCapture))
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' HangCapture is not one of None, Stacks, Dump.");
        if (RealmId < 0)
            throw new InvalidOperationException($"SupervisedProcessConfig '{Name}' RealmId must not be negative (was {RealmId}); 0 means none.");
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
    /// How many times a child may be restarted within <see cref="CrashLoopWindowSeconds"/> before the supervisor gives up on it (default 5; 1 to 1000): that many
    /// restarts are allowed, and the next failure inside the window (the one after the last allowed restart) leaves it Faulted and is recorded in the fault and anomaly ledgers. A restart request (exit code 2) after a healthy run does not count.
    /// </summary>
    public int CrashLoopMaxRestarts { get; set; } = 5;

    /// <summary>The window the crash-loop count is taken over, in seconds (default 300; 10 to 86400).</summary>
    public int CrashLoopWindowSeconds { get; set; } = 300;

    /// <summary>
    /// A child that ran this many seconds (default 120; 1 to 86400) before it exited is healthy: its restart backoff starts over at the minimum and an
    /// exit-code-2 restart is not a crash-loop failure.
    /// </summary>
    public int HealthyUptimeSeconds { get; set; } = 120;

    /// <summary>Milliseconds the supervisor waits before it relaunches a child that exited with the restart code 2 (default 0; 0 to 600000). Crashes use the backoff instead.</summary>
    public int RestartDelayMs { get; set; }

    /// <summary>
    /// Stop everything when fewer than <see cref="MinClusterProcesses"/> children are left running (default true): the rest are drained and the supervisor exits
    /// 0 when every stop was deliberate and 1 when a child was given up on. Off, the supervisor keeps the remaining children running.
    /// </summary>
    public bool StopWhenBelowMinimum { get; set; } = true;

    /// <summary>Where hang captures (stack reports and dumps) are written (default <c>logs/supervisor-captures</c>); relative to the working directory.</summary>
    public string HangCaptureDirectory { get; set; } = "logs/supervisor-captures";

    /// <summary>The longest a hang capture may take before the supervisor drains and kills the child anyway, in seconds (default 300; 1 to 3600).</summary>
    public int HangCaptureTimeoutSeconds { get; set; } = 300;

    /// <summary>The dotnet-stack executable for <c>HangCapture=Stacks</c> (default empty: none; required when any child uses Stacks).</summary>
    public string StackCaptureTool { get; set; } = string.Empty;

    /// <summary>
    /// Supervised process definitions, started in ascending <see cref="SupervisedProcessConfig.StartOrder"/> (ties by key), keyed by a configuration key that must not contain <c>:</c>
    /// (e.g. <c>worker-EK</c>); the display name may, via <see cref="SupervisedProcessConfig.Name"/> (<c>worker:EK</c>).
    /// </summary>
    public Dictionary<string, SupervisedProcessConfig> Processes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Added to the sum of the children's drain timeouts when the host's shutdown timeout is sized.</summary>
    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(15);

    /// <summary>The longest host shutdown the supervisor asks for; a drain sum above it is a <see cref="Warnings"/> entry.</summary>
    public static readonly TimeSpan MaxShutdown = TimeSpan.FromHours(1);

    /// <summary>Sum of every child's <see cref="SupervisedProcessConfig.DrainTimeoutMs"/> (each clamped to <see cref="SupervisedProcessConfig.MaxDrainTimeoutMs"/>): children drain one after another.</summary>
    public TimeSpan TotalDrainBudget => TimeSpan.FromMilliseconds(Processes.Values.Sum(p => (long)Math.Clamp(p.DrainTimeoutMs, 0, SupervisedProcessConfig.MaxDrainTimeoutMs)));

    /// <summary>Non-fatal findings (empty when none), for the startup check.</summary>
    public IEnumerable<string> Warnings()
    {
        if (Enabled && TotalDrainBudget + ShutdownMargin > MaxShutdown)
        {
            yield return $"{SectionName}:Processes drain timeouts add up to {TotalDrainBudget.TotalMinutes:0.#} min, but the host's shutdown timeout is capped at {MaxShutdown.TotalMinutes:0} min; the children last in the drain order can be killed before their DrainTimeoutMs elapses.";
        }

        if (Enabled && Processes.Values.Any(p => p.HangCapture == SupervisedHangCaptureMode.Dump))
        {
            yield return $"{SectionName}:Processes:<key>:HangCapture is Dump: a dump of a hung child is written to {HangCaptureDirectory}, and it holds everything in that process's memory (session keys, password hashes in flight, chat). Keep the directory private and delete the dumps when the hang is understood.";
        }
    }

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
        if (CrashLoopMaxRestarts is < 1 or > 1000)
            problems.Add($"{section}:CrashLoopMaxRestarts must be 1 to 1000 (was {CrashLoopMaxRestarts}).");
        if (CrashLoopWindowSeconds is < 10 or > 86_400)
            problems.Add($"{section}:CrashLoopWindowSeconds must be 10 to 86400 (was {CrashLoopWindowSeconds}).");
        if (HealthyUptimeSeconds is < 1 or > 86_400)
            problems.Add($"{section}:HealthyUptimeSeconds must be 1 to 86400 (was {HealthyUptimeSeconds}).");
        if (RestartDelayMs is < 0 or > SupervisedProcessConfig.MaxDrainTimeoutMs)
            problems.Add($"{section}:RestartDelayMs must be 0 to {SupervisedProcessConfig.MaxDrainTimeoutMs} (was {RestartDelayMs}).");
        if (HangCaptureTimeoutSeconds is < 1 or > 3600)
            problems.Add($"{section}:HangCaptureTimeoutSeconds must be 1 to 3600 (was {HangCaptureTimeoutSeconds}).");
        if (Processes.Values.Any(p => p.HangCapture != SupervisedHangCaptureMode.None) && string.IsNullOrWhiteSpace(HangCaptureDirectory))
            problems.Add($"{section}:HangCaptureDirectory must be set when a process has HangCapture Stacks or Dump.");
        if (Processes.Values.Any(p => p.HangCapture == SupervisedHangCaptureMode.Stacks) && string.IsNullOrWhiteSpace(StackCaptureTool))
            problems.Add($"{section}:StackCaptureTool must name the dotnet-stack executable when a process has HangCapture Stacks.");

        var mapOwners = new Dictionary<uint, string>();
        var realmOwners = new Dictionary<int, string>();
        var nameOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, SupervisedProcessConfig config) in Processes)
        {
            if (key.Contains(':', StringComparison.Ordinal))
                problems.Add($"{section}:Processes key '{key}' must not contain ':' (use e.g. 'worker-EK' and set Name to 'worker:EK').");

            string name = string.IsNullOrWhiteSpace(config.Name) ? key : config.Name;
            if (nameOwners.TryGetValue(name, out string? sameNameKey))
                problems.Add($"{section}:Processes:{key} has the same name '{name}' as {section}:Processes:{sameNameKey}; names must be unique (case-insensitive).");
            else
                nameOwners[name] = key;

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
                    HangTimeoutSeconds = config.HangTimeoutSeconds,
                    HeartbeatGraceSeconds = config.HeartbeatGraceSeconds,
                    HangDrainTimeoutMs = config.HangDrainTimeoutMs,
                    StopGraceMs = config.StopGraceMs,
                    HangCapture = config.HangCapture,
                    RealmId = config.RealmId,
                };
                effective.Validate();
            }
            catch (InvalidOperationException ex)
            {
                problems.Add($"{section}:Processes:{key}: {ex.Message}");
            }

            if (config.RealmId > 0)
            {
                if (realmOwners.TryGetValue(config.RealmId, out string? realmOwner))
                    problems.Add($"{section}:Processes:{key}:RealmId {config.RealmId} is already used by '{realmOwner}'; realm ids must be unique.");
                else
                    realmOwners[config.RealmId] = key;
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
