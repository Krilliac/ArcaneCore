using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Metrics;
using Microsoft.Extensions.Configuration;

// The conventional namespace for AddXxx extensions, so Program.cs needs one added line and no new using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Server metrics wiring for both daemons (docs/ops/metrics.md).</summary>
public static class WorldMetricsServiceCollectionExtensions
{
    /// <summary>Bind <c>Ops:Metrics</c> and register the store and exporters; the world instruments come from <c>WorldMetricsFeature</c>.</summary>
    public static IServiceCollection AddWorldMetrics(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MetricsOptions>(configuration.GetSection(MetricsOptions.SectionName));
        return services.AddOpsMetrics("arcanecore-world");
    }
}
