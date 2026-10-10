using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>
/// DI wiring shared by both daemons. The caller binds <see cref="MetricsOptions"/> (<c>services.Configure</c>) and calls
/// <see cref="AddOpsMetrics"/>. The store and the host are always registered (so a world feature can ask for them);
/// with <c>Ops:Metrics:Enabled=false</c> the host starts nothing and the store never attaches its listener.
/// </summary>
public static class MetricsServiceCollectionExtensions
{
    public static IServiceCollection AddOpsMetrics(this IServiceCollection services, string serviceName)
    {
        services.AddOptions<MetricsOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MetricsOptions>, MetricsOptionsValidation>());
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<MetricsOptions>>().Value);
        services.TryAddSingleton<MetricsStore>();
        services.TryAddSingleton(sp => new MetricsHost(
            sp.GetRequiredService<MetricsOptions>(), sp.GetRequiredService<MetricsStore>(), sp.GetRequiredService<ILogger<MetricsHost>>(), serviceName));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MetricsHost>(sp => sp.GetRequiredService<MetricsHost>()));
        return services;
    }
}
