using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

// Deliberately in the Configuration namespace, which both Program.cs files already import: wiring the provider is then the
// single line `builder.Services.AddArcaneCoreLogging(builder.Configuration);`. The implementation lives in Logging/.
namespace ArcaneCore.Kernel.Configuration;

public static class ArcaneLoggingServiceCollectionExtensions
{
    /// <summary>
    /// Replaces every host-default logger provider (console, debug, EventSource, EventLog) with <see cref="ArcaneLoggerProvider"/>,
    /// configured from <c>Logging:ArcaneCore</c> (docs/ops/logging.md). The standard <c>Logging:LogLevel</c> rules keep filtering
    /// categories, and <c>Logging:ArcaneCore:LogLevel</c> overrides them for this provider alone. Fails closed: an invalid section
    /// throws here, before the host is built, with every problem listed.
    /// </summary>
    public static IServiceCollection AddArcaneCoreLogging(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        ConfigReport report = ConfigReport.Run(configuration, [new LoggingConfigChecks()]);
        if (report.ErrorCount > 0)
        {
            throw new InvalidOperationException("The logging configuration is invalid; the daemon refuses to start:\n" + string.Join('\n', report.Issues));
        }

        services.AddOptions<ArcaneLoggingOptions>().Bind(configuration.GetSection(ArcaneLoggingOptions.SectionName));
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ArcaneLoggerProvider>());
        });
        return services;
    }
}
