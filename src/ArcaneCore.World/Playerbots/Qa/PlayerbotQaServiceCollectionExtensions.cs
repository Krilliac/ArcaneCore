using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>DI wiring of bot QA: the options, the signal bus and the log tap that feeds it (the feature itself is discovered by <c>AddWorldFeatures</c>).</summary>
public static class PlayerbotQaServiceCollectionExtensions
{
    public static IServiceCollection AddPlayerbotQa(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<PlayerbotQaOptions>(options => PlayerbotQaOptions.ApplyConfiguration(options, configuration));
        services.TryAddSingleton(sp =>
        {
            PlayerbotQaOptions options = sp.GetRequiredService<IOptions<PlayerbotQaOptions>>().Value;
            return new PlayerbotQaSignalBus(Math.Clamp(options.SignalQueueCapacity, 64, 1_000_000));
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, PlayerbotQaLogTap>());
        return services;
    }
}
