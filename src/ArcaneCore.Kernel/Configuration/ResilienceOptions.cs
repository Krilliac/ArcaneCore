using ArcaneCore.Kernel.Resilience;

namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>Resilience</c> section: how the daemons behave when a dependency misbehaves (docs/ops/resilience.md).
/// Every key is restart-only; <c>.reload config</c> does not touch this section. ArcaneCore-specific: vmangos has no
/// equivalent (a dead database there makes every query fail and log until the server is restarted).
/// </summary>
public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    /// <summary>Protection of the database connections (<c>Resilience:Database</c>).</summary>
    public DatabaseResilienceOptions Database { get; } = new();

    /// <summary>Every problem with the bound values, as "Resilience:Key: problem" lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Database.Validate(SectionName + ":Database", problems);
        return problems;
    }
}

/// <summary>The database guard: breaker, per-call timeout, bulkhead and the start-up bootstrap retry.</summary>
public sealed class DatabaseResilienceOptions
{
    /// <summary>`false` turns the whole database guard off: calls go straight to the store and a start-up failure is not retried (the pre-resilience behaviour).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Longest one guarded database call may take before it is cancelled and counted as a failure, in milliseconds; 0 disables the timeout.</summary>
    public int QueryTimeoutMs { get; set; } = 5000;

    /// <summary>The circuit breaker, one per logical database (Auth, Characters, World), all with these settings.</summary>
    public CircuitBreakerSettings Breaker { get; } = new();

    /// <summary>How many guarded calls may run and wait at once per logical database.</summary>
    public BulkheadSettings Bulkhead { get; } = new();

    /// <summary>How a start retries the schema bootstrap when the server cannot be reached.</summary>
    public BootstrapRetrySettings Bootstrap { get; } = new();

    internal void Validate(string prefix, List<string> problems)
    {
        if (QueryTimeoutMs is < 0 or > 600_000)
        {
            problems.Add($"{prefix}:QueryTimeoutMs: must be 0 (disabled) or 1..600000 milliseconds.");
        }

        Breaker.Validate(prefix + ":Breaker", problems);
        Bulkhead.Validate(prefix + ":Bulkhead", problems);
        Bootstrap.Validate(prefix + ":Bootstrap", problems);
    }
}

/// <summary>Configuration form of <see cref="CircuitBreakerOptions"/> (durations in milliseconds, as every other option in the files).</summary>
public sealed class CircuitBreakerSettings
{
    /// <summary>Consecutive failed calls that open the circuit; 0 disables this trip (the rate trip must then be on).</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>Share of failed calls (0..1) in the sampling window that opens the circuit once MinimumThroughput calls are in it; 0 disables this trip.</summary>
    public double FailureRateThreshold { get; set; } = 0.5;

    /// <summary>Calls that must fall in the sampling window before the failure rate is judged.</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>Length of the sliding window the failure rate is measured over, in milliseconds.</summary>
    public int SamplingWindowMs { get; set; } = 10_000;

    /// <summary>How long an open circuit refuses every call before it lets one probe through, in milliseconds.</summary>
    public int OpenDurationMs { get; set; } = 10_000;

    /// <summary>Probe calls let through at once while half-open; one success closes the circuit, one failure re-opens it.</summary>
    public int HalfOpenMaxProbes { get; set; } = 1;

    /// <summary>The runtime options for a breaker named <paramref name="name"/>.</summary>
    public CircuitBreakerOptions ToOptions(string name) => new()
    {
        Name = name,
        FailureThreshold = FailureThreshold,
        FailureRateThreshold = FailureRateThreshold,
        MinimumThroughput = MinimumThroughput,
        SamplingWindow = TimeSpan.FromMilliseconds(SamplingWindowMs),
        OpenDuration = TimeSpan.FromMilliseconds(OpenDurationMs),
        HalfOpenMaxProbes = HalfOpenMaxProbes,
    };

