using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Combat;

/// <summary>
/// The retail power economy in the world daemon (discovered <see cref="IWorldFeature"/>): binds the
/// <c>Combat</c> rates (<see cref="CombatOptions"/>; defaults are the retail values), gives combat its aura source
/// (Berserker Rage, Bloodrage's rage-decay stop, power-regen auras) and registers the 82% power refund of
/// dodged and parried abilities (<see cref="PowerRefundObserver"/>).
/// </summary>
public sealed class PowerFeature(IServiceProvider services, ILogger<PowerFeature> logger) : IWorldFeature
{
    public CombatOptions Options { get; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetService<IConfiguration>()?.GetSection(CombatOptions.SectionName).Bind(Options);
        foreach (string name in Options.Normalize())
        {
            logger.LogError("{Rate} can't be negative. Using 1 instead.", name);
        }

        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        spells.System.RegisterObserver(new PowerRefundObserver());
        PowerEnvironment.Register(world, new PowerEnvironment(Options, new SpellSystemPowerAuras(spells.System)));
        logger.LogInformation(
            "Power rates: rage income {Income}, rage loss {Loss}, energy {Energy}, mana {Mana}",
            Options.RateRageIncome, Options.RateRageLoss, Options.RateEnergy, Options.RateMana);
    }
}
