using ArcaneCore.Game.Entities;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Spells.Scripts;

/// <summary>
/// Routes the <see cref="ISpellScript"/> hooks of a <see cref="SpellScriptRegistry"/> on a <see cref="SpellSystem"/> without editing it: a
/// cast check in <see cref="SpellCheckPhase.Final"/> with the highest order (vmangos runs the script check last, Spell.cpp:6480),
/// an <see cref="ISpellCastObserver"/> for OnCast and OnSummon, and chained handlers for DUMMY, SCRIPT_EFFECT and DISPEL that keep whatever was
/// installed before (another lane's handler, the built-in dispel) and call it after the script.
/// <para>
/// SCRIPT_EFFECT (77) has no behaviour of its own: vmangos EffectScriptEffect falls through to the database script table
/// (SpellEffects.cpp:3685-3700, :4560-4570), which is empty here, so a spell no script claims does nothing instead of being
/// reported as an unsupported effect (the six mage Teleport spells and the five Create Healthstone ranks carry one).
/// </para>
/// Install once per <see cref="SpellSystem"/> (a second call throws).
/// </summary>
public sealed class SpellScriptDispatcher : ISpellCastCheck, ISpellCastObserver
{
    private SpellScriptDispatcher(SpellScriptRegistry registry) => Registry = registry;

    public SpellScriptRegistry Registry { get; }

    /// <summary>
    /// Install the dispatcher on <paramref name="system"/>. A script whose spell id is not in the spell table is ignored with one
    /// warning per id (the table is reloadable, so the id may appear later).
    /// </summary>
    public static SpellScriptDispatcher Install(SpellSystem system, SpellScriptRegistry registry, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(registry);
        if (system.Observers.OfType<SpellScriptDispatcher>().Any())
        {
            throw new InvalidOperationException("the spell script dispatcher is already installed on this spell system");
        }

        if (logger is not null)
        {
            foreach (uint id in registry.SpellIds.Order().Where(id => system.Store.Get(id) is null))
            {
                logger.LogWarning("Spell script for spell {Spell} ignored: the spell is not in the spell table", id);
            }
        }

        var dispatcher = new SpellScriptDispatcher(registry);
        system.RegisterCastCheck(dispatcher);
        system.RegisterObserver(dispatcher);
        Chain(system, SpellEffectName.Dummy, registry);
        Chain(system, SpellEffectName.ScriptEffect, registry);
        foreach (SpellEffectName effect in registry.ExecuteEffects)
        {
            // Only an effect the world handles is chained (an unhandled one must keep being reported as not implemented); Dispel has its own wrapper below.
            if (effect is not (SpellEffectName.Dummy or SpellEffectName.ScriptEffect or SpellEffectName.Dispel) && system.HasEffectHandler(effect))
            {
                Chain(system, effect, registry);
            }
        }

        SpellEffectHandler? dispel = system.GetEffectHandler(SpellEffectName.Dispel);
        system.RegisterEffect(SpellEffectName.Dispel, context =>
        {
            ISpellScript? script = registry.Find(context.Spell.Id);
            if (script is null)
            {
                dispel?.Invoke(context);
                return;
            }

            int before = Stacks(system, context.Target);
            dispel?.Invoke(context);
            int removed = before - Stacks(system, context.Target);
            if (removed > 0)
            {
                script.OnSuccessfulDispel(context, removed);
            }
        });
        return dispatcher;
    }

    private static void Chain(SpellSystem system, SpellEffectName effect, SpellScriptRegistry registry)
    {
        SpellEffectHandler? previous = system.GetEffectHandler(effect);
        system.RegisterEffect(effect, context =>
        {
            registry.Find(context.Spell.Id)?.OnEffectExecute(context);
            previous?.Invoke(context);
        });
    }

    private static int Stacks(SpellSystem system, Unit unit)
        => system.GetAuras(unit).Where(h => !h.IsRemoved).Sum(h => Math.Max((int)h.StackAmount, 1));

    SpellCheckPhase ISpellCastCheck.Phase => SpellCheckPhase.Final;

    int ISpellCastCheck.Order => int.MaxValue;

    SpellCastResult ISpellCastCheck.Check(in SpellCastCheckContext context)
        => Registry.Find(context.Spell.Id)?.OnCheckCast(context) ?? SpellCastResult.CastOk;

    void ISpellCastObserver.OnCast(SpellCast cast) => Registry.Find(cast.Spell.Id)?.OnCast(cast);

    void ISpellCastObserver.OnSummoned(SpellEffectContext context, Creatures.Creature summon) => Registry.Find(context.Spell.Id)?.OnSummon(context, summon);
}
