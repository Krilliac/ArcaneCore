using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Configuration;

// The conventional namespace for AddXxx extensions, so WorldProductionServices needs one added line and no new using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>The world daemon's client-rejection log and duplicate-packet detector wiring (docs/ops/observability.md).</summary>
public static class WorldObservabilityServiceCollectionExtensions
{
    /// <summary>Bind <c>Ops:Observability</c> and register <see cref="ClientRejectionLog"/> and <see cref="DuplicatePacketDetector"/>, which every <see cref="WorldSession"/> resolves once.</summary>
    public static IServiceCollection AddWorldObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton<ClientRejectionLog>();
        services.AddSingleton<DuplicatePacketDetector>();
        return services;
    }
}
