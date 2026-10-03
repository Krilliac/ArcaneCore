using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
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
    public CombatOptions Options { get; private set; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        CombatEnvironment environment = CombatEnvironments.GetOrCreate(services, world, logger);
        Options = environment.Options;

        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        spells.System.RegisterObserver(new PowerRefundObserver());
        environment.Auras = new SpellSystemPowerAuras(spells.System);
        logger.LogInformation(
            "Power rates: rage income {Income}, rage loss {Loss}, energy {Energy}, mana {Mana}",
            Options.RateRageIncome, Options.RateRageLoss, Options.RateEnergy, Options.RateMana);
    }
}
