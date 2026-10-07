using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data.Resilience;

/// <summary>
/// One <see cref="CircuitBreaker"/> per logical database (<see cref="DatabaseComponent"/>), shared by every guarded
/// call of the process, with the operator-facing log lines on each transition: an opening is one ERROR line naming
/// the cause and how long access is refused, the probe one WARNING, the recovery one INFORMATION. Singleton; the
/// breakers are built once from <c>Resilience:Database:Breaker</c> and never replaced.
/// </summary>
public sealed class DatabaseCircuits
{
    private readonly CircuitBreaker[] _breakers;
    private readonly ILogger<DatabaseCircuits> _logger;
    private readonly TimeSpan _openDuration;

    public DatabaseCircuits(IOptions<ResilienceOptions> options, ILogger<DatabaseCircuits> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        CircuitBreakerSettings settings = options.Value.Database.Breaker;
        _openDuration = TimeSpan.FromMilliseconds(settings.OpenDurationMs);
        DatabaseComponent[] components = Enum.GetValues<DatabaseComponent>();
        _breakers = new CircuitBreaker[components.Length];
        foreach (DatabaseComponent component in components)
        {
            _breakers[(int)component] = new CircuitBreaker(
                settings.ToOptions(Describe(component)), timeProvider, DatabaseTransience.Classifier, OnStateChange);
        }
    }

    /// <summary>The breaker guarding one logical database.</summary>
    public CircuitBreaker For(DatabaseComponent component)
    {
        int index = (int)component;
        if ((uint)index >= (uint)_breakers.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(component));
        }

        return _breakers[index];
    }

    /// <summary>Every breaker, in <see cref="DatabaseComponent"/> order (diagnostics, operator commands).</summary>
    public IReadOnlyList<CircuitBreaker> All => _breakers;

    /// <summary>The breaker name of a component, e.g. "Auth database".</summary>
    public static string Describe(DatabaseComponent component) => component + " database";

    private void OnStateChange(CircuitStateChange change)
    {
        switch (change.To)
        {
            case CircuitState.Open:
                _logger.LogError(
                    "{Circuit} circuit OPENED ({Reason}); every call is refused for the next {OpenDurationMs} ms (fail closed), then one probe is let through",
                    change.Name, Reason(change.Cause), _openDuration.TotalMilliseconds);
                break;

            case CircuitState.HalfOpen:
                _logger.LogWarning("{Circuit} circuit half-open: probing the database with one call", change.Name);
                break;

            case CircuitState.Closed:
                _logger.LogInformation("{Circuit} circuit closed: database access restored", change.Name);
                break;

            case CircuitState.Isolated:
                _logger.LogWarning("{Circuit} circuit isolated by hand; every call is refused until it is reset", change.Name);
                break;
        }
    }

    /// <summary>The cause as one line: type and message, with the innermost message when it adds something. Provider messages carry no credentials.</summary>
    internal static string Reason(Exception? cause)
    {
        if (cause is null)
        {
            return "no cause recorded";
        }

        Exception innermost = cause;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        string text = $"{cause.GetType().Name}: {cause.Message.ReplaceLineEndings(" ")}";
        return ReferenceEquals(innermost, cause) ? text : $"{text} <- {innermost.GetType().Name}: {innermost.Message.ReplaceLineEndings(" ")}";
    }
}