    internal void Validate(string prefix, List<string> problems)
    {
        if (FailureThreshold < 0)
        {
            problems.Add($"{prefix}:FailureThreshold: must be 0 (disabled) or positive.");
        }

        if (FailureRateThreshold is < 0 or > 1 || double.IsNaN(FailureRateThreshold))
        {
            problems.Add($"{prefix}:FailureRateThreshold: must be between 0 (disabled) and 1.");
        }

        if (FailureThreshold == 0 && FailureRateThreshold == 0)
        {
            problems.Add($"{prefix}:FailureThreshold: FailureThreshold and FailureRateThreshold cannot both be 0; the breaker would never open.");
        }

        if (MinimumThroughput < 1)
        {
            problems.Add($"{prefix}:MinimumThroughput: must be at least 1.");
        }

        if (SamplingWindowMs is < 1 or > 86_400_000)
        {
            problems.Add($"{prefix}:SamplingWindowMs: must be 1..86400000 milliseconds.");
        }

        if (OpenDurationMs is < 1 or > 86_400_000)
        {
            problems.Add($"{prefix}:OpenDurationMs: must be 1..86400000 milliseconds.");
        }

        if (HalfOpenMaxProbes < 1)
        {
            problems.Add($"{prefix}:HalfOpenMaxProbes: must be at least 1.");
        }
    }
}

/// <summary>Configuration form of <see cref="BulkheadOptions"/>.</summary>
public sealed class BulkheadSettings
{
    /// <summary>Guarded calls allowed to run at once per logical database; 0 disables the bulkhead (no cap).</summary>
    public int MaxConcurrency { get; set; }

    /// <summary>Calls allowed to wait for a slot when every slot is busy; more are refused at once.</summary>
    public int MaxQueue { get; set; } = 64;

    /// <summary>The runtime options, or null when the bulkhead is disabled.</summary>
    public BulkheadOptions? ToOptions(string name)
        => MaxConcurrency <= 0 ? null : new BulkheadOptions { Name = name, MaxConcurrency = MaxConcurrency, MaxQueue = MaxQueue };

    internal void Validate(string prefix, List<string> problems)
    {
        if (MaxConcurrency < 0)
        {
            problems.Add($"{prefix}:MaxConcurrency: must be 0 (disabled) or positive.");
        }

        if (MaxQueue < 0)
        {
            problems.Add($"{prefix}:MaxQueue: must be 0 or positive.");
        }
    }
}

/// <summary>Configuration form of <see cref="RetryOptions"/> for the start-up schema bootstrap.</summary>
public sealed class BootstrapRetrySettings
{
    /// <summary>Attempts to reach the database at start, including the first; 1 means no retry. The last failure exits the process with code 6.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Wait after the first failed attempt, in milliseconds; it doubles after each further failure (with full jitter).</summary>
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>Cap on the wait between attempts, in milliseconds.</summary>
    public int MaxDelayMs { get; set; } = 5000;

    /// <summary>Total time budget for all attempts, in milliseconds; 0 lets the attempts alone bound it.</summary>
    public int MaxTotalDurationMs { get; set; }

    public RetryOptions ToOptions() => new()
    {
        MaxAttempts = MaxAttempts,
        BaseDelay = TimeSpan.FromMilliseconds(BaseDelayMs),
        MaxDelay = TimeSpan.FromMilliseconds(MaxDelayMs),
        MaxTotalDuration = TimeSpan.FromMilliseconds(MaxTotalDurationMs),
        Jitter = RetryJitter.Full,
    };

    internal void Validate(string prefix, List<string> problems)
    {
        if (MaxAttempts < 1)
        {
            problems.Add($"{prefix}:MaxAttempts: must be at least 1.");
        }

        if (BaseDelayMs is < 0 or > 3_600_000)
        {
            problems.Add($"{prefix}:BaseDelayMs: must be 0..3600000 milliseconds.");
        }

        if (MaxDelayMs < BaseDelayMs || MaxDelayMs > 3_600_000)
        {
            problems.Add($"{prefix}:MaxDelayMs: must be at least BaseDelayMs and at most 3600000 milliseconds.");
        }

        if (MaxTotalDurationMs is < 0 or > 86_400_000)
        {
            problems.Add($"{prefix}:MaxTotalDurationMs: must be 0 (unbounded) or up to 86400000 milliseconds.");
        }
    }
}
