using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Combat;

/// <summary>
/// Aura states and the reactive abilities in the world daemon (discovered <see cref="IWorldFeature"/>): installs the caster
/// aura state and 20% target state casts checks (Revenge, Riposte, Execute), opens the reactive windows when a swing or an
/// ability is dodged, parried or blocked (Overpower, Revenge), and ticks the windows and the 20% health state of every map.
/// The Overpower marker is a combo point, so <see cref="ComboFeature"/> must be present.
/// </summary>
public sealed class ReactiveFeature(IServiceProvider services) : IWorldFeature
{
    public AuraStateService? AuraStates { get; private set; }

    public ReactiveService? Reactives { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellFeature spells = services.GetRequiredService<SpellFeature>();
        var states = new AuraStateService(spells.System, player => spells.Spellbook.GetSpells(player));
        AuraStateCastChecks.Install(spells.System, states);
        var reactives = new ReactiveService(states, () => services.GetRequiredService<ComboFeature>().Service
            ?? throw new InvalidOperationException("the combo point service is not available yet (ComboFeature.Service is null: it is attached after the reactive feature needed it)"));
        spells.System.RegisterObserver(new ReactiveSpellObserver(reactives));
        AuraStates = states;
        Reactives = reactives;

        world.MapCreated += Install;
        foreach (Map map in world.Maps)
        {
            Install(map);
        }
    }

    private void Install(Map map)
    {
        if (Reactives is not { } reactives)
        {
            return;
        }

        map.AddUpdater(new ReactiveUpdater(reactives));
        if (map.FindUpdater<MapCombat>() is { } combat)
        {
            reactives.Observe(combat);
        }
    }
}
