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
        // The direct report path (IPlayerbotQaSink): any feature reports a signal without a log line; inert while QA is disabled.
        services.TryAddSingleton<PlayerbotQaSink>();
        services.TryAddSingleton<IPlayerbotQaSink>(sp => sp.GetRequiredService<PlayerbotQaSink>());
        // The interaction signals: the game's decision sites (IGameQaObserver), the bot sessions' packet tap and opcode gate (IManagedPacketTap) and,
        // through PlayerbotQaInteractionFeature, the kill events all reach one monitor; it is inert while QA is disabled.
        services.TryAddSingleton(sp => new PlayerbotQaInteractionMonitor(sp.GetRequiredService<IPlayerbotQaSink>(), sp));
        services.TryAddSingleton<Game.Diagnostics.IGameQaObserver>(sp => sp.GetRequiredService<PlayerbotQaInteractionMonitor>());
        services.TryAddSingleton<Net.IManagedPacketTap>(sp => sp.GetRequiredService<PlayerbotQaInteractionMonitor>());
        return services;
    }
}
