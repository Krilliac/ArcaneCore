using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Links the melee swing to the spell system (discovered <see cref="IWorldFeature"/>): a queued next-swing spell
/// (Heroic Strike, Cleave) is cast by the swing instead of the white hit, a unit casting a non-melee spell does not
/// swing (<c>Combat:MeleeCastingBlocksSwing</c>, default true), and a queued spell ends when the attack stops.
/// </summary>
public sealed class MeleeSpellFeature(IServiceProvider services, ILogger<MeleeSpellFeature> logger) : IWorldFeature
{
    public CombatOptions Options { get; private set; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        CombatEnvironment environment = CombatEnvironments.GetOrCreate(services, world, logger);
        Options = environment.Options;
        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        environment.MeleeSpells = new SpellSystemMeleeHooks(spells.System);
    }
}
