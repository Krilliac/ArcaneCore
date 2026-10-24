using ArcaneCore.World.Bots;
using ArcaneCore.World.Features;
using ArcaneCore.World.OptionKeys;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The playerbot pack's service registrations (docs/design/hot-reload-tiers.md, section 4.4 2a), discovered by
/// <c>AddWorldDaemon</c> through <see cref="IWorldServiceModule"/> so the core names no playerbot type. The registrations are
/// the ones <c>AddWorldDaemon</c> used to list, in the same order.
/// </summary>
public sealed class PlayerbotServiceModule : IWorldServiceModule
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<PlayerbotOptions>(options => PlayerbotOptions.ApplyConfiguration(options, configuration));
        services.AddSingleton<IPlayerbotService>(sp => sp.GetRequiredService<ManagedPlayerbotFeature>());
        // Far travel (docs/areas/playbots.md, Travel): the journey seam; the brain drives journeys, callers begin them through it.
        services.AddSingleton<IPlayerbotTravel>(sp => new PlayerbotTravel(sp.GetRequiredService<IOptions<PlayerbotOptions>>().Value));
        // The home progression's travel seam answered by it: a town home across the sea is reachable by boat or zeppelin.
        services.AddSingleton<IPlayerbotHomeTravel>(sp => new PlayerbotTravelHomeTravel(sp.GetRequiredService<IOptions<PlayerbotOptions>>().Value));
        services.AddPlayerbotQa(configuration); // bot QA: findings from the bots' warnings (docs/areas/playbots-qa.md)
        services.AddSingleton<IOptionKeyProvider, PlayerbotOptionRegistration>(); // the pack's own keys for .server config
        // The bot anomaly history subscribes to the process anomaly ledger when first touched: as early as the core used to.
        _ = PlayerbotAnomalyHistory.Shared;
    }
}
