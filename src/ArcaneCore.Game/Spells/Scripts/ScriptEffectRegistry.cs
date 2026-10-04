using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Spells;

/// <summary>An implementation of SPELL_EFFECT_SCRIPT_EFFECT (77) for one spell.</summary>
public delegate void ScriptEffectHandler(SpellEffectContext context);

/// <summary>
/// The spell-id table behind SPELL_EFFECT_SCRIPT_EFFECT (vmangos Spell::EffectScriptEffect, SpellEffects.cpp, a switch on
/// the spell family and id). A spell system has exactly one handler for the effect (<see cref="ScriptEffectModule"/>,
/// because handler modules may not replace each other, see <see cref="SpellSystem.RegisterModules(IEnumerable{Type})"/>);
/// every area that needs a script effect adds its spell id here from its own module instead of registering the effect again.
/// A script effect of a spell with no entry does nothing, as in vmangos.
/// </summary>
public sealed class ScriptEffectRegistry
{
    private static readonly ConditionalWeakTable<SpellSystem, ScriptEffectRegistry> s_registries = new();

    private readonly Dictionary<uint, ScriptEffectHandler> _handlers = [];

    /// <summary>The registry of <paramref name="system"/>, created on first use (so module order does not matter).</summary>
    public static ScriptEffectRegistry For(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return s_registries.GetValue(system, static _ => new ScriptEffectRegistry());
    }

    /// <summary>The spell ids with a script, in no particular order.</summary>
    public IReadOnlyCollection<uint> SpellIds => _handlers.Keys;

    /// <summary>Add the script of <paramref name="spellId"/>; a second script for the same spell is a startup error.</summary>
    public void Add(uint spellId, ScriptEffectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(spellId, handler))
        {
            throw new InvalidOperationException($"spell {spellId} already has a script effect");
        }
    }

    internal void Run(SpellEffectContext context)
    {
        if (_handlers.TryGetValue(context.Spell.Id, out ScriptEffectHandler? handler))
        {
            handler(context);
        }
    }
}

/// <summary>
/// The one owner of <see cref="SpellEffectName.ScriptEffect"/>: it dispatches to the <see cref="ScriptEffectRegistry"/> by
/// spell id. Another handler module registering the effect makes startup fail (SpellSystem.RegisterModules), by design.
/// </summary>
public sealed class ScriptEffectModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        ScriptEffectRegistry registry = ScriptEffectRegistry.For(system);
        system.RegisterEffect(SpellEffectName.ScriptEffect, registry.Run);
    }
}
