using ArcaneCore.Data.Resilience;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Data;

/// <summary>DI wiring of the database guard (docs/ops/resilience.md). Lives beside <see cref="DataServiceCollectionExtensions"/> so the hosts need no extra using.</summary>
public static class DatabaseResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Bind and validate <c>Resilience</c>, and register <see cref="DatabaseCircuits"/> and <see cref="DatabaseGuard"/> as
    /// singletons. Invalid values fail the host start with every problem listed (<see cref="IValidateOptions{TOptions}"/>);
    /// nothing is bound to a port before that. Idempotent: calling it twice registers once.
    /// </summary>
    public static IServiceCollection AddDatabaseResilience(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(d => d.ServiceType == typeof(DatabaseGuard)))
        {
            return services;
        }

        services.AddOptions<ResilienceOptions>().Bind(configuration.GetSection(ResilienceOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ResilienceOptions>, ResilienceOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DatabaseCircuits>();
        services.AddSingleton<DatabaseGuard>();
        return services;
    }

    private sealed class ResilienceOptionsValidator : IValidateOptions<ResilienceOptions>
    {
        public ValidateOptionsResult Validate(string? name, ResilienceOptions options)
        {
            IReadOnlyList<string> problems = options.Validate();
            return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
        }
    }
}
