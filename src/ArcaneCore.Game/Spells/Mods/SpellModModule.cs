using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// Makes the talent modifier auras live: handlers for SPELL_AURA_ADD_FLAT_MODIFIER (107) and SPELL_AURA_ADD_PCT_MODIFIER
/// (108), the engine installed as <see cref="SpellSystem.SpellModifiers"/>, the cast-time and duration hooks, and the hard-coded
/// ward mods. Discovered by <see cref="SpellHandlerModules.BuiltIn"/>, so a bare <see cref="SpellSystem"/> has it too. A host
/// reconfigures the installed engine (<see cref="SpellModExtensions"/> <c>spells.Mods</c>) from <see cref="SpellModOptions"/>.
/// <para>
/// A foreign <see cref="ISpellModifiers"/> installed before this module runs is left in place (only the identity default is
/// replaced): the engine still holds the mods, and whoever installed the other implementation decides whether to read them.
/// </para>
/// </summary>
public sealed class SpellModModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        SpellModEngine engine = SpellModExtensions.Install(system);
        var handlers = new SpellModAuraHandlers(system, engine);
        system.RegisterAura(AuraType.AddFlatModifier, new AuraHandler(handlers.OnAura, null));
        system.RegisterAura(AuraType.AddPctModifier, new AuraHandler(handlers.OnAura, null));
        if (ReferenceEquals(system.SpellModifiers, ISpellModifiers.None))
        {
            system.SpellModifiers = engine;
        }

        system.RegisterValueModifier(new SpellModValueAdapter(engine));
        engine.RemoveAura = (player, spellId) => system.RemoveAuras(player, spellId);
        system.RegisterObserver(new SpellModCastObserver(engine));
        new HardcodedMods(system, engine).Attach();
    }
}
