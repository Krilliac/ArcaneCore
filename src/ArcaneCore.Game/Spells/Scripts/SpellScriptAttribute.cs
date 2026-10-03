namespace ArcaneCore.Game.Spells.Scripts;

/// <summary>
/// Declares the spell ids an <see cref="ISpellScript"/> serves. Take the ids from the vmangos script header comments
/// (src/scripts/spells/*.cpp), not from the classic-db spell_scripts table, which names cmangos scripts.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SpellScriptAttribute(params uint[] spellIds) : Attribute
{
    public IReadOnlyList<uint> SpellIds { get; } = spellIds ?? throw new ArgumentNullException(nameof(spellIds));
}
