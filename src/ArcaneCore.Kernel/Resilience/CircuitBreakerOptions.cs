namespace ArcaneCore.Kernel.Resilience;

/// <summary>
/// How a <see cref="CircuitBreaker"/> decides to open and when it probes again. Two independent trip conditions:
/// a run of consecutive failures (<see cref="FailureThreshold"/>) and a failure rate over a sliding window
/// (<see cref="FailureRateThreshold"/> of the calls in <see cref="SamplingWindow"/>, judged only once
/// <see cref="MinimumThroughput"/> calls are in the window). Either trips the circuit.
/// </summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Name used in exceptions, state-change notifications and log lines.</summary>
    public string Name { get; set; } = "circuit";

    /// <summary>Consecutive failures that open the circuit; 0 disables the consecutive-failure trip.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>Share (0 &lt; rate ≤ 1) of failed calls in the sampling window that opens the circuit; 0 disables the rate trip.</summary>
    public double FailureRateThreshold { get; set; } = 0.5;

    /// <summary>Calls that must be in the sampling window before the rate is judged, so a single early failure cannot trip it.</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>Length of the sliding window the failure rate is measured over.</summary>
    public TimeSpan SamplingWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Number of fixed buckets the window is divided into (the resolution of the sliding window).</summary>
    public int SamplingBuckets { get; set; } = 10;

    /// <summary>How long the circuit stays open before it lets probes through (half-open).</summary>
    public TimeSpan OpenDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Probe calls allowed through at once while half-open; the first success closes the circuit, the first failure re-opens it.</summary>
    public int HalfOpenMaxProbes { get; set; } = 1;

    /// <summary>The first problem with these options, or null when they are usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "Name must not be empty";
        }

        if (FailureThreshold < 0)
        {
            return "FailureThreshold must be 0 (disabled) or positive";
        }

        if (FailureRateThreshold is < 0 or > 1 || double.IsNaN(FailureRateThreshold))
        {
            return "FailureRateThreshold must be between 0 (disabled) and 1";
        }

        if (FailureThreshold == 0 && FailureRateThreshold == 0)
        {
            return "FailureThreshold and FailureRateThreshold cannot both be disabled; the breaker would never open";
        }

        if (MinimumThroughput < 1)
        {
            return "MinimumThroughput must be at least 1";
        }

        if (SamplingWindow <= TimeSpan.Zero || SamplingWindow > TimeSpan.FromDays(1))
        {
            return "SamplingWindow must be positive and at most one day";
        }

        if (SamplingBuckets is < 1 or > 1000)
        {
            return "SamplingBuckets must be 1..1000";
        }

        if (OpenDuration <= TimeSpan.Zero || OpenDuration > TimeSpan.FromDays(1))
        {
            return "OpenDuration must be positive and at most one day";
        }

        if (HalfOpenMaxProbes < 1)
        {
            return "HalfOpenMaxProbes must be at least 1";
        }

        return null;
    }
}
