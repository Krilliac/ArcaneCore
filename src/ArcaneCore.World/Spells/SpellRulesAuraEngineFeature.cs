using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Binds the <c>Auras</c> options section (docs/areas/aura-engine.md) onto the world's spell system. Features attach in full-name order, so the
/// <c>SpellRules</c> prefix keeps it after <see cref="SpellFeature"/>, which creates the system.
/// </summary>
public sealed class SpellRulesAuraEngineFeature(IServiceProvider services) : IWorldFeature
{
    /// <summary>The bound options (defaults until <see cref="Attach"/>).</summary>
    public AuraOptions Options { get; private set; } = new();

    /// <summary>Read <c>Auras</c> from <paramref name="configuration"/>; absent keys keep the retail defaults.</summary>
    public static AuraOptions BindOptions(IConfiguration? configuration)
    {
        var options = new AuraOptions();
        configuration?.GetSection(AuraOptions.SectionName).Bind(options);
        return options;
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        Options = BindOptions(services.GetService<IConfiguration>());
        services.GetRequiredService<SpellFeature>().System.AuraOptions = Options;
    }
}
