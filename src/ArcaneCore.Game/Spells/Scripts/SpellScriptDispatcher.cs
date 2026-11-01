using ArcaneCore.Game.Entities;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Spells.Scripts;

/// <summary>
/// Routes the <see cref="ISpellScript"/> hooks of a <see cref="SpellScriptRegistry"/> on a <see cref="SpellSystem"/> without editing it: a
/// cast check in <see cref="SpellCheckPhase.Final"/> with the highest order (vmangos runs the script check last, Spell.cpp:6480),
/// an <see cref="ISpellCastObserver"/> for OnCast and OnSummon, and chained handlers for DUMMY, SCRIPT_EFFECT and DISPEL that keep whatever was
/// installed before (another lane's handler, the built-in dispel) and call it after the script.
/// <para>
/// SCRIPT_EFFECT (77) has no behaviour of its own: vmangos runs the spell's script (OnEffectExecute, Spell.cpp:5254-5257) and then
/// EffectScriptEffect, whose id switch falls through to the database script table (SpellEffects.cpp:3685-3700, :4560-4570): here the
/// spell's <c>dbscripts_on_spell</c> rows when it has some (SpellSystem.DbSpellScripts.cs), otherwise nothing, instead of an unsupported effect. Spells whose script effect
/// does real work need an <see cref="ISpellScript"/> or a <see cref="ScriptEffectRegistry"/> entry: the five Create Healthstone ranks,
/// for one, create their item from Warlock.CreateHealthstoneScript (vmangos spell_warlock.cpp:260-368).
/// </para>
/// Install once per <see cref="SpellSystem"/> (a second call throws).
/// <para>
/// Hot swap (<see cref="SpellScriptRegistry.CommitSwap"/>): every hook reads the registry's live snapshot when it fires, so added, replaced and
/// removed scripts take effect from the next hook on. The only thing built at install is the set of chained effect handlers, so the dispatcher
/// listens to <see cref="SpellScriptRegistry.ScriptsChanged"/> and chains any execute effect a newly added script declares (under the same rule as
/// at install: only an effect the world handles). A chained effect stays chained after the script that declared it is removed, but then finds no
/// script (<see cref="SpellScriptRegistry.FindForEffect"/>) and only calls the handler it wraps. The wrapper is installed through
/// <see cref="SpellSystem.RegisterTransparentEffectWrapper"/>, so while no current script declares the effect
/// <see cref="SpellSystem.HasBuiltInEffectHandler"/> looks through it to the wrapped handler: after a module declaring TELEPORT_UNITS or SUMMON is
/// unloaded, quest reward preflight accepts those rewards again, as after a restart without that script. While such a script is loaded the
/// effect is not built-in (the script's code cannot be modelled).
/// </para>
/// </summary>
public sealed class SpellScriptDispatcher : ISpellCastCheck, ISpellCastObserver
{
    private readonly HashSet<SpellEffectName> _chained = [];

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
        dispatcher.Chain(system, SpellEffectName.Dummy);
        dispatcher.Chain(system, SpellEffectName.ScriptEffect);
        dispatcher.ChainExecuteEffects(system);
        registry.ScriptsChanged += () => dispatcher.ChainExecuteEffects(system);

        SpellEffectHandler? dispel = system.GetEffectHandler(SpellEffectName.Dispel);
        SpellEffectHandler dispelWrapper = context =>
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
        };
        system.RegisterEffect(SpellEffectName.Dispel, dispelWrapper);
        system.GuardScriptChain(dispelWrapper);
        return dispatcher;
    }

    /// <summary>Chain every execute effect the registry's current scripts declare that is not chained yet (install, and after each swap).</summary>
    private void ChainExecuteEffects(SpellSystem system)
    {
        foreach (SpellEffectName effect in Registry.ExecuteEffects.Order())
        {
            // Only an effect the world handles is chained (an unhandled one must keep being reported as not implemented); Dispel has its own wrapper.
            if (effect is not (SpellEffectName.Dummy or SpellEffectName.ScriptEffect or SpellEffectName.Dispel) && system.HasEffectHandler(effect))
            {
                Chain(system, effect);
            }
        }
    }

    private void Chain(SpellSystem system, SpellEffectName effect)
    {
        if (!_chained.Add(effect))
        {
            return;
        }

        SpellScriptRegistry registry = Registry;
        SpellEffectHandler? previous = system.GetEffectHandler(effect);

        // Transparent (counts as the wrapped handler for SpellSystem.HasBuiltInEffectHandler) exactly when FindForEffect cannot return a script.
        system.RegisterTransparentEffectWrapper(
            effect,
            context =>
            {
                // A script that returns false cancels this effect for this target (vmangos: the effect is skipped when OnEffectExecute is false).
                if (registry.FindForEffect(context.Spell.Id, effect)?.OnEffectExecute(context, true) ?? true)
                {
                    previous?.Invoke(context);
                }
            },
            previous,
            () => effect is not (SpellEffectName.Dummy or SpellEffectName.ScriptEffect) && !registry.ExecuteEffects.Contains(effect));
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
