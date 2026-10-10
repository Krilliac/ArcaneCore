using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>
/// DI wiring shared by both daemons: each calls <see cref="AddOpsMetrics(IServiceCollection, IConfiguration, string, MetricsRole)"/>
/// with its role, which binds <see cref="MetricsOptions"/> and registers the exporters. The store and the host are always
/// registered (so a world feature can ask for them); with <c>Ops:Metrics:Enabled=false</c> the host starts nothing and the
/// store never attaches its listener.
/// </summary>
public static class MetricsServiceCollectionExtensions
{
    /// <summary>Register the store and the exporters of <paramref name="role"/> for options the caller has bound.</summary>
    public static IServiceCollection AddOpsMetrics(this IServiceCollection services, string serviceName, MetricsRole role = MetricsRole.World)
    {
        services.AddOptions<MetricsOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MetricsOptions>, MetricsOptionsValidation>());
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<MetricsOptions>>().Value);
        services.TryAddSingleton<MetricsStore>();
        services.TryAddSingleton(sp => new MetricsHost(
            sp.GetRequiredService<MetricsOptions>(), sp.GetRequiredService<MetricsStore>(), sp.GetRequiredService<ILogger<MetricsHost>>(), serviceName, role));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MetricsHost>(sp => sp.GetRequiredService<MetricsHost>()));
        return services;
    }

    /// <summary>Bind <c>Ops:Metrics</c> and register the exporters of <paramref name="role"/>: the World binds <c>PrometheusPrefix</c>, the Realm <c>RealmPrometheusPrefix</c>.</summary>
    public static IServiceCollection AddOpsMetrics(this IServiceCollection services, IConfiguration configuration, string serviceName, MetricsRole role)
    {
        services.Configure<MetricsOptions>(configuration.GetSection(MetricsOptions.SectionName));
        return services.AddOpsMetrics(serviceName, role);
    }
}
