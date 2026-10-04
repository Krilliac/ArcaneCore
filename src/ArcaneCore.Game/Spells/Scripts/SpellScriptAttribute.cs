namespace ArcaneCore.Game.Spells.Scripts;

/// <summary>
/// Declares the spell ids an <see cref="ISpellScript"/> serves. Take the ids from the vmangos script header comments
/// (src/scripts/spells/*.cpp), not from the classic-db spell_scripts table, which names cmangos scripts.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SpellScriptAttribute(params uint[] spellIds) : Attribute
{
    public IReadOnlyList<uint> SpellIds { get; } = spellIds ?? throw new ArgumentNullException(nameof(spellIds));

    /// <summary>
    /// Effects, besides DUMMY and SCRIPT_EFFECT, whose execution the script wants to see before it runs (vmangos raises <c>OnEffectExecute</c> before every
    /// effect, Spell.cpp:5254-5257: Demonic Sacrifice reads its target before the INSTAKILL effect kills it). The dispatcher chains the handler of each
    /// declared effect that is installed when it is installed itself. Effects the world does not handle are not chained, so they stay "not implemented".
    /// </summary>
    public SpellEffectName[] ExecuteEffects { get; init; } = [];
}
