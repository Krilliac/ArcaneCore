namespace ArcaneCore.Game.Spells.Mods;

/// <summary><c>spells.Mods</c>: the spell-modifier engine the built-in <see cref="SpellModModule"/> installed on a spell system.</summary>
public static class SpellModExtensions
{
    private static readonly InstanceAttachments<SpellSystem, SpellModEngine> Engines = new();

    extension(SpellSystem spells)
    {
        /// <summary>The modifier engine of this spell system (docs/areas/spell-mods.md).</summary>
        public ISpellModEngine Mods => Engines.Find(spells) is { } engine
            ? engine
            : throw new InvalidOperationException("the spell-modifier module is not registered on this spell system");
    }

    internal static SpellModEngine Install(SpellSystem spells) => Engines.GetOrCreate(spells, static _ => new SpellModEngine());
}
