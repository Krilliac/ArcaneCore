using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Ranged;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ranged;

/// <summary>
/// The hunter / ranged settings in the world daemon (discovered <see cref="IWorldFeature"/>):
/// reads configuration section "Ranged" (every default is the retail behaviour,
/// <see cref="RangedOptions"/>) and hands it to the spell system, which applies the ammunition
/// rules in its cast pipeline.
/// </summary>
public sealed class RangedFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RangedFeature> _logger;

    public RangedFeature(IServiceProvider services, ILogger<RangedFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = Bind(services.GetService<IConfiguration>());
    }

    /// <summary>The bound settings.</summary>
    public RangedOptions Options { get; }

    /// <summary>The options of <paramref name="configuration"/>'s "Ranged" section over the retail defaults.</summary>
    public static RangedOptions Bind(IConfiguration? configuration)
    {
        var options = new RangedOptions();
        configuration?.GetSection(RangedOptions.SectionName).Bind(options);
        return options;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _services.GetRequiredService<SpellFeature>().System.RangedOptions = Options;
        if (Options.Ammo.Mode != AmmoMode.Retail || Options.Range.Leeway != RangeLeewayMode.Retail)
        {
            _logger.LogWarning("Ranged settings deviate from retail: Ammo.Mode={Ammo}, Range.Leeway={Leeway}", Options.Ammo.Mode, Options.Range.Leeway);
        }
    }
}
