using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary><c>spells.Mods</c>: the spell-modifier engine the built-in <see cref="SpellModModule"/> installed on a spell system.</summary>
public static class SpellModExtensions
{
    private static readonly ConditionalWeakTable<SpellSystem, SpellModEngine> Engines = new();

    extension(SpellSystem spells)
    {
        /// <summary>The modifier engine of this spell system (docs/areas/spell-mods.md).</summary>
        public ISpellModEngine Mods => Engines.TryGetValue(spells, out SpellModEngine? engine)
            ? engine
            : throw new InvalidOperationException("the spell-modifier module is not registered on this spell system");
    }

    internal static SpellModEngine Install(SpellSystem spells) => Engines.GetValue(spells, static _ => new SpellModEngine());
}
